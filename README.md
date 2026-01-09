# CrxMem - Cheatengine inpsired memory-scanner

**Created by ZxPwd**

**Discord: zxpwd**

A powerful, open-source memory scanner and debugger suite for Windows, inspired by Cheat Engine. This repository contains the main application and its companion projects.

![CrxMem Preview](CrxMem/preview.png)
![CrxMem Preview](preview2.png)
---

## IMPORTANT: Project Status

> **This project is currently under active development.** Many features are still being implemented, refined, or are in experimental stages. Expect bugs and incomplete functionality.

### Known Issues & Work In Progress

| Feature | Status | Notes |
|---------|--------|-------|
| **Find What Writes/Accesses** | Buggy | Detection may miss some memory accesses |
| **Debugger** | In Development | Core functionality works, advanced features incomplete |
| **Breakpoints** | In Development | Hardware breakpoints partially implemented |
| **Lua Engine** | Incomplete | Core functions work, full API not yet finished |
| **UI Improvements** | Planned | Various polish and enhancements coming |

### Upcoming Features
- Complete debugger with full breakpoint support
- Enhanced memory access detection reliability
- Comprehensive Lua scripting API
- Improved pointer scanner
- Memory region comparison
- Advanced signature scanning
- Structure dissector
- And much more...

---

## Repository Structure

This repository contains three interconnected projects:

```
CrxMem/
├── CrxMem/           # Main application - Memory scanner and debugger
├── CrxShield/        # Kernel driver for enhanced memory access
└── VEHDebugDll/      # VEH-based debugging DLL
```

| Project | Description | Language |
|---------|-------------|----------|
| **CrxMem** | Main GUI application for memory scanning, editing, and debugging | C# (.NET 8.0) |
| **CrxShield** | Windows kernel driver for kernel-level memory access | C (WDK) |
| **VEHDebugDll** | Vectored Exception Handler DLL for usermode debugging | C++ |

---

## CrxMem (Main Application)

The core memory scanner and debugger with a modern dark-themed interface.

### Features
- **Memory Scanning** - Multiple scan types (exact, increased, decreased, changed, unknown, etc.)
- **Value Types** - Byte, 2/4/8 Bytes, Float, Double, String, AOB
- **Memory Editing** - Real-time value modification and freezing
- **Memory View** - Hex editor with disassembly
- **PE Analysis** - Analyze executables, imports, exports
- **Lua Scripting** - Automate tasks (work in progress)
- **Debugging** - Hardware breakpoints, VEH debugging (work in progress)

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
```
CrxMem/
├── Core/                    # Core functionality
│   ├── MemoryScanner.cs     # Scanning engine
│   ├── ProcessAccess.cs     # Memory read/write
│   ├── DebugMonitor.cs      # Debugging
│   └── PEAnalyzer.cs        # PE analysis
├── LuaScripting/            # Lua engine
├── MemoryView/              # Hex editor & disassembler
├── Legacy/                  # Legacy WinForms UI
├── Themes/                  # WPF themes
└── MainWindow.xaml          # Main UI
```

### CrxShield Structure
```
CrxShield/
├── driver.c                 # Main driver code
├── CrxDriverController.h    # IOCTL definitions
└── CrxShield.inf            # Installation file
```

### VEHDebugDll Structure
```
VEHDebugDll/
├── VEHDebug.cpp             # Main implementation
├── VEHDebug.h               # API definitions
└── VEHDebug.def             # DLL exports
```

---

## Contributing

Contributions are welcome! This project is in early development.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Push to the branch
5. Open a Pull Request

---

## Disclaimer

**This software is provided for educational and security research purposes only.**

- Do NOT use for malicious purposes
- Do NOT use to bypass anti-cheat in online games
- The author is NOT responsible for any misuse
- Always test kernel drivers on isolated systems

---

## License

MIT License

---

**Created by ZxPwdz**
