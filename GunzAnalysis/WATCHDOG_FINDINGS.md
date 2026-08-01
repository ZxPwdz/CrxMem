# GunZ Integrity Watchdog — Identification (2026-07-21)

## Method (no breakpoints, no in-process hooks)
1. CrxShield driver ENUM_THREADS → kernel thread census (immune to user-mode hooks).
2. NtQueryInformationThread(ThreadQuerySetWin32StartAddress) from an elevated EXTERNAL
   process → true per-thread start addresses (in-process hooks can't affect us).
3. GetThreadTimes deltas over 65 s → CPU activity profile.
4. CrxShield kernel READ of thread-function bytes.

## Findings (PID 14920, Gunz.exe base 0x170000, image spans 0x170000-0x41E0000)

- 22 threads. 21 report ntdll!RtlUserThreadStart (0x7FFB8CABCC70) as kernel start = normal.
- **TID 15536 — THE WATCHDOG:**
  - Kernel Win32StartAddress = NULL (scrubbed; stealth thread creation, e.g. raw NtCreateThreadEx)
  - True start: **Gunz.exe!+0x3EAB61B (VA 0x401B61B)**
  - CPU: 24.8 s over 65 s (~38% of a core, continuous — a tight watchdog loop)
  - Code at start is OBFUSCATED (junk ops, bswap/rol/neg constant-mixing chains —
    protector-style polymorphism). Normal game code has standard 55 8B EC prologues.
- TID 20316 (Gunz.exe!+0xF7880) = main game thread, 26.4 s CPU — normal.

## Why previous analysis failed
- The checker is obfuscated (static disassembly = garbage) and its thread hides
  from thread enumeration (NULL start). RPM/NtRVM hooks (fxp.dll) are irrelevant —
  nothing here was caught by API hooking.

## Open questions / next steps
- [ ] Confirm 0x401B61B reads the patched bytes: arm KERNEL READ-watchpoint
      (DR0-DR3) on Gunz.exe+2BDC7D (VA 0x42DC7D) with CrxMem debugger attached;
      #DB arrives as EXCEPTION_SINGLE_STEP with the reader's EIP -> expect EIP
      inside the obfuscated region (~0x401Bxxx), proving 15536 is the reader.
- [ ] Check if 15536's start address is stable across restarts (detection signature).
- [ ] Map the obfuscated function's structure (entry at 0x401B61B, length, call targets).

## Tooling used (all in C:\Users\admin\Documents\CrxMem\GunzAnalysis\)
- gunz_thread_census.py <pid> [--sample N]  (kernel census + w32 starts + CPU profile)
- kread.py <pid> <hexaddr> <size>           (kernel memory read)
- census_elevated.log                        (raw run output)
