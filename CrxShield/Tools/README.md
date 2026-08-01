# CrxShield Tools — Signing, Install, Verification

Tooling for signing `CrxShield.sys` with a **private root CA** and loading it
on this machine **without test mode** (requires **Secure Boot OFF**).

## How it works

- `New-CrxShieldCA.ps1` creates a self-signed `CrxMem Root CA` (RSA 4096,
  10 years) plus a `CrxShield Driver Signing` code-signing cert issued by it,
  both in `Cert:\CurrentUser\My` (no admin needed).
- `Install-CrxShieldDriver.ps1` (admin) puts the public root into
  `LocalMachine\Root` + `LocalMachine\TrustedPublisher`, signs the driver
  with `signtool /fd SHA256` (no timestamp server — self-signed, so no TSA
  needed and no network dependency), creates/starts the `CrxShield` service,
  and proves the driver answers `IOCTL_GET_VERSION`.
- A driver signed this way loads on Windows 10/11 x64 **only when Secure Boot
  is disabled**. Under Secure Boot, kernel drivers must chain to Microsoft's
  trust (WHQL/attestation or EV cross-signed) — a private root is rejected.

## Run order (this machine)

1. Open **PowerShell as Administrator** (right-click → *Run as administrator*).
2. `cd C:\Users\admin\Documents\CrxMem\CrxShield\Tools`
3. `.\New-CrxShieldCA.ps1`
   - Creates both certs (skips them if they already exist).
   - Exports `CrxMemRootCA.cer` (public, shareable) and prompts for a password
     to export `CrxMemRootCA.pfx` (root **+ private key** — store offline;
     anyone with it can sign code your machines will trust).
4. `.\Install-CrxShieldDriver.ps1` (add `-Configuration Debug` to sign the
   Debug build; Release is the default)

### Expected output of step 4

```
[ ok ] Running elevated.
[ ok ] Driver: C:\Users\admin\Documents\CrxMem\CrxShield\bin\Release\CrxShield.sys
[ ok ] Root imported into LocalMachine\Root          (or [skip] if present)
[ ok ] Root imported into LocalMachine\TrustedPublisher
[ ok ] signtool: C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe
[ ok ] Signing cert: <thumbprint>
[ ok ] Driver signed.
[ ok ] signtool verify /pa passed.
[ ok ] Secure Boot is OFF (private-root kernel signing allowed).
[ ok ] sc.exe create -> [SC] CreateService SUCCESS     (or "already exists")
[ ok ] sc.exe start -> ... RUNNING                     (or "already running")
[ ok ] Opened \\.\CrxShield
[ ok ] GET_VERSION returned 1.1.0 (12 bytes)
=== PASS: CrxShield driver is signed, loaded, and answering (version 1.1.0) ===
```

If Secure Boot is enabled you will see a loud red warning box — the driver
will **not** load until Secure Boot is disabled in UEFI.

### Uninstall

```powershell
.\Uninstall-CrxShieldDriver.ps1               # stop + delete service only
.\Uninstall-CrxShieldDriver.ps1 -RemoveCerts  # also strip CA from LocalMachine
```

The `Cert:\CurrentUser\My` certs (with private keys) are never removed by the
scripts; use `certmgr.msc` if you want them gone.

## Deploying to another machine (no SDK needed)

Copy to the target:

- `CrxMemRootCA.cer` (public root)
- the **signed** `CrxShield.sys`

Then on the target, from an **elevated** prompt:

```bat
certutil -addstore -f Root CrxMemRootCA.cer
certutil -addstore -f TrustedPublisher CrxMemRootCA.cer
sc.exe create CrxShield type= kernel binPath= C:\full\path\to\CrxShield.sys start= demand
sc.exe start CrxShield
```

Requirements on the target: x64 Windows, **Secure Boot OFF**. No test mode,
no `bcdedit` changes needed.

## IOCTL reference (must match driver)

```
CTL_CODE = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method
DeviceType = FILE_DEVICE_UNKNOWN = 0x22
Access     = FILE_ANY_ACCESS = 0, Method = METHOD_BUFFERED = 0

GET_VERSION       (Fn 0x1337+0) = 0x00224CDC
READ_MEMORY       (Fn 0x1337+1) = 0x00224CE0
WRITE_MEMORY      (Fn 0x1337+2) = 0x00224CE4
GET_PROCESS_BASE  (Fn 0x1337+3) = 0x00224CE8
```

`GET_VERSION` returns three `ULONG`s (pack=1): `1, 1, 0` → version 1.1.0.

## Secure Boot caveat

Private-root kernel signing is honored by Windows **only when Secure Boot is
disabled**. Check state:

```powershell
(Get-ItemProperty HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State).UEFISecureBootEnabled
```

`0` = off (OK). `1` = on (driver will fail to load; disable in UEFI firmware
settings). If this machine ever needs Secure Boot on, the driver must be
signed via the Microsoft Partner Center / attestation signing flow instead.
