#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Turns on the Windows auditing that Data Scope's File activity needs, on one file server.

.DESCRIPTION
    Run this yourself, as an administrator, on each file server whose activity you want in Data Scope. Data Scope
    itself never changes audit policy, folder auditing or group membership; this script is how you do it, and
    -WhatIf shows every change before it is made.

    It:
      1. Turns on the audit subcategories File System and File Share (success and failure), and Detailed File Share
         only with -DetailedFileShare. Subcategory settings are made to take precedence over the older category
         settings ("Audit: Force audit policy subcategory settings").
      2. Adds audit entries for Everyone to each folder: changes, deletes, permission and owner changes (success and
         failure), refused reads, and successful reads of files (not folder listings) unless -NoReads.
      3. Adds Data Scope's scan account to this server's Event Log Readers group (-ReaderAccount), so Data Scope can
         read the Security log remotely. Nothing else is granted.
      4. Turns on the built-in Remote Event Log Management firewall rules, unless -SkipFirewall.
      5. Makes the Security log at least -SecurityLogSizeMB big. It never makes it smaller and never changes what
         Windows does when it is full.

    -Check changes nothing and reports each of these. -Remove takes out the folder audit entries and the group
    membership this script added; it leaves audit policy and the firewall as they are and prints how to change them.

    In a domain, Group Policy can overwrite audit policy at its next refresh. If your domain sets audit policy, set
    Audit File System and Audit File Share under Advanced Audit Policy Configuration in a GPO instead; -Check shows
    what is in effect.

.PARAMETER Path
    Folders to audit, usually the folders behind your shares (for example D:\Shares\HR). By default every disk share
    on this server except hidden and administrative shares (names ending in $).

.PARAMETER ReaderAccount
    Data Scope's scan account, for example 'YOURDOMAIN\svc-datascope$'. Added to Event Log Readers on this server.

.PARAMETER NoReads
    Do not audit successful reads. Far fewer events; Data Scope then sees changes, deletes, permission changes and
    refused attempts, but not who opened files.

.PARAMETER DetailedFileShare
    Also audit every access check through a share (event 5145). Very noisy. Data Scope never counts these as reads.

.PARAMETER SecurityLogSizeMB
    Make the Security log at least this big, in MB (default 1024). 0 leaves it alone.

.PARAMETER SkipFirewall
    Leave the firewall alone (for example when your firewall is managed by Group Policy).

.PARAMETER Check
    Report the current state only. Exits with 1 when something Data Scope needs is missing.

.PARAMETER Remove
    Take out this script's folder audit entries and the -ReaderAccount group membership.

.EXAMPLE
    .\Enable-DataScopeAuditing.ps1 -ReaderAccount 'YOURDOMAIN\svc-datascope$' -WhatIf
    Shows what would change for every share on this server.

.EXAMPLE
    .\Enable-DataScopeAuditing.ps1 -Path D:\Shares\HR, D:\Shares\Finance -ReaderAccount 'YOURDOMAIN\svc-datascope$'

.EXAMPLE
    .\Enable-DataScopeAuditing.ps1 -Check -ReaderAccount 'YOURDOMAIN\svc-datascope$'
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Enable')]
param(
    [string[]]$Path,
    [string]$ReaderAccount,
    [Parameter(ParameterSetName = 'Enable')][switch]$NoReads,
    [Parameter(ParameterSetName = 'Enable')][switch]$DetailedFileShare,
    [Parameter(ParameterSetName = 'Enable')][ValidateRange(0, 65536)][int]$SecurityLogSizeMB = 1024,
    [Parameter(ParameterSetName = 'Enable')][switch]$SkipFirewall,
    [Parameter(ParameterSetName = 'Check', Mandatory = $true)][switch]$Check,
    [Parameter(ParameterSetName = 'Remove', Mandatory = $true)][switch]$Remove
)
$ErrorActionPreference = 'Stop'

# Audit subcategories by GUID, so the script works whatever the Windows display language.
$FileSystem = '{0CCE921D-69AE-11D9-BED3-505054503030}'
$FileShare = '{0CCE9224-69AE-11D9-BED3-505054503030}'
$DetailedShare = '{0CCE9244-69AE-11D9-BED3-505054503030}'
$EventLogReaders = 'S-1-5-32-573'
$Everyone = New-Object Security.Principal.SecurityIdentifier 'S-1-1-0'
$script:problems = 0

