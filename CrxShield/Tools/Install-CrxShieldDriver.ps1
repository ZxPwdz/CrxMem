<#
.SYNOPSIS
    Trusts the CrxMem root CA, signs CrxShield.sys, installs/starts the
    CrxShield kernel service, and verifies the driver answers GET_VERSION.

.DESCRIPTION
    REQUIRES an elevated (Administrator) PowerShell.

    Steps:
      1. Import CrxMemRootCA.cer into LocalMachine\Root and
         LocalMachine\TrustedPublisher (idempotent).
      2. Sign the driver with the 'CrxShield Driver Signing' cert from
         Cert:\CurrentUser\My using signtool /fd SHA256. No /tr timestamp:
         this is a self-signed private-root scenario, no TSA needed, and
         skipping it avoids any network dependency. (Consequence: the
         signature dies with the cert, which is fine for a private tool.)
      3. Verify the signature with signtool verify /pa.
      4. Check Secure Boot state and WARN LOUDLY if enabled (private-root
         kernel signing is blocked under Secure Boot).
      5. sc.exe create/start the CrxShield service (tolerant of
         'already exists' / 'already running').
      6. Open \\.\CrxShield and issue IOCTL_GET_VERSION, expecting 1.4.0.

.PARAMETER Configuration
    Release (default) or Debug - selects bin\<Configuration>\CrxShield.sys.

.PARAMETER WhatIf
    Shows what would change (cert imports, signing, service creation)
    without doing it.

.NOTES
    IOCTL_GET_VERSION arithmetic (must match driver.h / driver.c):
      CTL_CODE = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
      DeviceType = FILE_DEVICE_UNKNOWN = 0x22   -> 0x22 << 16 = 0x00220000
      Access     = FILE_ANY_ACCESS   = 0        -> 0
      Function   = 0x1337                       -> 0x1337 << 2 = 0x4CDC
      Method     = METHOD_BUFFERED   = 0        -> 0
      IOCTL_GET_VERSION = 0x00220000 | 0x00004CDC = 0x00224CDC
      (Sanity: 0x1337 = 4919; 4919 * 4 = 19676 = 0x4CDC. OR into 0x220000
       gives 0x224CDC. A value like 0x22EE20 would imply Function 0x3B88,
       which is NOT ours.)
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$ToolsDir = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 0) Elevation check - hard requirement (LocalMachine stores, sc.exe, driver).
# ---------------------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: This script MUST run elevated.' -ForegroundColor Red
    Write-Host '       Right-click PowerShell -> "Run as administrator", then re-run.' -ForegroundColor Red
    exit 1
}
Write-Host '[ ok ] Running elevated.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$cerPath = Join-Path $ToolsDir 'CrxMemRootCA.cer'
$sysPath = Join-Path (Split-Path $ToolsDir -Parent) "bin\$Configuration\CrxShield.sys"
$sysPath = (Resolve-Path $sysPath -ErrorAction Stop).Path   # absolute path for sc.exe

if (-not (Test-Path $cerPath)) {
    Write-Host "ERROR: $cerPath not found. Run .\New-CrxShieldCA.ps1 first." -ForegroundColor Red
    exit 1
}
Write-Host "[ ok ] Driver: $sysPath"

# ---------------------------------------------------------------------------
# 1) Import root CA into LocalMachine\Root + LocalMachine\TrustedPublisher
#    (idempotent: skip if the same thumbprint is already there)
# ---------------------------------------------------------------------------
$rootCer  = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
foreach ($storeName in 'Root', 'TrustedPublisher') {
    $existing = Get-ChildItem "Cert:\LocalMachine\$storeName" |
        Where-Object { $_.Thumbprint -eq $rootCer.Thumbprint }
    if ($existing) {
        Write-Host "[skip] Root already in LocalMachine\$storeName" -ForegroundColor Yellow
    }
    elseif ($PSCmdlet.ShouldProcess("LocalMachine\$storeName", "Import CrxMem Root CA $($rootCer.Thumbprint)")) {
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\$storeName" | Out-Null
        Write-Host "[ ok ] Root imported into LocalMachine\$storeName" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------------------
# 2) Locate signtool.exe (x64). Prefer the known SDK, fall back to newest.
# ---------------------------------------------------------------------------
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path $signtool)) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
if (-not $signtool) {
    Write-Host 'ERROR: signtool.exe (x64) not found under Windows Kits\10\bin.' -ForegroundColor Red
    exit 1
}
Write-Host "[ ok ] signtool: $signtool"

# ---------------------------------------------------------------------------
# 3) Locate the signing certificate in CurrentUser\My
# ---------------------------------------------------------------------------
$signingCert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=CrxShield Driver Signing' -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $signingCert) {
    Write-Host "ERROR: 'CrxShield Driver Signing' cert (with private key) not found in Cert:\CurrentUser\My." -ForegroundColor Red
    Write-Host '       Run .\New-CrxShieldCA.ps1 as THIS user first.' -ForegroundColor Red
    exit 1
}
Write-Host "[ ok ] Signing cert: $($signingCert.Thumbprint) (expires $($signingCert.NotAfter.ToString('yyyy-MM-dd')))"

# ---------------------------------------------------------------------------
# 4) Sign the driver (/fd SHA256, no /tr timestamp server - see .DESCRIPTION)
# ---------------------------------------------------------------------------
if ($PSCmdlet.ShouldProcess($sysPath, "signtool sign /fd SHA256 (cert $($signingCert.Thumbprint))")) {
    & $signtool sign /v /fd SHA256 /sha1 $signingCert.Thumbprint $sysPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: signtool sign failed (exit $LASTEXITCODE)." -ForegroundColor Red
        exit 1
    }
    Write-Host '[ ok ] Driver signed.' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 5) Verify the signature (/pa = use Default Authentication Verification Policy)
