# MemoryView Existing Implementation Audit

Date: 2026-07-18  
Branch: `chore/repository-foundation`  
Reference: `C:\Users\admin\Desktop\CrmMem 2.0\Design Screenshots\MemoryView.png`

## Scope and evidence standard

This audit covers the active Memory Viewer entry point, its WinForms controls, process-memory access, register and stack context access, patching, bookmarks, comments, access monitoring, and the command surface. A feature is classified as working only when its handler and the underlying execution path were inspected. Runtime behavior against arbitrary protected targets is not claimed.

The active WPF shell opens `MemoryViewForm` from `MainWindow.OpenOrFocusMemoryView`. The old viewer remains the only active Memory Viewer and must remain available as the migration fallback.

## Current architecture

```text
WPF MainWindow
  -> opens WinForms MemoryViewForm with the shared ProcessAccess instance
       |-- DisassemblerViewControl (WinForms UserControl, GDI+ custom drawing)
       |    |-- Iced decoder and formatter
       |    |-- hand-written Iced assembler adapter
       |    |-- NOPManager
       |    |-- BookmarkManager / CommentManager static events
       |    `-- direct ProcessAccess and Win32 protection calls
       |-- HexViewControl (WinForms UserControl, GDI+ custom drawing)
       |    |-- independent 4 KiB page cache
       |    |-- direct ProcessAccess reads/writes
       |    `-- launches MemoryAccessForm
       |-- RegistersViewControl (WinForms UserControl, GDI+ custom drawing)
       |    `-- direct OpenThread/GetThreadContext/SetThreadContext calls
       |-- StackViewControl (WinForms UserControl, GDI+ custom drawing)
       |    `-- duplicate thread-context P/Invoke and direct memory reads
       `-- WinForms dialogs for modules, threads, memory map, search, and settings

ProcessAccess
  |-- user-mode ReadProcessMemory / WriteProcessMemory
  |-- optional global kernel-driver path
  |-- process/module/thread enumeration
  |-- protection-changing writes
  `-- process suspend/resume

DebugMonitor
  |-- injected VEH DLL and shared memory
  |-- PAGE_GUARD monitoring
  |-- hardware debug registers through HardwareBreakpointManager
  `-- background polling thread
