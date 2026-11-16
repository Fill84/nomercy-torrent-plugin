#!/usr/bin/env bash
#
# Puts one release on the NoMercy Marketplace and waits until it is released.
#
# Servers install this plugin from the marketplace, not from the forges. A tag
# that reaches Forgejo and GitHub and not the marketplace is a release no server
# is ever offered: 0.8.2 sat on both forges while every server still saw 0.8.1,
# until its source was uploaded by hand.
#
# The marketplace builds what it signs, so what goes up is the tagged source, not
# the package. It then builds, scans and signs it. This waits for that to end:
# "released" passes, "held" fails with the marketplace's own reason, so a run
# goes red rather than leaving a release that never reaches anybody.
#
# Idempotent: a re-run whose version the marketplace already offers passes
# without uploading it again.
#
# Usage:
#   scripts/publish-marketplace.sh <version> <notes.md> <plugin.json> [<commit>]
#
# Environment:
#   MARKETPLACE_REFRESH_TOKEN  a Keycloak offline token of the publisher's
#                              account (scripts/marketplace-login.py stores it)
#   MARKETPLACE_API            default https://api.nomercy.tv/v1
#   MARKETPLACE_WAIT_SECONDS   default 1800
set -euo pipefail

usage="usage: publish-marketplace.sh <version> <notes.md> <plugin.json> [<commit>]"
version="${1:?$usage}"
notes="${2:?$usage}"
manifest="${3:?$usage}"
commit="${4:-HEAD}"

api="${MARKETPLACE_API:-https://api.nomercy.tv/v1}"
wait_seconds="${MARKETPLACE_WAIT_SECONDS:-1800}"
here="$(cd "$(dirname "$0")" && pwd)"

for file in "$notes" "$manifest"; do
    [ -f "$file" ] || { echo "no such file: $file" >&2; exit 1; }
done
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

listing="$(jq -r '.id' "$manifest")"
manifest_version="$(jq -r '.version' "$manifest")"
[ "$manifest_version" = "$version" ] || {
    echo "plugin.json says $manifest_version, the tag says $version" >&2
    exit 1
}

# The same opening paragraph the repository index quotes, so the marketplace
# and the forges describe a release in the same words.
changelog="$(awk 'NR>1 && NF {print; found=1; next} found {exit}' "$notes" | tr '\n' ' ' | sed 's/  */ /g; s/ $//')"
[ -n "$changelog" ] || { echo "the release notes have no opening paragraph to quote" >&2; exit 1; }

token="$("$here/marketplace-token.sh")"
auth="Authorization: Bearer $token"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# Whether the public index already offers this version, which is what "released"
# means to a server. The publisher API has no way to list a listing's versions,
# so a re-run that finds the version taken asks here instead.
offered() {
    curl -fsS "$api/marketplace/index.json" \
        | jq -e --arg id "$listing" --arg v "$version" \
            '[.. | objects | select(.id? == $id) | .versions[]? | select(.version == $v)] | length > 0' \
            >/dev/null 2>&1
}

source_zip="$work/source.zip"
git archive --format=zip -o "$source_zip" "$commit"

echo "marketplace: uploading $version of listing $listing"

status_code="$(curl -sS -o "$work/upload.json" -w '%{http_code}' \
    -H "$auth" -H "Accept: application/json" \
    -F "semver=$version" \
    -F "changelog=$changelog" \
    -F "channel=stable" \
    -F "source=@$source_zip;type=application/zip" \
    "$api/publisher/listings/$listing/versions")"

case "$status_code" in
    201)
        version_id="$(jq -r '.data.id' "$work/upload.json")"
        ;;
    422)
        # Most often: this version was uploaded before (a re-run). If servers are
        # already offered it there is nothing left to do; anything else is a
        # refusal worth reading.
        if offered; then
            echo "marketplace: $version was uploaded before and is released"
            exit 0
        fi
        echo "marketplace refused the upload ($status_code):" >&2
        cat "$work/upload.json" >&2
        exit 1
        ;;
    *)
        echo "marketplace refused the upload ($status_code):" >&2
        cat "$work/upload.json" >&2
        exit 1
        ;;
esac

if [ -z "$version_id" ] || [ "$version_id" = "null" ]; then
    echo "the marketplace answered without a version id:" >&2
    cat "$work/upload.json" >&2
    exit 1
fi

# Built, scanned and signed by the marketplace's own queue. Seconds when it is
# idle; the limit is for a queue that is not.
deadline=$(( $(date +%s) + wait_seconds ))
last=""
while :; do
    curl -fsS -H "$auth" -H "Accept: application/json" \
        "$api/publisher/listings/$listing/versions/$version_id/scan" > "$work/scan.json"

    state="$(jq -r '.data.status' "$work/scan.json")"
    [ "$state" != "$last" ] && echo "marketplace: $version is $state"
    last="$state"

    case "$state" in
        released)
            echo "marketplace: $version released"
            exit 0
            ;;
        held|refused|failed|revoked)
            echo "marketplace held $version back:" >&2
            jq '.data | {status, hold_until, scan_result}' "$work/scan.json" >&2
            echo "The scan report is in the publisher console:" >&2
            echo "  https://nomercy.tv/publisher/listings/$listing/versions/$version_id/scan" >&2
            exit 1
            ;;
    esac

    if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "marketplace: $version is still $state after ${wait_seconds}s" >&2
        exit 1
    fi

    sleep 15
done
