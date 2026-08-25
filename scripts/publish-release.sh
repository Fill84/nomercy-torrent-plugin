#!/usr/bin/env bash
#
# Publishes one release, with its package, to both forges — and leaves them
# saying exactly the same thing.
#
# This repository has two remotes and they are not a primary and a backup: they
# must carry the same tags, the same releases and the same bytes. They drifted
# once, quietly — 0.2.0 was released on forgejo and never on GitHub, 0.3.9 on
# GitHub and never on forgejo — because each release was made by hand, on
# whichever forge whoever was releasing happened to be looking at.
#
# So a release is never made by hand again. The build workflow calls this on a
# `v*` tag, and a person can call it too when a runner is down, with the same
# arguments and the same result.
#
# Idempotent on purpose. It updates a release that already exists and replaces
# an asset of the same name, so a re-run after a half-finished publish ends with
# both forges correct rather than with one of them refusing.
#
# Usage:
#   scripts/publish-release.sh <version> <zip> <notes.md>
#
# Environment:
#   FORGEJO_URL     https://forgejo.example            (no trailing slash)
#   FORGEJO_REPO    owner/repo
#   FORGEJO_TOKEN   a token with write access
#   MIRROR_REPO     owner/repo        (the GitHub side)
#   MIRROR_TOKEN    a token with write access
#
# The GitHub pair is not called GITHUB_ANYTHING on purpose: a CI runner owns
# that prefix and sets a dozen of them itself.
#
# Either forge can be skipped by leaving its token empty, and it says so rather
# than passing over it in silence: a mirror that stopped being written to is the
# fault this script exists to prevent.

set -euo pipefail

version="${1:?usage: publish-release.sh <version> <zip> <notes.md>}"
zip="${2:?usage: publish-release.sh <version> <zip> <notes.md>}"
notes="${3:?usage: publish-release.sh <version> <zip> <notes.md>}"

tag="v${version}"
asset="$(basename "$zip")"

