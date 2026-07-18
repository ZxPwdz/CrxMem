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
- The disassembly and hex panes use interim virtualized `DataGrid` controls rather than the final custom renderers.
- Address input is currently limited to absolute hexadecimal and `module+offset`.
- Process reads now report partial/unreadable/unavailable states through typed services, but symbol, register, arithmetic, and dereference address expressions are not implemented yet.
- Metadata is represented as module plus RVA at the WPF service boundary, while the compatibility backend still reads legacy runtime-address storage.
- Debug commands, registers, stack, watches, breakpoints, call stack, memory map, assembly, and patching are disabled rather than simulated.
- The preview is read-only and does not change target memory or protection.
- Splitter/window layout persistence is not implemented yet.

## Explicit nonclaims

- No reliable software-breakpoint engine is currently connected to MemoryView.
- No correct step-into, step-over, or step-out implementation is currently connected to MemoryView.
- Hardware access monitoring does not imply attached-debugger correctness.
- Driver/VEH behavior is not covered by the managed CI build.
- Symbol/source-line resolution is not implemented for MemoryView.
- Call-stack unwinding is not implemented.
- Multi-session debugging is not implemented and will not be faked by the initial process tab.

This file must be updated as limitations are removed or newly discovered.
