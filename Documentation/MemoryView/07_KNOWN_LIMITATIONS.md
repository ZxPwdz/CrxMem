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

The read-only WPF preview shell is available from `Tools > Memory View (WPF Preview)` after selecting a process. It displays real process/module identity, bounded disassembly, region information, comments, bookmarks, and hex data.

- The legacy viewer remains the default and the WPF preview is not at feature parity.
- Disassembly uses a bounded custom visible-row renderer. Hex still uses an interim virtualized `DataGrid`.
- Disassembly breakpoint/current-instruction state is unavailable until a verified debug session exists; branch lanes currently cover visible in-window targets only.
- Address input supports hexadecimal values, modules, arithmetic, parentheses, and bounded pointer dereferences. Register and symbol names produce explicit unknown-name errors until their providers are connected.
- Navigation Back/Forward is session-scoped; recent expression text and basic pane/window layout persist locally.
- Process reads report partial/unreadable/unavailable states through typed services.
- Metadata is represented as module plus RVA at the WPF service boundary, while the compatibility backend still reads legacy runtime-address storage.
- Attach, Detach, Pause, Resume, stepping, software-breakpoint toggle, paused-thread registers, read-only stack, hex, watches, and verified raw-byte patch transactions are connected. Call stack, memory map, assembly, and the breakpoint-manager UI remain unavailable rather than simulated.
- Stack module labels are explicitly heuristic and are not call-stack unwinding.
- Hex supports selection/copy/change display only. Raw-byte patching and NOP replacement are available in the patch pane through the safe transaction service; direct nibble editing and paste remain gated.
- Register editing is intentionally unavailable until verified write-back is implemented.
- Hardware breakpoint execution/write/access behavior is implemented and tested at the service boundary, but has no WPF creation/editor panel yet.
- Step Out and Run Until Return use the pointer at the selected thread's current stack pointer and do not claim unwind-metadata accuracy.
- The native debug integration test is opt-in and currently validated with a 64-bit debugger process on Windows.
- Patch history is in memory only. Although records contain module/RVA fields, persisted module identity validation and stale-binary recovery are not implemented.
- Basic splitter and window layout persistence is implemented; columns, selected tabs, and pane visibility remain deferred.

## Explicit nonclaims

- Conditional breakpoints, thread filters, and source-level stepping are not implemented.
- Hardware access monitoring does not imply attached-debugger correctness.
- Driver/VEH behavior is not covered by the managed CI build.
- Symbol/source-line resolution is not implemented for MemoryView.
- Call-stack unwinding is not implemented.
- Multi-session debugging is not implemented and will not be faked by the initial process tab.

This file must be updated as limitations are removed or newly discovered.
