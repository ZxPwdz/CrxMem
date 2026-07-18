# CrxMem - Cheat Engine-inspired memory analysis tool

**Created by ZxPwd**

**Discord: zxpwd**

An open-source memory scanner and analysis project for Windows, inspired by Cheat Engine. This repository contains the main application and its experimental companion projects.

> [!WARNING]
> CrxMem is pre-release research software. User-mode scanning is the primary development focus; debugger, injection, and kernel-driver features remain experimental and can destabilize target processes or Windows.

![CrxMem Preview](CrxMem/preview.png)
![CrxMem Preview](preview2.png)
---

## IMPORTANT: Project Status

> **This project is currently under active development.** Many features are still being implemented, refined, or are in experimental stages. Expect bugs and incomplete functionality.

### Known Issues & Work In Progress

| Feature | Status | Notes |
|---------|--------|-------|
| **User-mode scanner** | Working, pre-release | Exact, comparison, undo, writable-region, and AOB paths have automated coverage |
| **Memory Viewer** | Working, pre-release | Hex view, disassembly, navigation, references, modules, memory map, and export; no attached debugger yet |
| **Native table files** | Working, pre-release | Versioned `.crxct` JSON with legacy CrxMem `.ct` JSON import |
| **Find What Writes/Accesses** | Experimental | Detection may miss some memory accesses |
| **Debugger and breakpoints** | Prototype | Attachment, run control, stepping, and reliable breakpoint management are not complete |
| **Lua Engine** | Incomplete | Core functions work; the full API and monitor injection are not finished |
| **Driver and injection tools** | Experimental | Isolated research environments only; not covered by the managed CI build |

### Upcoming Features
- Attached debugger with run control and breakpoint support
- Enhanced memory access detection reliability
- Comprehensive Lua scripting API
- Improved pointer scanner
- Memory region comparison
- Advanced signature scanning
- Structure dissector
- And much more...

---

## Repository Structure

This repository contains the managed application and two experimental native companions:

```text
CrxMem/
|-- CrxMem/           # Managed WPF application
|-- CrxMem.Tests/     # Managed scanner, table, and settings tests
|-- CrxShield/        # Experimental kernel driver
`-- VEHDebugDll/      # Experimental VEH debugging DLL
```

| Project | Description | Language |
|---------|-------------|----------|
| **CrxMem** | Main GUI application for memory scanning, editing, and analysis | C# (.NET 8.0) |
| **CrxShield** | Windows kernel driver for kernel-level memory access | C (WDK) |
| **VEHDebugDll** | Vectored Exception Handler DLL for usermode debugging | C++ |

---

## CrxMem (Main Application)

The user-mode memory scanner and analysis application with a dark-themed WPF shell.

### Features
- **Memory Scanning** - Multiple scan types (exact, increased, decreased, changed, unknown, etc.)
- **Value Types** - Byte, 2/4/8 Bytes, Float, Double, String, AOB
- **Memory Editing** - Real-time value modification and freezing
- **Memory View** - Hex editor with disassembly
- **PE Analysis** - Analyze executables, imports, exports
- **Lua Scripting** - Automate tasks (work in progress)
- **Debugging prototypes** - Hardware-breakpoint and VEH experiments (not a complete attached debugger)

### Tech Stack
- .NET 8.0 (WPF + WinForms)
- [Iced](https://github.com/icedland/iced) - x86/x64 disassembler
- [NLua](https://github.com/NLua/NLua) - Lua scripting
- [ReaLTaiizor](https://github.com/Developer-Flavor/ReaLTaiizor) - Modern UI controls

---

## CrxShield (Kernel Driver)

Windows kernel driver providing enhanced memory access capabilities.

### Features
- Kernel-level memory read/write
- Process base address retrieval
- Kernel callback enumeration and removal
- Driver hiding (experimental)

### Warning
Kernel drivers can cause system instability (BSOD). Test on isolated systems only.

---

## VEHDebugDll (Debugging Library)

Vectored Exception Handler based debugging DLL for injection into target processes.

### Features
- Hardware breakpoints (DR0-DR3)
- Memory access monitoring
- Shared memory IPC with CrxMem
- Lock-free, thread-safe operations

---

## Requirements

- **OS:** Windows 10/11 (64-bit)
- **.NET:** .NET 8.0 Runtime (for CrxMem)
- **WDK:** Windows Driver Kit (for building CrxShield)
- **Visual Studio:** 2022 with C++ and .NET workloads

---

## Building

### Managed solution

```powershell
git clone https://github.com/ZxPwdz/CrxMem.git
cd CrxMem
dotnet restore CrxMem.sln
dotnet build CrxMem.sln --configuration Release --no-restore
```

Authentication is not required to clone the public repository. Contributors should use Git Credential Manager, GitHub CLI browser login, or SSH when pushing; never put a token in a command or remote URL.

### CrxMem (Main Application)
```bash
cd CrxMem
dotnet build -c Release
```
Output: `CrxMem/bin/Release/net8.0-windows/`

### CrxShield (Kernel Driver)
1. Install Windows Driver Kit (WDK)
2. Open `CrxShield/CrxShield.sln` in Visual Studio
3. Build in Release mode
4. Enable test signing: `bcdedit /set testsigning on`

### VEHDebugDll
```bash
cd VEHDebugDll
# Using Visual Studio
Open VEHDebug64.vcxproj and build Release x64

