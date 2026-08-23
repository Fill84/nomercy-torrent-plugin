#Requires -Version 7
<#
.SYNOPSIS
    Packs NoMercy.Plugins.Abstractions and NoMercy.Plugins.Mvc out of the media
    server into _nupkgs/, so this repository can build against the contract.

.DESCRIPTION
    Neither package is published anywhere, and the alternative — copying a DLL
    out of somebody's build — is how a plugin ends up compiled against a
    contract nobody can name. So the media server is cloned (shallow, sparse,
    branch master) into _server/ and the two projects are packed locally.

    Only five projects are checked out: the two packable ones, the two they
    reference, and the analyzer every project in that repository inherits.

    The packed version is the media server's own. When it has not moved since
    the last run, NuGet keeps serving the copy already in the global cache
    however new the .nupkg is, and nothing says so — the build simply goes on
    compiling against yesterday's contract. So the cache entry is deleted every
    time.
#>
[CmdletBinding()]
param(
    # Branch of the media server to pack from. master, always, unless somebody
    # is testing against something else.
    #
    # Never dev. Its Directory.Build.props carries a fixed <Version>0.1.404</Version>
    # that has not moved since July and never will, so packing from it produces
    # a package NuGet believes it already has however much the contract changed.
    # This repository sat on 0.1.404 while the server shipped 0.1.478, and every
    # contract added in between was invisible here — the table action cell among
    # them, which is why the Downloads page still drew its buttons in a second
    # list under the table.
    [string] $Branch = 'master',

    # Throw away _server/ and clone again.
    [switch] $Fresh
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $repositoryRoot '_server'
$packagePath = Join-Path $repositoryRoot '_nupkgs'
$remote = 'https://github.com/NoMercy-Entertainment/nomercy-media-server.git'

# The .NET 10 SDK is user-local on the machines this runs on; the dotnet on PATH
# is 8.0 and cannot build any of this.
$dotnet = if (Test-Path "$HOME/.dotnet/dotnet.exe") { "$HOME/.dotnet/dotnet.exe" } else { 'dotnet' }

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string] $Executable,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($Arguments -join ' ') exited with $LASTEXITCODE"
    }
}

function Get-MsBuildProperty {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Name
    )

    $node = ([xml](Get-Content -Raw $Path)).SelectSingleNode("/Project/PropertyGroup/$Name")
    if ($null -eq $node) {
        return $null
    }
    return $node.InnerText.Trim()
}

$sparsePaths = @(
    'src/NoMercy.Plugins.Abstractions'
    'src/NoMercy.Plugins.Mvc'
    'src/NoMercy.Events'
    'src/NoMercy.Design'
    'src/NoMercy.Analyzers'
)

if ($Fresh -and (Test-Path $serverPath)) {
    Remove-Item -Recurse -Force $serverPath
}

if (Test-Path (Join-Path $serverPath '.git')) {
    Write-Host "Updating _server to origin/$Branch ..."
    # With the refspec spelled out. A single-branch clone tracks only the branch
    # it was made from, so fetching another by name lands in FETCH_HEAD and
    # leaves origin/<branch> missing — and the checkout below then fails saying
    # it is not a commit.
    Invoke-Checked 'git' @(
        '-C', $serverPath, 'fetch', '--depth', '1', 'origin',
        "+refs/heads/$Branch`:refs/remotes/origin/$Branch")
    Invoke-Checked 'git' (@('-C', $serverPath, 'sparse-checkout', 'set') + $sparsePaths)
    Invoke-Checked 'git' @('-C', $serverPath, 'checkout', '-B', $Branch, "origin/$Branch")
}
else {
    Write-Host "Cloning the media server ($Branch) into _server ..."
    # blob:none rather than a plain shallow clone: the sparse checkout then only
    # ever downloads the blobs for the five projects named above.
    Invoke-Checked 'git' @('clone', '--filter=blob:none', '--no-checkout', '--depth', '1', '--branch', $Branch, $remote, $serverPath)
    Invoke-Checked 'git' @('-C', $serverPath, 'sparse-checkout', 'init', '--cone')
    Invoke-Checked 'git' (@('-C', $serverPath, 'sparse-checkout', 'set') + $sparsePaths)
    Invoke-Checked 'git' @('-C', $serverPath, 'checkout', $Branch)
}

$serverProps = Join-Path $serverPath 'Directory.Build.props'
$version = Get-MsBuildProperty -Path $serverProps -Name 'Version'
if (-not $version) {
    throw "No <Version> in $serverProps. The media server changed how it versions itself."
}

Write-Host "The media server on $Branch is version $version."

$declared = Get-MsBuildProperty -Path (Join-Path $repositoryRoot 'Directory.Build.props') -Name 'NoMercyContractVersion'
if ($declared -ne $version) {
    Write-Warning "Directory.Build.props asks for $declared. Set NoMercyContractVersion to $version, or the build restores nothing."
}

New-Item -ItemType Directory -Force -Path $packagePath | Out-Null

# All four, because the two packable ones declare the other two as package
# dependencies rather than carrying their types. Packing only the first two
# left a restore that could resolve the contract and nothing it referenced —
# it worked for as long as the global cache still had yesterday's copies, and
# stopped the moment the version moved.
foreach ($project in @(
    'NoMercy.Plugins.Abstractions',
    'NoMercy.Plugins.Mvc',
    'NoMercy.Design',
    'NoMercy.Events')) {
    # The cache entry goes first. Restore prefers an already-extracted folder of
    # the same version over the file in _nupkgs, however new that file is.
    $cached = Join-Path $HOME ".nuget/packages/$($project.ToLowerInvariant())/$version"
    if (Test-Path $cached) {
        Write-Host "Clearing cached $project $version ..."
        Remove-Item -Recurse -Force $cached
    }

    Write-Host "Packing $project ..."
    Invoke-Checked $dotnet @(
        'pack'
        (Join-Path $serverPath "src/$project/$project.csproj")
        '-c', 'Release'
        '-o', $packagePath
    )
}

Write-Host "Packed $version into $packagePath."
