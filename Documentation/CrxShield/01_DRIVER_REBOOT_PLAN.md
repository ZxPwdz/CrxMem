# CrxShield Driver Reboot Plan

> **Date:** 2026-07-19 · **Status:** Proposed · **Scope:** CrxShield kernel driver + its CrxMem integration
> Based on a full audit of `driver.c`, `CrxDriverController.*`, `KernelDriverManager.cs`, `ProcessAccess.cs`, the build config, project docs 01–12, and current web research (Windows 11 24H2 / PatchGuard / HVCI realities).

---

## 0. Executive Summary

**Current reality:** CrxShield is a working usermode↔kernel read/write/base-address bridge — but it has **trivially-triggerable kernel memory corruption bugs**, a **build config that produces a driver that will not load**, two dead IOCTLs, broken DKOM hiding, and effectively no stealth. Three of the four features advertised in the header comment are non-functional.

**On "moving away from IOCTL":** Don't replace it — **demote it**. Keep IOCTL (ideally via KMDF queues) as a small, fixed control plane, and add a **shared-memory data plane** (named section + two KEVENTs) for bulk traffic. This captures ~90% of the benefit of exotic channels at ~5% of the cost. The project already proves the MMF pattern works: `VEHSharedMemory.cs` (VEHDebugDll channel).

