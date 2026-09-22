#!/usr/bin/env bash
#
# Packs NoMercy.PluginSdk.Abstractions and NoMercy.PluginSdk.Mvc out of the
# media server into _nupkgs/, so this repository can build against the
# contract. The PowerShell script beside this one does the same thing and
# explains why.
#
# Usage: scripts/fetch-abstractions.sh [branch] [--fresh]

set -euo pipefail

# dev, since 22 September 2026. The contract is versioned on its own now —
# <PluginPackageVersion> in the server's Directory.Build.props, 12.0.0 at the
# time of writing — rather than with the server, so the reason this script
# refused dev (a <Version> frozen at 0.1.404 that NuGet took for "already
# have it") is gone. And master is where the contract is not: it sits on a
# release from August carrying ABI 11, while the servers the owner and Stoney
# run come from dev and refuse anything under 12. The packages go to nuget.org
# with the next server release; until they are there, this is the only feed.
branch="dev"
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

# The two packable projects, the two whose assemblies travel inside the first
# of them, and the analyzer every project in that repository inherits through
# its Directory.Build.props.
sparse_paths=(
    "src/NoMercy.PluginSdk.Abstractions"
    "src/NoMercy.PluginSdk.Mvc"
    "src/NoMercy.Events"
    "src/NoMercy.Design"
    "src/NoMercy.Analyzers"
)

if [[ "$fresh" -eq 1 && -d "$server_path" ]]; then
    rm -rf "$server_path"
fi

if [[ -d "$server_path/.git" ]]; then
    echo "Updating _server to origin/$branch ..."
    # With the refspec spelled out, exactly as the PowerShell twin does and
    # for the reason it gives: a single-branch shallow clone tracks only the
    # branch it was made from, so fetching one by name lands in FETCH_HEAD and
    # never writes refs/remotes/origin/<branch>. The checkout below then resets
    # to whatever origin/<branch> was at clone time and reports "Reset branch"
    # as though it had moved. On 30 August 2026 a local checkout sat on
    # contract 0.1.478 that way while CI, which always clones fresh, packed
    # 0.1.479 — and the difference only showed as a red run nobody could
    # reproduce locally.
    git -C "$server_path" fetch --depth 1 origin "+refs/heads/$branch:refs/remotes/origin/$branch"
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

# The contract's own version, not the server's. Since the rename to
# NoMercy.PluginSdk the packages carry PluginPackageVersion — the major is the
# ABI a server refuses or accepts by, the minor moves when the contract gains a
# member — and it is the number NuGet caches the package under below.
version="$(msbuild_property "$server_path/Directory.Build.props" PluginPackageVersion)"
if [[ -z "$version" ]]; then
    echo "No <PluginPackageVersion> in _server/Directory.Build.props. The media server changed how it versions the plugin contract." >&2
    exit 1
fi

echo "The plugin contract on $branch is version $version (server $(msbuild_property "$server_path/Directory.Build.props" Version))."

# NoMercyContractVersion pins the major and floats the minor, so the build takes
# whatever 12.x is packed below. The line above is what a build compiled
# against, and it is the only record of it that a floating minor leaves.

mkdir -p "$package_path"

# Only the two. NoMercy.Events and NoMercy.Design used to be packages of their
# own that the contract depended on, and this loop packed all four; since the
# rename they ship as assemblies inside NoMercy.PluginSdk.Abstractions, are
# IsPackable=false, and packing them produces nothing a restore needs. The
# server's own release workflow packs exactly these two, plus Testing and
# Analyzers, which this repository does not use.
for project in NoMercy.PluginSdk.Abstractions NoMercy.PluginSdk.Mvc; do
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
