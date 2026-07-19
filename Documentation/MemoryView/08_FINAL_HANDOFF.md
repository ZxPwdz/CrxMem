# MemoryView WPF Final Handoff — CrxMem 1.2.0

## Release decision

The WPF MemoryView remains the primary `Memory View` route. `Memory Viewer (Legacy)` remains available for at least the 1.2.0 release and is not removed by this milestone.

Phases 15–20 close the reported interaction, native unwind, register-write, and safe Auto Assembler milestones. Unsupported Cheat Engine compatibility forms remain disabled with diagnostics rather than silently approximated.

## Verified capability matrix

| Area | Status | Verification |
| --- | --- | --- |
| Process inspection | Pass | Selection/inspection does not attach or suspend; complete, partial, unreadable, cancelled, guard, and exited states are explicit |
| Metadata | Pass | Structured mutation diagnostics; module fingerprint plus RVA persistence across ASLR/restart |
| Navigation/disassembly | Pass | Address expressions, centered history, stale-result rejection, bounded Iced decoding, virtual rendering |
| Full-region hex | Pass | Virtual region rows, 4 KB demand pages, 128-page LRU, coalescing, cancellation, adjacent-region navigation, independent history |
| Context commands | Pass with gated commands | Copy/follow/metadata/watch/address-list/breakpoint/patch/inspection routes; unavailable debugger features remain visibly disabled |
| Native call stack/symbols | Pass | Serialized x64/WOW64 DbgHelp unwind, local symbols/source, module+RVA fallback, opt-in Microsoft server |
| Registers/flags | Pass | Paused thread/generation invariants, x86/x64 write, recapture, verification, verified rollback, DR ownership |
| Patching | Pass | Expected-original validation, protection restoration, cache flush, read-back, rollback, history, undo/redo |
| Auto Assembler core | Pass for documented corpus | Immutable preview, assertions, bounded AOB, exact allocations, data/instructions, transactional enable/disable |
| Restricted AA Lua | Pass for documented subset | Separate state, no CLR, dangerous libraries removed, instruction/time/output limits, source mapping, capability trust |
| Persistence/accessibility | Pass in automated coverage | Pane/window/tab/columns/fonts/density/recent disassembly+hex/symbol policy; keyboard/focus/non-color states |

## Auto Assembler compatibility

Supported: `[ENABLE]`, `[DISABLE]`, bitness sections, comments, `define`, labels, `db/dw/dd/dq`, `assert`, `aobscan` variants, `alloc/allocnx/allocxo`, `globalalloc/sharedalloc`, `dealloc`, symbol registration, `readmem`, `align`, module/address expressions, common Iced-backed instructions, exact preview, and process-bound enable/disable sessions.

Restricted support: `{$LUA}` generated AA, `autoAssemble`, `autoAssembleCheck`, custom `aaCommand`, and approved-root `include`. Lua has no CLR, filesystem, OS, package, debug, or native loading access. Nested mutation requires per-session trust.

Explicitly unavailable in 1.2.0: `createthread`, `createthreadandwait`, `loadlibrary`, `reassemble` forms not handled by the current binder, `{$LUACODE}`, `{$C}`, `{$CCODE}`, kernel allocations, driver-only directives, and unimplemented Intel/NASM operand forms. These fail with named diagnostics and make the plan non-applicable.

Main cheat-table `AutoAssemblerRecord` enable/disable/edit integration is intentionally deferred to the main-application roadmap. Current version 2.0 address tables remain unchanged.

## Acceptance record

- Release build: successful, zero errors.
- Default plus all opt-in Windows gates: **119 passed, 0 failed**.
- Native gates exercised: debug attach/pause/resume/detach, DbgHelp unwind, register mutate/verify/restore, software/hardware breakpoints, verified patch apply/restore, AA enable/disable, and local navigation performance.
- A 100,000-row disassembly source renders only its viewport in automated WPF coverage.
- Local-target navigation performance test passed its 250 ms median budget.
- DPI resources/render coverage exists for 100%, 125%, 150%, and 200%; final per-monitor visual judgment remains a manual workstation check.
- No test target was left suspended or running. Debug, symbol, patch, and AA test targets detached or terminated cleanly.

Run the complete native suite:

```powershell
$env:CRXMEM_RUN_DEBUG_TESTS = '1'
$env:CRXMEM_RUN_PATCH_TESTS = '1'
$env:CRXMEM_RUN_BREAKPOINT_TESTS = '1'
$env:CRXMEM_RUN_PERF_TESTS = '1'
$env:CRXMEM_RUN_AA_TESTS = '1'
dotnet test CrxMem.sln -c Release --no-build
```

## Recovery and safety

- Layout: `%LocalAppData%\CrxMem\MemoryView.layout.json`; rename it while MemoryView is closed to reset layout.
- Metadata and patch history use atomic/versioned stores and fingerprint-backed module-relative locations. Stale binaries do not receive persisted writes.
- An AA preview never mutates. Apply aborts if the PID/start time/architecture, assertion bytes, original bytes, or exact allocation location changed.
- AA failure rolls back verified writes and allocations in reverse order. Incomplete disable retains a recoverable session error instead of claiming success.
- Microsoft symbol downloads remain opt-in and use `%LocalAppData%\CrxMem\Symbols`.
- The legacy viewer provides a release fallback while 1.2.0 is manually exercised against Notepad and Gunz.

## Next milestone

Move to the main application: first-class Auto Assembler cheat-table records, enable/disable/edit state, script hotkeys, table migration, then scanner and main-window parity work. Do not remove the legacy Memory Viewer until a later explicit release decision.
