# Gunz.exe Anti-Cheat Analysis — Complete Documentation

## Overview

**Target**: Gunz.exe (FxpGunZ private server client), 32-bit PE under WoW64, packed with Themida
**Goal**: Inject a DLL (fxp.dll) that applies memory patches (Ninja Jump, Shotbot) without the anti-cheat detecting and crashing the game
**Status**: Patches are detected within ~60 seconds despite API-level hooks. The detection mechanism is **direct memory reads** (pointer dereference), which no API hook can intercept.

---

## Environment

| Detail | Value |
|--------|-------|
| Gunz.exe path | `D:\FxpGunZ\Gunz.exe` |
| DLL source | `E:\Git.Projects\Curse-X\Overclock\FXP STRIP\` |
| DLL output | `E:\Git.Projects\Curse-X\Overclock\FXP STRIP\Release\fxp.dll` |
| Gunz.exe base (typical) | `0x00130000` (ASLR) |
| Architecture | 32-bit PE, running under WoW64 on Windows 10 64-bit |
| Packer | Themida (corrupts PE OptionalHeader values) |
| Build system | Visual Studio 2022, MSBuild, Release|Win32 |

---

## PE Structure (from disk)

Gunz.exe has 8 sections. Themida spoofs `OptionalHeader.BaseOfCode=0x06` and `OptionalHeader.SizeOfCode=0x200` (512 bytes — fake).

### Real Section Layout

| Section | VirtualAddress | VirtualSize | Characteristics | Notes |
|---------|---------------|-------------|----------------|-------|
| `.text` | `0x00001000` | `0x0078898C` (~7.9MB) | `0x60000020` (CNT_CODE\|EXECUTE\|READ) | Real game code |
| `.rdata` | `0x0078A000` | `0x00134CFA` | `0x40000040` | Data + pointer table |
| `.data` | `0x008BF000` | `0x01E05120` | `0xC0000040` | Game data |
| `.M]e` | `0x026C5000` | `0x0086BAE3` | `0x60000020` (CNT_CODE\|EXECUTE\|READ) | **Themida VM section** |
| `.NuT` | `0x02F31000` | `0x00006CA0` | `0xC0000040` | Themida data |
| `.ML8` | `0x02F38000` | `0x0108435C` | `0x60000020` (CNT_CODE\|EXECUTE\|READ) | **Themida VM section** |
| `.rsrc` | `0x03FBD000` | `0x000272BC` | `0x40000040` | Resources |
| `.reloc` | `0x03FE5000` | `0x00000720` | `0x42000040` | Relocations |

**Critical**: Sections `.M]e` and `.ML8` have the `IMAGE_SCN_CNT_CODE` flag. Our `MODULE::InitializeModule()` finds the FIRST code section, which is `.text`. But anti-cheat code may run from `.M]e` or `.ML8` (Themida virtualized code).

### Effective Address Ranges (base 0x00130000)

| Section | Start Address | End Address | Size |
|---------|--------------|-------------|------|
| `.text` | `0x00131000` | `0x008B998C` | ~7.9 MB |
| `.rdata` | `0x008BA000` | `0x009EECFA` | ~1.2 MB |
| `.M]e` (Themida VM) | `0x037F5000` | `0x04060AE3` | ~8.4 MB |
| `.ML8` (Themida VM) | `0x04068000` | `0x050EC35C` | ~16.5 MB |

---

## Key RVA Table

All offsets are from Gunz.exe base (typically 0x00130000):

| Feature | RVA | Full Address | Size | Description |
|---------|-----|-------------|------|-------------|
| **Ninja Jump patch** | `0x2B7C3D` | `0x003E7C3D` | 6 bytes | `jz rel32` → 6× NOP |
| VirtualQuery detection | `0x788322` | `0x008B8322` | ~20 bytes | Checks page protection flags |
| Ground Slash | `0x7909EC` | `0x008C09EC` | 4 bytes | Float value (OFF=80.0, ON=0.0) |
| Overclock | `0x79FC90` | `0x008CFC90` | 4 bytes | Float value (default=4.8, max=7.0) |
| Crosshair write 1 | `0x0D60FB` | `0x002060FB` | 10 bytes | Shotbot hook site |
| Crosshair write 0 | `0x0D6291` | `0x00206291` | 10 bytes | Shotbot hook site |
| Weapon gun state | `0x0D4227` | `0x00204227` | 7 bytes | Shotbot hook site |
| Weapon sword state | `0x0D835E` | `0x0020835E` | 10 bytes | Shotbot hook site |
| Pointer table (custom IAT) | `0x78A000` | `0x008BA000` | variable | Contains direct API function pointers |

---

## Anti-Cheat Mechanisms Identified

### 1. NtProtectVirtualMemory Syscall Detour

The anti-cheat/DRM detours `ntdll!NtProtectVirtualMemory` (32-bit) with a JMP instruction (`E9 xx xx xx xx`), intercepting all `VirtualProtect` calls to prevent memory protection changes.

**Original bytes**: `B8 50 00 00 00 BA F0 91 7E 77 FF D2` (mov eax, 0x50; mov edx, ...; call edx)
**With detour**: `E9 2A C9 A1 8C` (JMP to detour handler)

**Our fix**: `PrepareMemoryWrites()` repairs this by writing `B8 50 00 00 00` back. Verified working — the syscall stub is restored.

### 2. VirtualQuery Page Protection Check (RVA 0x788322)

Code at `Gunz.exe+0x788322` calls `VirtualQuery` on `.text` pages and checks the returned protection flags:

```
test byte ptr [protect], 0xEE   → sete al    ; detection if NO standard protection bits
test [protect], 0x101            → cmovne     ; detection if NOACCESS or GUARD
```

**Our fix**: `hook_VirtualQuery` normalizes protection flags: removes `PAGE_NOACCESS`/`PAGE_GUARD` and ensures at least `PAGE_READWRITE` is set when the caller is from Gunz.exe code.

### 3. ReadProcessMemory Self-Code Integrity (23 sites, 6 bytes each)

The anti-cheat reads 6 bytes from each of 23 specific locations in `.text` using `ReadProcessMemory` (or `NtReadVirtualMemory` directly), comparing against expected values. Any deviation triggers detection.

**Our fix**: MinHook on `kernel32!ReadProcessMemory` AND `ntdll!NtReadVirtualMemory`. Both hooks serve clean `.text` backup data when the game reads from its own code section. The `.text` backup is taken at DLL initialization before any patches are applied.

### 4. **DIRECT MEMORY READS** (THE UNSOLVED PROBLEM)

The anti-cheat has at least one dedicated thread that reads `.text` bytes via direct pointer dereference (e.g., `mov eax, [esi+0xNN]` or `rep cmpsb`). Since `.text` is `PAGE_EXECUTE_READ`, these reads succeed without calling any API. **No API hook can intercept direct memory access.**

Evidence:
- Both `kernel32!ReadProcessMemory` and `ntdll!NtReadVirtualMemory` are hooked and verified active
- Game still detects `.text` patches in ~60 seconds
- `.text` is `PAGE_EXECUTE_READ` — direct reads are always possible
- The process has ~26 threads; one or more are likely dedicated integrity monitors

### 5. CreateThread Monitoring (suspected)

The anti-cheat may create worker threads from within `.text` for integrity checking. The (now-disabled) `hook_CreateThread` used to intercept this by checking if the thread start address was in Gunz.exe code range and blocking it unless specific return-address magic values matched.

### 6. Pointer Table (Custom IAT at RVA 0x78A000)

Gunz.exe maintains a secondary import table at RVA `0x78A000` containing direct function pointers to various Windows APIs (ADVAPI32, GDI32, etc.). The anti-cheat may use these pointers to call `NtReadVirtualMemory` directly — which is why we hooked ntdll in addition to kernel32.

---

## What We've Tried

### Attempt 1: Original Hooks (CRASHED IMMEDIATELY)

Installed 5 hooks: VirtualQuery, GetTickCount, ReadProcessMemory, TerminateProcess, SetUnhandledExceptionFilter.
**Result**: Shotbot activation caused immediate crash.

### Attempt 2: Reduced Hooks (STILL DETECTED)

Removed TerminateProcess and SetUnhandledExceptionFilter, keeping only VirtualQuery, GetTickCount, ReadProcessMemory.
**Result**: Ninja Jump detected in 8-16 seconds.

### Attempt 3: Fixed .text Section Parsing (STILL DETECTED)

Fixed `module.cpp` to parse PE section headers instead of using Themida-spoofed `OptionalHeader.BaseOfCode/SizeOfCode`.
Old (broken): CodeBase = fake value (wrong), CodeSize = 512 bytes (only 512 bytes backed up!)
New (fixed): CodeBase = real .text VA, CodeSize = real .text VirtualSize (~7.9MB)
**Result**: `.text` backup now covers the full 7.9MB, but game still detected patches.

### Attempt 4: Added NtReadVirtualMemory Hook (STILL DETECTED, ~60s)

Added MinHook for `ntdll!NtReadVirtualMemory` (the lower-level syscall wrapper). Also broadened the return-address gate from `.text`-only to the full 63MB Gunz module (to cover Themida VM sections `.M]e` and `.ML8`).
**Result**: Game survived 60+ seconds in initial monitoring, but user reported crash after my monitoring window ended.

### Attempt 5: Remove Return-Address Gate (NOT YET TRIED)

The ReadProcessMemory/NtReadVirtualMemory hooks still gate on `IsCallerInGunzModule()`. If the anti-cheat thread has a return address outside the Gunz module (e.g., in ntdll.dll from an APC callback), the hook won't serve backup data. But this wouldn't matter for direct memory reads anyway.

### Attempt 6: Thread Investigation (IN PROGRESS)

Tried using CE debugger and x64dbg to dump thread contexts (EIP per thread) to find the integrity check thread. CE's MCP bridge doesn't expose thread context properly. x64dbg path not configured.

---

## Current DLL Hook State

### Active Hooks

| API | Module | Purpose | Verified |
|-----|--------|---------|----------|
| `LoadLibraryA` | kernel32.dll | Redirect EhSvc.dll → fake\EhSvc.dll | Installed at DLL init |
| `VirtualQuery` | kernel32.dll | Normalize page protection for GunZ callers | Active |
| `GetTickCount` | kernel32.dll | Ensure non-zero return | Active |
| `ReadProcessMemory` | kernel32.dll | Serve .text backup for GunZ self-reads | Active (JMP verified) |
| `NtReadVirtualMemory` | ntdll.dll | Serve .text backup for direct ntdll callers | Active (JMP verified) |

### Verified Hook Bytes (from last live check)

```
NtProtectVirtualMemory (0x777D36E0):  B8 50 00 00 00    ← Repaired syscall stub
ReadProcessMemory     (0x75F66830):  E9 3B 3C 2F FD    ← JMP → fxp.dll
NtReadVirtualMemory   (0x777D35D0):  E9 5B 6F A8 FB    ← JMP → fxp.dll
```

### Disabled Hooks (from original code, kept for reference)

| API | Original Purpose | Why Disabled |
|-----|-----------------|--------------|
| `TerminateProcess` | Block process exit from GunZ code | Caused crash |
| `SetUnhandledExceptionFilter` | Block exception handler removal | Caused crash |
| `CreateThread` | Block GunZ-origin threads | Blocked essential threads, lobby crash |
| `GetCurrentThreadId` | Unsafe byte inspection at call site | Crashed |
| `PostMessageA` | Suppress WM_CLOSE | Unnecessary |
| `SetCursorPos` | Block cursor when not foreground | Unnecessary |
| `PeekMessageA` | Chat message injection | Uninitialized state |
| `SetWindowLongA` | Block non-GunZ windows | Unnecessary |
| `CreateMutexA` | Trigger PrepareMemoryWrites | Race condition risk |
| `SetWindowsHookExA` | Always return NULL | Unnecessary |
| `BitBlt` | Always TRUE for GunZ callers | Unnecessary |
| `ClipCursor` | Always pass NULL rect | Unnecessary |

---

## Source Files

| File | Key Contents |
|------|-------------|
| `dllmain.cpp` | Entry: `InstallHooks()`, spawns `MainThread` + `ConsoleThread` |
| `hooks.cpp` | All API hooks, Ninja Jump, compat layer, `.text` backup |
| `module.cpp` | PE parsing — finds real `.text` boundaries from section headers |
| `shotbot.cpp` | Shotbot hook site validation and installation |
| `gunz.cpp` | Console UI (F1-F5 keys) |
| `ehsvc.cpp` | Embedded fake Hackshield DLL (187KB byte array) |

---

## Key Observations & Potential Misses

### 1. The Return-Address Gate Still Exists

Even the NtReadVirtualMemory hook uses `IsCallerInGunzModule()`. If the anti-cheat calls from a thread whose call stack doesn't originate in Gunz.exe (e.g., via `RtlQueueApcW` or a kernel callback), the hook falls through and returns real bytes. **But this only matters for API-based reads, not direct reads.**

### 2. Direct Memory Reads Are the Real Enemy

Since `.text` is `PAGE_EXECUTE_READ`, the anti-cheat can read `.text` bytes at any time without calling any API. The only ways to stop this:
- Make `.text` non-readable (breaks the game — pages must be readable for execution)
- Use hardware breakpoints to intercept reads (DR0-DR3, only 4 registers, max 8 bytes each)
- Find and neutralize the integrity check thread itself
- Patch the comparison data (hash table / expected values) to match our modified byte
- Patch the integrity check function to return "OK" regardless of actual bytes
- Use page guard exceptions (PAGE_GUARD) to intercept reads — but this is one-shot per page

### 3. Themida VM Sections Run Code

`.M]e` and `.ML8` have `IMAGE_SCN_CNT_CODE` and `MEM_EXECUTE`. Themida-virtualized code runs here. The anti-cheat logic may be virtualized and spread across these sections, making static analysis difficult.

### 4. The 60-Second Detection Window

The detection takes ~60 seconds, suggesting a periodic timer-based check rather than continuous monitoring. This implies:
- There's likely a `Sleep()` or `SetTimer()` between checks
- The check thread spends most of its time sleeping
- We could potentially hook `NtDelayExecution` or `Sleep` to prevent wake-ups

### 5. Thread Count

The process typically has ~26 threads. Previous investigation showed 23-29 threads across different sessions. Some are D3D/driver threads, some are Themida VM threads, and some are game logic. We need to identify which thread(s) do integrity checking.

### 6. The Ninja Jump Patch Location (RVA 0x2B7C3D)

This is in `.text` at offset `0x2B7C3D` from the module base. After the patch, CE was unable to read from this address — suggesting Themida may have virtualized or protected the page after our write. The page could not be read via `readBytes()` or `read_memory` MCP tools.

### 7. The Shotbot Hook Sites

The 4 Shotbot hook sites have been verified live with correct signatures in a previous session. They are at RVAs: `0xD60FB`, `0xD6291`, `0xD4227` (not `0xD4227` in the code — shotbot.cpp uses `0xD4227`), `0xD835E`.

### 8. NtProtectVirtualMemory Repair Dependencies

The repair in `PrepareMemoryWrites()` calls `PatchMemory()` which internally calls `VirtualProtect()` which calls `NtProtectVirtualMemory()` — the very function being repaired. If the detour blocks protection changes, the repair silently fails.

---

## Possible Next Approaches

### A. Hook Sleep/NtDelayExecution to Block Integrity Wakeups
If the integrity thread uses `Sleep()` between checks, hooking `NtDelayExecution` could prevent it from waking up.

### B. Find Thread by Start Address
Use `NtQueryInformationThread(ThreadQuerySetWin32StartAddress)` from within the DLL to enumerate all threads and their start addresses. Threads starting in `.M]e` or `.ML8` are Themida VM and likely anti-cheat related.

### C. Find the Integrity Check Function via Hardware Watchpoint
Set a hardware breakpoint on the patched address (0x2B7C3D) for read access. When the integrity checker reads it, the debugger breaks and reveals the checking code.

### D. Patch at a Higher Level
Instead of hiding `.text` modifications, find the function that reports detection results and patch it to always return "clean."

### E. Use PAGE_GUARD for Stealth
Set `.text` pages to `PAGE_GUARD` + `PAGE_EXECUTE_READ`. On first access, a one-shot exception fires. In the exception handler, temporarily restore the original bytes, let the access proceed, then re-apply the patch on next instruction boundary. This is complex but theoretically possible.

### F. Remove the Return-Address Gate from Hooks
Simplest change: remove `IsCallerInGunzModule()` from both RPM and NtRVM hooks. This would serve backup data for ALL callers, not just Gunz.exe code. If the integrity check thread somehow uses API calls despite being in a different module, this would catch it.

### G. Brute Force Thread Suspension
Enumerate all threads, suspend threads one at a time (or in batches) and test which suspension stops detection without breaking the game.

---

## Testing Log

| Date/Time | Test | Duration | Result |
|-----------|------|----------|--------|
| Session 1 | Shotbot activation with 5 hooks | ~2s | Immediate crash |
| Session 1 | Shotbot with 3 hooks (reduced) | ~5s | Crash |
| Session 2 | Ninja Jump with 3 hooks | 8-16s | Detected, crash |
| Session 3 | Ninja Jump with fixed .text parsing | 8-16s | Still detected |
| Session 4 | Ninja Jump with NtReadVirtualMemory hook | 60s+ (monitoring) | User reported crash after window |
| Session 5 | Thread investigation with CE debugger | N/A | MCP bridge limitations |

---

## Key 32-bit Module Addresses

| Module | Base Address | Size |
|--------|-------------|------|
| Gunz.exe | `0x00130000` | ~64 MB |
| ntdll.dll (32-bit) | `0x77760000` | ~1.6 MB |
| kernel32.dll (32-bit) | `0x75F30000` | ~960 KB |
| kernelbase.dll (32-bit) | `0x755D0000` | ~2.2 MB |
| fxp.dll | `0x73250000`-`0x73270000` (varies) | ~456 KB |
