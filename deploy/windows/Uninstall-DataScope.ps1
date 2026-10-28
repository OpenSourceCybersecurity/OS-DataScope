#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the Data Scope services and programs from this server.

.DESCRIPTION
    Stops and deletes the "Data Scope API" and "Data Scope Scanner" services, removes
    the nightly backup task and the firewall rule, and deletes "C:\Program Files\Data Scope".

    The database is kept: scan results and the audit log stay in PostgreSQL. When Data Scope installed its own
    PostgreSQL (the "Data Scope Database" service, from DataScope-Setup.exe or -InstallDatabase), that service keeps
    running with the data unless you pass -RemoveDatabase, which deletes it, its programs and all its data for good.
    Backups in C:\ProgramData\DataScope\backups are always kept. Settings in
    C:\ProgramData\DataScope\config (including the encrypted database passwords and
    the backup key) are kept unless you pass -RemoveSettings, so a reinstall picks up
    where you left off. Save the backup key first if you remove them: without it the
    backups cannot be restored.

    It also removes Data Scope from Windows Settings, Apps, whose Uninstall button runs this script with -FromApps:
    it asks first and waits for Enter at the end, so the window doesn't close before you've read it.

.EXAMPLE
    .\Uninstall-DataScope.ps1
.EXAMPLE
    .\Uninstall-DataScope.ps1 -RemoveSettings
.EXAMPLE
    .\Uninstall-DataScope.ps1 -RemoveSettings -RemoveDatabase    # remove everything except the backups
#>
[CmdletBinding()]
param(
    [switch]$RemoveSettings,
    # Also delete the "Data Scope Database" service Data Scope installed, its programs and all its data.
    [switch]$RemoveDatabase,
    # Run from Settings, Apps: ask before removing anything, and wait for Enter at the end.
    [switch]$FromApps,
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Data Scope')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ConfigDir = Join-Path $env:ProgramData 'DataScope\config'
$BackupDir = Join-Path $env:ProgramData 'DataScope\backups'
$DatabaseService = 'DataScopeDatabase'
$PgHome = Join-Path $env:ProgramFiles 'Data Scope Database'
$PgData = Join-Path $env:ProgramData 'DataScope\database'
$PgAdminFile = Join-Path $ConfigDir 'setup-only\database-admin.dat'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

# The copy in the program folder deletes that folder, so it must not be the current directory.
Set-Location $env:SystemRoot
if ($FromApps) {
    $answer = Read-Host 'Remove Data Scope from this server? Scan results, settings and backups are kept. (Y/N)'
    if ($answer -notmatch '^(y|yes)$') { Read-Host 'Nothing was changed. Press Enter to close' | Out-Null; return }
}
try {
    Write-Step 'Removing services'
    foreach ($name in 'DataScopeScanner', 'DataScopeApi') {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $svc) { continue }
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name $name -Force
            $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        & sc.exe delete $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not delete service $name (sc.exe exit code $LASTEXITCODE)." }
        Write-Host "    Removed $($svc.DisplayName)"
    }

    Write-Step 'Removing the nightly backup task'
    if (Get-ScheduledTask -TaskPath '\Data Scope\' -TaskName 'Data Scope backup' -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskPath '\Data Scope\' -TaskName 'Data Scope backup' -Confirm:$false
    }

    Write-Step 'Removing the firewall rule'
    Get-NetFirewallRule -Name 'DataScope-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    Write-Step 'Removing Data Scope from Settings, Apps'
    Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataScope' -Recurse -ErrorAction SilentlyContinue

    Write-Step "Removing $InstallPath"
    if ((Test-Path $InstallPath) -and (Test-Path (Join-Path $InstallPath 'api\DataScope.Api.exe'))) {
        Remove-Item -Recurse -Force $InstallPath
    } elseif (Test-Path $InstallPath) {
        Write-Warning "$InstallPath doesn't look like a Data Scope folder, so it was left alone."
    }

    $ownDatabase = Get-Service -Name $DatabaseService -ErrorAction SilentlyContinue
    if ($ownDatabase -and $RemoveDatabase) {
        Write-Step 'Removing the Data Scope database and all its data'
        if ($ownDatabase.Status -ne 'Stopped') {
            Stop-Service -Name $DatabaseService -Force
            $ownDatabase.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        & sc.exe delete $DatabaseService | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not delete service $DatabaseService (sc.exe exit code $LASTEXITCODE)." }
        foreach ($path in $PgHome, $PgData, $PgAdminFile) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }

    if ($RemoveSettings) {
        if ((Test-Path $BackupDir) -and (Get-ChildItem $BackupDir -Filter '*.dsbackup' -ErrorAction SilentlyContinue)) {
            Write-Warning "The backup key is in these settings. Backups in $BackupDir can't be restored without it unless you saved a copy (DataScope.Setup backup-key)."
        }
        Write-Step "Removing settings in $ConfigDir"
        Remove-Item -Recurse -Force $ConfigDir -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Write-Host 'Data Scope is uninstalled.' -ForegroundColor Green
    if ($ownDatabase -and $RemoveDatabase) {
        Write-Host '    The Data Scope database and its data were deleted.'
    } elseif ($ownDatabase) {
        Write-Host '    The Data Scope database (service "Data Scope Database") was kept, with the scan results. Installing Data Scope'
        Write-Host '    again uses it. To delete it for good: .\Uninstall-DataScope.ps1 -RemoveDatabase'
    } else {
        Write-Host '    The PostgreSQL database was kept. To delete it and its login roles for good, run in psql as the admin user:'
        Write-Host '      DROP DATABASE datascope WITH (FORCE);  DROP ROLE datascope_app;  DROP ROLE datascope_owner;'
    }
    if (-not $RemoveSettings) { Write-Host "    Settings were kept in $ConfigDir." }
    if (Test-Path $BackupDir) { Write-Host "    Backups were kept in $BackupDir." }
    Write-Host '    The self-signed certificate (if one was created) is still in LocalMachine\My, named "Data Scope (self-signed)".'
} catch {
    if (-not $FromApps) { throw }
    Write-Host "Uninstall stopped: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    if ($FromApps) { Read-Host 'Press Enter to close' | Out-Null }
}
