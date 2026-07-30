#!/usr/bin/env sh
# Packs NoMercy.Plugins.Abstractions into a local NuGet feed.
#
# The contract is not published to nuget.org. Its csproj is IsPackable and carries
# full package metadata precisely because "a plugin author outside this repository
# has no other way to get it" - but nothing publishes it yet. So we clone and pack.
#
# NoMercy.Events must be packed too: it is a ProjectReference of the abstractions,
# so packing only the abstractions yields a package whose dependency cannot resolve.

set -eu

# CI puts the right SDK on PATH. On a Windows dev machine the `dotnet` on PATH is
# an older SDK that cannot build net10.0, and the usable one is a side-by-side
# install under the user profile, so prefer that when it is there.
if [ -x "${USERPROFILE:-}/.dotnet/dotnet.exe" ]; then
    dotnet="${USERPROFILE}/.dotnet/dotnet.exe"
elif [ -x "${HOME:-}/.dotnet/dotnet" ]; then
    dotnet="${HOME}/.dotnet/dotnet"
else
    dotnet=dotnet
fi

root=$(cd "$(dirname "$0")/.." && pwd)
server="$root/_server"
feed="$root/_nupkgs"
# A release must be rebuildable. SERVER_REF pins the contract to one commit; it
# defaults to a branch for day-to-day work, but CI sets it to a SHA for a tag build
# so the artifact is reproducible instead of "whatever dev happened to be".
ref="${SERVER_REF:-${SERVER_BRANCH:-dev}}"

if [ ! -d "$server" ]; then
    git clone --depth=1 --branch="${SERVER_BRANCH:-dev}" --filter=blob:none --no-checkout \
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git "$server"
    git -C "$server" sparse-checkout init --cone
    git -C "$server" sparse-checkout set src/NoMercy.Plugins.Abstractions src/NoMercy.Events
    git -C "$server" fetch --depth=1 origin "$ref"
    git -C "$server" checkout -q FETCH_HEAD
else
    git -C "$server" fetch --depth=1 origin "$ref"
    git -C "$server" reset --hard FETCH_HEAD
fi

mkdir -p "$feed"

# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
"$dotnet" pack "$server/src/NoMercy.Events/NoMercy.Events.csproj" -c Release -o "$feed"

"$dotnet" pack "$server/src/NoMercy.Plugins.Abstractions/NoMercy.Plugins.Abstractions.csproj" -c Release -o "$feed"

find "$feed" -maxdepth 1 -name '*.nupkg' -print

echo "contract packed from nomercy-media-server $(git -C "$server" rev-parse HEAD)"