function Write-Check([string]$Level, [string]$Name, [string]$Detail) {
    $color = @{ OK = 'Green'; WARN = 'Yellow'; FAIL = 'Red'; DONE = 'Cyan'; SKIP = 'Gray' }[$Level]
    Write-Host ("    [{0,-4}] {1}: {2}" -f $Level, $Name, $Detail) -ForegroundColor $color
    if ($Level -eq 'FAIL') { $script:problems++ }
}

function Get-AuditSetting([string]$Guid) {
    $csv = & auditpol.exe /get /subcategory:$Guid /r
    if ($LASTEXITCODE -ne 0) { throw "auditpol could not read subcategory $Guid." }
    $row = $csv | Where-Object { $_ } | ConvertFrom-Csv | Select-Object -First 1
    # Columns: machine, target, subcategory, GUID, inclusion setting, exclusion setting.
    $values = @($row.PSObject.Properties | ForEach-Object { $_.Value })
    [pscustomobject]@{ Name = $values[2]; Setting = $values[4] }
}

function Test-AuditSetting([string]$Guid, [string]$Label, [bool]$Needed) {
    $current = Get-AuditSetting $Guid
    $both = $current.Setting -match 'Success' -and $current.Setting -match 'Failure'
    if ($both) { Write-Check OK $Label $current.Setting }
    elseif ($Needed) { Write-Check FAIL $Label "$($current.Setting) (needs Success and Failure)" }
    else { Write-Check SKIP $Label "$($current.Setting) (optional)" }
}

function Set-AuditSetting {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([string]$Guid, [string]$Label)
    if ($PSCmdlet.ShouldProcess("audit policy: $Label", 'Audit success and failure')) {
        & auditpol.exe /set /subcategory:$Guid /success:enable /failure:enable | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "auditpol could not turn on $Label." }
        Write-Check DONE $Label 'success and failure audited'
    }
}

function Get-DataScopeAuditRule([bool]$IncludeReads) {
    $changes = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $both = [Security.AccessControl.AuditFlags]'Success, Failure'
    $tree = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $rules = @(
        # Creates, writes, deletes, permission and owner changes, anywhere below the folder.
        (New-Object Security.AccessControl.FileSystemAuditRule $Everyone, $changes, $tree, 'None', $both),
        # Refused reads and folder listings.
        (New-Object Security.AccessControl.FileSystemAuditRule $Everyone, 'ReadData', $tree, 'None', 'Failure')
    )
    if ($IncludeReads) {
        # Successful reads of files only (inherit-only to files), so folder listings don't flood the log.
        $rules += New-Object Security.AccessControl.FileSystemAuditRule $Everyone, 'ReadData', 'ObjectInherit', 'InheritOnly', 'Success'
    }
    $rules
}

function Get-FolderAuditState([string]$Folder) {
    $sec = (Get-Item -LiteralPath $Folder).GetAccessControl([Security.AccessControl.AccessControlSections]::Audit)
    $rules = @($sec.GetAuditRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference -eq $Everyone })
    $has = {
        param($Right, $Flag)
        @($rules | Where-Object { ($_.FileSystemRights -band $Right) -eq $Right -and ($_.AuditFlags -band $Flag) -eq $Flag }).Count -gt 0
    }
    [pscustomobject]@{
        Changes = (& $has ([Security.AccessControl.FileSystemRights]'WriteData, Delete, ChangePermissions') ([Security.AccessControl.AuditFlags]::Success))
        Reads   = (& $has ([Security.AccessControl.FileSystemRights]::ReadData) ([Security.AccessControl.AuditFlags]::Success))
    }
}

function Resolve-Folder([string[]]$Requested) {
    if ($Requested) { $folders = $Requested }
    else {
        $folders = @(Get-SmbShare -Special $false | Where-Object { $_.Path -and -not $_.Name.EndsWith('$') -and $_.ShareType -eq 'FileSystemDirectory' } |
                ForEach-Object { $_.Path })
        if (-not $folders) { throw 'This server has no disk shares to audit. Name the folders with -Path.' }
    }
    foreach ($folder in $folders) {
        if ($folder -notmatch '^[A-Za-z]:\\') { throw "'$folder' is not a local folder on this server (use a path like D:\Shares\HR)." }
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "Folder '$folder' does not exist." }
    }
    $folders
}

function Get-EventLogReaderMember {
    # The comma keeps an empty group an empty list; without it the caller gets $null, which means "could not list".
    try { , @(Get-LocalGroupMember -SID $EventLogReaders | ForEach-Object { $_.SID.Value }) }
    catch { $null }
}

