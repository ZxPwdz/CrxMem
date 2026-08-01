# CrxShield - Kernel Driver

> **Part of the [CrxMem Suite](../README.md)**

A Windows kernel driver for enhanced memory access and security research. Provides kernel-level capabilities for the CrxMem memory scanner.

**Driver type:** Pure WDM (raw `DriverEntry` / `IoCreateDevice`, no WDF/KMDF) — **version 1.1.0**.

---

## IMPORTANT: Project Status

> **This project is for educational and security research purposes only.** Kernel drivers can cause system instability (BSOD). Always test on a system you can afford to crash.

> **This driver is currently under development** and should be used with caution.

---

## Features

- **Kernel-Level Memory Access** - Read/write process memory from kernel mode
- **Process Base Address Retrieval** - Get the base address of any process
- **Callback Enumeration** - List registered kernel callbacks
- **Callback Removal** - Remove specific kernel callbacks (for research)
- **Driver Hiding** - DKOM-based driver concealment (experimental)
- **String Obfuscation** - Basic anti-signature measures

---

## Driver IOCTLs

| IOCTL | Description |
|-------|-------------|
| `IOCTL_CRXSHIELD_GET_VERSION` | Get driver version information |
| `IOCTL_CRXSHIELD_READ_MEMORY` | Read memory from target process |
| `IOCTL_CRXSHIELD_WRITE_MEMORY` | Write memory to target process |
| `IOCTL_CRXSHIELD_GET_PROCESS_BASE` | Get process base address |
| `IOCTL_CRXSHIELD_ENUM_CALLBACKS` | Enumerate kernel callbacks |
| `IOCTL_CRXSHIELD_REMOVE_CALLBACK` | Remove a kernel callback |

---

## Building

### Requirements
- Windows Driver Kit (WDK) for Windows 10/11
- Visual Studio 2022 with C++ workload
- Test signing enabled or a valid code signing certificate

### Build Steps
1. Open `CrxShield.sln` in Visual Studio
2. Select Release configuration
3. Build the solution
4. Output: `bin/Release/CrxShield.sys`

---

## Installation

### Test Signing Mode (Development)
```cmd
bcdedit /set testsigning on
```
Reboot after enabling.

### Loading the Driver
```cmd
sc create CrxShield type= kernel binPath= "C:\path\to\CrxShield.sys"
sc start CrxShield
```

### Unloading the Driver
```cmd
sc stop CrxShield
sc delete CrxShield
```

---

## Usage with CrxMem

CrxShield is automatically detected by CrxMem when loaded. The driver provides:
- Bypassing of user-mode memory protections
- Access to protected processes
- Enhanced debugging capabilities

---

## Project Structure

```
CrxShield/
├── driver.c                 # Main driver implementation (WDM, v1.1.0)
├── CrxShield.inf            # Driver installation file
├── CrxShield.vcxproj        # Visual Studio project (DriverType: WDM)
├── CrxShield.sln            # Solution file
└── README.md                # This file
```

> The user-mode controller (`CrxDriverController.cs`) lives in the [CrxMem](../CrxMem) project, not here.

---

## Related Projects

| Project | Description |
|---------|-------------|
| [CrxMem](https://github.com/ZxPwdz/CrxMem) | Main memory scanner application |
| [VEHDebugDll](https://github.com/ZxPwdz/VEHDebugDll) | VEH-based debugging DLL |

---

## Disclaimer

**This software is provided for educational and security research purposes only.**

- Do NOT use this driver for malicious purposes
- Do NOT use this driver to bypass anti-cheat in online games
- The author is NOT responsible for any damage caused by this software
- Always test on isolated systems or virtual machines

Using kernel drivers improperly can result in:
- System crashes (BSOD)
- Data loss
- Security vulnerabilities
- Legal consequences

---

## License

MIT License - For educational use only.

---

**Created by ZxPwdz**
