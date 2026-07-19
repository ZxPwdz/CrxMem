# MemoryView Remaining Roadmap

## Phase 15 — Interaction and data-parity repair

This phase is release-blocking and precedes additional debugger features.

### Metadata reliability

- Reproduce comment and bookmark failures against real x86 and x64 processes.
- Verify selected-row synchronization, module fingerprint resolution, RVA calculation, persistence, reload, edit, toggle, and delete.
- Surface storage or identity failures in the UI instead of silently leaving metadata unchanged.
- Add real-process and restart round-trip integration tests.

### Full hex-region browsing

- Replace the fixed 24-row snapshot with a virtual, page-backed address source spanning the readable memory region.
- Fetch pages on demand while scrolling in either direction and bound cache growth.
- Add a dedicated hex address box with Go, Back, Forward, refresh, and recent-address support.
- Synchronize hex selection with disassembly, information, patching, and navigation without stale results.
- Preserve partial-read, guard-page, unreadable-page, process-exit, and cancellation states.

### Address context-command parity

- Audit the legacy viewer, Cheat Engine reference, and supplied design for address-level context actions.
- Add verified commands for copy variants, navigation/follow, comments, bookmarks, watches, address-list transfer, breakpoint operations, NOP/patch/assemble, region/module inspection, and supported access/write tracing.
- Commands requiring debugger state must expose their prerequisite and remain disabled until the real service path is valid.
- Right-click must preserve the clicked row or selected range and never act on a stale address.

### Exit criteria

- Comments and bookmarks round-trip after restarting CrxMem and after target ASLR changes.
- Hex navigation can browse the complete readable region without loading it all into memory.
- The entered hex address is selected and visible, with backward and forward scrolling available.
- The context-menu parity checklist has an implemented, deliberately deferred, or unsupported disposition for every action.
- Release build, default tests, native integration tests, and manual Gunz/notepad verification pass.

## Phase 16 — Native call-stack unwinding and symbols

- Replace heuristic stack scanning with x86/x64 unwind frames captured from a selected paused thread.
- Resolve module plus RVA first, then optionally resolve function, source, and line data through a bounded symbol service.
- Keep heuristic frames separately labelled when native unwind data is unavailable.

## Phase 17 — Verified register editing

- Add paused-thread register and flag editing with architecture-specific validation.
- Suspend-state invariants, context write-back, read-back verification, rollback/error reporting, and integration tests are mandatory.

## Phase 18 — Assembler and remaining debugger parity

- Expand assembly through separately tested memory-operand, SIMD/vector, label, directive, and multi-line families.
- Complete the remaining debugger/context-command parity items without exposing inactive controls.

## Phase 19 — Final MemoryView acceptance

- Repeat feature parity, performance, DPI, accessibility, persistence, cleanup, and recovery testing.
- Update the final handoff with verified limitations and decide the long-term legacy-viewer route.

## Work after MemoryView

After Phase 19, development moves to a separate main-application roadmap covering scanning correctness and performance, scan-result management, address-list workflows, settings, persistence, process selection/attachment, hotkeys, and the remaining primary-window features. MemoryView work is not allowed to obscure or silently expand that later scope.
