#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades Data Scope on a Windows management server.

.DESCRIPTION
    Run from the unzipped package folder in an elevated PowerShell window.

    A first install needs a scan account and an admin group. Run without them, it asks for each, suggesting
    YOURDOMAIN\svc-datascope$ and YOURDOMAIN\DataScope-Admins when they exist (Prepare-DataScopeDomain.ps1 makes
    both), and asks before changing anything. It:
      * copies the programs to "C:\Program Files\Data Scope",
      * creates two Windows services: "Data Scope API" (runs as the virtual account
        NT SERVICE\DataScopeApi) and "Data Scope Scanner" (runs as -ScanAccount),
      * creates the PostgreSQL database and two logins for it: an owner login that
        changes the schema (its password is kept in config\setup-only, readable only by
        administrators and SYSTEM) and an app login for the services, which can read
        and write rows but not change the schema or the audit log. Both passwords are
        generated and saved encrypted with DPAPI in C:\ProgramData\DataScope\config,
      * sets up HTTPS with -CertificateThumbprint, or a self-signed certificate,
      * maps -AdminGroup (and optional groups) to Data Scope roles,
      * opens the HTTPS port in Windows Firewall for the Domain and Private profiles,
      * takes a first encrypted database backup and schedules one every night (task
        "\Data Scope\Data Scope backup", as SYSTEM) into C:\ProgramData\DataScope\backups,
        keeping the newest -BackupsToKeep. Backups need PostgreSQL's pg_dump on this
        server; pass -PgBinPath when PostgreSQL runs elsewhere,
      * saves the self-signed certificate (public part only) as C:\ProgramData\DataScope\datascope-https.cer,
        for browsers to trust, and prints what to do next.

    It first checks this server, the package (and its code signature, when signed), the
    PostgreSQL server and tools, the domain, the accounts and the certificate, and stops
    before changing anything if a check fails. -PreflightOnly runs just these checks. With a gMSA as the scan
    account it also checks this server may use it (installing the AD PowerShell tools to do so when they are
    missing) and says what to run on a domain controller if not.

    Running it again later upgrades in place: it stops the services, replaces the
    programs, backs up the database, applies database migrations and starts the services. Settings and data
    are kept. Parameters passed on an upgrade change only what they name. An
    installation from before the owner login existed asks once for the PostgreSQL
    admin login, to split the database between the two logins.

    With -InstallDatabase it first installs the PostgreSQL that comes in the package (app\postgresql) as the
    service "Data Scope Database": programs in "C:\Program Files\Data Scope Database", data in
    C:\ProgramData\DataScope\database (administrators, SYSTEM and the service only), reachable from this server
    only, on port 5432 or the next free port. Its admin password is generated, used to create the Data Scope
    database, and kept encrypted with DPAPI in config\setup-only. An upgrade updates its programs when the package
    carries a newer minor version of the same PostgreSQL. DataScope-Setup.exe, the setup wizard, runs this script.

    It also adds Data Scope to Windows Settings, Apps (Uninstall runs Uninstall-DataScope.ps1).

    Nothing here changes file shares, NTFS permissions or Active Directory.

.EXAMPLE
    .\Install-DataScope.ps1        # a first install asks for the scan account and admin group

.EXAMPLE
    .\Install-DataScope.ps1 -ScanAccount 'YOURDOMAIN\svc-datascope$' -AdminGroup 'YOURDOMAIN\DataScope-Admins'

.EXAMPLE
    .\Install-DataScope.ps1 -InstallDatabase   # a first install with the PostgreSQL that comes in the package

.EXAMPLE
    .\Install-DataScope.ps1        # upgrade an existing installation
#>
[CmdletBinding()]
param(
    # Account the scanner runs as. A gMSA ends with '$' and needs no password.
    [string]$ScanAccount,
    # Password for a normal (non-gMSA) -ScanAccount. Asked for when missing.
    [pscredential]$ScanCredential,
    # AD groups mapped to roles, as DOMAIN\Group or a SID.
    [string]$AdminGroup,
    [string]$AnalystGroup,
    [string]$AuditorGroup,
    [string]$ViewerGroup,
    [string]$DatabaseHost = 'localhost',
    [ValidateRange(1, 65535)][int]$DatabasePort = 5432,
    # PostgreSQL admin login, used once to create the database. Asked for when missing.
    [pscredential]$DatabaseAdminCredential,
    [string]$DatabaseName = 'datascope',
    [string]$DatabaseOwnerUser = 'datascope_owner',
    [string]$DatabaseAppUser = 'datascope_app',
    [ValidateRange(1, 65535)][int]$Port = 443,
    # Name users type in the browser. Defaults to this server's DNS name.
    [string]$HostName,
    # Certificate in LocalMachine\My for HTTPS. Without it, a self-signed one is created.
    [string]$CertificateThumbprint,
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'Data Scope'),
    [switch]$SkipFirewall,
    # Folder with pg_dump.exe and pg_restore.exe. Found automatically when PostgreSQL is installed on this server.
    [string]$PgBinPath,
    # Local time for the nightly backup, as HH:mm.
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$BackupTime = '02:30',
    [ValidateRange(1, 365)][int]$BackupsToKeep = 14,
    # Don't schedule nightly backups (and remove the task if an earlier install created it).
    [switch]$NoScheduledBackup,
    # Upgrade without taking a backup first. Only for when the backup itself is what fails.
    [switch]$SkipUpgradeBackup,
    # Run the checks only (this server, the package, PostgreSQL, the domain, the certificate) and change nothing.
    [switch]$PreflightOnly,
    # Refuse a package that is not code-signed. Signed packages are always checked.
    [switch]$RequireSignature,
    # Install the PostgreSQL that comes in this package as the service "Data Scope Database" (first install only).
    [switch]$InstallDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ApiService = 'DataScopeApi'
$ScanService = 'DataScopeScanner'
$ApiIdentity = "NT SERVICE\$ApiService"
$EventSources = @('Data Scope API', 'Data Scope Scanner')
$DataRoot = Join-Path $env:ProgramData 'DataScope'
$ConfigDir = Join-Path $DataRoot 'config'
$ConfigFile = Join-Path $ConfigDir 'datascope.json'
$OwnerFile = Join-Path $ConfigDir 'setup-only\database-owner.json'
$LogDir = Join-Path $DataRoot 'logs'
$BackupDir = Join-Path $DataRoot 'backups'
$BackupTaskPath = '\Data Scope\'
$BackupTaskName = 'Data Scope backup'
$SelfSignedName = 'Data Scope (self-signed)'
$CertificateFile = Join-Path $DataRoot 'datascope-https.cer'
$DatabaseService = 'DataScopeDatabase'
$PgHome = Join-Path $env:ProgramFiles 'Data Scope Database'
$PgData = Join-Path $DataRoot 'database'
$PgAdminFile = Join-Path $ConfigDir 'setup-only\database-admin.dat'
$AppsKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataScope'
$PgAdminEntropy = [Text.Encoding]::UTF8.GetBytes('Data Scope database admin')
$Package = $PSScriptRoot
$Version = (Get-Content (Join-Path $Package 'VERSION') -ErrorAction SilentlyContinue | Select-Object -First 1)
if (-not $Version) { $Version = 'unknown' }
$BundledPg = Join-Path $Package 'app\postgresql'
$VcRedist = Join-Path $Package 'app\vcredist\vc_redist.x64.exe'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string]$Message) { Write-Host "    $Message" }

