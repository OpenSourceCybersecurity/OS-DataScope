#Requires -Version 7
<#
.SYNOPSIS
    Checks a Data Scope installation end to end through its HTTPS API, signed in with
    Windows accounts. Used by CI after Install-DataScope.ps1; also handy in a lab.
    Read-only except for enrolling -Server and running one scan.
#>
param(
    # A member of the Data Scope admin group.
    [Parameter(Mandatory)][pscredential]$Admin,
    # A valid Windows account in no Data Scope group.
    [pscredential]$NoRole,
    [int]$Port = 443,
    [string]$Server = $env:COMPUTERNAME,
    [string]$Share = 'DSLabFinance',
    # Folder behind -Share; its files must be unchanged by the scan.
    [string]$SharePath,
    # Skip enrolling and scanning; just check the earlier scan's results are still there.
    [switch]$AfterUpgrade
)
$ErrorActionPreference = 'Stop'
$base = "https://localhost:$Port"
$signedIn = @{ SkipCertificateCheck = $true; Credential = $Admin }
$change = @{ 'X-DataScope-Client' = 'lab-test' }
$failures = 0

function Check([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Host "ok   $What" } else { Write-Host "FAIL $What" -ForegroundColor Red; $script:failures++ }
}

# A5: everything a scan must not change, for the folder itself and everything under it: contents (SHA-256), size,
# created and modified times, attributes, owner, and the whole security descriptor (owner, group, every allow and
# deny entry, inheritance) as SDDL. Last-access times are left out: Windows itself may update them when a file is read.
function Snapshot([string]$Path) {
    @(Get-Item -LiteralPath $Path -Force) + @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes !ReparsePoint -ErrorAction SilentlyContinue) |
        ForEach-Object {
            $acl = Get-Acl -LiteralPath $_.FullName
            $hash = if ($_.PSIsContainer) { '' } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
            '{0}|{1}|{2}|{3:o}|{4:o}|{5}|{6}|{7}' -f $_.FullName, ($_.PSIsContainer ? '' : $_.Length), $hash,
                $_.CreationTimeUtc, $_.LastWriteTimeUtc, $_.Attributes, $acl.Owner, $acl.Sddl
        }
}

function ShareAccess([string]$Name) {
    Get-SmbShareAccess -Name $Name | ForEach-Object { '{0}|{1}|{2}' -f $_.AccountName, $_.AccessControlType, $_.AccessRight } | Sort-Object
}

$health = Invoke-WebRequest "$base/health" -SkipCertificateCheck
Check ($health.StatusCode -eq 200) 'Health check answers over HTTPS'

$anonymous = Invoke-WebRequest "$base/api/v1/me" -SkipCertificateCheck -SkipHttpErrorCheck
Check ($anonymous.StatusCode -eq 401) 'Anonymous API calls are refused'
Check (($anonymous.Headers['WWW-Authenticate'] -join ',') -match 'Negotiate') 'Server offers Windows sign-in'

$me = Invoke-RestMethod "$base/api/v1/me" @signedIn
Check ($me.roles -contains 'PlatformAdmin') "Signed in as $($me.name), roles: $($me.roles -join ', '), version $($me.version)"

if ($NoRole) {
    $nobody = @{ SkipCertificateCheck = $true; Credential = $NoRole; SkipHttpErrorCheck = $true }
    $who = Invoke-RestMethod "$base/api/v1/me" @nobody
    Check ($who.roles.Count -eq 0) "A user in no Data Scope group signs in with no roles ($($who.name))"
    $denied = Invoke-WebRequest "$base/api/v1/servers" -Method Post -ContentType 'application/json' -Headers $change -Body '{"hostname":"denied"}' @nobody
    Check ($denied.StatusCode -eq 403) 'That user cannot enroll servers'
}

$ui = Invoke-WebRequest "$base/" -SkipCertificateCheck
Check ($ui.Content -match 'id="root"') 'Web UI is served'
Check ([bool]$ui.Headers['Content-Security-Policy']) 'Security headers are set'

