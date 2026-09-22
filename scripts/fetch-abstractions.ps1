#Requires -Version 7
<#
.SYNOPSIS
    Packs NoMercy.PluginSdk.Abstractions and NoMercy.PluginSdk.Mvc out of the
    media server into _nupkgs/, so this repository can build against the
    contract.

.DESCRIPTION
    The packages reach nuget.org only with a server release, and none carrying
    the renamed contract has shipped yet; the alternative — copying a DLL out
    of somebody's build — is how a plugin ends up compiled against a contract
    nobody can name. So the media server is cloned (shallow, sparse, branch
    dev) into _server/ and the two projects are packed locally.

    Only five projects are checked out: the two packable ones, the two whose
    assemblies travel inside the first of them, and the analyzer every project
    in that repository inherits.

    The packed version is the contract's own, PluginPackageVersion. When it has
    not moved since the last run, NuGet keeps serving the copy already in the
    global cache however new the .nupkg is, and nothing says so — the build
    simply goes on compiling against yesterday's contract. So the cache entry
    is deleted every time.
#>
[CmdletBinding()]
param(
    # Branch of the media server to pack from.
    #
    # dev, since 22 September 2026. This script refused dev for as long as the
    # contract was versioned with the server: dev's <Version> is frozen at
    # 0.1.404, so every pack from it produced a package NuGet believed it
    # already had. Since the rename to NoMercy.PluginSdk the contract carries
    # its own <PluginPackageVersion>, 12.0.0 at the time of writing, and that
    # objection is gone. master is where the contract is not: it sits on a
    # release from August carrying ABI 11, while the servers the owner and
    # Stoney run come from dev and refuse anything under 12.
    [string] $Branch = 'dev',

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
    'src/NoMercy.PluginSdk.Abstractions'
    'src/NoMercy.PluginSdk.Mvc'
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

# The contract's own version, not the server's: the major is the ABI a server
# refuses or accepts by, the minor moves when the contract gains a member, and
# it is the number NuGet caches the package under below.
$version = Get-MsBuildProperty -Path $serverProps -Name 'PluginPackageVersion'
if (-not $version) {
    throw "No <PluginPackageVersion> in $serverProps. The media server changed how it versions the plugin contract."
}

Write-Host "The plugin contract on $Branch is version $version (server $(Get-MsBuildProperty -Path $serverProps -Name 'Version'))."

# NoMercyContractVersion pins the major and floats the minor, so the build takes
# whatever 12.x is packed below. The line above is what a build compiled
# against, and it is the only record of it that a floating minor leaves.

New-Item -ItemType Directory -Force -Path $packagePath | Out-Null

# Only the two. NoMercy.Events and NoMercy.Design used to be packages of their
# own that the contract depended on, and this loop packed all four; since the
# rename they ship as assemblies inside NoMercy.PluginSdk.Abstractions, are
# IsPackable=false, and packing them produces nothing a restore needs.
foreach ($project in @(
    'NoMercy.PluginSdk.Abstractions',
    'NoMercy.PluginSdk.Mvc')) {
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