# Whether a person is at the keyboard to answer: not a scheduled task, a service or powershell -NonInteractive.
function Test-CanAsk {
    [Environment]::UserInteractive -and -not ([Environment]::GetCommandLineArgs() | Where-Object { $_ -match '^-NonI' })
}

function Read-Answer([string]$Question, [string]$Default) {
    $shown = if ($Default) { " [$Default]" } else { '' }
    $answer = "$(Read-Host "    $Question$shown")".Trim()
    if ($answer) { return $answer }
    return $Default
}

# DOMAIN\Name for an account or group that exists, else $null. Finds names given without a domain too.
function Find-Account([string]$Name) {
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($Name)).Translate([System.Security.Principal.SecurityIdentifier])
        return $sid.Translate([System.Security.Principal.NTAccount]).Value
    } catch { return $null }
}

# Asks until the answer names an account or group that exists. Returns DOMAIN\Name.
function Read-Account([string]$Question, [string[]]$Suggestions, [string]$What) {
    $default = $Suggestions | ForEach-Object { Find-Account $_ } | Where-Object { $_ } | Select-Object -First 1
    for ($try = 0; $try -lt 5; $try++) {
        $answer = Read-Answer $Question $default
        if (-not $answer) { Write-Note "Data Scope needs $What. Type its name, such as YOURDOMAIN\Name."; continue }
        $found = Find-Account $answer
        if ($found) { return $found }
        Write-Note "No account or group called '$answer' was found. Check the spelling, and use DOMAIN\Name."
    }
    throw "No $What was given. Run the installer again, or pass it as a parameter."
}

# Whether Test-GmsaUse can run here: it needs the AD PowerShell tools.
function Test-CanCheckGmsa { [bool](Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue) }

# For a gMSA scan account: this server must be allowed to fetch its password, or the scanner can't start.
# Returns $null when it is, else what to do about it.
function Test-GmsaUse([string]$Account) {
    $name = ($Account -split '\\')[-1].TrimEnd('$')
    if (Test-ADServiceAccount -Identity $name -WarningAction SilentlyContinue) { return $null }
    return "This server ($env:COMPUTERNAME) isn't allowed to use the gMSA $Account. On a domain controller run: .\Prepare-DataScopeDomain.ps1 -DataScopeServer $env:COMPUTERNAME (or: Set-ADServiceAccount $name -PrincipalsAllowedToRetrieveManagedPassword '$env:COMPUTERNAME`$'), then run the installer again."
}

function Invoke-Native {
    # Runs a program and fails on a non-zero exit code. Output is shown, not returned.
    param([string]$FilePath, [string[]]$Arguments, [int[]]$OkCodes = @(0))
    & $FilePath @Arguments | ForEach-Object { Write-Note $_ }
    if ($OkCodes -notcontains $LASTEXITCODE) {
        throw "$(Split-Path $FilePath -Leaf) failed with exit code $LASTEXITCODE."
    }
}

function Get-ConfigValue([string[]]$Keys) {
    # Reads a value from datascope.json; $null when the file or key is missing.
    if (-not (Test-Path $ConfigFile)) { return $null }
    $node = Get-Content $ConfigFile -Raw | ConvertFrom-Json
    foreach ($key in $Keys) {
        if ($null -eq $node -or -not ($node.PSObject.Properties.Name -contains $key)) { return $null }
        $node = $node.$key
    }
    return $node
}

function Resolve-Sid([string]$Name, [string]$What) {
    if ($Name -match '^S-1-\d+(-\d+)+$') { return $Name.ToUpperInvariant() }
    # Services report local accounts as .\user.
    if ($Name.StartsWith('.\')) { $Name = "$env:COMPUTERNAME\$($Name.Substring(2))" }
    try {
        return (New-Object System.Security.Principal.NTAccount($Name)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "Could not find $What '$Name'. Check the spelling, and use DOMAIN\Name."
    }
}

function Test-PackageIntegrity {
    $sums = Join-Path $Package 'SHA256SUMS'
    if (-not (Test-Path $sums)) { throw "SHA256SUMS is missing. Run this script from the unzipped Data Scope package." }
    $bad = 0
    foreach ($line in Get-Content $sums) {
        if (-not $line.Trim()) { continue }
        $hash, $relative = $line -split '  ', 2
        $path = Join-Path $Package ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash.ToUpperInvariant()) {
            Write-Note "Changed or missing: $relative"
            $bad++
        }
    }
    if ($bad -gt 0) { throw "$bad file(s) in the package don't match SHA256SUMS. Download the package again." }
    return Test-PackageSignature
}

# A signed package has every Data Scope program and script signed with one certificate. SHA256SUMS catches
# accidental damage; the signature catches someone who changed a file and SHA256SUMS together.
function Test-PackageSignature {
    $installer = Get-AuthenticodeSignature -LiteralPath $PSCommandPath
    if ($installer.Status -eq 'NotSigned') {
        if ($RequireSignature) { throw 'This package is not code-signed, and -RequireSignature was given.' }
        return 'not code-signed'
    }
    if ($installer.Status -ne 'Valid') {
        throw "The installer's code signature is not valid ($($installer.Status): $($installer.StatusMessage)). Download the package again."
    }
    $signer = $installer.SignerCertificate.Thumbprint
    # Filtered by name, not -Include: Windows PowerShell 5.1 ignores -Include with -LiteralPath.
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Package 'app') -Recurse -File |
        Where-Object { $_.Name -match '^DataScope\..+\.(exe|dll)$' }) +
        @(Get-ChildItem -LiteralPath $Package -File -Filter '*.ps1')
    $wizard = Join-Path $Package 'DataScope-Setup.exe'
    if (Test-Path -LiteralPath $wizard) { $files += Get-Item -LiteralPath $wizard }
    $bad = 0
    foreach ($file in $files) {
        $sig = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Thumbprint -ne $signer) {
            Write-Note "Not signed by the package's publisher: $($file.FullName.Substring($Package.Length + 1)) ($($sig.Status))"
            $bad++
        }
    }
    if ($bad -gt 0) { throw "$bad file(s) in the package are not signed by its publisher. Download the package again." }
    return "signed by $($installer.SignerCertificate.GetNameInfo('SimpleName', $false)), $($files.Count) files checked"
}