**Hard NOs on Windows 11 24H2:** SSDT/IRP hooking (PatchGuard → 0x109 bugcheck), runtime DSE bypass (boot-time only now), Ob-callback removal (highest detection surface, least useful), DKOM driver hiding as-is (broken + can't unload).

---

## 1. Audit Findings (what we know now)

### 1.1 P0 — Will BSOD / corrupt memory
| # | Bug | Location |
|---|-----|----------|
| 1 | Integer overflow in write length check: `inLen < sizeof(REQ) + req->Size` wraps with attacker-controlled `Size` → OOB copy from SystemBuffer | `driver.c:421` |
| 2 | MDL length truncation: `IoAllocateMdl(addr, (ULONG)Size)` truncates SIZE_T >4GB, then `RtlCopyMemory(..., Size)` overruns the mapping | `driver.c:273` |
| 3 | MDL + locked pages **leaked** on exception in write fallback (unmap/unlock/free skipped in `__except`) | `driver.c:322-330` |
| 4 | `ProbeForRead` on target address executed in the **caller's** process context — semantically wrong, spurious pass/fail | `driver.c:232-238` |
| 5 | OOB read: `req->Size` dereferenced **before** validating `inLen >= sizeof(WRITE_REQUEST)` | `driver.c:421` |
| 6 | No size caps anywhere → arbitrary non-paged pool allocation DoS via buffered I/O | dispatch |

### 1.2 Broken / dead features
- **`GET_VERSION` can never succeed**: C# sends zero-length input; driver rejects any `inLen == 0` before the switch (`driver.c:394`). Silently masked in UI ("Connected (version unknown)"). → Effectively **no protocol version check** guarding 4 duplicated struct definitions.
- **`ENUM_CALLBACKS` / `REMOVE_CALLBACK` dead**: `ResolveObCallbackListHead()` is a stub returning NULL; `_OB_CALLBACK_ENTRY` layout is a placeholder; even if enabled: no list locking, no outLen bounds check → pool corruption. **Zero call sites in C#.** → Delete.
- **DKOM hiding**: entire body `#if 0`'d ("causes BSOD on unload"); the disabled code itself saved the wrong link values and corrupted hash links. → Delete or rewrite properly.
- **Build mismatch**: vcxproj declares `DriverType=KMDF` + INF has a WDF section, but `driver.c` is pure WDM → **compiled binary will not load**. Fix: `DriverType=WDM`, remove INF WDF sections.
- **INF stale**: DriverVer 1.0.0.0 vs code 1.1.0; no signing configured; Debug config lacks Inf2Cat path.

### 1.3 Security posture
- `IoCreateDevice` with no SDDL + `FILE_ANY_ACCESS` → **any local process (even low-IL) can read/write arbitrary process memory**. Textbook vulnerable-driver; this is exactly what Microsoft's blocklist and EDRs flag.
- Verbose `DbgPrintEx` with full PID/address/size on every op — contradicts "Reduced logging" claim.
- IOCTL base `0x1337` is a signature, not anti-signature. XOR-0xAA on two strings is theater; imports and log strings are plaintext.

### 1.4 C# side
- Wire format is **consistent** (Pack=1 everywhere, all 7 structs match). One class (`CrxDriverController`) funnels everything; consumed at one choke point (`ProcessAccess.Read/Write/GetProcessBaseAddress`) → transport swap is cheap; the real refactor cost is the **static globals** on `AntiCheatBypassForm` (`UseKernelMode`/`DriverController` — flagged as a smell back in doc 01:212).
- Read silently falls back to usermode on driver failure (mixing trust levels mid-scan); Write has no fallback (inconsistent).
- Synchronous IOCTL everywhere, no overlapped/cancel; error reporting collapses to bool.
- `KDMapperLoader` exists (undocumented in any README) — iqvw64e.sys carrier is on Microsoft's blocklist; only viable with Memory Integrity off.
- `CrxShield/CrxDriverController.{cpp,h,cs}` = orphaned dead duplicates. Delete or move to `samples/`.

---

## 2. The IOCTL Decision (researched)

| Channel | Verdict for CrxMem |
|---|---|
| **IOCTL (KMDF queues, pended-IOCTL events)** | **Keep as control plane.** Debuggable, safe, PG/HVCI-safe. "IOCTL done right." |
| **Shared section + dual KEVENT ring buffer** | **Add as data plane.** Near-zero per-op overhead, MB/s throughput for hex view/scans; 1–2 days work; bootstrap handles passed via one IOCTL. |
| MDL user-buffer mailbox | Stealthier cousin (no named objects); defer until teardown discipline needed. |
| Named pipes / mailslots | Strictly worse than section+events. Skip. |
| ALPC | No supported kernel-side endpoint. Skip. |
| Data-pointer / BYOVD comms | Blocklist + HVCI arms race on your own machine. Skip. |
| Hypervisor (DBVM-style, hypercall comms) | Separate multi-month research project; requires VBS off. Long-term only. |

**Key insight from research:** almost every "IOCTL-free" channel still needs a one-time bootstrap — "one IOCTL at init, then memory-only traffic" is the realistic target. Your threat model is your own machine, not EAC/BE; the reasons to leave IOCTL are engineering ones (throughput, async events), not stealth.

---

## 3. The Plan

### Phase 0 — Stabilize (do this before ANY new feature) · ~2–4 days

> **EXECUTION LOG 2026-07-19 — CODE COMPLETE, both configs build clean.**
> - `driver.c` (548 → ~425 lines): write-path overflow fixed (header validated before `req->Size` deref, wrap-safe length check), `CRXSHIELD_MAX_TRANSFER` = 16 MB cap on read/write, MDL truncation guard + always-run cleanup in exception path, bogus `ProbeForRead` removed, `GET_VERSION` fixed (per-IOCTL length validation; `inLen==0` now legal), NULL-check on section base, dead code deleted (callback IOCTLs/structs/resolver, DKOM hiding + saved-link globals), release builds log-free, device secured via `IoCreateDeviceSecure` + SDDL `D:P(A;;GA;;;SY)(A;;GA;;;BA)`, header comment reflects reality.
> - Build config: `DriverType=WDM` (was KMDF — binary wouldn't load), `DriverTargetPlatform=Desktop`, `wdmsec.lib` linked, `GUID_DEVCLASS_UNKNOWN` defined locally (uuid.lib is user-mode-only), INF WDF section removed + DriverVer 1.1.0.0, `SkipPackageVerification` set (WDK DPVerifierTask null-ref crashes on this machine; INF validated standalone with `infverif.exe` — VALID), `SignMode=Off` until a test cert is set up.
> - Dead files deleted: `CrxShield/CrxDriverController.{h,cpp,cs}` (orphaned duplicates).
> - C# side: dead callback IOCTL constants/structs/methods removed from `CrxDriverController.cs`, dead `ob_callback` test stub removed from `BypassTestRunner.cs` + `AntiCheatBypassForm.cs`, `GetVersion` now reports Win32 errors via `LastError`. `dotnet build` clean (0 errors).
> - **Verified:** `CrxShield.sys` builds in Debug AND Release x64 with zero errors.
> - **Remaining (needs admin + reboot, owner approval):** create/install a test cert, sign the driver, enable testsigning if not already on, load via `sc create`, run Driver Verifier + soak test against `CrxMem.DebugTarget`.
>
> **✅ PHASE 0 COMPLETE — 2026-07-19 (evening).** Private root CA created (`CrxMem Root CA` + `CrxShield Driver Signing`, tooling in `CrxShield/Tools/`). NOTE: private-CA-without-testmode does NOT work on this machine (clean 1607+ install → kernel error 577); driver loads with **testsigning ON** (Secure Boot is off). Driver signed, service created, `CrxShield` RUNNING. End-to-end verification (`CrxShield/Tools/VerifyDriver/`, runner: desktop `Verify-CrxShield.cmd`) — **6/6 PASS**: connect, GET_VERSION 1.1.0, kernel READ (pattern match), kernel WRITE (confirmed via user-mode readback + restore), GET_PROCESS_BASE. New deliverable: `CrxShieldClient.dll` (native client library, Debug+Release, zero warnings) + C# P/Invoke wrapper. Driver Verifier soak test still pending (recommended before Phase 2).

1. Fix write-path validation: overflow-safe length check (`Size > inLen - sizeof(header)`), hard `MAX_TRANSFER_SIZE` (e.g. 16 MB) on read + write.
2. Fix MDL truncation (reject/chunk >4GB) and the `__except` MDL leak (`goto cleanup` discipline).
3. Remove bogus `ProbeForRead`; document `KernelMode` PreviousMode choice.
4. Delete dead code: ENUM/REMOVE_CALLBACK IOCTLs + structs, `HideDriver/UnhideDriver` + saved-link globals, orphaned `CrxShield/CrxDriverController.{cpp,h,cs}` copies.
5. Fix build: `DriverType=WDM`, strip INF WDF section, bump DriverVer, remove KMDF refs.
6. Fix `GET_VERSION` contract (allow `inLen==0` for output-only IOCTLs) → restores a real protocol version handshake.
7. Lock down device: `IoCreateDeviceSecure` with SDDL `D:P(A;;GA;;;SY)(A;;GA;;;BA)`, IOCTLs → `FILE_READ_DATA|FILE_WRITE_DATA`.
8. Gate logging behind `#if DBG`; strip PID/addr/size from release.
9. Add **Driver Verifier** (special pool + force IRQL checking) to the test loop — every P0 bug above would have been caught by it.

**Exit criteria:** driver builds, loads via `sc create` under testsigning, survives Verifier, C# `GetVersion` round-trips, read/write soak test against DebugTarget passes.

### Phase 1 — Modern comms: control plane + data plane · ~3–5 days
1. (Optional but recommended) Port to **KMDF**: `WdfDriverCreate`, queue-based `EvtIoDeviceControl`, pended-IOCTL for KM→UM events. KMDF gives IRP tracking/cancellation/Verifier support for free. The vcxproj already half-believes it's KMDF.
2. Add **shared-section data plane**: `\BaseNamedObjects` section (page-file-backed, `SEC_COMMIT`) + two named KEVENTs (request-ready / reply-ready), ring-buffer mailbox. Bootstrap IOCTL passes handle names (per-boot randomized names, not fixed strings).
3. Migrate the hot path first: `ProcessAccess.Read` bulk reads (hex view + scanner) through the ring buffer; keep IOCTL for control commands and small ops.
4. Clean up C# plumbing while you're in there: replace `AntiCheatBypassForm` statics with a proper `IKernelBackend` behind `IProcessMemoryService` (fulfills the doc-06 "fake-driver consistency" intent); make Read-fallback behavior explicit/configurable; propagate NTSTATUS instead of bool.
5. Reuse proven patterns from `VEHSharedMemory.cs` (magic header, ring layout, Pack=1 mirroring).

**Exit criteria:** hex view scrolls a 1 MB region through shared memory measurably faster than IOCTL; overnight scan soak without handle leaks; driver survives client process death (orphaned mapping teardown).

### Phase 2 — New kernel capabilities (ranked by value/effort)
1. **Kernel thread enumeration** (difficulty 2/5, low risk) — ETHREAD walk of target EPROCESS. Feeds debugger UI and is the foundation for #2.
2. **Kernel hardware-breakpoint engine** (3/5, medium risk) — DR0–DR7 via per-CPU DPC broadcast; `#DB` routed to VEHDebugDll as the exception sink. Biggest CE-parity gap closed.
3. **Cross-thread HWBP management** (2/5 incremental) — `PsSetCreateThreadNotifyRoutine` to auto-arm new threads, cleanup on exit.
4. **Module hiding for injected DLLs** (3/5) — PEB LDR unlink (user mode) + VAD trim (kernel). Protects the VEH DLL story end-to-end. Not PG-protected.
5. **(Evaluate) Handle-table access granting** (3/5, version-fragile) — elevate GrantedAccess so plain Win32 paths keep working on stubborn targets.

### Phase 3 — Long-term / optional
- **Manual-map loading** (kdmapper-style or own reflective loader + PiDDBCache/MmUnloadedDrivers cleanup): only with Memory Integrity off; pairs naturally with the shared-memory channel (no device object at all).
- **EPT hypervisor / DBVM-style "find what writes"**: the dream feature — page-granularity watchpoints, invisible to in-guest scans. Months of work; requires VBS/HVCI off; anti-cheat timing detection is well-documented (irrelevant on your own box). Consider booting CE's DBVM rather than writing a VMM from scratch.
- **EV + attestation signing**: only if the tool must run on machines where you can't enable test mode.

### Explicitly rejected (with reasons)
- SSDT/IRP hooking — PatchGuard on 24H2 is alive and strengthened (0x109 within minutes).
- Runtime DSE bypass (`g_CiOptions`) — boot-time-only territory now.
- Ob-callback enum/removal — detection bait, zero utility for a CE-style tool; deleted in Phase 0.
- PPL reading as a "new feature" — you already bypass PPL via `MmCopyVirtualMemory`; VTL1 (Credential Guard lsass) is permanently out of reach from VTL0.

---

## 4. Sequencing rationale

- **Phase 0 first** because adding a shared-memory channel or breakpoints on top of the current write path would carry memory-corruption bugs into a new transport — and because the current build config literally cannot load.
- **Phase 1 before Phase 2** because the breakpoint engine wants the async event channel (KM→UM `#DB` notifications) that the data plane provides.
- **Thread enum before HWBP** because arming breakpoints needs the thread list anyway.

## 5. Test & safety workflow (standing rules)
- Dedicated test box or VM snapshot before every driver build; Driver Verifier on for CrxShield during all dev.
- `CrxMem.DebugTarget` as the primary test target; add a driver soak test to the opt-in (non-CI) test suite.
- Testsigning stays the permanent signing answer; Secure Boot off on the research machine.
- One transport, one choke point: all driver traffic funnels through `CrxDriverController` → `ProcessAccess` (or its Phase-1 successor). No new static globals.
