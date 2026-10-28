#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates an installed Data Scope to a newer package in one step.

.DESCRIPTION
    For labs and pilots. Gets a package, checks it, unzips it and runs its Install-DataScope.ps1, which upgrades in
    place: it stops the services, backs up the database, replaces the programs, updates the database and starts the
    services again. Settings, scan results and the audit log are kept.

    Where the package comes from:
      * -Package: a DataScope-<version>.zip you already have (with its .sha256 file next to it).
      * Otherwise the newest green CI build of -Branch (main by default) on GitHub, downloaded with the GitHub CLI
        (gh) as you. Install it once with "winget install --id GitHub.cli" and sign in with "gh auth login".

    Only the zip is downloaded, and only Install-DataScope.ps1 from inside it is run. Nothing else is sent or
    fetched. A first install is done with Install-DataScope.ps1 from the unzipped package (INSTALL.md).

.EXAMPLE
    .\Update-DataScope.ps1                      # newest green build of main

.EXAMPLE
    .\Update-DataScope.ps1 -Branch risks        # try a pull request's build before it is merged

.EXAMPLE
    .\Update-DataScope.ps1 -Package C:\Install\DataScope-0.2.0-lab.52.zip
#>
[CmdletBinding()]
param(
    # Branch whose newest green CI build to install.
    [ValidatePattern('^[A-Za-z0-9._/-]{1,200}$')][string]$Branch = 'main',
    # A package zip to install instead of downloading one.
    [string]$Package,
    # GitHub repository the CI builds come from.
    [ValidatePattern('^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$')][string]$Repository = 'OpenSourceCybersecurity/OS-DataScope',
    # Where packages are unzipped. The newest three are kept.
    [string]$UpdatesPath = (Join-Path $env:ProgramData 'DataScope\updates'),
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Data Scope'),
    # Install even when that version is already installed.
    [switch]$Force,
    # Refuse a package that is not code-signed (passed to Install-DataScope.ps1).
    [switch]$RequireSignature,
    # Download and check the package, show what would be installed, and change nothing.
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string]$Message) { Write-Host "    $Message" }

function Get-InstalledVersion {
    # The managed assembly carries the version on every build machine; the .exe launcher only when built on Windows.
    $exe = Join-Path $InstallPath 'api\DataScope.Api.dll'
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    $v = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if (-not $v) { return $null }
    # Builds add "+<commit>" to the version; the package name doesn't have it.
    return ($v -split '\+')[0]
}

function Test-Installed {
    return [bool](Get-Service -Name 'DataScopeApi' -ErrorAction SilentlyContinue)
}

function Get-FromGitHub([string]$Into, [string]$FromBranch, [string]$FromRepository) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        throw "The GitHub CLI (gh) isn't installed. Install it with 'winget install --id GitHub.cli', open a new PowerShell window, run 'gh auth login', then run this again. Or download the package yourself and pass -Package."
    }
    & $gh.Source auth status --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) { throw "The GitHub CLI isn't signed in. Run 'gh auth login', then run this again." }

    Write-Step "Finding the newest green build of $FromBranch in $FromRepository"
    $json = & $gh.Source run list --repo $FromRepository --branch $FromBranch --workflow ci.yml --status success --limit 1 --json 'databaseId,headSha,createdAt,displayTitle,url'
    if ($LASTEXITCODE -ne 0) { throw "Couldn't list CI runs for $FromRepository. Check that your GitHub account can see it." }
    $run = @($json | ConvertFrom-Json) | Select-Object -First 1
    if (-not $run) { throw "No green CI build of '$FromBranch' was found. Check the branch name, or wait for its CI to finish." }
    Write-Note "$($run.displayTitle) ($($run.headSha.Substring(0, 7)), $(([datetime]$run.createdAt).ToLocalTime().ToString('yyyy-MM-dd HH:mm')))"
    Write-Note $run.url

    Write-Step 'Downloading the package'
    & $gh.Source run download $run.databaseId --repo $FromRepository --name DataScope-windows-package --dir $Into
    if ($LASTEXITCODE -ne 0) { throw "Couldn't download the package from that build. Packages are kept for 30 days; a newer push to $FromBranch makes a new one." }
    $zip = Get-ChildItem -LiteralPath $Into -Filter 'DataScope-*.zip' | Select-Object -First 1
    if (-not $zip) { throw 'The build had no package zip.' }
    return $zip.FullName
}

