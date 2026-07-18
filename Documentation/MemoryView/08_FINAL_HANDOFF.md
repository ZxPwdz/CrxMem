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
| Hex/stack/watch | Pass (inspection) | Virtualized hex, selection/copy/deltas, paused-thread stack, expression watches and explicit errors |
| Safe byte patches | Pass | Original-byte validation, protection restoration, cache flush, read-back, rollback, undo/redo |
| Breakpoints and stepping | Pass | Software and hardware breakpoints, reinsertion, hit counts, thread propagation, step/run commands |
| Remaining panels | Pass with labelled limitations | Breakpoints, memory map, modules, threads, patches and heuristic call-stack projection |
| Accessibility/persistence | Pass | Keyboard focus, non-color state labels, font/density settings, pane/window/tab/column persistence |
| Default route | WPF promoted by owner decision | Legacy remains explicitly available while the outstanding items are completed |

## Outstanding parity gates

1. Extract and verify a real assembler service before enabling free-form instruction patches.
2. Persist patches and metadata using strong module identity plus RVA, with stale-binary detection and recoverable migration.
3. Add complete WPF comment/bookmark create, edit, and delete workflows.
4. Add direct hex nibble/paste editing on top of the verified patch transaction service.
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
- Patch history is session-local. Do not treat it as recovery across process restarts or changed binaries.
- Each phase is a dedicated Git commit, allowing an individual phase to be reviewed or reverted without discarding the legacy route.

## Recommended next milestone

Treat assembler extraction and durable module-identity persistence as Phase 13. Those two boundaries unlock safe instruction editing and reliable cross-run recovery. Follow with metadata editing, direct hex editing, and a true unwinder before repeating the default-route acceptance review.
