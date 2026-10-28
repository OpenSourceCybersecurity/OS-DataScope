#Requires -RunAsAdministrator
<#
.SYNOPSIS
    CI only: turns a throwaway Windows runner into a tiny lab for the install test.
    Starts the preinstalled PostgreSQL, creates a local admin group, a local scan
    account and a share of synthetic files. Never run this on a real server.
#>
param([Parameter(Mandatory)][string]$ScanPassword, [Parameter(Mandatory)][string]$UserPassword)
$ErrorActionPreference = 'Stop'

$pg = Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $pg) { throw 'This runner image has no PostgreSQL service.' }
Set-Service -Name $pg.Name -StartupType Manual
Start-Service -Name $pg.Name
Write-Host "Started $($pg.Name)"

# Separate sign-in accounts: same-machine NTLM reuses the runner's existing token, which predates any group change.
New-LocalGroup -Name 'DataScopeAdmins' -Description 'Data Scope install test' | Out-Null
$user = ConvertTo-SecureString $UserPassword -AsPlainText -Force
New-LocalUser -Name 'dsadmin' -Password $user -PasswordNeverExpires -Description 'Data Scope admin (CI)' | Out-Null
New-LocalUser -Name 'dsnobody' -Password $user -PasswordNeverExpires -Description 'Signed in, no Data Scope role (CI)' | Out-Null
Add-LocalGroupMember -Group 'DataScopeAdmins' -Member 'dsadmin'
$secure = ConvertTo-SecureString $ScanPassword -AsPlainText -Force
New-LocalUser -Name 'dsscan' -Password $secure -PasswordNeverExpires -Description 'Data Scope scan account (CI)' | Out-Null

$root = 'C:\DSLab\Finance'
foreach ($dir in 'Budget', 'Payroll\2026', 'Restricted', 'Projects') { New-Item -ItemType Directory -Force -Path (Join-Path $root $dir) | Out-Null }
'synthetic budget' | Set-Content "$root\Budget\fy2026.xlsx"
# A made-up SSN in a valid range, for the classification check. Not a real person.
"Name,SSN`r`nAvery Okafor,512-48-3307" | Set-Content "$root\Payroll\2026\march.csv"
# A synthetic scanned form with the same made-up SSN, read by OCR (scripts/make-ocr-fixtures.py).
Copy-Item (Join-Path $PSScriptRoot '..\..\..\tests\DataScope.Scanner.Tests\Content\Fixtures\ocr\form-scan.png') "$root\Payroll\2026\new-hire-scan.png"
'synthetic payroll' | Set-Content "$root\Payroll\2026\april.csv"
'read me' | Set-Content "$root\readme.txt"
'synthetic secret' | Set-Content "$root\Restricted\secret.txt"
cmd /c mklink /J "$root\Projects\loop" $root | Out-Null

& icacls.exe $root /grant 'dsscan:(OI)(CI)RX' /Q | Out-Null
& icacls.exe "$root\Restricted" /deny 'dsscan:(OI)(CI)RX' /Q | Out-Null
New-SmbShare -Name 'DSLabFinance' -Path $root -ReadAccess "$env:COMPUTERNAME\dsscan" | Out-Null
Write-Host 'Lab share \\localhost\DSLabFinance is ready'
