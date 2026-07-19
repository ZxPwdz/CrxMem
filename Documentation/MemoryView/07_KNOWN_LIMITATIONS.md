# MemoryView Known Limitations

## Current legacy viewer

- The active Memory Viewer is WinForms/GDI+, not WPF.
- It is a process inspector and patching prototype, not an attached debugger.
- Run, pause, stop, stepping, and breakpoint commands are disabled placeholders.
- Registers may remain demo values because context reads are not coordinated with a paused thread.
- Stack can display fabricated demo values; the header labels them `Demo`, but this is unsuitable for the new viewer.
- 32-bit stack context retrieval is not implemented.
- Watch is an empty tab.
- Breakpoint manager and call stack tabs do not exist.
- Modules, memory map, and threads are basic modal text views.
- Address expressions support only absolute hexadecimal and `module+offset`.
- No browser-style navigation history exists.
- Find References searches only the current disassembly window.
- AOB search has no cancellation/progress and tolerates malformed bytes as wildcards.
- Hex single-character editing is incorrect and should not be relied on.
- Hex page-protection change is permanent for the target session.
- Patches are held only in memory and are not verified after write.
- Longer assembled replacements may overwrite following instructions.
- Metadata persistence errors are silent.
- Several memory/metadata operations block the UI thread.

## New WPF viewer status

The WPF inspection/debugging viewer is the main `Memory View` route and is also available from `Tools > Memory View (WPF)` after selecting a process. It displays real process/module identity, bounded disassembly, region information, comments, bookmarks, hex data, paused-thread state, and verified debugger/patch tools.

- The WPF viewer is the default at the project owner's direction. The legacy viewer remains available at `Tools > Memory Viewer (Legacy)` while WPF parity work continues.
- Disassembly and hex use bounded custom visible-row renderers; branch lanes currently cover visible in-window targets only.
- Disassembly breakpoint and current-instruction markers are populated from verified debug-session state.
- Address input supports hexadecimal values, modules, arithmetic, parentheses, and bounded pointer dereferences. Register and symbol names produce explicit unknown-name errors until their providers are connected.
- Navigation Back/Forward is session-scoped; recent expression text and pane/window/column layout persist locally.
- Process reads report partial/unreadable/unavailable states through typed services.
- Metadata is represented as module plus RVA at the WPF service boundary, while the compatibility backend still reads legacy runtime-address storage.
- Attach, Detach, Pause, Resume, stepping, breakpoint manager, paused-thread registers, read-only stack, hex, watches, memory map, patch history, modules, threads, verified raw-byte patches, and bounded single-instruction assembly are connected.
- Assembly currently supports common no-operand, control-flow, 32/64-bit register, and immediate forms. Memory operands, SIMD/vector operands, labels, directives, and multi-line scripts fail explicitly.
- Stack module labels are explicitly heuristic and are not call-stack unwinding.
- Hex range copy/edit/paste routes through safe patch transactions, but the view still exposes only a fixed 24-row snapshot. It cannot yet page through the full readable region or navigate independently to an entered hex address.
- Register editing is intentionally unavailable until verified write-back is implemented.
- Hardware breakpoint execution/write/access behavior is implemented and exposed through a paused-session WPF editor with slot, alignment, access, and size validation.
- Step Out and Run Until Return use the pointer at the selected thread's current stack pointer and do not claim unwind-metadata accuracy.
- The native debug integration test is opt-in and currently validated with a 64-bit debugger process on Windows.
- Fingerprint-backed module-relative patches persist and are revalidated against SHA-256, image size, RVA bounds, and current bytes. Absolute-address patches or modules that cannot be fingerprinted remain session-only and are never auto-applied.
- Window placement, splitters, pane visibility, selected tabs, code font/row density, and recent addresses persist. Bottom tool grids autosize from current content on each session; the custom disassembly renderer's internal column geometry remains fixed.
- Context menus cover a small verified subset, but address-level parity is incomplete. Follow/navigation, address-list, watch, metadata, tracing, breakpoint, patch/assemble, and region/module actions require a formal parity audit and state-aware implementation.
- Comments and bookmarks have a versioned module-fingerprint plus RVA service and visible editor, but real-process create/edit/toggle persistence has been reported nonfunctional and is not considered verified until Phase 15 integration tests pass.
- The WPF route still has explicitly documented parity gaps; see `08_FINAL_HANDOFF.md` for the owner-directed route decision.

## Explicit nonclaims

- Conditional breakpoints, thread filters, and source-level stepping are not implemented.
- Hardware access monitoring does not imply attached-debugger correctness.
- Driver/VEH behavior is not covered by the managed CI build.
- Symbol/source-line resolution is not implemented for MemoryView.
- Call-stack unwinding is not implemented.
- The Call Stack panel is deliberately labelled as a heuristic stack scan and is not unwind data.
- Multi-session debugging is not implemented and will not be faked by the initial process tab.

This file must be updated as limitations are removed or newly discovered.
