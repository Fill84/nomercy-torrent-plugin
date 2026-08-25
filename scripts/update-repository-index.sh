#!/usr/bin/env bash
#
# Keeps `repository.json` in this repository current: every version this plugin
# has released, where to download it, and the checksum of that download.
#
# **This is the file the catalogue reads.** A server's default catalogue lists a
# plugin per version — version, download, checksum — so a plugin that is listed
# once goes stale the moment it ships again, and somebody has to open another
# pull request for every release. That is how the catalogue came to offer 1.2.0
# of a plugin whose own CI knew nothing about it.
#
# So this plugin keeps its own index, at a URL that does not move, and the
# catalogue reads it. An author submits that one URL once and never again.
#
# It writes into this repository and nothing else. No token beyond the one the
# runner already has, and no rights on anybody else's repository — which is the
# whole point: a plugin nobody has heard of must be able to do this too.
#
# Usage:
#   scripts/update-repository-index.sh <version> <zip> <notes.md> <plugin.json>
#
# Environment:
#   FORGEJO_URL   https://forgejo.example
#   FORGEJO_REPO  owner/repo of this plugin
#   INDEX_FILE    where the index lives      (default repository.json)

set -euo pipefail

version="${1:?usage: update-repository-index.sh <version> <zip> <notes.md> <plugin.json>}"
zip="${2:?usage: update-repository-index.sh <version> <zip> <notes.md> <plugin.json>}"
notes="${3:?usage: update-repository-index.sh <version> <zip> <notes.md> <plugin.json>}"
manifest="${4:?usage: update-repository-index.sh <version> <zip> <notes.md> <plugin.json>}"

index="${INDEX_FILE:-repository.json}"

for file in "$zip" "$notes" "$manifest"; do
    [ -f "$file" ] || { echo "no such file: $file" >&2; exit 1; }
done
command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }

# From the manifest the server will read, never written twice. An id or an ABI
# that disagreed with the package would offer a plugin the server then refuses.
id="$(jq -r '.id' "$manifest")"
name="$(jq -r '.name' "$manifest")"
description="$(jq -r '.description' "$manifest")"
abi="$(jq -r '.targetAbi' "$manifest")"

checksum="$(sha256sum "$zip" | cut -d' ' -f1)"
asset="$(basename "$zip")"
project="${FORGEJO_URL:?FORGEJO_URL is not set}/${FORGEJO_REPO:?FORGEJO_REPO is not set}"
download="$project/releases/download/v${version}/${asset}"

# The opening paragraph of the release notes, which those notes are written to
# lead with and which reads on its own.
changelog="$(awk 'NR>1 && NF {print; found=1; next} found {exit}' "$notes" | tr '\n' ' ' | sed 's/  */ /g; s/ $//')"
[ -n "$changelog" ] || { echo "the release notes have no opening paragraph to quote" >&2; exit 1; }

[ -f "$index" ] || printf '{"name":"NoMercy Torrent Downloader","url":"","plugins":[]}' > "$index"

updated="$(jq \
    --arg id "$id" --arg name "$name" --arg description "$description" \
    --arg version "$version" --arg abi "$abi" --arg download "$download" \
    --arg checksum "$checksum" --arg changelog "$changelog" --arg project "$project" \
    --arg stamp "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '
    {
        version: $version, targetAbi: $abi, downloadUrl: $download,
        checksum: $checksum, changelog: $changelog, timestamp: $stamp
    } as $entry |
    .name = $name | .url = $project |
    if any(.plugins[]; .id == $id) then
        .plugins |= map(
            if .id == $id then
                .name = $name | .description = $description | .projectUrl = $project
                # Newest first, and one entry per version: releasing the same
                # number twice corrects it rather than listing it twice.
                | .versions = ([$entry] + (.versions | map(select(.version != $version))))
            else . end)
    else
        .plugins += [{
            id: $id, name: $name, description: $description,
            author: "NoMercy Community", projectUrl: $project, versions: [$entry]
        }]
    end' "$index")"

# Proof before it is written. An index offering a download that cannot be
# fetched, or a checksum that does not match it, is worse than no listing: the
# install fails saying only that verification failed.
printf '%s' "$updated" | jq -e --arg id "$id" --arg v "$version" --arg c "$checksum" \
    '[.plugins[] | select(.id == $id) | .versions[] | select(.version == $v and .checksum == $c)] | length == 1' \
    > /dev/null || { echo "the entry did not go in exactly once" >&2; exit 1; }

if [ "$(printf '%s' "$updated" | jq -S .)" = "$(jq -S . "$index")" ]; then
    echo "index: already says this, nothing to write"
    exit 0
fi

printf '%s\n' "$(printf '%s' "$updated" | jq --indent 2 .)" > "$index"

echo "index: $name $version"
echo "index: $download"
echo "index: sha256 $checksum"
