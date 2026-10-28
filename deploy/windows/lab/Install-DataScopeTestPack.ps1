#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Lab only: copies the Data Scope detection test pack to a folder on this file server and shares it,
    read-only, to the scan account, so a scan can be checked with Test-DataScopeTestPack.ps1 (D138).

.DESCRIPTION
    Every file in the pack is synthetic: made-up names, test card numbers and SSNs in the test range. The pack's files
    are copied from the "files" folder next to this script. A few cases can't travel in a zip, so they are made here,
    under 7-Lab-only:
      - offline-file.txt has the Offline attribute, as files moved to archive storage do: it must not be read.
      - Denied\ is a folder the scan account is refused: it must be reported as a gap.
      - loop is a junction pointing back up the tree: it must be reported and not followed.
      - long-path\...\ssn.txt has a full path over 260 characters.
    Run it again with -Force to replace the folder with a fresh copy. Nothing outside -Path is changed, except the
    share named -ShareName.

.EXAMPLE
    .\Install-DataScopeTestPack.ps1 -ScanAccount 'YOURDOMAIN\svc-datascope$'
#>
[CmdletBinding()]
param(
    # The account Data Scope scans as, as given to Install-DataScope.ps1 -ScanAccount.
    [Parameter(Mandatory)][string]$ScanAccount,
    [string]$Path = 'C:\Shares\TestPack',
    [string]$ShareName = 'TestPack',
    # Replace an earlier copy of the pack.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'files'
if (-not (Test-Path (Join-Path $source '1-Each-kind-of-data'))) { throw "The pack's files were not found in $source. Unzip the whole pack and run this script from it." }

# DOMAIN\name for an account that exists, else $null.
function Find-Account([string]$Name) {
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($Name)).Translate([System.Security.Principal.SecurityIdentifier])
        return $sid.Translate([System.Security.Principal.NTAccount]).Value
    } catch { return $null }
}
# Checked before anything is copied. A gMSA's name ends with $, which is easy to leave off.
$account = Find-Account $ScanAccount
if (-not $account -and -not $ScanAccount.EndsWith('$')) { $account = Find-Account "$ScanAccount`$" }
if (-not $account) {
    throw "There is no account called $ScanAccount. Give it as DOMAIN\name, with a `$ on the end for a gMSA, in single quotes: -ScanAccount 'YOURDOMAIN\svc-datascope`$'"
}
if ($account -ne $ScanAccount) { Write-Host "Scan account: $account" }
$ScanAccount = $account
if (Test-Path $Path) {
    if (-not (Test-Path (Join-Path $Path '1-Each-kind-of-data'))) { throw "$Path exists and does not hold the test pack. Choose another -Path." }
    if (-not $Force) { throw "$Path already holds the test pack. Add -Force to replace it with a fresh copy." }
    # Undo what an earlier run set up, so the folder can be removed.
    $denied = Join-Path $Path '7-Lab-only\Denied'
    if (Test-Path $denied) { & icacls.exe $denied /reset /T /Q | Out-Null }
    $loop = Join-Path $Path '7-Lab-only\loop'
    if (Test-Path $loop) { cmd /c rmdir $loop | Out-Null }
    # Long paths: remove with the \\?\ prefix, which has no 260-character limit.
    cmd /c rmdir /s /q "\\?\$Path" | Out-Null
}

New-Item -ItemType Directory -Path $Path -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $Path -Recurse -Force
$count = @(Get-ChildItem $Path -Recurse -File).Count
Write-Host "Copied $count files to $Path"

$lab = Join-Path $Path '7-Lab-only'
New-Item -ItemType Directory -Path $lab -Force | Out-Null

$offline = Join-Path $lab 'offline-file.txt'
"Archived payroll note`r`nSocial Security Number: 512-48-3395" | Set-Content -Path $offline -Encoding UTF8
(Get-Item $offline).Attributes = 'Archive, Offline'

$denied = Join-Path $lab 'Denied'
New-Item -ItemType Directory -Path $denied -Force | Out-Null
"Social Security Number: 512-48-3396" | Set-Content -Path (Join-Path $denied 'secret.txt') -Encoding UTF8

cmd /c mklink /J (Join-Path $lab 'loop') $Path | Out-Null

$deep = Join-Path $lab 'long-path'
foreach ($i in 1..6) { $deep = Join-Path $deep ('folder-with-a-long-name-to-pass-260-characters-' + $i) }
New-Item -ItemType Directory -Path "\\?\$deep" -Force | Out-Null
[IO.File]::WriteAllText("\\?\$deep\ssn.txt", "Social Security Number: 512-48-3397`r`n")
Write-Host "Made the lab-only cases in $lab (a path of $(("$deep\ssn.txt").Length) characters)"

# Read-only for the scan account, at the share and on the files.
& icacls.exe $Path /grant "${ScanAccount}:(OI)(CI)RX" /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not give $ScanAccount read access to $Path. Check the account name." }
& icacls.exe $denied /deny "${ScanAccount}:(OI)(CI)RX" /Q | Out-Null

$existing = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
if ($existing -and $existing.Path -ne $Path) { throw "A share named $ShareName already points to $($existing.Path). Choose another -ShareName." }
if (-not $existing) {
    New-SmbShare -Name $ShareName -Path $Path -ReadAccess $ScanAccount -FullAccess 'BUILTIN\Administrators' | Out-Null
}
Write-Host "Shared as \\$env:COMPUTERNAME\$ShareName, read-only for $ScanAccount"
Write-Host ''
Write-Host 'Next: scan this server in Data Scope, then check the results:'
Write-Host "  .\Test-DataScopeTestPack.ps1 -Url https://<your Data Scope server> -Server $($env:COMPUTERNAME.ToLowerInvariant()) -Share $ShareName"
