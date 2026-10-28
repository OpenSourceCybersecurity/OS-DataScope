#Requires -Version 7
<#
.SYNOPSIS
    CI only: checks File activity end to end on a throwaway Windows runner. Expects Enable-DataScopeAuditing.ps1 to
    have audited -SharePath and added the scan account to Event Log Readers. Creates, reads and deletes a file through
    the share, turns collection on, and checks that Data Scope read the events from the Security log as the non-admin
    scan account, matched them to the share, and kept what each proves.
#>
param(
    [Parameter(Mandatory)][pscredential]$Admin,
    [int]$Port = 443,
    [string]$Server = $env:COMPUTERNAME,
    [string]$Share = 'DSLabFinance',
    [Parameter(Mandatory)][string]$SharePath,
    [string]$ScanAccount = 'dsscan'
)
$ErrorActionPreference = 'Stop'
$base = "https://localhost:$Port"
$signedIn = @{ SkipCertificateCheck = $true; Credential = $Admin }
$change = @{ 'X-DataScope-Client' = 'lab-test' }
$failures = 0

function Check([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Host "ok   $What" } else { Write-Host "FAIL $What" -ForegroundColor Red; $script:failures++ }
}

# The scanner reads the log as the scan account, which is not an administrator.
$service = Get-CimInstance Win32_Service -Filter "Name = 'DataScopeScanner'"
$adminGroup = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value.Split('\')[-1]
$admins = @(([adsi]"WinNT://./$adminGroup,group").psbase.Invoke('Members') | ForEach-Object { $_.GetType().InvokeMember('Name', 'GetProperty', $null, $_, $null) })
Check ($service.StartName -like "*\$ScanAccount") "The scanner runs as $($service.StartName)"
Check ($admins.Count -gt 0 -and $admins -notcontains $ScanAccount) "$ScanAccount is not an administrator ($($admins -join ', '))"

# Activity on the share's files: write, read, change permissions, then delete through the share. On the runner,
# Windows logged deletes made through \\localhost but not reads or writes made through it (seen in CI; the cause is
# not confirmed), so the write and read are made on the server's own path, as a local program would, and still
# belong to the share by its folder. Reads and writes through a share from another computer are lab check W41.
$unc = "\\localhost\$Share"
$name = "activity-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$local = Join-Path $SharePath "$name.txt"
$started = Get-Date
Set-Content -LiteralPath $local -Value 'synthetic activity test'
Set-Content -LiteralPath (Join-Path $SharePath "$name-read.txt") -Value 'synthetic file to read'
$stream = [IO.FileStream]::new($local, [IO.FileMode]::Append, [IO.FileAccess]::Write)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes("second line`r`n")
    $stream.Write($bytes, 0, $bytes.Length)
} finally { $stream.Dispose() }
$null = [IO.File]::ReadAllBytes((Join-Path $SharePath "$name-read.txt"))
& icacls.exe $local /grant "${ScanAccount}:(R)" /Q | Out-Null
Remove-Item -LiteralPath "$unc\$name.txt"
Write-Host "     made activity on $name.txt and $name-read.txt"

$srv = (Invoke-RestMethod "$base/api/v1/servers" @signedIn).items | Where-Object hostname -eq $Server.ToLowerInvariant() | Select-Object -First 1
Check ([bool]$srv) "Server $Server is enrolled"
Invoke-RestMethod "$base/api/v1/activity/sources/$($srv.id)" -Method Put -ContentType 'application/json' -Headers $change -Body '{"enabled":true}' @signedIn | Out-Null

$deadline = (Get-Date).AddMinutes(5)
do {
    Start-Sleep -Seconds 5
    $source = (Invoke-RestMethod "$base/api/v1/activity/sources" @signedIn) | Where-Object serverId -eq $srv.id
    $events = @((Invoke-RestMethod "$base/api/v1/activity/events?search=$name&limit=200" @signedIn).items)
    # The events become visible before the pass that stored them marks the source Ok, so wait for both.
    $done = $source.state -eq 'Error' -or ($source.state -eq 'Ok' -and @($events | Where-Object operation -in 'Deleted', 'DeleteOrRename').Count -gt 0 -and @($events | Where-Object operation -eq 'Read').Count -gt 0)
} while (-not $done -and (Get-Date) -lt $deadline)

