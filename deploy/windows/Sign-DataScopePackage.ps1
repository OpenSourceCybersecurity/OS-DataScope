#Requires -Version 5.1
<#
.SYNOPSIS
    Code-signs an unzipped Data Scope package with an Authenticode certificate, then re-zips it.

.DESCRIPTION
    Release step, run on a Windows build machine that holds the code signing certificate.
    It signs Data Scope's own programs (app\*\DataScope.*.exe and .dll), the setup wizard
    (DataScope-Setup.exe) and the install scripts, rewrites SHA256SUMS to match the signed files,
    and writes <package>.zip and <package>.zip.sha256 next to the folder. Then it writes the
    one-file installer DataScope-Setup-<version>.exe next to them (the wizard with the signed zip
    inside, D169) and signs it too, with signed copies of Prepare-DataScopeDomain.ps1 and
    Enable-DataScopeAuditing.ps1 beside it, the scripts customers run on other servers (D171).

    Third-party files (the .NET runtime, Npgsql and the rest) keep their publishers'
    own signatures and are not re-signed.

    The installer checks the signatures: every Data Scope program and script must be
    validly signed by the same certificate as Install-DataScope.ps1 itself.

    Nothing is uploaded anywhere, except that -TimestampServer, when given, sends each
    file's hash (not the file) to that time-stamping service.

.EXAMPLE
    .\Sign-DataScopePackage.ps1 -PackagePath C:\Build\DataScope-1.0.0 -CertificateThumbprint 0123...ABCD -TimestampServer http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    # A code signing certificate with its private key, in CurrentUser\My or LocalMachine\My.
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    # RFC 3161 time-stamping URL. Without it, signatures stop validating when the certificate expires.
    [string]$TimestampServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path.TrimEnd('\')
if (-not (Test-Path (Join-Path $PackagePath 'SHA256SUMS'))) { throw "$PackagePath is not an unzipped Data Scope package (no SHA256SUMS)." }

$clean = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
$cert = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert | Where-Object Thumbprint -eq $clean) | Select-Object -First 1
if (-not $cert) { throw "No code signing certificate $clean with a private key in CurrentUser\My or LocalMachine\My." }
if ($cert.NotAfter -lt (Get-Date)) { throw "The certificate expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
Write-Host "==> Signing with $($cert.Subject) (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Cyan

# Filtered by name, not -Include: Windows PowerShell 5.1 ignores -Include with -LiteralPath and returns every file.
$files = @(Get-ChildItem -LiteralPath (Join-Path $PackagePath 'app') -Recurse -File |
    Where-Object { $_.Name -match '^DataScope\..+\.(exe|dll)$' }) +
    @(Get-ChildItem -LiteralPath $PackagePath -File -Filter '*.ps1')
# The one-file installer is built from the wizard before it is signed: a signature must be the last thing in a file.
$wizard = Join-Path $PackagePath 'DataScope-Setup.exe'
$unsignedWizard = $null
if (Test-Path -LiteralPath $wizard) {
    if ((Get-AuthenticodeSignature -LiteralPath $wizard).Status -ne 'NotSigned') { throw "$wizard is already signed. Sign a freshly unzipped package." }
    $unsignedWizard = Join-Path ([IO.Path]::GetTempPath()) ('DataScope-Setup-{0}.exe' -f [guid]::NewGuid())
    Copy-Item -LiteralPath $wizard -Destination $unsignedWizard
    $files += Get-Item -LiteralPath $wizard
}
$options = @{ Certificate = $cert; HashAlgorithm = 'SHA256' }
if ($TimestampServer) { $options.TimestampServer = $TimestampServer }
foreach ($file in $files) {
    $result = Set-AuthenticodeSignature -LiteralPath $file.FullName @options
    if ($result.Status -ne 'Valid') {
        throw "Signing $($file.Name) failed: $($result.Status) $($result.StatusMessage)"
    }
}
Write-Host "    Signed $($files.Count) files"

Write-Host '==> Rewriting SHA256SUMS' -ForegroundColor Cyan
$appFiles = Get-ChildItem -LiteralPath (Join-Path $PackagePath 'app') -Recurse -File |
    ForEach-Object { $_.FullName.Substring($PackagePath.Length + 1).Replace('\', '/') } |
    Sort-Object { $_ } -Culture ([Globalization.CultureInfo]::InvariantCulture)
$lines = foreach ($relative in $appFiles) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $PackagePath $relative) -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
[IO.File]::WriteAllText((Join-Path $PackagePath 'SHA256SUMS'), (($lines -join "`n") + "`n"))

Write-Host '==> Zipping' -ForegroundColor Cyan
$zip = "$PackagePath.zip"
Remove-Item -LiteralPath $zip, "$zip.sha256" -ErrorAction SilentlyContinue
Compress-Archive -Path $PackagePath -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n")
Write-Host "Signed package: $zip" -ForegroundColor Green

if ($unsignedWizard) {
    # [wizard][zip][zip length, 8 bytes little-endian]["DSSETUP1"], as scripts/make-setup.mjs writes it.
    Write-Host '==> Writing and signing the one-file installer' -ForegroundColor Cyan
    $version = "$(Get-Content -LiteralPath (Join-Path $PackagePath 'VERSION') | Select-Object -First 1)".Trim()
    $setup = Join-Path (Split-Path $PackagePath) "DataScope-Setup-$version.exe"
    $output = [IO.File]::Create($setup)
    try {
        foreach ($part in $unsignedWizard, $zip) {
            $reader = [IO.File]::OpenRead($part)
            try { $reader.CopyTo($output) } finally { $reader.Dispose() }
        }
        $output.Write([BitConverter]::GetBytes([long](Get-Item -LiteralPath $zip).Length), 0, 8)
        $output.Write([Text.Encoding]::ASCII.GetBytes('DSSETUP1'), 0, 8)
    } finally {
        $output.Dispose()
        Remove-Item -LiteralPath $unsignedWizard -ErrorAction SilentlyContinue
    }
    $result = Set-AuthenticodeSignature -LiteralPath $setup @options
    if ($result.Status -ne 'Valid') { throw "Signing $(Split-Path $setup -Leaf) failed: $($result.Status) $($result.StatusMessage)" }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$setup.sha256", "$hash  $(Split-Path $setup -Leaf)`n")
    foreach ($name in 'Prepare-DataScopeDomain.ps1', 'Enable-DataScopeAuditing.ps1') {
        Copy-Item -LiteralPath (Join-Path $PackagePath $name) -Destination (Split-Path $setup) -Force
    }
    Write-Host "Signed installer: $setup" -ForegroundColor Green
}
