# Phase 17 Handoff — Verified Register Editing

## Delivered

- Extended `IRegisterService` with structured validation and verified write results.
- Register writes require a live matching process, verified paused debugger state, selected thread, architecture, and current capture generation.
- General-purpose registers, IP, SP, and supported status flags can be edited. DR0–DR7 remain read-only under breakpoint-service ownership.
- Input is hexadecimal by default; `dec:` explicitly selects decimal. Values are width-checked for x86 targets.
- Native writes use `SetThreadContext` or `Wow64SetThreadContext`, recapture the context, and compare the requested value.
- Verification failures attempt to restore and recapture the original value, reporting whether rollback was verified.
- Reserved flag bits are preserved through a writable-bit mask.
- Double-click and context-menu editing are available in the register pane, with explicit flag toggles in the flags footer.
- Successful writes refresh the register snapshot, IP disassembly, SP stack, watches, flags, and changed-value indicators.

## Verification

- Release build passes.
- The default Release suite passes with view-model write/refresh coverage.
- The opt-in Windows debugger test performs a real scratch-register mutation, verifies it, restores the original value, verifies restoration, and then detaches cleanly.

## Recovery notes

- A stale UI snapshot cannot overwrite a newer capture generation.
- A failed verification never reports success; rollback state is retained in `RegisterWriteResult`.
- Register editing is unavailable in inspection mode and while the debugged process is running.
