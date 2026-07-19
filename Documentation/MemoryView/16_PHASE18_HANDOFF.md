# Phase 18 Handoff — Transactional Auto Assembler Core

## Delivered

- Added a dedicated Auto Assembler editor with diagnostics, non-mutating execution preview, templates, Enable, and Disable.
- Added parser, preprocessor, binder/compiler, near-allocation planner, transaction executor, session store, and service boundaries.
- Added source-spanned diagnostics and `[ENABLE]`, `[DISABLE]`, `[32-BIT]`, `[64-BIT]`, `define`, labels, typed data directives, `assert`, `alloc`, `allocnx`, `allocxo`, `dealloc`, `registersymbol`, `unregistersymbol`, `readmem`, and `align` handling.
- Added bounded and cancellable `aobscan`, `aobscanmodule`, and `aobscanregion` with explicit zero/multiple-match failures.
- AA instructions use the same Iced-backed single-instruction service as the patch pane; unsupported operand forms fail without partial output.
- Previewed allocations are discovered through read-only region enumeration. Apply requires the exact proposed address and aborts on an allocation race.
- Plans are immutable and bound to script hash, PID, process start time, architecture, assertions, allocations, symbols, and write blocks.
- Enable allocates first, verifies assertions and original bytes, applies protection-safe writes, flushes the instruction cache, reads back, and publishes the session only after full success.
- Failures restore verified patches and release allocations in reverse order. Disable retains recoverable session state if cleanup is incomplete.
- The old unrestricted Lua `autoAssemble` writer was removed and now returns an explicit unavailable message until the restricted Phase 19 bridge is used.

## Verification

- Release build passes.
- Parser/compiler tests cover section selection, defines, assertions, coalesced data/instructions, allocation proposals, and unavailable AOB sources.
- The opt-in native AA test enables a script against the benign target's executable probe, verifies the new bytes, disables it, verifies the original bytes, and exits cleanly.

## Compatibility boundaries

- The compiler currently accepts the operand forms supported by `IcedAssemblerService`; unsupported Intel/NASM forms produce diagnostics rather than guessed bytes.
- `{$LUA}`, includes, shared/global allocations, `reassemble`, remote threads, and DLL loading are Phase 19 features.
- Kernel/driver-only directives and embedded C remain unsupported by design.
- AA sessions are live process-instance state and are never persisted as reusable enabled sessions.
