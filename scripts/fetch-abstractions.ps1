#!/usr/bin/env pwsh
# Packs NoMercy.Plugins.Abstractions into a local NuGet feed.
#
# The contract is not published to nuget.org. Its csproj is IsPackable and carries
# full package metadata precisely because "a plugin author outside this repository
# has no other way to get it" - but nothing publishes it yet. So we clone and pack.
#
# NoMercy.Events must be packed too: it is a ProjectReference of the abstractions,
# so packing only the abstractions yields a package whose dependency cannot resolve.
# So must NoMercy.Design, which the abstractions picked up so a plugin can name every
# design component rather than the handful it had tags for.
#
# Every ProjectReference of NoMercy.Plugins.Abstractions has to be both in the
# sparse-checkout list AND packed into the feed. Miss the checkout and the compile fails
# on the types it cannot see; miss the pack and the plugin's own restore fails on a
# dependency the feed does not have.
#
# This is the PowerShell twin of fetch-abstractions.sh. They drifted once - this one was
# still packing two projects when the shell script had moved to four - and the drift was
# only found when a Windows run failed on a project the other script already knew about.
# Change one, change the other.

$ErrorActionPreference = 'Stop'

# The version decides which SDK to use, not the location: a side-by-side install without
# a 10.x SDK fails global.json resolution before it packs anything.
$dotnet = $null
foreach ($candidate in @((Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'), 'dotnet')) {
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^10\.' })) { $dotnet = $candidate; break }
}
if (-not $dotnet) { throw 'no dotnet SDK on this machine can build net10.0' }

$root   = Split-Path -Parent $PSScriptRoot
# The server checkout lives beside this repo, not inside it: it is a full clone of another
# project and a sibling is where a developer expects to find one. SERVER_DIR overrides it,
# which is how CI keeps the clone inside its own disposable workspace.
$server = if ($env:SERVER_DIR) { $env:SERVER_DIR } else { Join-Path (Split-Path -Parent $root) 'nomercy-media-server' }
$feed   = Join-Path $root '_nupkgs'
$branch = if ($env:SERVER_BRANCH) { $env:SERVER_BRANCH } else { 'dev' }
# A release must be rebuildable. SERVER_REF pins the contract to one commit; it defaults
# to a branch for day-to-day work, but CI sets it to a SHA for a tag build so the artifact
# is reproducible instead of "whatever dev happened to be".
$ref    = if ($env:SERVER_REF) { $env:SERVER_REF } else { $branch }

if (-not (Test-Path $server)) {
    git clone --depth=1 --branch=$branch --filter=blob:none --no-checkout `
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git $server
    git -C $server sparse-checkout init --cone
}

# Applied on every run, not only on the initial clone. Setting it once meant adding a
# project to the list silently did nothing on a checkout that already existed.
git -C $server sparse-checkout set `
    src/NoMercy.Plugins.Abstractions src/NoMercy.Events src/NoMercy.Design src/NoMercy.Plugins.Mvc

git -C $server fetch --depth=1 origin $ref
git -C $server reset --hard FETCH_HEAD

New-Item -ItemType Directory -Force $feed | Out-Null

$abstractions = Join-Path $server 'src\NoMercy.Plugins.Abstractions\NoMercy.Plugins.Abstractions.csproj'
if (-not (Test-Path $abstractions)) { throw "NoMercy.Plugins.Abstractions is not present at $ref - nothing to build against" }

# Dependency order, and each one only if the ref actually has it. SERVER_REF pins this
# script to any commit, so it has to keep working on a ref from before a project existed:
# NoMercy.Design is not in the tree at all at the commit v0.1.0 was built from.
#
# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
foreach ($project in @('NoMercy.Events', 'NoMercy.Design', 'NoMercy.Plugins.Abstractions', 'NoMercy.Plugins.Mvc')) {
    $csproj = Join-Path $server "src\$project\$project.csproj"
    if (-not (Test-Path $csproj)) { continue }
    & $dotnet pack $csproj -c Release -o $feed
    if ($LASTEXITCODE -ne 0) { throw "packing $project failed" }
}

Get-ChildItem $feed -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host "contract packed from nomercy-media-server $(git -C $server rev-parse HEAD)"
