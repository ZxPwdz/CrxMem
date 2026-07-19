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

## Phase 18 — Auto Assembler core and safe enable/disable

- Implement the stateful two-pass Auto Assembler core, execution-plan preview, verified batch transaction, enable/disable session state, AOB/alloc/symbol directives, and injection templates described in `12_AUTO_ASSEMBLER_RESEARCH.md`.
- Expand instruction assembly through separately tested memory-operand, SIMD/vector, label, directive, and multi-line families as required by AA scripts.

## Phase 19 — Auto Assembler compatibility and trusted Lua

- Add advanced directives plus trusted `{$LUA}` preprocessing, `luacall`, Lua `autoAssemble`/`autoAssembleCheck`, and extension-command registration using CrxMem's existing NLua runtime.
- Gate remote threads, DLL loading, host callbacks, includes, and other executable capabilities behind explicit policy and integration tests.
- Keep `{$LUACODE}`, C blocks, and kernel-only behavior explicitly deferred until their separate runtimes are designed and verified.

## Phase 20 — Final MemoryView acceptance

- Repeat feature parity, performance, DPI, accessibility, persistence, cleanup, and recovery testing.
- Update the final handoff with verified limitations and decide the long-term legacy-viewer route.

## Work after MemoryView

After Phase 20, development moves to a separate main-application roadmap covering scanning correctness and performance, scan-result management, address-list workflows, settings, persistence, process selection/attachment, hotkeys, and the remaining primary-window features. MemoryView work is not allowed to obscure or silently expand that later scope.

That main-application roadmap must include first-class Auto Assembler cheat records. The AA editor will provide **Add to Cheat Table** / **Send to Address List**, and the main cheat list will support enable, disable, edit, rename, duplicate, delete, hotkeys, visible errors/state, and ordered script dependencies through the same AA service. The table format must evolve from its current address-only `CheatEntry` model to a versioned record-kind model with backward-compatible migration. Script source is persisted; live allocations, resolved addresses, and enable-session handles are not. Full requirements are recorded in `12_AUTO_ASSEMBLER_RESEARCH.md`.
