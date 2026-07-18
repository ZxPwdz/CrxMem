# MemoryView Debug Engine Repair Log

## Baseline — 2026-07-18

No debugger behavior was changed during the audit phase.

### Confirmed engine inventory

- `ProcessAccess` exposes Windows debug API P/Invoke declarations, process suspend/resume, thread context functions, and memory operations.
- No active MemoryView code owns a `WaitForDebugEvent` / `ContinueDebugEvent` loop.
- `DebugMonitor` is a target-injected VEH access monitor using shared memory, PAGE_GUARD, or hardware debug registers. It is not an attached debugger session.
- `HardwareBreakpointManager` mutates DR0–DR3 for threads present during a set/clear request.
- MemoryView Run, Pause, Stop, Step Into, Step Over, Step Out, and Toggle Breakpoint handlers are placeholders and are disabled by the current UI.

### Confirmed repair prerequisites

1. Define a debug-session state machine and event-loop ownership.
2. Establish safe thread-handle/context lifetime and paused-state invariants.
3. Separate access monitoring from execution debugging.
4. Implement software-breakpoint original-byte storage and verified reinsertion.
5. Handle thread creation/exit before promising hardware breakpoint persistence.
6. Add process-exit/detach cleanup and never kill target on debugger shutdown unless explicitly selected.
7. Add integration tests against a purpose-built benign debug target.

### Commands intentionally unavailable

| Command | Reason |
|---|---|
| Run/Continue | No attached debug-session state |
| Pause | Existing process-wide suspend is not equivalent to debugger break |
| Step Into | No trap-flag/debug-event loop |
| Step Over | No temporary-breakpoint lifecycle |
| Step Out | No call-stack/return-address execution control |
| Toggle software breakpoint | No central breakpoint transaction/reinsertion model |
| Breakpoint manager | No synchronized breakpoint collection |

Entries will be appended when a command obtains a tested implementation path.

## Phase 2 preview — 2026-07-18

- Added no debugger behavior and made no debugger correctness claims.
- The WPF preview labels itself `Inspection mode` and keeps Attach, Run, Pause, stepping, breakpoint, register-editing, assembly, and patch commands disabled.
- The legacy viewer remains available for existing behavior; its known debugger and patching limitations are unchanged.

## Phase 6A state/register foundation — 2026-07-18

- Added explicit debug-session states and a single paused-selected-thread invariant for context access.
- Added truthful x86/x64 register capture results with initialized native buffers, balanced handles, previous values, and deltas.
- Context reads reject detached, attaching, running, detaching, exited, and faulted states before opening a thread.
- No execution command was enabled. Native attach/event continuation remains blocked on a purpose-built integration target and review of the existing `DEBUG_EVENT` interop layout.
