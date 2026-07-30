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

dotnet=dotnet

root=$(cd "$(dirname "$0")/.." && pwd)
server="$root/_server"
feed="$root/_nupkgs"
branch="${SERVER_BRANCH:-dev}"

if [ ! -d "$server" ]; then
    git clone --depth=1 --branch="$branch" --filter=blob:none --no-checkout \
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git "$server"
    git -C "$server" sparse-checkout init --cone
    git -C "$server" sparse-checkout set src/NoMercy.Plugins.Abstractions src/NoMercy.Events
    git -C "$server" checkout "$branch"
else
    git -C "$server" fetch --depth=1 origin "$branch"
    git -C "$server" reset --hard FETCH_HEAD
fi

mkdir -p "$feed"

# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
"$dotnet" pack "$server/src/NoMercy.Events/NoMercy.Events.csproj" -c Release -o "$feed"

"$dotnet" pack "$server/src/NoMercy.Plugins.Abstractions/NoMercy.Plugins.Abstractions.csproj" -c Release -o "$feed"

find "$feed" -maxdepth 1 -name '*.nupkg' -print