```

There is no MemoryView MVVM layer, dependency-injection boundary, debug-session state machine, navigation service, patch transaction service, symbol service, or common cancellation/error model.

## UI technology by component

| Component | Technology | Rendering/data approach |
|---|---|---|
| Main application shell | WPF | XAML plus code-behind |
| `MemoryViewForm` | WinForms | Designer-created split containers, tabs, menus, toolbar |
| Disassembly | WinForms custom `UserControl` | GDI+ `OnPaint`; only visible rows drawn from a predecoded list |
| Hex dump | WinForms custom `UserControl` | GDI+ `OnPaint`; synchronous page reads with a 100 ms cache |
| Registers | WinForms custom `UserControl` | GDI+ `OnPaint`; timer-driven context reads |
| Stack | WinForms custom `UserControl` | GDI+ `OnPaint`; one-time context/memory read or demo fallback |
| Bookmarks | WinForms `ListView` | Static JSON-backed manager |
| Watch | Empty WinForms tab | No model or behavior |
| Modules/threads/map | Modal WinForms text dialogs | Fully materialized strings, no sorting/filtering/navigation |
| Access monitor | WinForms `ListView` | `DebugMonitor` events marshalled to UI thread |

## Inspected file inventory

| File | Responsibility | Audit note |
|---|---|---|
| `MemoryView/MemoryViewForm.cs` | Window orchestration, navigation, search, dialogs, refresh | 1,658 lines; owns unrelated UI and scan logic |
| `MemoryView/MemoryViewForm.Designer.cs` | WinForms layout and command declarations | 828 lines |
| `MemoryView/DisassemblerViewControl.cs` | Decode, format, render, navigate, assemble, patch, metadata | 2,340 lines; primary coupling hotspot |
| `MemoryView/HexViewControl.cs` | Hex rendering, selection, editing, page cache | 730 lines |
| `MemoryView/RegistersViewControl.cs` | Register/flag rendering and editing | 654 lines; direct context mutation |
| `MemoryView/StackViewControl.cs` | Stack retrieval/rendering | 389 lines; includes fabricated demo data |
| `MemoryView/NOPManager.cs` | In-memory NOP/restore list | 318 lines; no persistence or read-back verification |
| `MemoryView/MemoryAccessForm.cs` | Find-access UI | 155 lines; starts invasive monitor in constructor |
| `Core/ProcessAccess.cs` | Process handles, memory, modules, threads, injection | 1,612 lines; broad global dependency surface |
| `Core/DebugMonitor.cs` | VEH/page-guard/hardware access monitoring | 1,547 lines; not an attached debugger loop |
| `Core/HardwareBreakpointManager.cs` | DR0-DR3 mutation for current threads | 445 lines; no durable manager model |
| `Core/BookmarkManager.cs` | Bookmark JSON persistence and ASLR re-resolution | Static mutable state, swallowed persistence errors |
| `Core/CommentManager.cs` | Address comment JSON persistence | Absolute-address keys, swallowed persistence errors |

## Command implementation matrix

### Window menus and toolbar

| Command | Visible state | Classification | Evidence/limitation |
|---|---|---|---|
| Open static file | Disabled and marked planned | Placeholder | Handler only selects a file and reports not implemented |
| Save analysis database | Disabled and marked planned | Placeholder | Handler only reports not implemented |
| Export disassembly | Enabled | Working, bounded | Exports the current predecoded instruction list, not a complete module |
| Exit | Enabled | Working | Closes form |
| View CPU | Enabled/checkable | Broken UI-only | No click handler; does not change layout |
| View Registers | Enabled/checkable | Partially working | Collapses entire right pane; no persisted layout |
| View Stack | Enabled/checkable | Broken UI-only | No click handler |
| View Hex Dump | Enabled/checkable | Broken UI-only | No click handler |
| Modules | Enabled | Working, basic | Synchronous text dialog; cached module enumeration |
| Memory Map | Enabled | Working, basic | Synchronous full enumeration into text; no interaction |
| Threads | Enabled | Working, basic | Synchronous process-thread enumeration into text |
| Run/Pause/Stop | Disabled as planned | Placeholder | Handlers only change status text |
| Step into/over/out | Disabled as planned | Placeholder | Handlers only change status text |
| Toggle breakpoint | Disabled as planned | Placeholder | Handler claims toggle but stores no breakpoint |
| Go to address | Enabled | Partially working | Hex and `module+offset`; no registers, dereference, symbols, history, or validation model |
| Find pattern | Enabled | Partially working | Background AOB loop, no cancellation/progress; invalid bytes become wildcards |
| Find references | Enabled | Misleading/limited | Searches only the current decoded window, not the process/module |
| Preferences | Enabled | Working, legacy | Opens the procedural WinForms settings form |
| About | Enabled | Working | Informational dialog |

### Disassembly context commands

| Command | Classification | Notes |
|---|---|---|
| Go to / Follow | Partially working | Follows near branch/call and IP-relative targets; no history stack |
| Copy address/bytes/instructions/formatted lines | Working | Operates on selected cached rows |
| Replace with NOPs | Unsafe partial implementation | Writes and flushes cache; no read-back verification, stale-byte check, persistence, or transaction |
| Restore original bytes | Unsafe partial implementation | Restores in-memory entry without verifying current bytes/module identity |
| Assemble instruction | Unsafe partial implementation | Explicitly permits longer instruction to overwrite following instructions after warning |
| Add/edit comment | Working, limited | Absolute-address persistence; errors swallowed |
| Add/remove bookmark | Working, limited | Module-relative bookmark string supports ASLR re-resolution |
| Keyboard selection/navigation | Working | Operates over predecoded bounded list |

### Hex commands

| Command | Classification | Notes |
|---|---|---|
| Navigate/scroll/select | Working, bounded | Fixed 16 bytes/row and fixed scrollbar range |
| Copy hex selection | Working for practical ranges | Selection length casts to `int`; reads through one-page helper |
| Paste bytes | Unsafe partial implementation | No confirmation, original-byte capture, read-back verification, undo, or region-boundary transaction |
| Type one hex character | Broken | Writes `0x00`–`0x0F` as a whole byte instead of tracking high/low nibbles |
| Display type | Partially working | Changes visual grouping only; six types |
| Make page writable | Unsafe | Permanently changes the full page to executable/read/write and does not restore protection |
| Find what accesses | Experimental | Launches injected VEH/page-guard or hardware-monitor path |

## Working or reusable implementation

- The shared `ProcessAccess` can open a process, perform partial reads, write memory, enumerate regions/modules/threads, change protection, flush instruction cache through `WriteWithProtection`, and optionally route through the existing driver.
- Iced.Intel is already integrated for x86/x64 decoding and syntax-aware formatter output.
- The disassembler draws only visible rows and reuses cached GDI brushes.
- Branch targets and simple branch-flow lines are computed for the current bounded decode window.
- The hex view uses bounded visible-row rendering and a small page cache.
- Module lookup is cached for five seconds in `ProcessAccess`.
- Bookmarks preserve a module-relative source expression when one exists and can be re-resolved after ASLR.
- Register rendering supports x86/x64 contexts and changed-value flags when a valid context is supplied.
- Memory writes used for assembled instructions and NOPs attempt to restore protection and flush the instruction cache.
- The current viewer remains a usable fallback for navigation, viewing, copying, basic metadata, AOB search, comments, bookmarks, and carefully constrained inspection.

## Partially implemented or misleading behavior

- Register values begin as demo values. Refresh calls `GetThreadContext` without suspending a running thread, so it is not a reliable debugger snapshot.
- The register timer refreshes only the first thread selected at form load and does not react to thread creation/exit or active-thread changes.
- The Stack tab is live only when a 64-bit first-thread context read succeeds. Otherwise it guesses a stack-like region and ultimately displays deterministic fabricated values labelled `Demo`.
- `Find References` means references within the currently decoded 16 KiB window, not global cross-references.
- AOB parsing silently treats malformed tokens as wildcards instead of rejecting input.
- The disassembler reads 4 KiB before and 12 KiB after an arbitrary address, then decodes linearly from that raw offset. Starting mid-instruction can yield incorrect boundaries before the target.
- The UI displays no real debugger state, stop reason, current instruction pointer, or selected debug thread.
- Hardware breakpoints are applied to threads existing at the time of the call. There is no central breakpoint collection, hit-count model, conditions, persistence, or guaranteed propagation to new threads in the external fallback.
- `DebugMonitor` is an access-monitor subsystem using target injection and polling. It is not a `WaitForDebugEvent`-based attached-debugger session and cannot power run/step commands as-is.

## Safety and correctness findings

Priority definitions: P0 can corrupt target state; P1 blocks trustworthy debugging; P2 affects reliability/performance; P3 is maintainability/polish.

| Priority | Finding | Consequence |
|---|---|---|
| P0 | Longer assembled instructions may overwrite subsequent instructions | Target code corruption/crash |
| P0 | Hex `Make page writable` never restores original protection | Persistent target security/state change |
| P0 | Patch/NOP restore does not verify current bytes or module identity | May restore stale bytes to a reused address |
| P1 | Register editing and reads do not establish a debugger-paused/suspended invariant | Context calls can fail or race execution |
| P1 | Stack fallback presents fabricated values inside a real attached session | Misleading analysis data |
| P1 | No patch read-back verification or atomic transaction result | UI can claim success without verified state |
| P1 | Debug command handlers are not connected to a debug session | No attached debugger behavior |
| P1 | Memory APIs and P/Invoke are owned by visual controls | Hard to test and unsafe to reuse |
| P2 | Register context polling runs every 100 ms on the WinForms UI thread | Repeated handle calls and UI stalls |
| P2 | Memory-map/module/thread enumeration and reference search run on UI thread | Window freezes on large targets |
| P2 | AOB scan has no cancellation and reads up to 50 MB per region | High allocations, delayed close, poor progress feedback |
| P2 | Background AOB lambda reads a WinForms checkbox from the worker thread | Cross-thread control access |
| P2 | Hex page reads occur inside `OnPaint` | Painting can block on target/driver latency |
| P2 | Disassembly decodes and resolves thousands of rows synchronously during navigation | Navigation stalls and excess work |
| P2 | Static bookmark/comment event subscriptions are not removed on control/form disposal | Closed viewers remain referenced and can leak |
| P2 | Several persistence and scan exceptions are swallowed | Failures are invisible and hard to diagnose |
| P3 | Three independent caches exist without shared invalidation policy | Stale data after writes/navigation |
| P3 | Duplicate context structures/P/Invoke exist in Stack and Core | Architecture drift and marshalling risk |

## Rendering and performance assessment

### Positive

- GDI+ controls draw only currently visible rows.
- Disassembly maintains a decoded list rather than creating a child control for each row.
- Hex memory pages and module lookups are cached.
- Disassembly brush reuse reduces paint-time allocations.

### Bottlenecks

- `DisassembleFromAddress` synchronously reads 16 KiB, decodes as many as 5,000 instructions, resolves addresses/comments, calculates all flow lines, and updates selection during navigation.
- The full decoded list is retained even though only visible rows are painted. This is bounded, but not demand virtualized.
- Hex `OnPaint` can initiate multiple process reads. A slow driver/read stalls message processing.
- Registers open a thread handle and request a full context ten times per second.
- Stack symbol/comment guesses enumerate modules repeatedly through the shared cache.
- Memory map creates one large string and text box rather than a virtualized collection.
- No navigation, decode, memory-page, module, or symbol work has cancellation tokens.
- No request generation/version prevents an obsolete background result from replacing newer navigation state.

## Tight coupling and duplicate logic

- `DisassemblerViewControl` combines rendering, decoder state, assembler parsing, memory protection, process writes, patch history, navigation, bookmarks, comments, clipboard, dialogs, and context menus.
- `MemoryViewForm` combines shell layout, navigation parsing, AOB scanning, reference analysis, metadata formatting, process/thread/module dialogs, settings, and refresh timing.
- `RegistersViewControl` and `StackViewControl` call thread-context APIs directly rather than sharing a thread/register service.
- Protection-changing and instruction-cache logic is duplicated in `ProcessAccess`, `DisassemblerViewControl`, and `NOPManager`.
- Module+offset formatting/parsing is repeated across the form, disassembler, bookmarks, results dialogs, and the WPF shell.
- Theme values are split between WinForms `ThemeManager` colors and WPF resource dictionaries.
- Driver selection is controlled through static properties on `AntiCheatBypassForm`, coupling core memory behavior to a form.

## Missing abstractions

- Debug session and state snapshot
- Process-memory service with structured partial-read/write results
- Safe patch transaction and history store
- Breakpoint service and synchronized breakpoint model
- Thread/register service with pause invariant
- Disassembly service with bounded decode requests and cancellation
- Symbol/address expression service
- Memory-map/module service interfaces
- Navigation history
- User metadata repository keyed by module identity and RVA
- Shared cache invalidation coordinator
- Error/log notification service
- Clock/scheduler abstraction for refresh tests

## Error handling and lifecycle gaps

- `ProcessAccess.Read` is declared non-nullable but returns `null` on failure.
- Many APIs return only `bool` or `null`, losing Win32 error code, partial length, and failure classification.
- Bookmark/comment persistence catches all exceptions and reports nothing.
- Scan loops swallow region exceptions.
- The viewer does not centrally react to process exit or detach.
- Static bookmark/comment subscriptions are not unsubscribed.
- MemoryAccessForm stops monitoring on close but does not dispose the monitor explicitly.
- Patch records exist only inside a single disassembler control instance.
- No unsaved-patch close confirmation exists.

## Test coverage

The current test project covers scanner behavior, table serialization, and JSON settings storage. It contains no tests for:

- disassembly or instruction boundaries;
- address expressions;
- hex selection/editing;
- register snapshots or changes;
- stack retrieval;
- memory protection restoration;
- write/read-back verification;
- NOP/patch undo;
- breakpoint insertion/reinsertion;
- debug-event processing;
- navigation history;
- bookmark/comment migration;
- MemoryView lifecycle/event leaks;
- rendering/theme resource loading;
- cancellation or rapid navigation.

## Recommended migration sequence

1. Freeze the legacy viewer as `MemoryViewForm` fallback and document its supported surface.
2. Add result-oriented interfaces around process memory, modules, threads, and contexts without changing the current shell.
3. Implement and test a safe patch service before exposing patch controls in WPF.
4. Implement address expressions and navigation history as UI-independent services.
5. Build centralized MemoryView WPF theme resources and a static shell matching the reference hierarchy.
6. Add a `MemoryViewViewModel` and state/command enablement using only proven services.
7. Implement bounded asynchronous disassembly and a WPF custom renderer.
8. Add truthful register/information panels; never show demo values for an attached target.
9. Implement virtualized hex and stack views on the shared memory/thread services.
10. Add breakpoint/debug-session services. Keep run/step controls disabled until an attached debug loop is tested.
11. Add watch, breakpoint, call-stack, and memory-map panels incrementally.
12. Route the main shell to the WPF viewer by default only after navigation/viewing parity; retain a `Legacy Memory Viewer` escape hatch until full acceptance.

## Audit conclusion

The current Memory Viewer has useful reusable decoding, memory, metadata, and custom-rendering code, but it is not a debugger workspace and cannot be safely restyled in place. The correct path is incremental service extraction plus a parallel WPF/MVVM shell. Patching, register context, fabricated stack data, and UI-thread memory access are the first correctness gates. Visual debugger commands must remain disabled until a real debug-session state machine exists.
