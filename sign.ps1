param([string]$CertThumbprint = '', [string]$TimestampUrl = 'http://timestamp.digicert.com', [switch]$Check)
# Code signing for a public release of the Lord of the Rings God Mode Cheat.
#
#   sign.ps1 -Check                         list the code-signing certificates in your certificate store and say
#                                           whether signtool.exe is present. Changes nothing.
#   sign.ps1 -CertThumbprint <thumbprint>   build a release (build.ps1 -Release -Sign): the trainer is signed first, the
#                                           installer is then built around the SIGNED trainer and signed too, the portable
#                                           ZIP gets the signed trainer, and every signature is verified at the end.
#
# What you need (this script never creates, downloads or installs any of it):
#   1. A code-signing certificate issued to you or your company by a certificate authority (or a signing service such
#      as Azure Trusted Signing). A self-signed certificate is NOT trusted by Windows or by antivirus products.
#   2. signtool.exe - part of the free Windows SDK ("Windows SDK Signing Tools for Desktop Apps").
#   3. InstallInfo.Publisher in src\Shared\InstallInfo.cs set to the name on the certificate.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
$certs = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' -and $_.NotAfter -gt (Get-Date) })

if ($Check -or -not $CertThumbprint) {
    if ($tool) { "signtool: $($tool.FullName)" } else { 'signtool: NOT FOUND (install the Windows SDK signing tools yourself)' }
    if ($certs.Count -eq 0) { 'code-signing certificates: none in your certificate store' }
    else {
        'code-signing certificates:'
        foreach ($c in $certs) {
            $self = if ($c.Subject -eq $c.Issuer) { '   <- SELF-SIGNED: not trusted by Windows or antivirus products' } else { '' }
            '  {0}  {1}  (valid until {2:yyyy-MM-dd}){3}' -f $c.Thumbprint, $c.Subject, $c.NotAfter, $self
        }
    }
    if (-not $CertThumbprint) { ''; 'Nothing was signed. To sign:  sign.ps1 -CertThumbprint <thumbprint from the list above>' }
    return
}

if (-not $tool) { throw 'signtool.exe was not found. Install the Windows SDK signing tools first; nothing is downloaded by this script.' }
$cert = $certs | Where-Object { $_.Thumbprint -eq ($CertThumbprint -replace '\s', '') } | Select-Object -First 1
if (-not $cert) { throw "No valid code-signing certificate with thumbprint $CertThumbprint in your certificate store. Run  sign.ps1 -Check  to list them." }
if ($cert.Subject -eq $cert.Issuer) { Write-Warning 'This certificate is self-signed. The files will carry a signature, but Windows and antivirus products will not trust it.' }

& "$root\build.ps1" -Release -Sign -CertThumbprint $cert.Thumbprint -TimestampUrl $TimestampUrl

''
'Verifying the signatures:'
$bad = 0
foreach ($f in "$root\build\LordOfTheRingsGodModeCheat.exe", "$root\dist\LordOfTheRingsGodModeCheat-Setup.exe") {
    $s = Get-AuthenticodeSignature $f
    '  {0}: {1}  ({2})' -f (Split-Path $f -Leaf), $s.Status, $s.SignerCertificate.Subject
    if ($s.Status -ne 'Valid') { $bad++ }
}
if ($bad -gt 0) { throw "$bad file(s) do not have a valid, trusted signature - do not publish them as signed." }
'All signatures are valid. The files to publish are in dist\ (compare with dist\SHA256SUMS.txt).'