Check ($source.state -eq 'Ok') "Collection is Ok ($($source.state): $($source.lastError))"
Check ($source.log -and $source.log.recordCount -gt 0) "Security log health was read ($($source.log.recordCount) records, oldest $($source.log.oldestEventAt))"
Write-Host "     $($events.Count) events about $name.txt:"
$events | Sort-Object recordId | ForEach-Object { Write-Host "       $($_.eventId) #$($_.recordId) $($_.operation) $($_.certainty) $($_.outcome) $($_.principalName) $($_.shareName)\$($_.relativePath)" }

$mapping = $source.shares | Where-Object name -eq $Share
Check ($mapping.localPath -eq $SharePath -and $mapping.localPathSource -eq 'Event') "The share's folder was learned from share events ($($mapping.localPath), $($mapping.localPathSource))"
$write = $events | Where-Object { $_.operation -eq 'Write' -and $_.certainty -eq 'Performed' -and $_.outcome -eq 'Success' -and $_.relativePath -eq "$name.txt" } | Select-Object -First 1
Check ([bool]$write) 'The write was recorded'
Check ($write.shareName -eq $Share -and $write.relativePath -eq "$name.txt") "It is matched to the share ($($write.shareName)\$($write.relativePath))"
Check ($write.principalName -like "*\$env:USERNAME") "It names who did it ($($write.principalName))"
Check (@($events | Where-Object { $_.operation -eq 'Read' -and $_.certainty -eq 'Performed' -and $_.relativePath -eq "$name-read.txt" }).Count -gt 0) 'The read was recorded'
$deleted = @($events | Where-Object { $_.operation -eq 'Deleted' -and $_.pathSource -eq 'Correlated' })
$deleteRequest = @($events | Where-Object operation -eq 'DeleteOrRename')
Check ($deleted.Count -gt 0 -or $deleteRequest.Count -gt 0) "The delete was recorded ($($deleted.Count) with the path from the same handle, $($deleteRequest.Count) delete-or-rename)"
$perm = @($events | Where-Object operation -eq 'PermissionChange')
if ($perm) { Write-Host "ok   The permission change was recorded" } else { Write-Host '     note: no 4670 for the permission change (Windows logs it only with policy change auditing)' }

# Access checks stay out of the default view and are never reads.
$checks = @((Invoke-RestMethod "$base/api/v1/activity/events?includeChecks=true&operation=ShareConnect&limit=50" @signedIn).items)
Check ($checks.Count -gt 0 -and @($checks | Where-Object certainty -ne 'ShareSession').Count -eq 0) "Share connections are kept as connections ($($checks.Count))"
Check (@($events | Where-Object certainty -in 'AccessCheck', 'ShareSession').Count -eq 0) 'The default view has no access checks or connections'

if ($events) {
    $detail = Invoke-RestMethod "$base/api/v1/activity/events/$($events[0].id)" @signedIn
    Check ($detail.rawXml -match '<EventRecordID>') 'The event is kept as Windows recorded it'
}

if ($failures -gt 0) {
    # What Windows itself logged about the files, to tell a collection fault from an auditing one.
    Write-Host '     Security log events naming the test files:'
    Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4656, 4658, 4660, 4663, 4670, 5145; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match [regex]::Escape($name) } | Sort-Object RecordId |
        ForEach-Object {
            $xml = [xml]$_.ToXml()
            $data = @{}; $xml.Event.EventData.Data | ForEach-Object { $data[$_.Name] = $_.'#text' }
            Write-Host "       $($_.Id) #$($_.RecordId) $($data.SubjectUserName) $($data.ObjectName)$($data.RelativeTargetName) mask $($data.AccessMask) $($data.AccessList)"
        }
    throw "$failures check(s) failed."
}
Write-Host 'All file activity checks passed.' -ForegroundColor Green