[ -f "$zip" ] || { echo "no package at $zip" >&2; exit 1; }
[ -f "$notes" ] || { echo "no release notes at $notes" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

body="$(cat "$notes")"

# The token is written to a curl config rather than passed as an argument. An
# argument is visible in the host's process list to anything else on the
# machine, which on a self-hosted runner means any co-tenant process.
umask 077
config="$(mktemp)"
trap 'rm -f "$config"' EXIT

# ---------------------------------------------------------------------------

# Every forge here speaks the same three calls, so they are written once.
#
# $1 the forge's name, for what is printed
# $2 the api root
# $3 owner/repo
# $4 the token
# $5 the url assets are uploaded to, with {id} where the release id goes
# $6 the url one asset is deleted from, with {release} and {asset} in it
publish() {
    local forge="$1" api="$2" repo="$3" token="$4" upload="$5" remove="$6"

    if [ -z "$token" ]; then
        echo "$forge: no token given, so nothing was published there." >&2
        echo "$forge: the two forges are now different, which is the thing this refuses to hide." >&2
        return 1
    fi

    printf 'header = "Authorization: Bearer %s"\nsilent\nshow-error\nlocation\n' "$token" > "$config"

    echo "$forge: looking for $tag"

    # Every release, matched on the tag here, rather than asking the forge for
    # the release of a tag. "Get a release by tag name" does not return drafts —
    # so against a drafted release it answered "no such thing", this made a
    # second release of the same version, and the repository ended up with two
    # v0.3.9 entries carrying different packages.
    #
    # A release becomes a draft on its own: GitHub demotes one whenever its tag
    # stops existing, which is what a moved tag does. So the drafted case is not
    # unusual and must be found.
    local id
    id="$(curl -K "$config" -fsS "$api/repos/$repo/releases?limit=100&per_page=100" \
          | jq -r --arg tag "$tag" 'map(select(.tag_name == $tag)) | first | .id // empty' || true)"

    if [ -n "$id" ]; then
        echo "$forge: $tag is already there as $id, updating it"

        curl -K "$config" -fsS -X PATCH \
            -H "Content-Type: application/json" \
            "$api/repos/$repo/releases/$id" \
            -d "$(jq -n --arg name "$tag" --arg body "$body" \
                  '{name: $name, body: $body, draft: false, prerelease: false}')" > /dev/null
    else
        echo "$forge: creating $tag"

        id="$(curl -K "$config" -fsS -X POST \
            -H "Content-Type: application/json" \
            "$api/repos/$repo/releases" \
            -d "$(jq -n --arg tag "$tag" --arg name "$tag" --arg body "$body" \
                  '{tag_name: $tag, name: $name, body: $body, draft: false, prerelease: false}')" \
            | jq -r '.id')"
    fi

    if [ -z "$id" ] || [ "$id" = "null" ]; then
        echo "$forge: no release id came back, so no asset was uploaded" >&2
        return 1
    fi

    # An asset of the same name is replaced rather than added beside itself.
    # Two files called the same thing on one release is worse than either of
    # them alone: nobody can tell which one they downloaded.
    #
    # The two forges spell this differently and that difference is not
    # cosmetic. Forgejo wants the release in the path; GitHub wants the asset
    # alone. Using GitHub's shape for both made every forgejo delete a 404,
    # which `|| true` swallowed — so forgejo kept an older build's package
    # while GitHub took the new one, and this script said they matched.
    local old
    old="$(curl -K "$config" -fsS "$api/repos/$repo/releases/$id/assets" \
           | jq -r --arg name "$asset" '.[] | select(.name == $name) | .id')"

    for one in $old; do
        echo "$forge: removing the existing $asset ($one)"

        curl -K "$config" -fsS -X DELETE \
            "$(printf '%s' "$remove" | sed -e "s/{release}/$id/" -e "s/{asset}/$one/")" > /dev/null
    done

    echo "$forge: uploading $asset"

    curl -K "$config" -fsS -X POST \
        -H "Content-Type: application/octet-stream" \
        --data-binary "@$zip" \
        "${upload//\{id\}/$id}?name=$asset" > /dev/null

    # Read back what is really on the release. Every step above can fail in a
    # way that leaves the old file in place, and a release that quietly carries
    # the wrong package is the one fault this script exists to prevent.
    local landed
    landed="$(curl -K "$config" -fsS "$api/repos/$repo/releases/$id/assets" \
              | jq -r --arg name "$asset" '[.[] | select(.name == $name) | .size] | @tsv')"

    local expected
    expected="$(wc -c < "$zip" | tr -d ' ')"

    if [ "$landed" != "$expected" ]; then
        echo "$forge: $asset should be $expected bytes and the release carries '$landed'." >&2
        echo "$forge: either the upload did not take or an older copy is still there." >&2
        return 1
    fi

    echo "$forge: $tag published, $expected bytes"
}

# ---------------------------------------------------------------------------

failed=0

publish "forgejo" \
    "${FORGEJO_URL:?FORGEJO_URL is not set}/api/v1" \
    "${FORGEJO_REPO:?FORGEJO_REPO is not set}" \
    "${FORGEJO_TOKEN:-}" \
    "${FORGEJO_URL}/api/v1/repos/${FORGEJO_REPO}/releases/{id}/assets"     "${FORGEJO_URL}/api/v1/repos/${FORGEJO_REPO}/releases/{release}/assets/{asset}"     || failed=1

# GitHub takes its uploads on a host of its own, which is the one difference
# between the two that is not this repository's doing.
publish "github" \
    "https://api.github.com" \
    "${MIRROR_REPO:?MIRROR_REPO is not set}" \
    "${MIRROR_TOKEN:-}" \
    "https://uploads.github.com/repos/${MIRROR_REPO}/releases/{id}/assets"     "https://api.github.com/repos/${MIRROR_REPO}/releases/assets/{asset}"     || failed=1

if [ "$failed" != "0" ]; then
    echo >&2
    echo "One of the two forges was not published to. They are meant to be identical," >&2
    echo "so this is a failure and not a warning." >&2
    exit 1
fi

echo
echo "$tag is on both forges, with the same package."
