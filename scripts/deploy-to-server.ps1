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

# The manifest travels with the assembly on purpose: the two carry the version
# independently, and updating one without the other leaves every server
# reporting a version it is not running.
$files = @(
    "$project.dll",
    "$project.Core.dll",

    # The protocol assembly. It arrived with 0.4.0 and this list did not
    # grow with it, so a deploy shipped an entry assembly referencing a dll
    # that was not there and the plugin vanished from the server's list
    # altogether — no error, no entry, nothing to tell the owner why.
    "$project.Bittorrent.dll",
    "$project.deps.json",
    'plugin.json',

    # The catalogue, and it is not optional. C1: it is read from the assembly's
    # own folder, so a deploy that ships every assembly and not this leaves the
    # plugin reading yesterday's sources - or none at all on a fresh install -
    # while looking perfectly healthy and asking nobody anything.
    'sources.json'
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

# Asked before anything is copied rather than worked out from the wreckage
# afterwards. A loaded plugin's assembly is held open, so the copy fails, the
# old build stays, and the hash check at the end is left to explain it one file
# at a time. Refusing up front says the one thing the owner has to do.
$running = ssh -o BatchMode=yes $Server 'tasklist 2>/dev/null | grep -i "NoMercy" || pgrep -fl "NoMercy" 2>/dev/null || true'
if ($LASTEXITCODE -ne 0) { throw "cannot reach $Server over ssh" }

if ($running -and ($running -join '') -match 'NoMercy') {
    throw @"
The server on $Server is still running, so this would copy nothing and say it worked.

  $($running -join "`n  ")

Stop it, run this again, and start it afterwards.
"@
}

# Asked for rather than built here: it is that machine's profile. Through
# cygpath because $LOCALAPPDATA is a Windows path with backslashes, and a
# redirect into one of those fails with "No such file or directory" - which
# reads exactly like the folder being missing, and sends whoever is deploying
# looking for the wrong fault.
$remoteDir = (ssh -o BatchMode=yes $Server 'cygpath -u "$LOCALAPPDATA"').Trim() + "/NoMercy/plugins/$project"
if (-not $remoteDir.StartsWith('/')) { throw "cannot work out where plugins live on $Server" }

# A server that has never had this plugin has no folder for it, and every copy
# below fails one at a time saying nothing about why. Nobody deploying a first
# install should have to make the directory by hand.
ssh -o BatchMode=yes $Server "mkdir -p '$remoteDir'"
if ($LASTEXITCODE -ne 0) { throw "cannot create $remoteDir on $Server" }

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

    # tr first, because PowerShell terminates a pipe into a native command with
    # CRLF and GNU base64 rejects the CR outright - "base64: invalid input",
    # with an empty hash that reads exactly like a file the server still holds
    # open. The bash script redirects a file into ssh instead and never grows
    # the extra byte; stripping here means one remote command serves both.
    $remote = ssh -o BatchMode=yes $Server "tr -d '\r' < /tmp/nm-deploy.b64 | base64 -d > `"$remoteDir/$file`" && md5sum `"$remoteDir/$file`""
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
