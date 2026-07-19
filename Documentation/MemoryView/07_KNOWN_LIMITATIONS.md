# MemoryView Known Limitations — 1.2.0

## WPF viewer

- One debugged process/session is supported per MemoryView window; multi-session tabs are not implemented.
- Conditional breakpoints, thread filters, and source-level stepping are not implemented.
- Branch-flow lanes cover visible in-window targets only.
- Step Out and Run Until Return can still fall back to the selected thread's stack pointer when unwind metadata is unavailable; heuristic entries are labelled.
- Local DbgHelp symbols are automatic. Microsoft symbol downloads require opt-in and depend on network/server availability.
- Native integration coverage is Windows x64-hosted. WOW64 code paths exist and have managed coverage, but the benign target suite does not yet ship a separate x86 executable.
- Per-monitor 100/125/150/200% DPI resources have automated render coverage; subjective visual inspection on every monitor arrangement remains manual.

## Text assembly and Auto Assembler

- The shared Iced text binder supports the documented common register, immediate, branch, and data corpus. Unsupported Intel/NASM memory, SIMD/vector, prefix, or relocation forms fail explicitly; they do not produce guessed bytes.
- Forward-label and `reassemble` combinations outside the bounded binder's stable layout forms are rejected.
- `createthread`, `createthreadandwait`, and `loadlibrary` remain disabled pending dedicated native security/integration gates.
- `{$LUACODE}`, `{$C}`, `{$CCODE}`, kernel allocations, and driver-only directives are unsupported by design.
- AA Lua cannot access CLR, filesystem, OS, package, debug, or native loading APIs. The separate `include` directive reads only approved CrxMem include roots.
- AA sessions are live process-instance state and are not persisted as enabled sessions.
- First-class Auto Assembler cheat-table records are deferred to the main-application roadmap.

## Legacy viewer

- `Memory Viewer (Legacy)` remains for one release as a recovery route. Its historical unsafe/incomplete behaviors are not inherited by the WPF viewer and are not being expanded.

See `08_FINAL_HANDOFF.md` for the verified matrix, tests, recovery steps, and next milestone.