function Test-Checksum([string]$Zip) {
    $sumFile = "$Zip.sha256"
    if (-not (Test-Path -LiteralPath $sumFile)) {
        throw "$sumFile is missing. Keep the .sha256 file that came with the zip next to it."
    }
    $expected = ((Get-Content -LiteralPath $sumFile -Raw).Trim() -split '\s+')[0]
    $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { throw "The zip doesn't match its checksum. Download it again." }
    Write-Note "Checksum matches ($($actual.Substring(0, 12))...)."
}

if (-not (Test-Installed)) {
    throw "Data Scope isn't installed on this server. For a first install, unzip the package and run Install-DataScope.ps1, which asks for what it needs (see INSTALL.md)."
}

New-Item -ItemType Directory -Force -Path $UpdatesPath | Out-Null
$download = $null
$returnTo = $null
if ($Package) {
    $zipPath = (Resolve-Path -LiteralPath $Package).Path
} else {
    $download = Join-Path $UpdatesPath ("download-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $download | Out-Null
    $zipPath = Get-FromGitHub $download $Branch $Repository
}

try {
    $name = [IO.Path]::GetFileNameWithoutExtension($zipPath)
    if ($name -notmatch '^DataScope-(?<version>[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?)$') {
        throw "$([IO.Path]::GetFileName($zipPath)) isn't a Data Scope package (expected DataScope-<version>.zip)."
    }
    $version = $Matches['version']
    $installed = Get-InstalledVersion
    Write-Step 'Checking the package'
    Test-Checksum $zipPath
    Write-Note "Installed: $(if ($installed) { $installed } else { 'unknown' }). Package: $version."
    if ($installed -eq $version -and -not $Force) {
        Write-Host "Data Scope $version is already installed. Use -Force to install it again."
        return
    }

    $target = Join-Path $UpdatesPath $name
    # Run from inside the folder it replaces (as you do after an update), PowerShell refuses to delete it. Step out
    # while it is replaced and back in at the end: the folder has the same path again by then.
    $here = (Get-Location).ProviderPath
    if ("$here\".StartsWith("$target\", [StringComparison]::OrdinalIgnoreCase)) {
        $returnTo = $here
        Set-Location -LiteralPath $UpdatesPath
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Write-Step "Unzipping to $target"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $target
    $folder = Join-Path $target $name
    $installer = Join-Path $folder 'Install-DataScope.ps1'
    if (-not (Test-Path -LiteralPath $installer)) { throw "The zip has no $name\Install-DataScope.ps1." }
    $packaged = (Get-Content -LiteralPath (Join-Path $folder 'VERSION') -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($packaged -ne $version) { throw "The zip is named $version but holds version '$packaged'." }
    # Files from the internet are marked as such; the package's checksum and (when signed) signature are checked by the installer.
    Get-ChildItem -LiteralPath $folder -Recurse -File | Unblock-File

    if ($CheckOnly) {
        Write-Host "Ready to update to $version. Run again without -CheckOnly to install it."
        return
    }

    Write-Step "Upgrading Data Scope to $version"
    $arguments = @{}
    if ($InstallPath -ne (Join-Path $env:ProgramFiles 'Data Scope')) { $arguments['InstallPath'] = $InstallPath }
    if ($RequireSignature) { $arguments['RequireSignature'] = $true }
    & $installer @arguments

    # Keep the newest three unzipped packages for a quick look back; older ones go.
    Get-ChildItem -LiteralPath $UpdatesPath -Directory -Filter 'DataScope-*' |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 3 |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
} finally {
    if ($download -and (Test-Path -LiteralPath $download)) { Remove-Item -LiteralPath $download -Recurse -Force }
    if ($returnTo -and (Test-Path -LiteralPath $returnTo)) { Set-Location -LiteralPath $returnTo }
}
