# Phase 16 Handoff — Native Call Stack and Symbols

## Delivered

- Added immutable native unwind and symbol-resolution models behind `ICallStackService` and `ISymbolService`.
- Added x64 and WOW64/x86 stack walking through Windows DbgHelp using the debugger's verified paused-thread context.
- DbgHelp initialization, stack walking, symbol lookup, policy changes, and cleanup are serialized through a process-wide gate.
- Resolution falls back predictably from local PDB/export information to module plus RVA.
- Source-file and line information are shown when local symbols provide it.
- Microsoft symbol-server use is disabled by default and requires an explicit consent action in the Tools menu.
- Native unwinding is bounded and cancellation-aware. The existing stack scan remains a visibly labelled heuristic fallback.
- Symbol sessions are released when MemoryView closes or the process changes.

## Verification

- Release build passes.
- The default Release suite passes with native-frame view-model projection coverage.
- The opt-in Windows debugger integration test passes against the isolated benign target and invokes the real DbgHelp unwinder.
- Existing inspection-only process selection remains non-suspending.

## Recovery notes

- A native unwind failure never fabricates frames; the UI reports the error and may use the explicit heuristic fallback.
- Enabling the Microsoft server stores downloaded symbols under the user's local CrxMem symbol cache. It never activates without confirmation.
- Closing the window cleans up the DbgHelp session and does not retain debugger or symbol handles.
