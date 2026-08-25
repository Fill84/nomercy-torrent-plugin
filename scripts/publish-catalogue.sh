#!/usr/bin/env bash
#
# Puts this release into the plugin catalogue every NoMercy server reads.
#
# A server ships one repository by default —
# raw.githubusercontent.com/NoMercy-Entertainment/nomercy-plugins/master/index.json
# — and that index is the only thing that makes a plugin installable from the
# dashboard. A release nobody can find is a release nobody has.
#
# It runs from the same tag as the release itself, immediately after it, so the
# catalogue cannot fall behind the thing it advertises. That is not a
# hypothetical: the two forges drifted the moment a person was doing the
# publishing, and a catalogue kept by hand would drift the same way.
#
# The download it advertises is the **forgejo** one, because forgejo is where
# this plugin's releases are made. GitHub holds a copy; it is not the source.
#
# Usage:
#   scripts/publish-catalogue.sh <version> <zip> <notes.md> <plugin.json>
#
# Environment:
#   CATALOGUE_REPO    owner/repo of the catalogue      (default NoMercy-Entertainment/nomercy-plugins)
#   CATALOGUE_BRANCH  branch holding index.json        (default master)
#   CATALOGUE_TOKEN   a token that may write to it
#   FORGEJO_URL       https://forgejo.example
#   FORGEJO_REPO      owner/repo of this plugin

set -euo pipefail

version="${1:?usage: publish-catalogue.sh <version> <zip> <notes.md> <plugin.json>}"
zip="${2:?usage: publish-catalogue.sh <version> <zip> <notes.md> <plugin.json>}"
notes="${3:?usage: publish-catalogue.sh <version> <zip> <notes.md> <plugin.json>}"
manifest="${4:?usage: publish-catalogue.sh <version> <zip> <notes.md> <plugin.json>}"

repo="${CATALOGUE_REPO:-NoMercy-Entertainment/nomercy-plugins}"
branch="${CATALOGUE_BRANCH:-master}"
token="${CATALOGUE_TOKEN:?CATALOGUE_TOKEN is not set}"

for file in "$zip" "$notes" "$manifest"; do
    [ -f "$file" ] || { echo "no such file: $file" >&2; exit 1; }
done
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

# What the entry says about this plugin comes from the manifest the server will
# read, never from anything written twice. An id or an ABI that disagreed with
# the package would offer a plugin the server then refuses to load.
id="$(jq -r '.id' "$manifest")"
name="$(jq -r '.name' "$manifest")"
description="$(jq -r '.description' "$manifest")"
abi="$(jq -r '.targetAbi' "$manifest")"

checksum="$(sha256sum "$zip" | cut -d' ' -f1)"
asset="$(basename "$zip")"
download="${FORGEJO_URL:?FORGEJO_URL is not set}/${FORGEJO_REPO:?FORGEJO_REPO is not set}/releases/download/v${version}/${asset}"

# The first paragraph of the release notes, which is what those notes lead with
# and is written to be read on its own.
changelog="$(awk 'NR>1 && NF {print; found=1; next} found {exit}' "$notes" | tr '\n' ' ' | sed 's/  */ /g; s/ $//')"
[ -n "$changelog" ] || { echo "the release notes have no opening paragraph to quote" >&2; exit 1; }

echo "catalogue: $name $version, abi $abi"
echo "catalogue: $download"
echo "catalogue: sha256 $checksum"

umask 077
config="$(mktemp)"
trap 'rm -f "$config"' EXIT
printf 'header = "Authorization: Bearer %s"\nheader = "Accept: application/vnd.github+json"\nsilent\nshow-error\nlocation\n' \
    "$token" > "$config"

api="https://api.github.com/repos/$repo/contents/index.json"

current="$(curl -K "$config" -fsS "$api?ref=$branch")"
sha="$(printf '%s' "$current" | jq -r '.sha')"
index="$(printf '%s' "$current" | jq -r '.content' | tr -d '\n' | base64 -d)"

# Newest first, and one entry per version: re-running a release must correct
# what is there rather than adding a second row for the same number.
updated="$(printf '%s' "$index" | jq \
    --arg id "$id" --arg name "$name" --arg description "$description" \
    --arg version "$version" --arg abi "$abi" --arg download "$download" \
    --arg checksum "$checksum" --arg changelog "$changelog" \
    --arg project "${FORGEJO_URL}/${FORGEJO_REPO}" \
    --arg stamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '
    ($version | split(".") | map(tonumber)) as $order |
    {
        version: $version, targetAbi: $abi, downloadUrl: $download,
        checksum: $checksum, changelog: $changelog, timestamp: $stamp
    } as $entry |
    if any(.plugins[]; .id == $id) then
        .plugins |= map(
            if .id == $id then
                .name = $name | .description = $description | .projectUrl = $project
                | .versions = ([$entry] + (.versions | map(select(.version != $version))))
            else . end)
    else
        .plugins += [{
            id: $id, name: $name, description: $description,
            author: "NoMercy Community", projectUrl: $project, versions: [$entry]
        }]
    end')"

# Proof before pushing. A catalogue that offers a download nobody can fetch, or
# a checksum that does not match it, is worse than no catalogue entry at all —
# the server refuses the install and says only that verification failed.
printf '%s' "$updated" | jq -e --arg id "$id" --arg v "$version" --arg c "$checksum" \
    '[.plugins[] | select(.id == $id) | .versions[] | select(.version == $v and .checksum == $c)] | length == 1' \
    > /dev/null || { echo "the entry did not go in exactly once" >&2; exit 1; }

if [ "$(printf '%s' "$updated" | jq -S .)" = "$(printf '%s' "$index" | jq -S .)" ]; then
    echo "catalogue: already says this, nothing to push"
    exit 0
fi

echo "catalogue: writing index.json on $repo@$branch"

curl -K "$config" -fsS -X PUT "$api" -d "$(jq -n \
    --arg message "catalogue: $name $version" \
    --arg content "$(printf '%s' "$updated" | jq --indent 2 . | base64 -w0)" \
    --arg sha "$sha" --arg branch "$branch" \
    '{message: $message, content: $content, sha: $sha, branch: $branch}')" > /dev/null

echo "catalogue: $name $version is in the catalogue"
