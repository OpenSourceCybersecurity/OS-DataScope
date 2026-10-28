#Requires -Version 5.1
<#
.SYNOPSIS
    Prepares Active Directory for Data Scope: its admin group, and its scan account as a gMSA the Data Scope server
    may use (D139).

.DESCRIPTION
    Run once, as a domain admin, on a domain controller (or a computer with the AD PowerShell tools), after the
    Data Scope server has joined the domain. It only adds what is missing:
      * the group -AdminGroup (DataScope-Admins), with -AdminMembers in it. Its members can run Data Scope.
      * a KDS root key, which every gMSA needs, if the domain has none.
      * the gMSA -ScanAccount (svc-datascope), which only -DataScopeServer may use. The scanner reads file
        shares as this account; it is in no group and gets no rights here.
    It checks every name before changing anything, never changes or removes anything else in AD, and -WhatIf
    shows what it would do. Data Scope itself never changes AD; only this script, run by you, does.

    It ends by printing the exact install command for the Data Scope server.

.EXAMPLE
    .\Prepare-DataScopeDomain.ps1 -DataScopeServer ds01 -AdminMembers luke -Lab
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # The server Data Scope will be installed on, already joined to the domain.
    [Parameter(Mandatory)][string]$DataScopeServer,
    # Users (or groups) to put in the admin group, by their sign-in name.
    [string[]]$AdminMembers = @(),
    [string]$AdminGroup = 'DataScope-Admins',
    # The gMSA's name, without the trailing $. At most 15 characters.
    [ValidatePattern('^[A-Za-z0-9-]{1,15}$')][string]$ScanAccount = 'svc-datascope',
    # Lab only: a new KDS root key works straight away instead of after 10 hours. Use it with one domain controller.
    [switch]$Lab
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note([string]$Message) { Write-Host "    $Message" }

function Find-AdObject([scriptblock]$Get) {
    # The AD cmdlets throw when an identity is not found; $null reads better here.
    try { & $Get } catch [Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException] { $null }
}

if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
    throw "The Active Directory PowerShell module isn't installed here. Run this on a domain controller, or first run: Install-WindowsFeature RSAT-AD-PowerShell"
}
Import-Module ActiveDirectory -Verbose:$false
try { $domain = Get-ADDomain } catch { throw "Couldn't reach the domain: $($_.Exception.Message) Run this as a domain admin on a computer in the domain." }
$netbios = $domain.NetBIOSName
Write-Host "Preparing $($domain.DNSRoot) ($netbios) for Data Scope" -ForegroundColor White

# Everything is looked up first, so a typo stops the script before it changes anything.
Write-Step 'Checking the names'
$serverName = ($DataScopeServer -split '\.')[0]
$computer = Find-AdObject { Get-ADComputer -Identity $serverName }
if (-not $computer) { throw "There is no computer named $serverName in $($domain.DNSRoot). Join it to the domain first, then run this again." }
Write-Note "Data Scope server: $($computer.DNSHostName)"
$members = foreach ($name in $AdminMembers) {
    $found = Find-AdObject { Get-ADUser -Identity $name }
    if (-not $found) { $found = Find-AdObject { Get-ADGroup -Identity $name } }
    if (-not $found) { throw "There is no user or group named $name in $($domain.DNSRoot). Check the spelling: use the sign-in name, such as luke." }
    Write-Note "Admin: $netbios\$($found.SamAccountName)"
    $found
}
$group = Find-AdObject { Get-ADGroup -Identity $AdminGroup }
$gmsa = Find-AdObject { Get-ADServiceAccount -Identity $ScanAccount -Properties PrincipalsAllowedToRetrieveManagedPassword }
# -ScanAccount is letters, digits and dashes only, so it is safe in the filter.
if (-not $gmsa -and @(Get-ADObject -LDAPFilter "(sAMAccountName=$ScanAccount`$)").Count -gt 0) {
    throw "$netbios\$ScanAccount`$ already exists and is not a gMSA. Choose another -ScanAccount."
}