# ---------------------------------------------------------------------------
& $signtool verify /pa /v $sysPath
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: signtool verify /pa failed (exit $LASTEXITCODE). Is the root in LocalMachine\Root?" -ForegroundColor Red
    exit 1
}
Write-Host '[ ok ] signtool verify /pa passed.' -ForegroundColor Green

# ---------------------------------------------------------------------------
# 6) Secure Boot check - private-root kernel signing CANNOT load under SB.
# ---------------------------------------------------------------------------
$sb = Get-ItemProperty 'HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -ErrorAction SilentlyContinue
if ($sb -and $sb.UEFISecureBootEnabled -eq 1) {
    Write-Host ''
    Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
    Write-Host '!!  SECURE BOOT IS ENABLED. The driver will NOT load with  !!' -ForegroundColor Red
    Write-Host '!!  a private-root signature. Disable Secure Boot in UEFI   !!' -ForegroundColor Red
    Write-Host '!!  or use a properly attestation/EV-signed driver.         !!' -ForegroundColor Red
    Write-Host '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!' -ForegroundColor Red
    Write-Host ''
}
else {
    Write-Host '[ ok ] Secure Boot is OFF (private-root kernel signing allowed).' -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 7) Create + start the service (tolerant of 1073 exists / 1056 running)
# ---------------------------------------------------------------------------
$serviceName = 'CrxShield'
if ($PSCmdlet.ShouldProcess($serviceName, "sc.exe create (type= kernel, binPath= $sysPath)")) {
    $out = & sc.exe create $serviceName type= kernel binPath= $sysPath start= demand 2>&1
    if ($LASTEXITCODE -ne 0 -and "$out" -notmatch '1073') {   # 1073 = already exists
        Write-Host "ERROR: sc.exe create failed: $out" -ForegroundColor Red
        exit 1
    }
    Write-Host ("[ ok ] sc.exe create -> " + ("$out" -replace '\s+', ' ').Trim()) -ForegroundColor Green
}
if ($PSCmdlet.ShouldProcess($serviceName, 'sc.exe start')) {
    $out = & sc.exe start $serviceName 2>&1
    if ($LASTEXITCODE -ne 0 -and "$out" -notmatch '1056') {   # 1056 = already running
        Write-Host "ERROR: sc.exe start failed: $out" -ForegroundColor Red
        exit 1
    }
    Write-Host ("[ ok ] sc.exe start -> " + ("$out" -replace '\s+', ' ').Trim()) -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 8) Live verification: \\.\CrxShield + IOCTL_GET_VERSION, expect 1.4.0
# ---------------------------------------------------------------------------
$probeSrc = @'
using System;
using System.Runtime.InteropServices;

public static class CrxShieldProbe
{
    public const uint IOCTL_GET_VERSION = 0x00224CDC; // see header comment
    public const uint GENERIC_RW  = 0xC0000000; // GENERIC_READ | GENERIC_WRITE
    public const uint SHARE_RW    = 0x00000003; // FILE_SHARE_READ | FILE_SHARE_WRITE
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
'@
try { Add-Type -TypeDefinition $probeSrc -ErrorAction Stop }
catch [System.Exception] {
    if ($_.Exception.Message -notmatch 'already exists') { throw }  # tolerate re-import in same session
}

$handle = [CrxShieldProbe]::CreateFile('\\.\CrxShield',
    [CrxShieldProbe]::GENERIC_RW, [CrxShieldProbe]::SHARE_RW,
    [IntPtr]::Zero, [CrxShieldProbe]::OPEN_EXISTING, 0, [IntPtr]::Zero)

if ($handle -eq [CrxShieldProbe]::INVALID_HANDLE) {
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "FAIL: CreateFile \\\\.\\CrxShield -> Win32 error $err (is the service running? is the SDDL blocking this account?)" -ForegroundColor Red
    exit 1
}
Write-Host '[ ok ] Opened \\.\CrxShield' -ForegroundColor Green

$outBuf = New-Object byte[] 12
$returned = [uint32]0
$ok = [CrxShieldProbe]::DeviceIoControl($handle, [CrxShieldProbe]::IOCTL_GET_VERSION,
    $null, 0, $outBuf, 12, [ref]$returned, [IntPtr]::Zero)
$ioctlErr = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
[CrxShieldProbe]::CloseHandle($handle) | Out-Null

if (-not $ok -or $returned -lt 12) {
    Write-Host "FAIL: DeviceIoControl IOCTL_GET_VERSION (0x00224CDC) -> ok=$ok bytes=$returned Win32=$ioctlErr" -ForegroundColor Red
    exit 1
}

$major = [BitConverter]::ToUInt32($outBuf, 0)
$minor = [BitConverter]::ToUInt32($outBuf, 4)
$patch = [BitConverter]::ToUInt32($outBuf, 8)
Write-Host "[ ok ] GET_VERSION returned $major.$minor.$patch ($returned bytes)"

if ($major -eq 1 -and $minor -ge 4 -and $patch -eq 0) {
    Write-Host ''
    Write-Host '=== PASS: CrxShield driver is signed, loaded, and answering (version 1.4.0) ===' -ForegroundColor Green
    exit 0
}
else {
    Write-Host "FAIL: expected 1.4.0, got $major.$minor.$patch" -ForegroundColor Red
    exit 1
}
