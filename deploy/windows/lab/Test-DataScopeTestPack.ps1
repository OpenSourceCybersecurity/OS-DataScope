<#
.SYNOPSIS
    Checks what Data Scope found in the detection test pack against expected.csv, file by file (D138).

.DESCRIPTION
    Reads Data Scope's results through its HTTPS API, signed in with Windows. Read-only, except that -StartScan
    queues one scan of -Server first. Prints one line per file (ok or FAIL, with what differed), a summary, and
    writes every result to -OutFile so it can be shared. Exits with 1 if any check failed.

    Works in Windows PowerShell 5.1 and PowerShell 7. The account you run it as (or -Credential) needs a Data Scope
    role that can see results: Platform admin, Auditor or Viewer.

.EXAMPLE
    .\Test-DataScopeTestPack.ps1 -Url https://ds01.yourdomain.local -Server fs01 -StartScan
#>
[CmdletBinding()]
param(
    # Data Scope's address, as you open it in the browser.
    [string]$Url = 'https://localhost',
    # The file server that holds the pack's share, as it is enrolled in Data Scope.
    [Parameter(Mandatory)][string]$Server,
    [string]$Share = 'TestPack',
    # Sign in as this account instead of the one running the script.
    [pscredential]$Credential,
    # For Data Scope's self-signed certificate when this computer does not trust it yet.
    [switch]$SkipCertificateCheck,
    # Queue a scan of -Server and wait for it to finish before checking.
    [switch]$StartScan,
    [int]$ScanTimeoutMinutes = 30,
    [string]$Expected = (Join-Path $PSScriptRoot 'expected.csv'),
    [string]$OutFile = (Join-Path $PSScriptRoot 'test-pack-results.csv')
)
$ErrorActionPreference = 'Stop'
$base = $Url.TrimEnd('/')
$modern = $PSVersionTable.PSVersion.Major -ge 7

if (-not $modern) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if ($SkipCertificateCheck) {
        # A PowerShell script block can't be the callback: .NET calls it on a thread with no runspace, and the
        # connection fails with "An unexpected error occurred on a send". So the callback is compiled.
        if (-not ('DataScopeTestPackTrust' -as [type])) {
            Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class DataScopeTestPackTrust
{
    private static bool Any(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) { return true; }
    public static void Install() { ServicePointManager.ServerCertificateValidationCallback = Any; }
}
'@
        }
        [DataScopeTestPackTrust]::Install()
    }
}

# How every call signs in and trusts the server.
$connection = @{}
if ($Credential) { $connection.Credential = $Credential } else { $connection.UseDefaultCredentials = $true }
if ($modern -and $SkipCertificateCheck) { $connection.SkipCertificateCheck = $true }

function Invoke-Api([string]$Path, [string]$Method = 'Get', $Body) {
    $call = @{ Uri = "$base$Path"; Method = $Method; Headers = @{ 'X-DataScope-Client' = 'test-pack' } } + $connection
    if ($null -ne $Body) { $call.ContentType = 'application/json'; $call.Body = ($Body | ConvertTo-Json -Compress) }
    Invoke-RestMethod @call
}

function Get-All([string]$Path) {
    $items = @()
    $offset = 0
    do {
        $sep = if ($Path.Contains('?')) { '&' } else { '?' }
        $page = Invoke-Api "$Path${sep}limit=200&offset=$offset"
        $items += @($page.items)
        $offset += 200
    } while ($offset -lt $page.total)
    $items
}

$rows = @(Import-Csv -Path $Expected)
if ($rows.Count -eq 0) { throw "No expected results in $Expected." }

$me = Invoke-Api '/api/v1/me'
Write-Host "Signed in to $base as $($me.name) ($($me.roles -join ', '); Data Scope $($me.version))"

$hostname = $Server.ToLowerInvariant()
$srv = @(Get-All '/api/v1/servers') | Where-Object { $_.hostname -eq $hostname -or $_.hostname -like "$hostname.*" } | Select-Object -First 1
if (-not $srv) { throw "Server $Server is not enrolled in Data Scope. Add it on Servers & scans first." }

if ($StartScan) {
    $job = Invoke-Api '/api/v1/scan-jobs' 'Post' @{ serverId = $srv.id }
    Write-Host "Scan queued for $($srv.hostname); waiting for it to finish (up to $ScanTimeoutMinutes minutes)"
    $deadline = (Get-Date).AddMinutes($ScanTimeoutMinutes)
    do {
        Start-Sleep -Seconds 5
        $job = Invoke-Api "/api/v1/scan-jobs/$($job.id)"
    } while ($job.state -in 'Queued', 'Running' -and (Get-Date) -lt $deadline)
    if ($job.state -ne 'Completed') { throw "The scan did not complete: $($job.state) $($job.lastError)" }
    Write-Host 'Scan completed'
}

$packShare = @(Get-All "/api/v1/shares?serverId=$($srv.id)") | Where-Object { $_.name -eq $Share } | Select-Object -First 1
if (-not $packShare) { throw "Share $Share was not found on $($srv.hostname). Scan the server after running Install-DataScopeTestPack.ps1." }

$found = @{}
foreach ($f in @(Get-All "/api/v1/classification/files?shareId=$($packShare.id)")) { $found[$f.path.ToLowerInvariant()] = $f }
$gaps = @(Get-All "/api/v1/shares/$($packShare.id)/gaps")
Write-Host "Share $Share ($($packShare.state)): $($found.Count) files, $($gaps.Count) gaps"
Write-Host ''

