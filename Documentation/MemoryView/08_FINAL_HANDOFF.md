# MemoryView WPF Final Handoff

## Release decision

The WPF MemoryView is the main `Memory View` route. The previous viewer remains available under `Tools > Memory Viewer (Legacy)` and will be retained for at least one release.

This route was promoted at the project owner's direction so the redesign is the normal testing surface. Phases 2–11 delivered a stable service-oriented WPF debugger and inspection surface; the outstanding parity items below remain explicitly documented and gated.

## Verified capabilities

| Area | Status | Evidence |
| --- | --- | --- |
| Process/module/memory inspection | Pass | Typed complete, partial, unreadable, cancelled, and terminated results; cancellable background loading |
| Address navigation | Pass | Absolute/module/RVA arithmetic, bounded dereferences, registers when paused, history, stale-result rejection |
| Disassembly | Pass | Bounded Iced service, immutable rows/tokens, visible-row rendering, markers, branch targets and flows |
| Debug session | Pass | Native attach/detach, pause/resume, process exit, selected thread, x86/x64 context capture |
| Registers and flags | Pass (read-only) | Paused-snapshot invariant and changed-value highlighting; editing remains gated |
| Hex/stack/watch | Partial | Hex range editing is safely routed, but full-region paging and independent address navigation remain Phase 15 blockers |
| Safe byte patches | Pass | Original-byte validation, protection restoration, cache flush, read-back, rollback, undo/redo |
| Breakpoints and stepping | Pass | Software and hardware breakpoints, reinsertion, hit counts, thread propagation, step/run commands |
| Remaining panels | Pass with labelled limitations | Breakpoints, memory map, modules, threads, patches and heuristic call-stack projection |
| Accessibility/persistence | Pass | Keyboard focus, non-color state labels, font/density settings, pane/window/tab persistence, content-sized tool columns |
| Default route | WPF promoted by owner decision | Legacy remains explicitly available while the outstanding items are completed |

## Outstanding parity gates

1. Repair and integration-test real-process comment/bookmark workflows.
2. Add page-backed full-region hex browsing and independent hex address navigation.
3. Complete the address context-menu parity audit and implement every verified state-aware action.
4. Replace the placeholder Lua `autoAssemble` binding with the stateful, transactional Auto Assembler design in `12_AUTO_ASSEMBLER_RESEARCH.md`; memory operands and multi-line scripts remain gated until that compiler exists.
5. Replace heuristic stack scanning with native unwind metadata and optional symbol/source resolution.
6. Add register write-back only after paused-thread integration tests prove restoration and error handling.
7. Complete manual per-monitor DPI checks and consider configurable internal disassembly columns.

## Performance and test record

- Warm local-target navigation median: **1.72 ms** over seven fresh-cache samples (1.11–3.40 ms), against a 250 ms engineering budget on the development machine.
- Renderer coverage: 100%, 125%, 150%, and 200% DPI.
- A 100,000-row disassembly source renders only the visible viewport inside the three-second guardrail.
- Default Release tests cover parsers, state transitions, deltas, cancellation, stale results, layout/resources, selection synchronization, patch failure recovery, and a live-process WPF render smoke test.
- Opt-in Windows tests cover native debug attach/pause/resume/detach, patches, software/hardware breakpoints, and the isolated performance probe.

Run the full native verification explicitly:

```powershell
$env:CRXMEM_RUN_DEBUG_TESTS = '1'
$env:CRXMEM_RUN_PATCH_TESTS = '1'
$env:CRXMEM_RUN_BREAKPOINT_TESTS = '1'
$env:CRXMEM_RUN_PERF_TESTS = '1'
dotnet test CrxMem.sln -c Release --no-build
```

## Recovery and compatibility

- The WPF viewer is reachable through the main `Memory View` button and `Tools > Memory View (WPF)`.
- The legacy viewer remains separately reachable through `Tools > Memory Viewer (Legacy)`.
- WPF layout is stored at `%LocalAppData%\CrxMem\MemoryView.layout.json`; close the window and rename or delete that file to reset layout safely.
- Fingerprint-backed module-relative patch history survives restarts and reports restored/applied/stale state from current bytes. Absolute-address entries remain session-local and no persisted patch is auto-applied.
- Each phase is a dedicated Git commit, allowing an individual phase to be reviewed or reverted without discarding the legacy route.

## Recommended next milestone

Phase 15 is the current release-blocking milestone: repair real-process comments/bookmarks, add full-region page-backed hex navigation, and complete address context-command parity. Native unwinding and register editing follow in Phases 16 and 17. The Auto Assembler is then delivered as a safe core compiler/executor in Phase 18 and trusted Lua compatibility in Phase 19, before final acceptance in Phase 20.
