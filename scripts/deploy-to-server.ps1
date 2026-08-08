# SPDX-License-Identifier: MIT
#
# Copies a built plugin onto a running NoMercy server over ssh.
#
#   scripts\deploy-to-server.ps1
#   scripts\deploy-to-server.ps1 -Build
#   scripts\deploy-to-server.ps1 -Server other-host
#
# THE SERVER MUST BE STOPPED FIRST. A loaded plugin's assembly is held open by
# the host, so the copy fails and the old build stays in place - which looks
# exactly like a deploy that worked and changed nothing. Every file's hash is
# compared afterwards, because that is the only way to tell those two apart.
#
# Files travel as base64 through ssh rather than with scp: scp against this host
# fails where a plain ssh session works.
[CmdletBinding()]
param(
    [string] $Server = $(if ($env:SERVER) { $env:SERVER } else { 'beast-unit' }),
    [string] $Configuration = 'Release',
    [string] $Framework = 'net10.0',
    [switch] $Build
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = 'NoMercy.Plugin.TorrentDownloader'
$out = Join-Path $root "src\$project\bin\$Configuration\$Framework"

# Expanded on the far side, not here: it is that machine's profile.
$remoteDir = "`$LOCALAPPDATA/NoMercy/plugins/$project"

# The manifest travels with the assembly on purpose: the two carry the version
# independently, and updating one without the other leaves every server
# reporting a version it is not running.
$files = @(
    "$project.dll",
    "$project.Core.dll",
    "$project.deps.json",
    'plugin.json'
)

if ($Build) {
    $dotnet = 'dotnet'
    $candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }

    Write-Host "building $Configuration…"
    & $dotnet build (Join-Path $root "src\$project\$project.csproj") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

if (-not (Test-Path $out)) { throw "nothing built at $out - run with -Build" }

Write-Host "deploying to $Server…"

foreach ($file in $files) {
    $path = Join-Path $out $file
    if (-not (Test-Path $path)) { Write-Host "  skip $file (not built)"; continue }

    $localSum = (Get-FileHash $path -Algorithm MD5).Hash.ToLowerInvariant()
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    $temp = Join-Path ([IO.Path]::GetTempPath()) 'nm-deploy.b64'
    [IO.File]::WriteAllText($temp, $b64)

    Get-Content -Raw $temp | ssh -o BatchMode=yes $Server 'cat > /tmp/nm-deploy.b64'
    Remove-Item $temp -Force

    $remote = ssh -o BatchMode=yes $Server "base64 -d /tmp/nm-deploy.b64 > `"$remoteDir/$file`" && md5sum `"$remoteDir/$file`""
    $remoteSum = ($remote -join '') -replace '[\\*]', '' -split ' ' | Select-Object -First 1

    # The hashes are the whole point. A busy file leaves the old bytes in place
    # and the copy still looks like it succeeded, so comparing is the only way
    # to know a deploy actually happened.
    if ($localSum -ne $remoteSum) {
        Write-Error "FAILED $file`n  local  $localSum`n  remote $remoteSum`n  Is the server still running? A loaded plugin's dll cannot be replaced."
    }

    Write-Host "  ok $file  $localSum"
}

Write-Host ''
Write-Host 'done - start the server again.'