# Or using CMake
mkdir build64 && cd build64
cmake .. -A x64
cmake --build . --config Release
```

---

## Installation

### From Release
1. Download the latest release
2. Extract to a folder of your choice
3. Run `CrxMem.exe`

### User settings

CrxMem stores its versioned settings in `%LocalAppData%\CrxMem\settings.json`. On the
first run after upgrading, existing values under `HKCU\Software\CrxMem` are imported
when no JSON settings file exists. Invalid JSON is preserved as
`settings.invalid-<timestamp>.json` and replaced with safe defaults.

### Optional: Install Kernel Driver
```cmd
sc create CrxShield type= kernel binPath= "C:\path\to\CrxShield.sys"
sc start CrxShield
```

---

## Usage

### Basic Memory Scanning
1. **File > Open Process** - Select target process
2. Enter the value to find
3. Select **Value Type** (4 Bytes for integers, Float for decimals)
4. Click **First Scan**
5. Change the value in the target application
6. Enter new value and click **Next Scan**
7. Repeat until you find the address
8. Double-click to add to Address List

### Address List
- **Double-click** to edit value
- **Checkbox** to freeze value
- **Right-click** for more options

---

## Project Details

### CrxMem Structure
```text
CrxMem/
|-- Core/                    # Scanning, process access, and analysis services
|-- LuaScripting/            # Incomplete Lua API
|-- MemoryView/              # WinForms hex viewer and disassembler
|-- Legacy/                  # Excluded shell retained for migration reference
|-- Themes/                  # WPF themes
|-- MainWindow.xaml          # Active WPF shell
`-- SettingsWindow.xaml      # Native WPF settings UI
```

### CrxShield Structure
```text
CrxShield/
|-- driver.c                 # Main driver code
|-- CrxDriverController.h    # IOCTL definitions
`-- CrxShield.inf            # Installation file
```

### VEHDebugDll Structure
```text
VEHDebugDll/
|-- VEHDebug.cpp             # Main implementation
|-- VEHDebug.h               # API definitions
`-- VEHDebug.def             # DLL exports
```

---

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and project boundaries. Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

---

## Disclaimer

**This software is provided for educational and security research purposes only.**

- Do NOT use for malicious purposes
- Do NOT use to bypass anti-cheat in online games
- The author is NOT responsible for any misuse
- Always test kernel drivers on isolated systems

---

## License

Licensed under the [MIT License](LICENSE).

---

**Created by ZxPwd**
