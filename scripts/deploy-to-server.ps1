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

# Which platform's native code that machine needs. Asked rather than assumed,
# because this script is also how a Linux server gets its copy.
$uname = (ssh -o BatchMode=yes $Server 'uname -sm').Trim()
$rid = switch -Regex ($uname) {
    '^(MINGW|MSYS|CYGWIN).*(x86_64|amd64)' { 'win-x64'; break }
    '^(MINGW|MSYS|CYGWIN).*(aarch64|arm64)' { 'win-arm64'; break }
    '^Linux.*(x86_64|amd64)' { 'linux-x64'; break }
    '^Linux.*(aarch64|arm64)' { 'linux-arm64'; break }
    '^Darwin.*(arm64)' { 'osx-arm64'; break }
    '^Darwin.*(x86_64)' { 'osx-x64'; break }
    default { throw "cannot tell what platform $Server is from '$uname'" }
}
Write-Host "$Server is $rid"

# Every file the build produced, worked out here rather than written down.
#
# A hand-kept list drifted three times. It missed the protocol assembly, so a
# deploy shipped an entry assembly referencing a dll that was not there. It
# missed sources.json, so the plugin read no catalogue at all on a fresh
# install. And it named six files while the manifest named twelve assemblies,
# which is how 0.4.0 reached a server unable to load: the host resolves a
# plugin's dependencies from beside the plugin, found none of them, and
# PluginLoader's ReflectionTypeLoadException path reports the fault and returns
# without registering anything - so the plugin is absent from the server's list
# with nothing at all to say why.
#
# Symbols and documentation stay behind: nothing at runtime reads them.
$carry = Get-ChildItem -File $out |
    Where-Object { $_.Extension -notin '.pdb', '.xml' } |
    ForEach-Object { $_.Name }

# Native code ships for the one machine being deployed to. The package carries
# SQLite built for twenty platforms and all of them together are 33MB of the
# 41MB output, every byte of it travelling base64 through ssh. The resolver
# only ever looks under the running platform's own identifier.
$nativeDir = Join-Path $out "runtimes\$rid\native"
if (Test-Path $nativeDir) {
    $carry += Get-ChildItem -File $nativeDir | ForEach-Object { "runtimes/$rid/native/$($_.Name)" }
} else {
    Write-Host "  note: no native code for $rid - nothing under runtimes\$rid\native"
}

# One call rather than one per file, and before anything is copied: a redirect
# into a directory that is not there fails per file saying only "No such file
# or directory".
$dirs = $carry | ForEach-Object { Split-Path -Parent $_ } | Where-Object { $_ } |
    ForEach-Object { ($_ -replace '\\', '/') } | Sort-Object -Unique
foreach ($dir in $dirs) {
    ssh -o BatchMode=yes $Server "mkdir -p '$remoteDir/$dir'"
    if ($LASTEXITCODE -ne 0) { throw "cannot create $remoteDir/$dir on $Server" }
}

Write-Host "deploying $($carry.Count) files to $Server…"

foreach ($file in $carry) {
    $path = Join-Path $out ($file -replace '/', '\')

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