Write-Step "Admin group $netbios\$AdminGroup"
if ($group) {
    Write-Note 'Already there'
} elseif ($PSCmdlet.ShouldProcess("$netbios\$AdminGroup", 'Create the Data Scope admin group')) {
    $group = New-ADGroup -Name $AdminGroup -SamAccountName $AdminGroup -GroupScope Global -GroupCategory Security `
        -Description 'Members can run Data Scope (Platform admin). Made by Prepare-DataScopeDomain.ps1.' -PassThru
    Write-Note 'Created'
}
$current = if ($group) { @(Get-ADGroupMember -Identity $group | ForEach-Object { $_.SID.Value }) } else { @() }
if (@($members).Count -eq 0 -and $current.Count -eq 0) {
    Write-Note 'The group has no members yet, so nobody can sign in to Data Scope. Run this again with -AdminMembers <your sign-in name>.'
}
foreach ($member in $members) {
    if ($current -contains $member.SID.Value) { Write-Note "$($member.SamAccountName) is already a member"; continue }
    if ($PSCmdlet.ShouldProcess("$netbios\$AdminGroup", "Add $($member.SamAccountName)")) {
        Add-ADGroupMember -Identity $group -Members $member
        Write-Note "Added $($member.SamAccountName). They must sign out of Windows and back in before Data Scope lets them in."
    }
}

Write-Step 'KDS root key (gMSAs need one)'
if (-not (Get-Command Get-KdsRootKey -ErrorAction SilentlyContinue)) {
    throw "The KDS PowerShell commands aren't available here. Run this on a domain controller."
}
# On a domain with no key, Get-KdsRootKey writes a $null, which @() would count as a key.
$keys = @(Get-KdsRootKey | Where-Object { $null -ne $_ })
if ($keys.Count -gt 0) {
    $ready = $keys | Sort-Object EffectiveTime | Select-Object -First 1
    Write-Note "Already there, usable from $($ready.EffectiveTime)"
    if ($ready.EffectiveTime -gt (Get-Date)) { throw "The domain's KDS root key can't be used until $($ready.EffectiveTime). Run this again after that." }
} elseif ($Lab) {
    if ($PSCmdlet.ShouldProcess($domain.DNSRoot, 'Add a KDS root key usable straight away (lab only)')) {
        Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10)) | Out-Null
        Write-Note 'Added, usable straight away (-Lab)'
    }
} else {
    if ($PSCmdlet.ShouldProcess($domain.DNSRoot, 'Add a KDS root key')) {
        Add-KdsRootKey -EffectiveImmediately | Out-Null
    }
    throw 'Added a KDS root key. Domain controllers need up to 10 hours to share it, so run this script again after that to create the scan account. (In a lab with one domain controller, -Lab skips the wait.)'
}

Write-Step "Scan account $netbios\$ScanAccount`$ (a gMSA)"
if ($gmsa) {
    $allowed = @($gmsa.PrincipalsAllowedToRetrieveManagedPassword)
    if ($allowed -contains $computer.DistinguishedName) {
        Write-Note "Already there, and $serverName may use it"
    } elseif ($PSCmdlet.ShouldProcess("$netbios\$ScanAccount`$", "Let $serverName use it")) {
        Set-ADServiceAccount -Identity $gmsa -PrincipalsAllowedToRetrieveManagedPassword ($allowed + $computer.DistinguishedName)
        Write-Note "Already there; $serverName may now use it too"
    }
} elseif ($PSCmdlet.ShouldProcess("$netbios\$ScanAccount`$", "Create the gMSA, usable only by $serverName")) {
    New-ADServiceAccount -Name $ScanAccount -DNSHostName "$ScanAccount.$($domain.DNSRoot)" -PrincipalsAllowedToRetrieveManagedPassword $computer `
        -KerberosEncryptionType AES128, AES256 -Description 'Data Scope scanner: reads file shares, read-only. Made by Prepare-DataScopeDomain.ps1.'
    Write-Note "Created, usable only by $serverName. It is in no group and has no rights yet."
}

if ($WhatIfPreference) { return }
Write-Host ''
Write-Host 'The domain is ready for Data Scope.' -ForegroundColor Green
Write-Host ''
Write-Host 'Next, on each file server, let the scan account read the shares, for example:'
Write-Host "  Grant-SmbShareAccess -Name 'HR' -AccountName '$netbios\$ScanAccount`$' -AccessRight Read -Force"
Write-Host "  icacls 'C:\Shares\HR' /grant '$netbios\$ScanAccount`$:(OI)(CI)RX'"
Write-Host "or share the detection test pack: .\Install-DataScopeTestPack.ps1 -ScanAccount '$netbios\$ScanAccount`$'"
Write-Host ''
Write-Host "Then on $serverName, in the unzipped package, as administrator:"
Write-Host "  .\Install-DataScope.ps1 -ScanAccount '$netbios\$ScanAccount`$' -AdminGroup '$netbios\$AdminGroup'"
