# Phase 19 Handoff — Restricted Lua and AA Compatibility

## Delivered

- Added a dedicated `IAutoAssemblerLuaHost`; it creates a new NLua state per preprocessing request and never calls `LoadCLRPackage`.
- Removed `os`, `io`, `package`, `debug`, `dofile`, `loadfile`, `require`, and native/module loading from user-visible AA Lua state.
- Added instruction-count, two-second, generated-output, cancellation, and nesting bounds.
- Added `{$LUA}` / `{$ASM}` generated-AA preprocessing, syntax-check mode, and generated-to-original line mapping.
- Added restricted `autoAssembleCheck`, capability-gated `autoAssemble`, and custom `aaCommand` registration through `IAutoAssemblerService`.
- Global Lua's `autoAssemble` and `autoAssembleCheck` compatibility functions now route to the transactional service. AA Lua embedded inside those scripts still runs in the isolated host.
- Added canonical `include` resolution restricted to approved CrxMem include roots, with cycle, depth, existence, traversal, and one-megabyte limits.
- Added `globalalloc` and `sharedalloc` parsing through the exact allocation planner and scoped `fullaccess` behavior through per-write protection transactions.
- Added named diagnostics for executable capability requests and permanently unsupported embedded C, LuaCode, and kernel/driver directives.
- The editor shows the capability summary and requires explicit per-session trust before nested AA Lua process mutation.

## Verification

- Release build passes.
- Tests verify generated source, removed dangerous libraries, original-source hashing, line flow, path traversal rejection, and termination of an infinite Lua loop.
- The Phase 18 native AA mutation test continues to pass through the same transaction service.

## Compatibility boundaries

- `createthread`, `createthreadandwait`, and `loadlibrary` remain disabled because dedicated native integration gates have not yet been added; requesting them produces a named capability diagnostic.
- `reassemble` and broader CE text-assembler operand parity remain explicitly diagnosed when unsupported by the current shared Iced binder.
- `{$LUACODE}`, `{$C}`, `{$CCODE}`, kernel allocations, and driver-only directives remain unsupported by design.
- Filesystem access from Lua remains disabled. Only the separate canonical `include` directive can read approved roots.
- Script records in the main cheat table remain the next main-application milestone; AA service/session APIs remain script-hash/record-ID ready.