$forged = Invoke-WebRequest "$base/api/v1/servers" -Method Post -ContentType 'application/json' -Body '{"hostname":"forged"}' -SkipHttpErrorCheck @signedIn
# RawContent is always text; Content is bytes for application/problem+json.
Check ($forged.StatusCode -eq 403 -and $forged.RawContent -match 'X-DataScope-Client') "An admin change without the anti-forgery header is refused ($($forged.StatusCode))"

$hostname = $Server.ToLowerInvariant()
if (-not $AfterUpgrade) {
    $before = if ($SharePath) { Snapshot $SharePath } else { @() }
    $shareAccessBefore = if ($SharePath) { ShareAccess $Share } else { @() }
    $srv = Invoke-RestMethod "$base/api/v1/servers" -Method Post -ContentType 'application/json' -Headers $change -Body (@{ hostname = $Server } | ConvertTo-Json) @signedIn
    $job = Invoke-RestMethod "$base/api/v1/scan-jobs" -Method Post -ContentType 'application/json' -Headers $change -Body (@{ serverId = $srv.id } | ConvertTo-Json) @signedIn
    $deadline = (Get-Date).AddMinutes(4)
    do {
        Start-Sleep -Seconds 3
        $job = Invoke-RestMethod "$base/api/v1/scan-jobs/$($job.id)" @signedIn
    } while ($job.state -in 'Queued', 'Running' -and (Get-Date) -lt $deadline)
    Check ($job.state -eq 'Completed') "Scan finished: $($job.state) $($job.lastError)"
    if ($SharePath) {
        $after = Snapshot $SharePath
        $changed = @(Compare-Object $before $after)
        $changed | ForEach-Object { Write-Host "     $($_.SideIndicator) $($_.InputObject)" }
        Check (-not (Compare-Object $shareAccessBefore (ShareAccess $Share))) "Scan changed no share permission on $Share ($($shareAccessBefore.Count) entries)"
        Check ($before.Count -ge 8 -and $changed.Count -eq 0) "Scan changed no file or folder: contents, size, times, attributes, owner and permissions of $($before.Count) items compared"
    }
}

