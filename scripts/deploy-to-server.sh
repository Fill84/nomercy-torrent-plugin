#!/usr/bin/env sh
# SPDX-License-Identifier: MIT
#
# Copies a built plugin onto a running NoMercy server over ssh.
#
#   scripts/deploy-to-server.sh                 # deploy what is already built
#   scripts/deploy-to-server.sh --build         # build Release first, then deploy
#   SERVER=beast-unit scripts/deploy-to-server.sh
#
# THE SERVER MUST BE STOPPED FIRST. A loaded plugin's assembly is held open by
# the host, so the copy fails with "Device or resource busy" and the old build
# stays in place - which looks exactly like a deploy that worked and changed
# nothing. This script refuses to report success in that case; see the check
# after each file.
#
# Files go over as base64 through ssh rather than with scp. Plain scp fails
# against this host because OpenSSH 9 made it speak SFTP by default and the host
# does not answer that; `scp -O` works and is faster. This stays on base64
# because it verifies every file's hash afterwards either way, and at a few
# hundred kilobytes the difference is not worth a second code path.
set -eu

server="${SERVER:-beast-unit}"
root="$(cd "$(dirname "$0")/.." && pwd)"
project="NoMercy.Plugin.TorrentDownloader"
configuration="${CONFIGURATION:-Release}"
framework="${FRAMEWORK:-net10.0}"

# Where the host looks for plugins. Expanded on the far side, not here: it is
# that machine's profile, not this one's.
remote_dir="\$LOCALAPPDATA/NoMercy/plugins/$project"

out="$root/src/$project/bin/$configuration/$framework"

# Everything the host reads. The manifest travels with the assembly on purpose:
# the two carry the version independently, and a build that updates one without
# the other leaves every server reporting a version it is not running.
files="$project.dll $project.Core.dll $project.deps.json plugin.json"

if [ "${1:-}" = "--build" ]; then
    dotnet="dotnet"
    for candidate in "${USERPROFILE:-}/.dotnet/dotnet.exe" "${HOME:-}/.dotnet/dotnet"; do
        [ -x "$candidate" ] && dotnet="$candidate" && break
    done

    echo "building $configuration…"
    "$dotnet" build "$root/src/$project/$project.csproj" -c "$configuration" --nologo
fi

[ -d "$out" ] || { echo "nothing built at $out - run with --build" >&2; exit 1; }

echo "deploying to $server…"

for file in $files; do
    [ -f "$out/$file" ] || { echo "  skip $file (not built)"; continue; }

    local_sum="$(md5sum "$out/$file" | cut -d' ' -f1)"

    base64 -w0 "$out/$file" > "$root/.deploy.b64"
    ssh -o BatchMode=yes "$server" "cat > /tmp/nm-deploy.b64" < "$root/.deploy.b64"
    rm -f "$root/.deploy.b64"

    remote_sum="$(
        ssh -o BatchMode=yes "$server" \
            "base64 -d /tmp/nm-deploy.b64 > \"$remote_dir/$file\" && md5sum \"$remote_dir/$file\"" \
            2>/dev/null | tr -d '\\\\*' | cut -d' ' -f1
    )"

    # The hashes are the whole point. A busy file leaves the old bytes in place
    # and the copy still "succeeds" from the shell's point of view, so comparing
    # is the only way to know a deploy actually happened.
    if [ "$local_sum" != "$remote_sum" ]; then
        echo "  FAILED $file" >&2
        echo "    local  $local_sum" >&2
        echo "    remote ${remote_sum:-<none>}" >&2
        echo "    Is the server still running? A loaded plugin's dll cannot be replaced." >&2
        exit 1
    fi

    echo "  ok $file  $local_sum"
done

echo
echo "done - start the server again."
