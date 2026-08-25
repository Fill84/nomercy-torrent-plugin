#!/usr/bin/env bash
#
# Packs NoMercy.Plugins.Abstractions and NoMercy.Plugins.Mvc out of the media
# server into _nupkgs/, so this repository can build against the contract.
# The PowerShell script beside this one does the same thing and explains why.
#
# Usage: scripts/fetch-abstractions.sh [branch] [--fresh]

set -euo pipefail

# master, never dev — the PowerShell twin of this script has said so all along
# and this one disagreed with it. dev's Directory.Build.props carries a fixed
# <Version>0.1.404</Version> that never moves, so packing from it gives a
# contract older than the one released servers ship. The build then fails with
# CS0246 naming a type, which reads like a missing using and is really a server
# too old. This plugin is installed on servers running a release, so the
# contract it compiles against is the one those servers carry.
branch="master"
fresh=0
for argument in "$@"; do
    case "$argument" in
        --fresh) fresh=1 ;;
        *) branch="$argument" ;;
    esac
done

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server_path="$repository_root/_server"
package_path="$repository_root/_nupkgs"
remote="https://github.com/NoMercy-Entertainment/nomercy-media-server.git"

# The .NET 10 SDK is user-local on the machines this runs on; the dotnet on PATH
# is 8.0 and cannot build any of this.
if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet="$HOME/.dotnet/dotnet"
elif [[ -x "$HOME/.dotnet/dotnet.exe" ]]; then
    dotnet="$HOME/.dotnet/dotnet.exe"
else
    dotnet="dotnet"
fi

sparse_paths=(
    "src/NoMercy.Plugins.Abstractions"
    "src/NoMercy.Plugins.Mvc"
    "src/NoMercy.Events"
    "src/NoMercy.Design"
    "src/NoMercy.Analyzers"
)

if [[ "$fresh" -eq 1 && -d "$server_path" ]]; then
    rm -rf "$server_path"
fi

if [[ -d "$server_path/.git" ]]; then
    echo "Updating _server to origin/$branch ..."
    git -C "$server_path" fetch --depth 1 origin "$branch"
    git -C "$server_path" sparse-checkout set "${sparse_paths[@]}"
    git -C "$server_path" checkout -B "$branch" "origin/$branch"
else
    echo "Cloning the media server ($branch) into _server ..."
    # blob:none rather than a plain shallow clone: the sparse checkout then only
    # ever downloads the blobs for the five projects named above.
    git clone --filter=blob:none --no-checkout --depth 1 --branch "$branch" "$remote" "$server_path"
    git -C "$server_path" sparse-checkout init --cone
    git -C "$server_path" sparse-checkout set "${sparse_paths[@]}"
    git -C "$server_path" checkout "$branch"
fi

msbuild_property() {
    # Deliberately crude: these two files are ours to read, not arbitrary XML.
    sed -n "s:.*<$2>\(.*\)</$2>.*:\1:p" "$1" | head -n 1
}

version="$(msbuild_property "$server_path/Directory.Build.props" Version)"
if [[ -z "$version" ]]; then
    echo "No <Version> in _server/Directory.Build.props. The media server changed how it versions itself." >&2
    exit 1
fi

echo "The media server on $branch is version $version."

declared="$(msbuild_property "$repository_root/Directory.Build.props" NoMercyContractVersion)"
if [[ "$declared" != "$version" ]]; then
    echo "warning: Directory.Build.props asks for $declared. Set NoMercyContractVersion to $version, or the build restores nothing." >&2
fi

mkdir -p "$package_path"

for project in NoMercy.Plugins.Abstractions NoMercy.Plugins.Mvc; do
    # The cache entry goes first. Restore prefers an already-extracted folder of
    # the same version over the file in _nupkgs, however new that file is.
    cached="$HOME/.nuget/packages/$(echo "$project" | tr '[:upper:]' '[:lower:]')/$version"
    if [[ -d "$cached" ]]; then
        echo "Clearing cached $project $version ..."
        rm -rf "$cached"
    fi

    echo "Packing $project ..."
    "$dotnet" pack "$server_path/src/$project/$project.csproj" -c Release -o "$package_path"
done

echo "Packed $version into $package_path."