$srv = (Invoke-RestMethod "$base/api/v1/servers" @signedIn).items | Where-Object hostname -eq $hostname | Select-Object -First 1
Check ([bool]$srv) "Server $hostname is enrolled"
$shares = (Invoke-RestMethod "$base/api/v1/shares?serverId=$($srv.id)" @signedIn).items
$lab = $shares | Where-Object name -eq $Share | Select-Object -First 1
Check ([bool]$lab) "Share $Share was found"
if ($lab) {
    Check ($lab.state -eq 'Partial') "Share is Partial because one folder is denied (state: $($lab.state))"
    Check ($lab.fileCount -eq 5) "5 readable files inventoried (found $($lab.fileCount))"
    $gaps = (Invoke-RestMethod "$base/api/v1/shares/$($lab.id)/gaps" @signedIn).items
    Check (($gaps | Where-Object { $_.kind -eq 'AccessDenied' -and $_.path -like '*Restricted*' }).Count -eq 1) 'Denied folder is reported as a gap'
    Check (($gaps | Where-Object { $_.kind -eq 'ReparsePointSkipped' -and $_.path -like '*loop*' }).Count -eq 1) 'Junction loop is not followed and is reported'

    # Contents were read over SMB as the scan account. march.csv holds a made-up SSN; fy2026.xlsx is not a real workbook.
    $flagged = @((Invoke-RestMethod "$base/api/v1/classification/files?shareId=$($lab.id)&minSensitivity=Confidential" @signedIn).items)
    $march = $flagged | Where-Object name -eq 'march.csv' | Select-Object -First 1
    Check ($march -and $march.sensitivity -eq 'Restricted' -and $march.contentState -eq 'Scanned') "The synthetic SSN in march.csv is flagged Restricted ($($flagged.Count) flagged)"
    # The scanned form is an image: decoded by Windows and read by the bundled OCR in the reader process (D129).
    $scan = $flagged | Where-Object name -eq 'new-hire-scan.png' | Select-Object -First 1
    Check ($scan -and $scan.sensitivity -eq 'Restricted' -and $scan.contentState -eq 'Scanned') "The synthetic SSN in the scanned form new-hire-scan.png is read by OCR and flagged Restricted ($($scan.contentState): $($scan.contentNote))"
    Check ($flagged.Count -eq 2) 'Only march.csv and new-hire-scan.png are flagged'
    if ($march) {
        $detail = Invoke-RestMethod "$base/api/v1/files/$($march.id)/classification" @signedIn
        $evidence = ($detail.findings | ForEach-Object { $_.evidence }) -join ' '
        Check ($evidence -match '\*\*\*-\*\*-3307' -and $evidence -notmatch '512-48') 'Stored evidence is masked'
    }
    $unread = @((Invoke-RestMethod "$base/api/v1/classification/files?shareId=$($lab.id)&notRead=true" @signedIn).items)
    Check (@($unread | Where-Object { $_.name -eq 'fy2026.xlsx' -and $_.contentState -eq 'Unreadable' }).Count -eq 1) 'A damaged Office file is reported as not read, not as clean'

    # Permissions were read with Windows APIs as the scan account, read-only. NTFS permissions need only Read on the
    # folder; share permissions and local groups need administrator rights, so a plain scan account records a refusal.
    $access = Invoke-RestMethod "$base/api/v1/access/shares/$($lab.id)" @signedIn
    $states = ($access.folderPermissionStates.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', '
    Check ($access.folderPermissionStates.Read -ge 3) "Folder NTFS permissions were read ($states)"
    Check ($access.share.state -in 'Read', 'AccessDenied') "Share permission read is recorded ($($access.share.state))"
    if ($access.rootFolderId) {
        $root = Invoke-RestMethod "$base/api/v1/access/folders/$($access.rootFolderId)" @signedIn
        Check ($root.folder.state -eq 'Read' -and $root.folder.entries.Count -ge 1) "Share root NTFS entries parsed (state $($root.folder.state), $($root.folder.entries.Count) entries, owner $($root.folder.owner.name))"
        Check ($root.directory.localGroups -in 'Read', 'AccessDenied', 'Failed') "Local group read is recorded ($($root.directory.localGroups))"
    }
    if ($march) {
        $who = Invoke-RestMethod "$base/api/v1/files/$($march.id)/access" @signedIn
        Check ($who.openAccess.certainty -in 'Likely', 'Partial') "Who can open march.csv is computed ($($who.openAccess.certainty): any domain user $($who.openAccess.rights), up to $($who.openAccess.upTo))"
    }
}
Write-Host ('     shares seen: ' + (($shares | ForEach-Object { "$($_.name) ($($_.kind), $($_.state))" }) -join '; '))
$special = @($shares | Where-Object { $_.name.EndsWith('$') })
Check (@($special | Where-Object { $_.kind -ne 'Special' -or $_.state -ne 'NotScanned' }).Count -eq 0) "Hidden and admin shares are never scanned ($($special.Count) listed)"
$all = (Invoke-RestMethod "$base/api/v1/servers" @signedIn).items
Check (-not ($all | Where-Object hostname -eq 'forged')) 'The refused request enrolled nothing'

$directory = Invoke-RestMethod "$base/api/v1/directory" @signedIn
Check ($null -ne $directory) "Directory status answers (latest: $($directory.latest.domain ?? 'none'); last failure: $($directory.lastFailure.lastError ?? 'none'))"

$audit = Invoke-RestMethod "$base/api/v1/audit-log/verify" @signedIn
Check ($audit.valid) "Audit log chain is intact ($($audit.checked) entries)"

if ($failures -gt 0) { throw "$failures check(s) failed." }
Write-Host 'All checks passed.' -ForegroundColor Green
