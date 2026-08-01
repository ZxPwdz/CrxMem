<#
.SYNOPSIS
    Stops and deletes the CrxShield kernel service; optionally removes the
    CrxMem Root CA from the LocalMachine stores.

.DESCRIPTION
    REQUIRES an elevated (Administrator) PowerShell.

    - Stops the service (tolerant of 'not running' / 'does not exist').
    - Deletes the service (tolerant of 'does not exist').
    - With -RemoveCerts, also deletes every 'CN=CrxMem Root CA' certificate
      from Cert:\LocalMachine\Root and Cert:\LocalMachine\TrustedPublisher.

    The CurrentUser\My certificates (root + signing cert with private keys)
    are NEVER touched by this script - delete them manually with
    certmgr.msc if you want them gone.

.PARAMETER RemoveCerts
    Also remove the CrxMem Root CA from LocalMachine\Root and
    LocalMachine\TrustedPublisher.

.PARAMETER WhatIf
    Shows what would change without doing it.

.EXAMPLE
    .\Uninstall-CrxShieldDriver.ps1
    .\Uninstall-CrxShieldDriver.ps1 -RemoveCerts
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$RemoveCerts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Elevation check
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: This script MUST run elevated.' -ForegroundColor Red
    Write-Host '       Right-click PowerShell -> "Run as administrator", then re-run.' -ForegroundColor Red
    exit 1
}
Write-Host '[ ok ] Running elevated.' -ForegroundColor Green

$serviceName = 'CrxShield'

# ---------------------------------------------------------------------------
# 1) Stop the service (tolerant: 1062 not running, 1060 not installed)
# ---------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($serviceName, 'sc.exe stop')) {
    $out = & sc.exe stop $serviceName 2>&1
    if ($LASTEXITCODE -ne 0 -and "$out" -notmatch '1062|1060') {
        Write-Host "WARN: sc.exe stop -> $out (continuing)" -ForegroundColor Yellow
    }
    else {
        Write-Host ("[ ok ] sc.exe stop -> " + ("$out" -replace '\s+', ' ').Trim()) -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# 2) Delete the service (tolerant: 1060 not installed)
# ---------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($serviceName, 'sc.exe delete')) {
    $out = & sc.exe delete $serviceName 2>&1
    if ($LASTEXITCODE -ne 0 -and "$out" -notmatch '1060') {
        Write-Host "ERROR: sc.exe delete failed: $out" -ForegroundColor Red
        exit 1
    }
    Write-Host ("[ ok ] sc.exe delete -> " + ("$out" -replace '\s+', ' ').Trim()) -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 3) Optionally remove the root CA from LocalMachine stores
# ---------------------------------------------------------------------------
if ($RemoveCerts) {
    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $certs = @(Get-ChildItem "Cert:\LocalMachine\$storeName" |
            Where-Object { $_.Subject -eq 'CN=CrxMem Root CA' })
        if ($certs.Count -eq 0) {
            Write-Host "[skip] No CrxMem Root CA in LocalMachine\$storeName" -ForegroundColor Yellow
            continue
        }
        foreach ($c in $certs) {
            if ($PSCmdlet.ShouldProcess("LocalMachine\$storeName", "Remove CrxMem Root CA $($c.Thumbprint)")) {
                Remove-Item -LiteralPath $c.PSPath -Force
                Write-Host "[ ok ] Removed $($c.Thumbprint) from LocalMachine\$storeName" -ForegroundColor Green
            }
        }
    }
}
else {
    Write-Host '[info] LocalMachine certificates left in place (use -RemoveCerts to strip them).' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Done. Note: Cert:\CurrentUser\My still holds the root + signing cert (with private keys).' -ForegroundColor Cyan
Write-Host 'Remove those manually via certmgr.msc if this machine should no longer be able to sign drivers.' -ForegroundColor Cyan
