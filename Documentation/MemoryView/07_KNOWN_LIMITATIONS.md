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
- Attach, Detach, Pause, Resume, stepping, breakpoint manager, paused-thread registers, read-only stack, hex, watches, memory map, patch history, modules, threads, and verified raw-byte patch transactions are connected. Free-form assembly remains unavailable rather than simulated.
- Stack module labels are explicitly heuristic and are not call-stack unwinding.
- Hex supports selection/copy/change display only. Raw-byte patching and NOP replacement are available in the patch pane through the safe transaction service; direct nibble editing and paste remain gated.
- Register editing is intentionally unavailable until verified write-back is implemented.
- Hardware breakpoint execution/write/access behavior is implemented and exposed through a paused-session WPF editor with slot, alignment, access, and size validation.
- Step Out and Run Until Return use the pointer at the selected thread's current stack pointer and do not claim unwind-metadata accuracy.
- The native debug integration test is opt-in and currently validated with a 64-bit debugger process on Windows.
- Patch history is in memory only. Although records contain module/RVA fields, persisted module identity validation and stale-binary recovery are not implemented.
- Window placement, splitters, pane visibility, selected tabs, code font/row density, and recent addresses persist. Bottom tool grids autosize from current content on each session; the custom disassembly renderer's internal column geometry remains fixed.
- Context menus cover verified copy, navigation, breakpoint, and NOP workflows. Metadata editing, direct hex editing, and unverified debugger actions remain absent rather than appearing as inactive context commands.
- Comments and bookmarks are displayed, but the WPF route does not yet provide complete create/edit/delete workflows or migrate the legacy runtime-address store to versioned module identity plus RVA records.
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
