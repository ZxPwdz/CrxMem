<#
.SYNOPSIS
    Creates the CrxMem private PKI: a self-signed 'CrxMem Root CA' and a
    'CrxShield Driver Signing' code-signing certificate issued by that root.

.DESCRIPTION
    Strategy: PRIVATE ROOT CA code signing.
    A driver signed with a certificate chaining to our OWN root CA loads
    WITHOUT test mode as long as:
      1. The root is present in LocalMachine\Root and LocalMachine\TrustedPublisher
         (done by Install-CrxShieldDriver.ps1), AND
      2. Secure Boot is OFF (private-root kernel signing is rejected under
         Secure Boot; that requires an EV/attestation-signed driver).

    Both certificates are created in Cert:\CurrentUser\My, so this script
    does NOT require elevation.

    Idempotent: existing certificates are reused, exports are refreshed.

.OUTPUTS
    .\CrxMemRootCA.cer  - public root certificate (deploy to target machines)
    .\CrxMemRootCA.pfx  - root + PRIVATE KEY, password protected (KEEP OFFLINE)
    The signing certificate stays in Cert:\CurrentUser\My (private key there).

.NOTES
    Run as your normal (non-elevated) account is fine and recommended, so the
    signing cert lands in YOUR user store where signtool will find it later
    even when Install-CrxShieldDriver.ps1 runs elevated for the same account.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$OutDir = $PSScriptRoot,
    [SecureString]$PfxPassword   # optional; if omitted and interactive, you will be prompted
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rootSubject    = 'CN=CrxMem Root CA'
$signingSubject = 'CN=CrxShield Driver Signing'
$cerPath        = Join-Path $OutDir 'CrxMemRootCA.cer'
$pfxPath        = Join-Path $OutDir 'CrxMemRootCA.pfx'

function Find-CertBySubject {
    param([string]$Subject)
    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

Write-Host '=== CrxMem PKI setup (CurrentUser\My) ===' -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1) Root CA: 'CrxMem Root CA'
#    - RSA 4096, 10-year validity
#    - KeyUsage: DigitalSignature + CertSign (certificate signing)
#    - BasicConstraints: CA=true (critical) -> -Type Custom + TextExtension
#    - KeyExportPolicy Exportable so we can export the .pfx backup
# ---------------------------------------------------------------------------
$root = Find-CertBySubject $rootSubject
if ($root) {
    Write-Host "[skip] Root CA already exists: $($root.Thumbprint) (expires $($root.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Yellow
}
else {
    Write-Host '[create] CrxMem Root CA (RSA 4096, 10 years) ...'
    $root = New-SelfSignedCertificate `
        -Subject $rootSubject `
        -Type Custom `
        -KeyUsage DigitalSignature, CertSign `
        -TextExtension @('2.5.29.19={critical}{text}ca=true') `
        -KeyAlgorithm RSA -KeyLength 4096 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -FriendlyName 'CrxMem Root CA'
    Write-Host "[ ok ] Root CA created: $($root.Thumbprint)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 2) Code-signing cert: 'CrxShield Driver Signing', issued by the root above.
#    -Type CodeSigningCert gives EKU 1.3.6.1.5.5.7.3.3 (code signing).
#    -Signer must live in the SAME store as the issued cert.
# ---------------------------------------------------------------------------
$signing = Find-CertBySubject $signingSubject
if ($signing) {
    Write-Host "[skip] Signing cert already exists: $($signing.Thumbprint) (expires $($signing.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Yellow
}
else {
    Write-Host '[create] CrxShield Driver Signing (issued by CrxMem Root CA) ...'
    $signing = New-SelfSignedCertificate `
        -Subject $signingSubject `
        -Type CodeSigningCert `
        -Signer $root `
        -KeyAlgorithm RSA -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -FriendlyName 'CrxShield Driver Signing'
    Write-Host "[ ok ] Signing cert created: $($signing.Thumbprint)" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 3) Export the public root as .cer (safe to distribute to target machines).
# ---------------------------------------------------------------------------
Write-Host "[export] Public root -> $cerPath"
Export-Certificate -Cert $root -FilePath $cerPath -Force | Out-Null

# ---------------------------------------------------------------------------
# 4) Export root + private key as password-protected .pfx (keep OFFLINE).
#    Prompts only when the .pfx is missing; delete the .pfx and re-run to
#    re-export with a new password.
# ---------------------------------------------------------------------------
if (Test-Path $pfxPath) {
    Write-Host "[skip] $pfxPath already exists (delete it and re-run to re-export)." -ForegroundColor Yellow
}
else {
    $pw = $PfxPassword
    if (-not $pw) {
        if ([Environment]::UserInteractive -and $Host.Name -notmatch 'Default Host') {
            $pw = Read-Host 'Choose a password to protect CrxMemRootCA.pfx' -AsSecureString
        }
    }
    if (-not $pw) {
        Write-Host "[skip] No -PfxPassword given and running non-interactively; .pfx export skipped." -ForegroundColor Yellow
        Write-Host "       Re-run with -PfxPassword (or interactively) to export the offline backup." -ForegroundColor Yellow
    }
    else {
        Write-Host "[export] Root + private key -> $pfxPath"
        Export-PfxCertificate -Cert $root -FilePath $pfxPath -Password $pw -Force | Out-Null
        Write-Host '       STORE THIS .pfx (and its password) OFFLINE. It can sign anything trusted by every machine that has the root installed.' -ForegroundColor Magenta
    }
}

Write-Host ''
Write-Host 'Done. Certificates in Cert:\CurrentUser\My:' -ForegroundColor Cyan
Find-CertBySubject $rootSubject    | Format-Table Subject, Thumbprint, NotAfter -AutoSize
Find-CertBySubject $signingSubject | Format-Table Subject, Thumbprint, NotAfter -AutoSize
Write-Host 'Next step (elevated): .\Install-CrxShieldDriver.ps1' -ForegroundColor Cyan