function Resolve-AccountSid([string]$Account) {
    try { (New-Object Security.Principal.NTAccount $Account).Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { throw "Account '$Account' was not found. Use DOMAIN\name, with a trailing `$ for a managed service account." }
}

$isDc = (Get-CimInstance Win32_ComputerSystem).DomainRole -ge 4
$inDomain = (Get-CimInstance Win32_ComputerSystem).PartOfDomain
$folders = Resolve-Folder $Path
$readerSid = if ($ReaderAccount) { Resolve-AccountSid $ReaderAccount } else { $null }

Write-Host "Data Scope auditing on $env:COMPUTERNAME" -ForegroundColor Cyan

if ($Check) {
    Test-AuditSetting $FileSystem 'Audit File System' $true
    Test-AuditSetting $FileShare 'Audit File Share' $true
    Test-AuditSetting $DetailedShare 'Audit Detailed File Share' $false
    $force = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name SCENoApplyLegacyAuditPolicy -ErrorAction SilentlyContinue).SCENoApplyLegacyAuditPolicy
    if ($force -eq 1) { Write-Check OK 'Subcategory settings' 'take precedence' } else { Write-Check WARN 'Subcategory settings' 'older category settings can override them' }
    foreach ($folder in $folders) {
        $state = Get-FolderAuditState $folder
        if ($state.Changes -and $state.Reads) { Write-Check OK $folder 'changes and reads audited' }
        elseif ($state.Changes) { Write-Check WARN $folder 'changes audited, successful reads not' }
        else { Write-Check FAIL $folder 'no audit entry for changes' }
    }
    if ($readerSid) {
        $members = Get-EventLogReaderMember
        if ($null -eq $members) { Write-Check WARN 'Event Log Readers' 'could not list members' }
        elseif ($members -contains $readerSid) { Write-Check OK 'Event Log Readers' "$ReaderAccount is a member" }
        else { Write-Check FAIL 'Event Log Readers' "$ReaderAccount is not a member" }
    }
    $rules = @(Get-NetFirewallRule -Name 'RemoteEventLogSvc-*' -ErrorAction SilentlyContinue)
    if (-not $rules) { Write-Check WARN 'Firewall' 'no Remote Event Log Management rules found' }
    elseif (@($rules | Where-Object { $_.Enabled -eq 'True' }).Count -eq $rules.Count) { Write-Check OK 'Firewall' 'Remote Event Log Management allowed' }
    else { Write-Check FAIL 'Firewall' 'Remote Event Log Management is not allowed in' }
    $log = Get-WinEvent -ListLog Security
    $size = [math]::Round($log.MaximumSizeInBytes / 1MB)
    if ($log.LogMode -ne 'Circular') { Write-Check WARN 'Security log' "$size MB, $($log.LogMode): when full, Windows does not overwrite old events" }
    elseif ($size -lt 512) { Write-Check WARN 'Security log' "$size MB; busy servers may overwrite events before Data Scope reads them" }
    else { Write-Check OK 'Security log' "$size MB, overwrites the oldest events when full" }
    if ($inDomain) { Write-Host '    Group Policy can change audit policy at its next refresh; this shows what is in effect now.' }
    if ($script:problems -gt 0) { Write-Host "$($script:problems) thing(s) Data Scope needs are missing." -ForegroundColor Red; exit 1 }
    Write-Host 'Everything Data Scope needs is in place.' -ForegroundColor Green
    exit 0
}

if ($Remove) {
    foreach ($folder in $folders) {
        if ($PSCmdlet.ShouldProcess($folder, 'Remove Data Scope audit entries')) {
            $item = Get-Item -LiteralPath $folder
            $sec = $item.GetAccessControl([Security.AccessControl.AccessControlSections]::Audit)
            foreach ($rule in Get-DataScopeAuditRule $true) { [void]$sec.RemoveAuditRule($rule) }
            $item.SetAccessControl($sec)
            Write-Check DONE $folder 'audit entries removed'
        }
    }
    if ($readerSid -and $PSCmdlet.ShouldProcess("Event Log Readers on $env:COMPUTERNAME", "Remove $ReaderAccount")) {
        try { Remove-LocalGroupMember -SID $EventLogReaders -Member $readerSid; Write-Check DONE 'Event Log Readers' "$ReaderAccount removed" }
        catch [Microsoft.PowerShell.Commands.MemberNotFoundException] { Write-Check SKIP 'Event Log Readers' "$ReaderAccount was not a member" }
    }
    Write-Host '    Audit policy and the firewall were left as they are. To stop auditing file access:'
    Write-Host "      auditpol /set /subcategory:$FileSystem /success:disable /failure:disable"
    Write-Host "      auditpol /set /subcategory:$FileShare /success:disable /failure:disable"
    exit 0
}

