# Copies this plugin into the server's own plugin folder.
#
# Stop the server first. A loaded plugin's assembly is held open, so the copy
# fails and the old build stays behind — which looks exactly like a deploy that
# worked and changed nothing. Every file's hash is verified afterwards for that
# reason: this script would rather say it failed than let a stale build be
# mistaken for a new one.
#
# The folder is the one docs/01-plugin.md names:
#   %LOCALAPPDATA%\NoMercy\plugins\NoMercy.Plugin.TorrentDownloader\

[CmdletBinding()]
param(
    # Build first. Without it, whatever was last published is what goes.
    [switch]$Build,

    # Where the server keeps its plugins, if it is not the documented place.
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'NoMercy\plugins\NoMercy.Plugin.TorrentDownloader')
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\NoMercy.Plugin.TorrentDownloader'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("nomercy-deploy-" + [guid]::NewGuid().ToString('n').Substring(0, 8))

if ($Build) {
    Write-Host "Publishing $project"

    & dotnet publish $project -c Release -o $staging --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "The build failed. Nothing was copied."
    }
}
else {
    $published = Join-Path $project 'bin\Release\net10.0\publish'

    if (-not (Test-Path $published)) {
        throw "There is nothing published at $published. Run with -Build."
    }

    $staging = $published
}

# The plugin's own assemblies and the two files it reads at startup. Nothing
# else: the server brings its own abstractions, and shipping a second copy of
# them is how a plugin ends up loading types the host will not accept.
$wanted = @('NoMercy.Plugin.TorrentDownloader*.dll', 'plugin.json', 'sources.json')
$files = $wanted | ForEach-Object { Get-ChildItem -Path $staging -Filter $_ -File -ErrorAction SilentlyContinue }

if (-not $files) {
    throw "Nothing to deploy was found in $staging."
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$copied = @()
$failed = @()

foreach ($file in $files) {
    $target = Join-Path $Destination $file.Name

    try {
        Copy-Item -Path $file.FullName -Destination $target -Force

        $before = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash
        $after = (Get-FileHash -Path $target -Algorithm SHA256).Hash

        if ($before -ne $after) {
            $failed += "$($file.Name): copied, but the hash does not match"
        }
        else {
            $copied += $file.Name
        }
    }
    catch {
        # Almost always the server still running and holding the assembly open.
        $failed += "$($file.Name): $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Deployed to $Destination"

foreach ($name in $copied) {
    Write-Host "  ok   $name"
}

foreach ($problem in $failed) {
    Write-Host "  FAIL $problem"
}

if ($failed.Count -gt 0) {
    throw "$($failed.Count) file(s) did not deploy. Is the server still running?"
}

Write-Host ""
Write-Host "$($copied.Count) files verified. Start the server."