# Checks that change nothing. Returns the number of failures; prints one line per check.
function Invoke-Preflight([bool]$IsUpgrade) {
    function Write-Check([string]$Level, [string]$Name, [string]$Detail) {
        $color = @{ OK = 'Green'; WARN = 'Yellow'; FAIL = 'Red' }[$Level]
        Write-Host ("    [{0,-4}] {1}: {2}" -f $Level, $Name, $Detail) -ForegroundColor $color
        if ($Level -eq 'FAIL') { $script:preflightFailed++ }
    }
    $script:preflightFailed = 0

    $os = Get-CimInstance Win32_OperatingSystem
    if (-not [Environment]::Is64BitOperatingSystem) { Write-Check FAIL 'Windows' 'Data Scope needs 64-bit Windows.' }
    elseif ([version]$os.Version -lt [version]'10.0.17763') { Write-Check FAIL 'Windows' "Needs Windows Server 2019 or later; this is $($os.Caption)." }
    else { Write-Check OK 'Windows' $os.Caption }

    try { Write-Check OK 'Package' (Test-PackageIntegrity) } catch { Write-Check FAIL 'Package' $_.Exception.Message }

    $computer = Get-CimInstance Win32_ComputerSystem
    $memoryGb = [math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
    # A VM given 4 GB reports about 3.9 GB to Windows; that is enough to start, so only less than 3.5 GB fails.
    if ($memoryGb -lt 3.5) { Write-Check FAIL 'Memory' "$memoryGb GB; Data Scope needs at least 4 GB (8 GB recommended)." }
    elseif ($memoryGb -lt 8) { Write-Check WARN 'Memory' "$memoryGb GB; 8 GB is recommended with PostgreSQL on this server." }
    else { Write-Check OK 'Memory' "$memoryGb GB" }
    $cores = [Environment]::ProcessorCount
    if ($cores -lt 2) { Write-Check WARN 'Processors' "$cores; 2 or more are recommended." } else { Write-Check OK 'Processors' "$cores" }

    foreach ($folder in @($env:ProgramData, (Split-Path $InstallPath -Qualifier)) | Select-Object -Unique) {
        $drive = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f (Split-Path $folder -Qualifier))
        if (-not $drive) { continue }
        $freeGb = [math]::Round($drive.FreeSpace / 1GB, 1)
        if ($freeGb -lt 5) { Write-Check FAIL "Disk $($drive.DeviceID)" "$freeGb GB free; at least 5 GB is needed for the programs, logs and backups." }
        elseif ($freeGb -lt 20) { Write-Check WARN "Disk $($drive.DeviceID)" "$freeGb GB free; 20 GB or more is recommended for backups." }
        else { Write-Check OK "Disk $($drive.DeviceID)" "$freeGb GB free" }
    }

    # Text recognition (D129) and the bundled PostgreSQL are built against the Visual C++ runtime. Without it, images are
    # listed as not read, and the bundled database can't start.
    $missing = Get-MissingVcRuntime
    if ($missing -and (Test-Path -LiteralPath $VcRedist)) { Write-Check OK 'Visual C++ runtime' 'Not installed yet; it is installed from the package' }
    elseif ($missing -and $UseBundledDatabase -and -not $existingDatabase) { Write-Check FAIL 'Visual C++ runtime' "Missing $($missing -join ', '), which the Data Scope database needs. Install the Microsoft Visual C++ Redistributable (2015-2022, x64): https://aka.ms/vs/17/release/vc_redist.x64.exe" }
    elseif ($missing) { Write-Check WARN 'Visual C++ runtime' "Missing $($missing -join ', '). Text in images and scanned PDFs is not read until the Microsoft Visual C++ Redistributable (2015-2022, x64) is installed: https://aka.ms/vs/17/release/vc_redist.x64.exe" }
    else { Write-Check OK 'Visual C++ runtime' 'Installed (needed to read text in images)' }

    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    $owner = if ($listener) { (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName } else { $null }
    if ($listener -and $owner -ne 'DataScope.Api') { Write-Check FAIL 'HTTPS port' "Port $Port is used by '$owner'. Stop it or choose another -Port." }
    else { Write-Check OK 'HTTPS port' "$Port" }

    if ($CertificateThumbprint) {
        $clean = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $clean | Select-Object -First 1
        if (-not $cert) { Write-Check FAIL 'Certificate' "No certificate $clean in the computer's Personal store." }
        elseif (-not $cert.HasPrivateKey) { Write-Check FAIL 'Certificate' "$($cert.Subject) has no private key on this server." }
        elseif ($cert.NotAfter -lt (Get-Date)) { Write-Check FAIL 'Certificate' "$($cert.Subject) expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
        else { Write-Check OK 'Certificate' "$($cert.Subject), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))" }
    }

    if ($computer.PartOfDomain) {
        & nltest.exe "/dsgetdc:$($computer.Domain)" *> $null
        if ($LASTEXITCODE -eq 0) { Write-Check OK 'Domain' "$($computer.Domain), a domain controller answers" }
        else { Write-Check WARN 'Domain' "$($computer.Domain): no domain controller answered (nltest /dsgetdc). Sign-in and directory sync need one." }
    } else {
        Write-Check WARN 'Domain' 'This server is not joined to a domain. Windows sign-in and share scanning need a domain in production.'
    }
    foreach ($pair in @(@('Scan account', $ScanAccount), @('Admin group', $AdminGroup), @('Analyst group', $AnalystGroup), @('Auditor group', $AuditorGroup), @('Viewer group', $ViewerGroup))) {
        if (-not $pair[1]) { continue }
        try { Write-Check OK $pair[0] "$($pair[1]) ($(Resolve-Sid $pair[1] $pair[0].ToLowerInvariant()))" } catch { Write-Check FAIL $pair[0] $_.Exception.Message }
    }
    if ($ScanAccount -and $ScanAccount.EndsWith('$')) {
        try { $problem = if (Test-CanCheckGmsa) { Test-GmsaUse $ScanAccount } else { 'not checked' } }
        catch { $problem = "Couldn't check that this server may use ${ScanAccount}: $($_.Exception.Message)" }
        if ($problem -eq 'not checked') { Write-Check WARN 'gMSA' 'Not checked here: the AD PowerShell tools are not installed. The installer installs them (RSAT-AD-PowerShell) and checks.' }
        elseif (-not $problem) { Write-Check OK 'gMSA' "This server may use $ScanAccount" }
        else { Write-Check WARN 'gMSA' "$problem The installer refreshes this server's sign-in and checks again first." }
    }

    if (-not $IsUpgrade -and $UseBundledDatabase) {
        if ($existingDatabase) {
            if (Test-Path -LiteralPath $PgAdminFile) { Write-Check OK 'PostgreSQL' "Data Scope Database is already installed here (port $(Get-BundledDatabasePort)); it is used again" }
            else { Write-Check FAIL 'PostgreSQL' "The Data Scope Database service exists, but its admin password is not in $PgAdminFile. Remove it with Uninstall-DataScope.ps1 -RemoveDatabase, or pass -DatabaseAdminCredential." }
        } elseif (-not (Test-Path -LiteralPath (Join-Path $BundledPg 'bin\postgres.exe'))) {
            Write-Check FAIL 'PostgreSQL' 'This package has no PostgreSQL of its own (app\postgresql). Install PostgreSQL 16 or later yourself and run without -InstallDatabase.'
        } else {
            $free = 5432..5450 | Where-Object { Test-PortFree $_ } | Select-Object -First 1
            if ($free) { Write-Check OK 'PostgreSQL' "PostgreSQL $(Get-PgFolderVersion $BundledPg) will be installed as Data Scope Database, on port $free" }
            else { Write-Check FAIL 'PostgreSQL' 'No free port between 5432 and 5450 for the Data Scope database.' }
        }
    } elseif (-not $IsUpgrade) {
        $tcp = New-Object System.Net.Sockets.TcpClient
        try {
            $reached = $tcp.ConnectAsync($DatabaseHost, $DatabasePort).Wait(5000) -and $tcp.Connected
        } catch { $reached = $false } finally { $tcp.Dispose() }
        if ($reached) { Write-Check OK 'PostgreSQL' "${DatabaseHost}:$DatabasePort answers" }
        else { Write-Check FAIL 'PostgreSQL' "Nothing answers on ${DatabaseHost}:$DatabasePort. Install PostgreSQL 16 or later (https://www.postgresql.org/download/windows/), or pass -DatabaseHost and -DatabasePort." }
    }
    # Needed for the nightly and first backups, and for the backup before an upgrade.
    if (-not $NoScheduledBackup -or ($IsUpgrade -and -not $SkipUpgradeBackup)) {
        $pgBin = Find-PgBin
        if ($pgBin) { Write-Check OK 'PostgreSQL tools' $pgBin }
        else { Write-Check FAIL 'PostgreSQL tools' 'pg_dump.exe was not found. Install the PostgreSQL command line tools and pass -PgBinPath, or pass -NoScheduledBackup -SkipUpgradeBackup.' }
    }
    return $script:preflightFailed
}

function Stop-DataScopeService([string]$Name) {
    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Note "Stopping $($svc.DisplayName)"
        Stop-Service -Name $Name -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

function Copy-Program([string]$Part) {
    $source = Join-Path $Package "app\$Part"
    $target = Join-Path $InstallPath $Part
    # Mirror: the target folder ends up identical to the package, old files removed.
    Invoke-Native robocopy.exe @($source, $target, '/MIR', '/R:3', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') -OkCodes @(0, 1, 2, 3)
}

function Install-Service([string]$Name, [string]$DisplayName, [string]$Exe, [string]$Description) {
    $binary = '"' + (Join-Path $InstallPath $Exe) + '"'
    if (-not (Get-Service -Name $Name -ErrorAction SilentlyContinue)) {
        New-Service -Name $Name -DisplayName $DisplayName -BinaryPathName $binary -Description $Description -StartupType Automatic | Out-Null
        Write-Note "Created service $DisplayName"
    } else {
        # Through CIM, not sc.exe: Windows PowerShell 5.1 drops the inner quotes of native arguments,
        # which would leave an unquoted path with spaces.
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
        $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ PathName = $binary }
        if ($result.ReturnValue -ne 0) { throw "Could not update the program path of $Name (Win32_Service.Change returned $($result.ReturnValue))." }
    }
    # Start after the network and database are up; restart after a crash.
    Invoke-Native sc.exe @('config', $Name, 'start=', 'delayed-auto')
    Invoke-Native sc.exe @('failure', $Name, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
}

function Set-ServiceAccount([string]$Name, [string]$Account, [string]$Password) {
    # Win32_Service.Change keeps the password off the command line. A gMSA takes no password.
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'"
    $arguments = @{ StartName = $Account }
    if ($Password) { $arguments['StartPassword'] = $Password }
    $result = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments $arguments
    if ($result.ReturnValue -ne 0) {
        throw "Could not set the account of $Name to $Account (Win32_Service.Change returned $($result.ReturnValue); 22 means the account was not found)."
    }
}

function Grant-ServiceLogonRight([string]$Sid) {
    # "Log on as a service" for the scan account. A domain GPO that sets this right
    # replaces local settings; then add the account in that GPO instead.
    $work = Join-Path $env:TEMP ('datascope-secpol-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $export = Join-Path $work 'current.inf'
        Invoke-Native secedit.exe @('/export', '/cfg', $export, '/areas', 'USER_RIGHTS', '/quiet')
        $line = Get-Content $export | Where-Object { $_ -match '^SeServiceLogonRight\s*=' } | Select-Object -First 1
        $holders = @()
        if ($line) { $holders = @(($line -split '=', 2)[1].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
        if ($holders -contains "*$Sid") { return }
        $inf = Join-Path $work 'grant.inf'
        $holders += "*$Sid"
        Set-Content -Path $inf -Encoding Unicode -Value @(
            '[Unicode]', 'Unicode=yes', '[Version]', 'signature="$CHICAGO$"', 'Revision=1',
            '[Privilege Rights]', ('SeServiceLogonRight = ' + ($holders -join ',')))
        Invoke-Native secedit.exe @('/configure', '/db', (Join-Path $work 'grant.sdb'), '/cfg', $inf, '/areas', 'USER_RIGHTS', '/quiet')
        Write-Note 'Granted "Log on as a service" to the scan account'
    } finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

function Protect-ConfigDirectory([string[]]$Readers) {
    # Only administrators and SYSTEM may change settings; the services may read them.
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    $grants = @('*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F')
    foreach ($r in $Readers) { $grants += "${r}:(OI)(CI)RX" }
    $arguments = @($ConfigDir, '/inheritance:r', '/grant:r') + $grants + @('/Q')
    Invoke-Native icacls.exe $arguments
}

function Get-HttpsCertificate([string]$Thumbprint, [string]$DnsName) {
    if ($Thumbprint) {
        $clean = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        $cert = Get-Item -Path "Cert:\LocalMachine\My\$clean" -ErrorAction SilentlyContinue
        if (-not $cert) { throw "No certificate with thumbprint $clean in LocalMachine\My." }
        if (-not $cert.HasPrivateKey) { throw "Certificate $clean has no private key." }
        if ($cert.NotAfter -lt (Get-Date)) { throw "Certificate $clean expired on $($cert.NotAfter)." }
        return $cert
    }
    $existing = Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.FriendlyName -eq $SelfSignedName -and $_.NotAfter -gt (Get-Date).AddDays(30) -and ($_.DnsNameList | ForEach-Object { $_.Unicode }) -contains $DnsName
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($existing) {
        Write-Note "Reusing self-signed certificate $($existing.Thumbprint)"
        return $existing
    }
    $names = @($DnsName)
    $short = $env:COMPUTERNAME.ToLowerInvariant()
    if ($names -notcontains $short) { $names += $short }
    $cert = New-SelfSignedCertificate -DnsName $names -CertStoreLocation 'Cert:\LocalMachine\My' -FriendlyName $SelfSignedName `
        -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
    Write-Note "Created self-signed certificate $($cert.Thumbprint) for $($names -join ', ')"
    return $cert
}

function Grant-PrivateKeyRead($Cert, [string]$Account) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
    if (-not $rsa) { throw "Certificate $($Cert.Thumbprint) does not have an RSA key. Use an RSA certificate." }
    if ($rsa -is [System.Security.Cryptography.RSACng]) {
        $file = $rsa.Key.UniqueName
        $candidates = @('Microsoft\Crypto\Keys', 'Microsoft\Crypto\SystemKeys', 'Microsoft\Crypto\RSA\MachineKeys')
    } else {
        $file = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
        $candidates = @('Microsoft\Crypto\RSA\MachineKeys')
    }
    $path = $candidates | ForEach-Object { Join-Path (Join-Path $env:ProgramData $_) $file } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $path) { throw "Could not find the private key file of certificate $($Cert.Thumbprint)." }
    Invoke-Native icacls.exe @($path, '/grant', "${Account}:R", '/Q')
}

function Invoke-Setup([string[]]$Arguments) {
    $exe = Join-Path $InstallPath 'setup\DataScope.Setup.exe'
    Invoke-Native $exe ($Arguments + @('--config', $ConfigFile))
}

# The folder with pg_dump.exe: -PgBinPath, the one saved earlier, the PATH, or the newest PostgreSQL install here.
function Find-PgBin {
    $candidates = @()
    if ($PgBinPath) { $candidates += $PgBinPath }
    else {
        $saved = Get-ConfigValue @('Backup', 'PgBinPath')
        if ($saved) { $candidates += $saved }
        $candidates += Join-Path $PgHome 'bin'
        if ($UseBundledDatabase) { $candidates += Join-Path $BundledPg 'bin' }
        $candidates += ($env:PATH -split ';' | Where-Object { $_ })
        $installed = Join-Path $env:ProgramFiles 'PostgreSQL'
        if (Test-Path $installed) {
            $candidates += Get-ChildItem $installed -Directory | Where-Object { $_.Name -match '^\d+$' } |
                Sort-Object { [int]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'bin' }
        }
    }
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'pg_dump.exe')) { return $dir }
    }
    return $null
}

# The PostgreSQL version in a folder of bundled PostgreSQL programs (its VERSION file), or $null.
function Get-PgFolderVersion([string]$Folder) {
    $file = Join-Path $Folder 'VERSION'
    if (Test-Path -LiteralPath $file) { return "$(Get-Content -LiteralPath $file | Select-Object -First 1)".Trim() }
    return $null
}

function Test-PortFree([int]$Number) {
    -not (Get-NetTCPConnection -LocalPort $Number -State Listen -ErrorAction SilentlyContinue)
}

# The port the Data Scope database listens on: the last port setting in its postgresql.conf.
function Get-BundledDatabasePort {
    $line = Select-String -LiteralPath (Join-Path $PgData 'postgresql.conf') -Pattern '^\s*port\s*=\s*(\d+)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
    if ($line) { return [int]$line.Matches[0].Groups[1].Value }
    return 5432
}

function Wait-ForPort([int]$Number) {
    for ($i = 0; $i -lt 60; $i++) {
        $tcp = New-Object System.Net.Sockets.TcpClient
        try {
            if ($tcp.ConnectAsync('127.0.0.1', $Number).Wait(2000) -and $tcp.Connected) { return $true }
        } catch { Write-Verbose "Port $Number not answering yet" } finally { $tcp.Dispose() }
        Start-Sleep -Seconds 1
    }
    return $false
}

# The Data Scope database's admin password, encrypted for this computer (DPAPI) in the admin-only setup-only folder.
function Save-DatabaseAdminPassword([string]$Password) {
    $folder = Split-Path $PgAdminFile
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Invoke-Native icacls.exe @($folder, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F', '/Q')
    Add-Type -AssemblyName System.Security
    $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Password), $PgAdminEntropy, 'LocalMachine')
    [IO.File]::WriteAllText($PgAdminFile, [Convert]::ToBase64String($bytes))
}

function Read-DatabaseAdminPassword {
    if (-not (Test-Path -LiteralPath $PgAdminFile)) { return $null }
    Add-Type -AssemblyName System.Security
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String((Get-Content -LiteralPath $PgAdminFile -Raw).Trim()), $PgAdminEntropy, 'LocalMachine')
    return [Text.Encoding]::UTF8.GetString($bytes)
}

function Get-MissingVcRuntime {
    @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$_")) }
}

# Installs the Visual C++ runtime from the package, once Microsoft's signature on it checks out.
function Install-VcRuntime {
    $signature = Get-AuthenticodeSignature -LiteralPath $VcRedist
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "app\vcredist\vc_redist.x64.exe is not validly signed by Microsoft ($($signature.Status)). Download the package again."
    }
    $process = Start-Process -FilePath $VcRedist -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    # 1638: a newer version is already installed. 3010: installed, and Windows would like a restart some time.
    if (@(0, 1638, 3010) -notcontains $process.ExitCode) { throw "The Visual C++ runtime did not install (exit code $($process.ExitCode))." }
    Write-Note 'Installed'
}

# Installs the package's PostgreSQL as the "Data Scope Database" service, reachable from this server only.
# Returns its port. The generated admin password is saved encrypted before anything else uses it.
function Install-BundledDatabase {
    $port = 5432..5450 | Where-Object { Test-PortFree $_ } | Select-Object -First 1
    if (-not $port) { throw 'No free port between 5432 and 5450 for the Data Scope database.' }
    Write-Note "Copying PostgreSQL $(Get-PgFolderVersion $BundledPg) to $PgHome"
    New-Item -ItemType Directory -Force -Path $PgHome | Out-Null
    Copy-Item -Path (Join-Path $BundledPg '*') -Destination $PgHome -Recurse -Force

    # Only administrators, SYSTEM and the service (Network Service) may open the data. initdb gives up administrator
    # rights before it writes (PostgreSQL won't run as an admin), so this user is allowed in while it runs, then removed.
    New-Item -ItemType Directory -Force -Path $PgData | Out-Null
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Invoke-Native icacls.exe @($PgData, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F',
        '*S-1-5-20:(OI)(CI)M', "*${me}:(OI)(CI)F", '/Q')

    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
    Save-DatabaseAdminPassword $password
    $pwFile = Join-Path $env:TEMP ('ds-pg-{0}.txt' -f [guid]::NewGuid())
    try {
        [IO.File]::WriteAllText($pwFile, $password)
        Write-Note "Creating the database in $PgData"
        Invoke-Native (Join-Path $PgHome 'bin\initdb.exe') @('-D', $PgData, '-U', 'postgres', "--pwfile=$pwFile", '-E', 'UTF8',
            '--locale=C', '-A', 'scram-sha-256')
    } finally {
        Remove-Item -LiteralPath $pwFile -Force -ErrorAction SilentlyContinue
    }
    Add-Content -LiteralPath (Join-Path $PgData 'postgresql.conf') -Value @(
        '', '# Data Scope: reachable from this server only, logs kept in the log folder here.',
        "listen_addresses = 'localhost'", "port = $port", 'logging_collector = on')
    Invoke-Native icacls.exe @($PgData, '/remove:g', "*$me", '/T', '/Q')

    Invoke-Native (Join-Path $PgHome 'bin\pg_ctl.exe') @('register', '-N', $DatabaseService, '-U', 'NT AUTHORITY\NetworkService',
        '-D', $PgData, '-S', 'auto', '-w')
    Invoke-Native sc.exe @('config', $DatabaseService, 'DisplayName=', 'Data Scope Database')
    Invoke-Native sc.exe @('description', $DatabaseService, 'PostgreSQL for Data Scope, reachable from this server only.')
    Start-Service $DatabaseService
    if (-not (Wait-ForPort $port)) { throw "The Data Scope database did not start on port $port. See the logs in $PgData\log." }
    Write-Note "Data Scope Database is running on port $port"
    return $port
}

# On an upgrade: newer programs of the same PostgreSQL major version replace the installed ones. The data stays.
function Update-BundledDatabase {
    $have = Get-PgFolderVersion $PgHome
    $new = Get-PgFolderVersion $BundledPg
    if (-not $new -or $have -eq $new) { return }
    if ($have -and ($have -split '\.')[0] -ne ($new -split '\.')[0]) {
        Write-Warning "The Data Scope database runs PostgreSQL $have and this package carries $new. A new major version needs pg_upgrade, so the database was left as it is."
        return
    }
    Write-Note "Updating the Data Scope database from PostgreSQL $have to $new"
    Stop-DataScopeService $DatabaseService
    Copy-Item -Path (Join-Path $BundledPg '*') -Destination $PgHome -Recurse -Force
    Start-Service $DatabaseService
    if (-not (Wait-ForPort (Get-BundledDatabasePort))) { throw "The Data Scope database did not start again after the update. See the logs in $PgData\log." }
}

# Lists Data Scope in Settings, Apps; Uninstall there runs the copy of Uninstall-DataScope.ps1 kept with the programs.
function Register-AppsEntry([string]$Address) {
    Copy-Item -LiteralPath (Join-Path $Package 'Uninstall-DataScope.ps1') -Destination $InstallPath -Force
    $uninstall = Join-Path $InstallPath 'Uninstall-DataScope.ps1'
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    New-Item -Path $AppsKey -Force | Out-Null
    $strings = [ordered]@{
        DisplayName = 'Data Scope'; DisplayVersion = $Version; Publisher = 'Data Scope'; InstallLocation = $InstallPath
        DisplayIcon = (Join-Path $InstallPath 'api\DataScope.Api.exe'); URLInfoAbout = $Address
        UninstallString = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstall`" -FromApps"
    }
    foreach ($name in $strings.Keys) { New-ItemProperty -Path $AppsKey -Name $name -Value $strings[$name] -PropertyType String -Force | Out-Null }
    $sizeKb = [int]((Get-ChildItem -LiteralPath $InstallPath -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $dwords = [ordered]@{ NoModify = 1; NoRepair = 1; EstimatedSize = $sizeKb }
    foreach ($name in $dwords.Keys) { New-ItemProperty -Path $AppsKey -Name $name -Value $dwords[$name] -PropertyType DWord -Force | Out-Null }
}

function Register-BackupTask([bool]$KeepAdjusted) {
    $existing = Get-ScheduledTask -TaskPath $BackupTaskPath -TaskName $BackupTaskName -ErrorAction SilentlyContinue
    if ($NoScheduledBackup) {
        if ($existing) {
            Unregister-ScheduledTask -TaskPath $BackupTaskPath -TaskName $BackupTaskName -Confirm:$false
            Write-Note 'Removed the nightly backup task (-NoScheduledBackup).'
        } else {
            Write-Note 'No nightly backup scheduled (-NoScheduledBackup). Schedule your own: see ADMIN-GUIDE.md, "Backups".'
        }
        return
    }
    # An upgrade keeps a schedule someone adjusted, unless new values are passed.
    if ($existing -and $KeepAdjusted) {
        $exe = Join-Path $InstallPath 'setup\DataScope.Setup.exe'
        if ($existing.Actions[0].Execute -eq $exe) {
            Write-Note 'Nightly backup task kept as it was.'
            return
        }
    }
    $action = New-ScheduledTaskAction -Execute (Join-Path $InstallPath 'setup\DataScope.Setup.exe') `
        -Argument "backup --keep $BackupsToKeep --config `"$ConfigFile`"" -WorkingDirectory (Join-Path $InstallPath 'setup')
    $at = [datetime]::ParseExact($BackupTime, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $trigger = New-ScheduledTaskTrigger -Daily -At $at
    # SYSTEM can read the admin-only owner settings and the DPAPI machine key; nothing else is needed.
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4)
    Register-ScheduledTask -TaskPath $BackupTaskPath -TaskName $BackupTaskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'Encrypted backup of the Data Scope database. See INSTALL.md, "Backups".' -Force | Out-Null
    Write-Note "Nightly backup at $BackupTime into $BackupDir, keeping the newest $BackupsToKeep."
}

function Wait-ForHealth([int]$HttpsPort) {
    # curl.exe ships with Windows Server 2019 and later. -k: the name is checked by browsers, not here.
    for ($i = 0; $i -lt 45; $i++) {
        $code = & curl.exe -k -s -o NUL -w '%{http_code}' "https://localhost:$HttpsPort/health"
        if ($code -eq '200') { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

# ---------------------------------------------------------------------------

$upgrade = [bool](Get-Service -Name $ApiService -ErrorAction SilentlyContinue) -and [bool](Get-ConfigValue @('ConnectionStrings', 'DataScope'))
$existingDatabase = [bool](Get-Service -Name $DatabaseService -ErrorAction SilentlyContinue)
# The PostgreSQL that comes in the package: asked for with -InstallDatabase, or already here from an earlier install
# (Data Scope uninstalled with its database kept, then installed again).
$UseBundledDatabase = -not $upgrade -and ($InstallDatabase -or
    ($existingDatabase -and -not $DatabaseAdminCredential -and -not $PSBoundParameters.ContainsKey('DatabaseHost')))
if ($PreflightOnly) {
    Write-Host "Data Scope $Version preflight (changes nothing)" -ForegroundColor White
    $failures = Invoke-Preflight $upgrade
    if ($failures -gt 0) { throw "$failures check(s) failed. Fix them, then run the installer." }
    Write-Host 'All required checks passed.' -ForegroundColor Green
    return
}

# DataScope-Setup.exe passes passwords in the environment, never on the command line. Read once, then removed.
if (-not $ScanCredential -and $ScanAccount -and $env:DATASCOPE_INSTALL_SCAN_PASSWORD) {
    $ScanCredential = New-Object pscredential $ScanAccount, (ConvertTo-SecureString $env:DATASCOPE_INSTALL_SCAN_PASSWORD -AsPlainText -Force)
}
if (-not $DatabaseAdminCredential -and -not $UseBundledDatabase -and $env:DATASCOPE_INSTALL_DB_ADMIN_PASSWORD) {
    $adminUser = if ($env:DATASCOPE_INSTALL_DB_ADMIN_USER) { $env:DATASCOPE_INSTALL_DB_ADMIN_USER } else { 'postgres' }
    $DatabaseAdminCredential = New-Object pscredential $adminUser, (ConvertTo-SecureString $env:DATASCOPE_INSTALL_DB_ADMIN_PASSWORD -AsPlainText -Force)
}
Remove-Item Env:\DATASCOPE_INSTALL_SCAN_PASSWORD, Env:\DATASCOPE_INSTALL_DB_ADMIN_PASSWORD, Env:\DATASCOPE_INSTALL_DB_ADMIN_USER -ErrorAction SilentlyContinue

# A first install asks for what it needs, before anything is written (answers are account names, not secrets).
$asked = $false
if (-not $upgrade -and -not ($ScanAccount -and $AdminGroup)) {
    if (-not (Test-CanAsk)) {
        if (-not $ScanAccount) { throw 'A first install needs -ScanAccount (the account the scanner runs as, such as YOURDOMAIN\svc-datascope$).' }
        throw 'A first install needs -AdminGroup (the AD group whose members administer Data Scope).'
    }
    Write-Step "Data Scope $($Version): a few questions (press Enter to take the suggestion in brackets)"
    if (-not $ScanAccount) {
        Write-Note 'The scan account reads the file shares. A gMSA ends with $ and needs no password. It must not be an admin.'
        $ScanAccount = Read-Account 'Scan account' @('svc-datascope$', 'svc-datascope') 'a scan account'
    }
    if (-not $AdminGroup) {
        Write-Note 'Members of the admin group can run Data Scope.'
        $AdminGroup = Read-Account 'Admin group' @('DataScope-Admins') 'an admin group'
    }
    $asked = $true
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$transcript = Join-Path $LogDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $transcript | Out-Null
try {
    Write-Host "Data Scope $Version installer" -ForegroundColor White

    Write-Step 'Checking this server'
    $failures = Invoke-Preflight $upgrade
    if ($failures -gt 0) { throw "$failures check(s) failed; nothing was changed. Fix them and run the installer again (-PreflightOnly runs just the checks)." }
    if ($asked) {
        Write-Host ''
        $go = Read-Answer "Install Data Scope $Version with the scanner running as $ScanAccount and $AdminGroup as admins? (Y/N)" 'Y'
        if ($go -notmatch '^(y|yes)$') { Write-Note 'Stopped. Nothing was changed.'; return }
    }

    if ($ScanAccount -and $ScanAccount.EndsWith('$')) {
        Write-Step "Checking this server may use the gMSA $ScanAccount"
        if (-not (Test-CanCheckGmsa)) {
            Write-Note 'Installing the Active Directory PowerShell tools (RSAT-AD-PowerShell) to check it'
            Install-WindowsFeature RSAT-AD-PowerShell | Out-Null
            Import-Module ActiveDirectory -Verbose:$false
        }
        $problem = Test-GmsaUse $ScanAccount
        if ($problem) {
            # The server may have been allowed after it last signed in: refresh its Kerberos tickets and look again.
            & klist.exe -li 0x3e7 purge *> $null
            $problem = Test-GmsaUse $ScanAccount
        }
        if ($problem) { throw "$problem Data Scope was not installed." }
        Write-Note 'OK'
    }

    if ($upgrade) {
        Write-Note 'Existing installation found: upgrading in place. Settings and data are kept.'
        $configuredPort = Get-ConfigValue @('Https', 'Port')
        if (-not $PSBoundParameters.ContainsKey('Port') -and $configuredPort) { $Port = [int]$configuredPort }
    }

    $roleArgs = @()
    foreach ($pair in @(@('PlatformAdmin', $AdminGroup), @('SecurityAnalyst', $AnalystGroup), @('Auditor', $AuditorGroup), @('Viewer', $ViewerGroup))) {
        if ($pair[1]) {
            $sid = Resolve-Sid $pair[1] 'group'
            $roleArgs += @('--role', "$($pair[0])=$sid")
            Write-Note "$($pair[0]): $($pair[1]) ($sid)"
        }
    }

    $scanSid = $null
    $scanPassword = $null
    if ($ScanAccount) {
        $scanSid = Resolve-Sid $ScanAccount 'scan account'
        if (-not $ScanAccount.EndsWith('$')) {
            if (-not $ScanCredential) { $ScanCredential = Get-Credential -UserName $ScanAccount -Message "Password for the scan account $ScanAccount" }
            $scanPassword = $ScanCredential.GetNetworkCredential().Password
        }
    }

    if (-not $HostName) {
        $computer = Get-CimInstance Win32_ComputerSystem
        $HostName = if ($computer.PartOfDomain) { "$($env:COMPUTERNAME).$($computer.Domain)" } else { $env:COMPUTERNAME }
        $HostName = $HostName.ToLowerInvariant()
    }

    # A first install, or an upgrade from before the owner login existed, needs the PostgreSQL admin login once.
    $needsDatabaseSetup = -not $upgrade -or -not (Test-Path $OwnerFile)

    # Backups run pg_dump on this server. Checked before anything changes, so a missing tool stops nothing half-done.
    $backupScheduleGiven = $PSBoundParameters.ContainsKey('BackupTime') -or $PSBoundParameters.ContainsKey('BackupsToKeep')
    $upgradeBackup = $upgrade -and -not $needsDatabaseSetup -and -not $SkipUpgradeBackup
    $firstBackup = (-not $upgrade -or $needsDatabaseSetup) -and -not $NoScheduledBackup
    # pg_dump was found by the preflight. Only a folder given with -PgBinPath is saved; otherwise setup looks again
    # each time, so a PostgreSQL upgrade is followed.
    if ($needsDatabaseSetup -and -not $DatabaseAdminCredential -and -not $UseBundledDatabase) {
        $why = if ($upgrade) { 'to give the services their own limited database login' } else { 'to create the Data Scope database' }
        $DatabaseAdminCredential = Get-Credential -UserName 'postgres' -Message "PostgreSQL admin login (used once $why; not saved)"
    }

    if ((Get-MissingVcRuntime) -and (Test-Path -LiteralPath $VcRedist)) {
        Write-Step 'Installing the Microsoft Visual C++ runtime'
        Install-VcRuntime
    }

    Write-Step 'Stopping Data Scope services'
    Stop-DataScopeService $ScanService
    Stop-DataScopeService $ApiService

    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $owner = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        throw "Port $Port is already used by '$owner'. Stop it or choose another -Port."
    }

    Write-Step "Copying programs to $InstallPath"
    foreach ($part in 'api', 'worker', 'setup') { Copy-Program $part }

    Write-Step 'Registering services'
    foreach ($source in $EventSources) {
        if (-not [System.Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
    }
    Install-Service $ApiService 'Data Scope API' 'api\DataScope.Api.exe' 'Data Scope web UI and API.'
    Install-Service $ScanService 'Data Scope Scanner' 'worker\DataScope.Worker.exe' 'Data Scope read-only file share scanner.'
    # A virtual account: no password, and on the network it acts as this computer, so Kerberos sign-in works without an SPN.
    Invoke-Native sc.exe @('config', $ApiService, 'obj=', $ApiIdentity)
    Invoke-Native sc.exe @('sidtype', $ApiService, 'unrestricted')
    if ($ScanAccount) {
        Set-ServiceAccount $ScanService $ScanAccount $scanPassword
        Grant-ServiceLogonRight $scanSid
        Write-Note "Scanner runs as $ScanAccount"
    }
    $scanRunsAs = (Get-CimInstance -ClassName Win32_Service -Filter "Name='$ScanService'").StartName
    if ($scanRunsAs -eq 'LocalSystem') { throw 'The scanner must not run as LocalSystem. Pass -ScanAccount.' }
    if (-not $scanSid) { $scanSid = Resolve-Sid $scanRunsAs 'scan account' }

    Write-Step 'Protecting the settings folder'
    Protect-ConfigDirectory @($ApiIdentity, "*$scanSid")
    Write-Note "$ConfigDir is readable by administrators, SYSTEM, $ApiIdentity and $scanRunsAs only"

    $configureArgs = @('configure') + $roleArgs
    if (-not $upgrade -or $PSBoundParameters.ContainsKey('Port')) { $configureArgs += @('--https-port', "$Port") }
    if ($CertificateThumbprint -or -not $upgrade) {
        Write-Step 'Setting up HTTPS'
        $cert = Get-HttpsCertificate $CertificateThumbprint $HostName
        Grant-PrivateKeyRead $cert $ApiIdentity
        if ($cert.FriendlyName -eq $SelfSignedName) {
            # Only the public part: what a browser needs to trust it, never the key.
            Export-Certificate -Cert $cert -FilePath $CertificateFile -Force | Out-Null
            Write-Note "Saved it for browsers to trust as $CertificateFile"
        }
        $configureArgs += @('--certificate-thumbprint', $cert.Thumbprint)
    } else {
        $current = Get-ConfigValue @('Https', 'CertificateThumbprint')
        if ($current) {
            $cert = Get-HttpsCertificate $current $HostName
            Grant-PrivateKeyRead $cert $ApiIdentity
            if ($cert.FriendlyName -eq $SelfSignedName -and -not (Test-Path $CertificateFile)) {
                Export-Certificate -Cert $cert -FilePath $CertificateFile -Force | Out-Null
                Write-Note "Saved the self-signed certificate for browsers to trust as $CertificateFile"
            }
        }
    }

    if ($UseBundledDatabase) {
        if ($existingDatabase) {
            Write-Step 'Using the Data Scope database already on this server'
            if ((Get-Service $DatabaseService).Status -ne 'Running') { Start-Service $DatabaseService }
            Update-BundledDatabase
            $DatabasePort = Get-BundledDatabasePort
            if (-not (Wait-ForPort $DatabasePort)) { throw "The Data Scope database does not answer on port $DatabasePort. See the logs in $PgData\log." }
        } else {
            Write-Step 'Installing the Data Scope database (PostgreSQL)'
            $DatabasePort = Install-BundledDatabase
        }
        $DatabaseHost = 'localhost'
        $DatabaseAdminCredential = New-Object pscredential 'postgres', (ConvertTo-SecureString (Read-DatabaseAdminPassword) -AsPlainText -Force)
        $PgBinPath = Join-Path $PgHome 'bin'
    } elseif ($InstallDatabase) {
        Write-Note 'Data Scope is already installed, so -InstallDatabase changes nothing: the database it uses is kept.'
    }

    Write-Step 'Preparing the database'
    if (-not $needsDatabaseSetup) {
        if ($PgBinPath) { Invoke-Setup @('configure', '--pg-bin', $PgBinPath) }
        if (-not $upgradeBackup) {
            Write-Warning 'Upgrading without a backup first (-SkipUpgradeBackup).'
        } else {
            Write-Note 'Backing up the database before upgrading it'
            Invoke-Setup @('backup', '--before-upgrade')
        }
        if ($existingDatabase) { Update-BundledDatabase }
        Invoke-Setup @('migrate')
    } else {
        $databaseArgs = @('database', '--admin-user', $DatabaseAdminCredential.UserName)
        # On an upgrade the saved connection names the server, database and app login; parameters override it.
        if (-not $upgrade -or $PSBoundParameters.ContainsKey('DatabaseHost')) { $databaseArgs += @('--host', $DatabaseHost) }
        if (-not $upgrade -or $PSBoundParameters.ContainsKey('DatabasePort')) { $databaseArgs += @('--port', "$DatabasePort") }
        if (-not $upgrade -or $PSBoundParameters.ContainsKey('DatabaseName')) { $databaseArgs += @('--database', $DatabaseName) }
        if (-not $upgrade -or $PSBoundParameters.ContainsKey('DatabaseAppUser')) { $databaseArgs += @('--app-user', $DatabaseAppUser) }
        $databaseArgs += @('--owner-user', $DatabaseOwnerUser)
        $env:DATASCOPE_PG_ADMIN_PASSWORD = $DatabaseAdminCredential.GetNetworkCredential().Password
        try {
            Invoke-Setup $databaseArgs
        } finally {
            Remove-Item Env:\DATASCOPE_PG_ADMIN_PASSWORD -ErrorAction SilentlyContinue
        }
    }
    # Encrypts any password an older version saved as plain text. Does nothing when there are none.
    Invoke-Setup @('protect')
    if ($PgBinPath) { $configureArgs += @('--pg-bin', $PgBinPath) }
    Invoke-Setup $configureArgs

    Write-Step 'Setting up backups'
    if ($firstBackup) {
        # Also creates this server's backup key, so it can be saved before anything depends on it.
        Invoke-Setup @('backup')
    }
    Register-BackupTask ($upgrade -and -not $backupScheduleGiven)

    Write-Step 'Checking the configuration'
    Invoke-Setup @('check')

    if (-not $SkipFirewall -and (-not $upgrade -or $PSBoundParameters.ContainsKey('Port'))) {
        Write-Step "Allowing HTTPS on port $Port (Domain and Private networks)"
        Get-NetFirewallRule -Name 'DataScope-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -Name 'DataScope-HTTPS' -DisplayName 'Data Scope (HTTPS)' -Direction Inbound -Protocol TCP `
            -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
    }

    Write-Step 'Starting services'
    Start-Service $ApiService
    if (-not (Wait-ForHealth $Port)) {
        throw "The API did not become healthy on port $Port. See Event Viewer > Windows Logs > Application, source 'Data Scope API'."
    }
    Start-Service $ScanService
    Start-Sleep -Seconds 5
    if ((Get-Service $ScanService).Status -ne 'Running') {
        throw "The scanner stopped after starting. See Event Viewer > Windows Logs > Application, source 'Data Scope Scanner'."
    }

    $address = "https://$HostName$(if ($Port -ne 443) { ":$Port" })"
    Write-Step 'Adding Data Scope to Settings, Apps'
    Register-AppsEntry "$address/"

    Write-Host ''
    Write-Host "Data Scope $Version is running." -ForegroundColor Green
    Write-Note "Open $address/ from a domain-joined computer."
    if (-not $upgrade) {
        Write-Host ''
        Write-Host 'Next steps' -ForegroundColor White
        $step = 1
        if (-not $CertificateThumbprint) {
            Write-Note "$step. On each computer that opens Data Scope, trust its certificate: copy $CertificateFile there, double-click it,"
            Write-Note '   choose Install Certificate, Local Machine, then Trusted Root Certification Authorities. (Or push it with Group Policy.)'
            $step++
        }
        Write-Note "$step. In Internet Options, Security, Local intranet, Sites, Advanced, add $address, so Edge and Chrome sign you in with Windows."
        $step++
        Write-Note "$step. Open $address/, choose Sign in with Windows (as a member of $AdminGroup), then Servers & scans, Add server."
        $step++
        Write-Note "$step. On each file server, give $ScanAccount read access to the shares (INSTALL.md, step 3), or share the detection test pack."
    }
    if ($firstBackup) {
        Write-Host ''
        Write-Host 'Save the backup key now.' -ForegroundColor Yellow
        Write-Note 'Backups can only be restored with this server''s backup key. Show it with:'
        Write-Note "  & '$(Join-Path $InstallPath 'setup\DataScope.Setup.exe')' backup-key --config '$ConfigFile'"
        Write-Note 'and keep a copy in your password manager, away from this server. (It is not written to this log.)'
    }
    Write-Note "Install log: $transcript"
} finally {
    Stop-Transcript | Out-Null
}
