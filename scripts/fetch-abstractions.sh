#!/usr/bin/env sh
# Packs NoMercy.Plugins.Abstractions into a local NuGet feed.
#
# The contract is not published to nuget.org. Its csproj is IsPackable and carries
# full package metadata precisely because "a plugin author outside this repository
# has no other way to get it" - but nothing publishes it yet. So we clone and pack.
#
# NoMercy.Events must be packed too: it is a ProjectReference of the abstractions,
# so packing only the abstractions yields a package whose dependency cannot resolve.
#
# NoMercy.Design is one as well, since the abstractions picked it up so a plugin can
# name every design component rather than the handful it had tags for, and it is what
# carries Newtonsoft into the shared-assembly set.
#
# Every ProjectReference of NoMercy.Plugins.Abstractions has to be both in the
# sparse-checkout list AND packed into the feed. Miss the checkout and the compile
# fails on the types it cannot see; miss the pack and the plugin's own restore fails
# on a dependency the feed does not have. When the server adds one, this script is
# what needs updating - the symptom is a wall of CS0246 for a namespace nobody in
# this repository has ever referenced.

set -eu

# CI puts the right SDK on PATH. On a Windows dev machine the `dotnet` on PATH may be
# an older SDK that cannot build net10.0, and the usable one is a side-by-side install
# under the user profile - but it can just as easily be the other way round, and a
# side-by-side install without a 10.x SDK fails global.json resolution before it packs
# anything. So the version decides, not the location.
can_build_net10() {
    [ -x "$1" ] || [ "$1" = dotnet ] || return 1
    "$1" --list-sdks 2>/dev/null | grep -q '^10\.'
}

for candidate in "${USERPROFILE:-}/.dotnet/dotnet.exe" "${HOME:-}/.dotnet/dotnet" dotnet; do
    if can_build_net10 "$candidate"; then
        dotnet="$candidate"
        break
    fi
done

if [ -z "${dotnet:-}" ]; then
    echo "no dotnet SDK on this machine can build net10.0" >&2
    exit 1
fi

root=$(cd "$(dirname "$0")/.." && pwd)
# The server checkout lives beside this repo, not inside it: it is a full clone of another
# project and a sibling is where a developer expects to find one. SERVER_DIR overrides it,
# which is how CI keeps the clone inside its own disposable workspace.
server="${SERVER_DIR:-$(dirname "$root")/nomercy-media-server}"
feed="$root/_nupkgs"
# A release must be rebuildable. SERVER_REF pins the contract to one commit; it
# defaults to a branch for day-to-day work, but CI sets it to a SHA for a tag build
# so the artifact is reproducible instead of "whatever dev happened to be".
#
# The default is master, not dev: this plugin is installed on servers running a
# release, so the contract it compiles against should be the one those servers
# actually ship. Building against dev meant a green build proved nothing about the
# machine the plugin lands on - and the two branches carry different version stamps
# for identical sources, so "it built" was not the same as "it will load".
# SERVER_BRANCH=dev is still one word away when the work needs an unreleased change.
ref="${SERVER_REF:-${SERVER_BRANCH:-master}}"

if [ ! -d "$server" ]; then
    git clone --depth=1 --branch="${SERVER_BRANCH:-master}" --filter=blob:none --no-checkout \
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git "$server"
    git -C "$server" sparse-checkout init --cone
fi

# Applied on every run, not only on the initial clone. Setting it once meant adding a project
# to the list silently did nothing on a checkout that already existed - the pack then failed
# with "project file does not exist" pointing at a path the sparse set had never been told to
# materialise, which is a confusing way to learn that this script is not idempotent.
git -C "$server" sparse-checkout set \
    src/NoMercy.Plugins.Abstractions src/NoMercy.Events src/NoMercy.Design src/NoMercy.Plugins.Mvc

git -C "$server" fetch --depth=1 origin "$ref"
git -C "$server" reset --hard FETCH_HEAD

mkdir -p "$feed"

abstractions="$server/src/NoMercy.Plugins.Abstractions/NoMercy.Plugins.Abstractions.csproj"
if [ ! -f "$abstractions" ]; then
    echo "NoMercy.Plugins.Abstractions is not present at $ref - nothing to build against" >&2
    exit 1
fi

# Dependency order, and each one only if the ref actually has it. SERVER_REF pins this
# script to any commit and a release's notes hand out that exact command, so it has to
# keep working on a ref from before a project existed: NoMercy.Design is not in the tree
# at all at the commit v0.1.0 was built from. Packing it unconditionally would break the
# reproduction path for every release so far.
#
# NoMercy.Plugins.Mvc holds PluginControllerBase, which this plugin's REST controllers
# inherit. Its own assembly rather than a type in Abstractions on purpose: the base class
# must keep one identity across the load-context boundary, so it lives in the host's
# shared set, and putting it in Abstractions would force a Microsoft.AspNetCore.App
# FrameworkReference on every plugin - including the ones that never serve a request.
#
# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
for project in NoMercy.Events NoMercy.Design NoMercy.Plugins.Abstractions NoMercy.Plugins.Mvc; do
    csproj="$server/src/$project/$project.csproj"
    if [ ! -f "$csproj" ]; then
        echo "skipping $project - not in the tree at $ref"
        continue
    fi
    "$dotnet" pack "$csproj" -c Release -o "$feed"
done

find "$feed" -maxdepth 1 -name '*.nupkg' -print

echo "contract packed from nomercy-media-server $(git -C "$server" rev-parse HEAD)"