# Enable.
Set-AuditSetting $FileSystem 'Audit File System'
Set-AuditSetting $FileShare 'Audit File Share'
if ($DetailedFileShare) { Set-AuditSetting $DetailedShare 'Audit Detailed File Share' }
if ($PSCmdlet.ShouldProcess('Local security option', 'Make audit subcategory settings take precedence')) {
    Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name SCENoApplyLegacyAuditPolicy -Value 1 -Type DWord
    Write-Check DONE 'Subcategory settings' 'take precedence'
}

$rules = Get-DataScopeAuditRule (-not $NoReads)
foreach ($folder in $folders) {
    $what = if ($NoReads) { 'Audit changes, deletes and refused access' } else { 'Audit changes, deletes, reads and refused access' }
    if ($PSCmdlet.ShouldProcess($folder, $what)) {
        $item = Get-Item -LiteralPath $folder
        $sec = $item.GetAccessControl([Security.AccessControl.AccessControlSections]::Audit)
        foreach ($rule in $rules) { $sec.AddAuditRule($rule) }
        $item.SetAccessControl($sec)
        Write-Check DONE $folder $what.ToLowerInvariant()
    }
}

if ($readerSid) {
    if ($isDc) {
        Write-Check WARN 'Event Log Readers' "this is a domain controller: add $ReaderAccount to the domain's Builtin\Event Log Readers group in Active Directory instead"
    }
    elseif ((Get-EventLogReaderMember) -contains $readerSid) {
        Write-Check SKIP 'Event Log Readers' "$ReaderAccount is already a member"
    }
    elseif ($PSCmdlet.ShouldProcess("Event Log Readers on $env:COMPUTERNAME", "Add $ReaderAccount")) {
        try { Add-LocalGroupMember -SID $EventLogReaders -Member $readerSid; Write-Check DONE 'Event Log Readers' "$ReaderAccount added" }
        catch [Microsoft.PowerShell.Commands.MemberExistsException] { Write-Check SKIP 'Event Log Readers' "$ReaderAccount is already a member" }
    }
}
else {
    Write-Check WARN 'Event Log Readers' 'no -ReaderAccount given; Data Scope needs its scan account in this group to read the log'
}

if (-not $SkipFirewall) {
    $fw = @(Get-NetFirewallRule -Name 'RemoteEventLogSvc-*' -ErrorAction SilentlyContinue)
    if (-not $fw) { Write-Check WARN 'Firewall' 'no Remote Event Log Management rules found; allow RPC to the event log service yourself' }
    elseif ($PSCmdlet.ShouldProcess('Remote Event Log Management firewall rules', 'Enable')) {
        $fw | Enable-NetFirewallRule
        Write-Check DONE 'Firewall' 'Remote Event Log Management allowed (consider limiting it to the Data Scope server: Set-NetFirewallRule -Name RemoteEventLogSvc-* -RemoteAddress <address>)'
    }
}

if ($SecurityLogSizeMB -gt 0) {
    $log = Get-WinEvent -ListLog Security
    $wanted = [int64]$SecurityLogSizeMB * 1MB
    if ($log.MaximumSizeInBytes -ge $wanted) { Write-Check SKIP 'Security log' "already $([math]::Round($log.MaximumSizeInBytes / 1MB)) MB" }
    elseif ($PSCmdlet.ShouldProcess('Security log', "Set maximum size to $SecurityLogSizeMB MB")) {
        & wevtutil.exe sl Security /ms:$wanted
        if ($LASTEXITCODE -ne 0) { throw 'wevtutil could not change the Security log size.' }
        Write-Check DONE 'Security log' "maximum size $SecurityLogSizeMB MB"
    }
    if ($log.LogMode -ne 'Circular') { Write-Check WARN 'Security log' "mode is $($log.LogMode): when full, Windows does not overwrite old events. Data Scope leaves this to you." }
}

if ($inDomain) {
    Write-Host '    In a domain, Group Policy can overwrite audit policy at its next refresh. Run this script with -Check afterwards,'
    Write-Host '    and if your domain sets audit policy, set it in a GPO under Advanced Audit Policy Configuration instead.'
}
Write-Host "Next: in Data Scope, open File activity and turn collection on for $env:COMPUTERNAME." -ForegroundColor Green
