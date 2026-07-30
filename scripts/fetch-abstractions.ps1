#!/usr/bin/env pwsh
# Packs NoMercy.Plugins.Abstractions into a local NuGet feed.
#
# The contract is not published to nuget.org. Its csproj is IsPackable and carries
# full package metadata precisely because "a plugin author outside this repository
# has no other way to get it" - but nothing publishes it yet. So we clone and pack.
#
# NoMercy.Events must be packed too: it is a ProjectReference of the abstractions,
# so packing only the abstractions yields a package whose dependency cannot resolve.

$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$root   = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root '_server'
$feed   = Join-Path $root '_nupkgs'
$branch = if ($env:SERVER_BRANCH) { $env:SERVER_BRANCH } else { 'dev' }

if (-not (Test-Path $server)) {
    git clone --depth=1 --branch=$branch --filter=blob:none --no-checkout `
        https://github.com/NoMercy-Entertainment/nomercy-media-server.git $server
    git -C $server sparse-checkout init --cone
    git -C $server sparse-checkout set src/NoMercy.Plugins.Abstractions src/NoMercy.Events
    git -C $server checkout $branch
} else {
    git -C $server fetch --depth=1 origin $branch
    git -C $server reset --hard FETCH_HEAD
}

New-Item -ItemType Directory -Force $feed | Out-Null

# MSB9008 about a missing NoMercy.Analyzers is expected under a sparse checkout.
# It is an analyzer reference; the package builds correctly without it.
& $dotnet pack (Join-Path $server 'src\NoMercy.Events\NoMercy.Events.csproj') -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw 'packing NoMercy.Events failed' }

& $dotnet pack (Join-Path $server 'src\NoMercy.Plugins.Abstractions\NoMercy.Plugins.Abstractions.csproj') -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw 'packing NoMercy.Plugins.Abstractions failed' }

Get-ChildItem $feed -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