$rank = @{ low = 1; medium = 2; high = 3 }
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$File, [string[]]$Problems, [string]$Actual, [string]$Tests) {
    $ok = $Problems.Count -eq 0
    $results.Add([pscustomobject]@{ File = $File; Result = $(if ($ok) { 'ok' } else { 'FAIL' }); Problems = ($Problems -join ' '); Actual = $Actual; Tests = $Tests })
    if ($ok) { Write-Host "ok   $File" } else { Write-Host "FAIL $File - $($Problems -join ' ')" -ForegroundColor Red }
}

function Format-Actual($f) {
    $categories = @($f.categories | ForEach-Object { "$($_.category) $($_.confidence)" }) -join ', '
    "Label $($f.policySensitivity), read $($f.contentState)$(if ($f.contentNote) { " ($($f.contentNote))" }); found: $(if ($categories) { $categories } else { 'nothing' })"
}

foreach ($row in $rows) {
    $key = $row.File.Replace('/', '\').ToLowerInvariant()
    $f = $found[$key]
    if (-not $f) {
        Add-Result $row.File @('Not in Data Scope''s inventory of the share.') '' $row.Tests
        continue
    }
    $problems = @()
    if ($f.contentState -ne $row.Read) { $problems += "Expected to be read as $($row.Read), was $($f.contentState)." }
    if ($f.policySensitivity -ne $row.Label) { $problems += "Expected label $($row.Label), was $($f.policySensitivity)." }
    foreach ($want in @($row.MustFind -split ';' | Where-Object { $_ })) {
        $name, $least = $want -split '@'
        if (-not $least) { $least = 'Medium' }
        $hit = @($f.categories | Where-Object { $_.category -eq $name -and $rank[$_.confidence] -ge $rank[$least.ToLowerInvariant()] })
        if ($hit.Count -eq 0) { $problems += "Expected $name at $least or higher." }
    }
    $banned = @($row.MustNotFind -split ';' | Where-Object { $_ })
    foreach ($c in @($f.categories)) {
        if ($rank[$c.confidence] -ge 2 -and ($row.MustNotFind -eq 'any' -or $banned -contains $c.category)) {
            $problems += "Did not expect $($c.category) at $($c.confidence)."
        }
    }
    Add-Result $row.File $problems (Format-Actual $f) $row.Tests
}

# The cases Install-DataScopeTestPack.ps1 makes on the server itself.
$offline = $found['7-lab-only\offline-file.txt']
if ($offline) {
    $problems = @(if ($offline.contentState -ne 'Offline') { "Expected to be left unread as Offline, was $($offline.contentState)." })
    Add-Result '7-Lab-only/offline-file.txt' $problems (Format-Actual $offline) 'A file with the Offline attribute: never opened, so never recalled from archive storage'
}
else {
    Add-Result '7-Lab-only/offline-file.txt' @('Not in the inventory. Was Install-DataScopeTestPack.ps1 run?') '' 'A file with the Offline attribute'
}
$deniedGap = @($gaps | Where-Object { $_.kind -eq 'AccessDenied' -and $_.path -like '*7-Lab-only\Denied*' })
$deniedFile = $found['7-lab-only\denied\secret.txt']
Add-Result '7-Lab-only/Denied/' @($(if ($deniedGap.Count -eq 0) { 'Expected an Access denied gap.' }), $(if ($deniedFile) { 'A file in the denied folder was listed.' }) | Where-Object { $_ }) `
    "$($deniedGap.Count) gap(s)" 'A folder the scan account is refused: reported as a gap, not skipped silently'
$loopGap = @($gaps | Where-Object { $_.kind -eq 'ReparsePointSkipped' -and $_.path -like '*7-Lab-only\loop*' })
Add-Result '7-Lab-only/loop' @($(if ($loopGap.Count -eq 0) { 'Expected a Link not followed gap.' }) | Where-Object { $_ }) "$($loopGap.Count) gap(s)" 'A junction pointing back up the tree: reported and not followed'
$long = $found.Keys | Where-Object { $_ -like '7-lab-only\long-path\*\ssn.txt' } | Select-Object -First 1
$longGap = @($gaps | Where-Object { $_.kind -eq 'PathTooLong' -and $_.path -like '*7-Lab-only\long-path*' })
if ($long) {
    $f = $found[$long]
    Add-Result '7-Lab-only/long-path/.../ssn.txt' @(if ($f.policySensitivity -ne 'Restricted') { "Expected label Restricted, was $($f.policySensitivity)." }) (Format-Actual $f) 'A path over 260 characters: read like any other file'
}
else {
    Add-Result '7-Lab-only/long-path/.../ssn.txt' @($(if ($longGap.Count -eq 0) { 'Neither listed nor reported as a Path too long gap.' }) | Where-Object { $_ }) "$($longGap.Count) gap(s)" 'A path over 260 characters: read, or reported as Path too long, never skipped silently'
}

$known = @($results | Where-Object { $_.Tests -like '*Known gap*' }).Count
$extra = @($found.Keys | Where-Object { -not $_.StartsWith('7-lab-only\') -and -not ($rows | Where-Object File -eq $_.Replace('\', '/')) })
$failed = @($results | Where-Object Result -eq 'FAIL')
$results | Export-Csv -Path $OutFile -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host "$($results.Count - $failed.Count) of $($results.Count) checks passed. $known known gap(s) are described in the Tests column. Results: $OutFile"
if ($extra.Count -gt 0) { Write-Host "Files in the share that are not part of the pack (not checked): $($extra.Count)" }
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'Every check passed.' -ForegroundColor Green
# Set the exit code callers check: without this a fresh session's $LASTEXITCODE stays empty.
exit 0
