# MemoryView Development Changelog

## 2026-07-18 — Phase 6B native debug session and registers UI

### Features added or repaired

- Added a dedicated `WaitForDebugEvent` / `ContinueDebugEvent` loop separate from `DebugMonitor`.
- Implemented attach, non-destructive detach, explicit debugger break, resume, process-exit state, pending-event ownership, and kill-on-exit disablement.
- Corrected the 64-bit `DEBUG_EVENT` union alignment and added bounded queued-event draining for reliable detach.
- Connected Attach, Detach, Pause, and Run commands to the WPF toolbar and Debug menu.
- Connected verified paused-thread x86/x64 register snapshots, flags, changed-value highlighting, and RIP selection to the register pane.
- Added the isolated `CrxMem.DebugTarget` executable for native integration testing.

### Tests added

- Fake-native event sequencing, attach failure, pending-event continuation, debug view-model/register synchronization, and isolated real Windows attach/pause/resume/detach.
- The native detach transaction was repeated five consecutive times after its queued-event repair.
- Release result: 55 default tests passed, 0 failed; the opt-in native integration test passed.

### Known issues

- Step Into/Over/Out, run-to-cursor, software/hardware breakpoints, exception policy UI, and register editing remain disabled.
- Native integration is opt-in through `CRXMEM_RUN_DEBUG_TESTS=1` because it exercises Windows debugging APIs.

### Next recommended task

Phase 7: implement the page-backed virtualized hex view, selected-thread stack, and expression-based watch tool before enabling memory writes.

## 2026-07-18 — Phase 6A debug/thread/register contracts

### Features added or repaired

- Added explicit detached/attaching/running/paused/detaching/exited/faulted debug-session states.
- Added typed thread descriptors and a `ProcessAccess` thread adapter.
- Added x86/x64 register snapshot models, previous-value deltas, and a Windows context adapter.
- Enforced that context capture requires the selected process, a selected thread, and a verified paused debug-session snapshot.
- Correctly initializes native context buffer arrays and balances every opened thread handle.

### Tests added

- Every non-paused state is rejected, missing selected threads fail the invariant, unavailable processes return no data, and register deltas are deterministic.
- Release result: 51 tests passed, 0 failed; solution build completed with 0 errors.

### Known issues

- This is the contract/snapshot half of Phase 6. No attached debugger event loop exists yet, so the UI correctly keeps register and execution commands disabled.
- Register write-back is intentionally absent.

### Next recommended task

Build a purpose-built debugger target and verify native attach/detach/event continuation before connecting these contracts to the WPF command surface.

## 2026-07-18 — Phase 5 disassembly service and renderer

### Features added or repaired

- Extracted bounded, cancellable Iced decoding behind `IDisassemblyService`.
- Added immutable syntax-token and branch-target data to disassembly rows.
- Replaced the interim disassembly DataGrid with a custom `IScrollInfo` renderer that draws only visible rows.
- Added keyboard selection, paging, Home/End, Ctrl+C, mouse selection/wheel navigation, syntax colors, bookmark gutter markers, and visible branch-flow lines.
- Kept decoding capped at 300 instructions per snapshot and invalid bytes advancing safely as `db` rows.

### Tests added

- x64 tokenization, branch targets, invalid trailing bytes, decode bounds, cancellation, and a 1,000-row render virtualization smoke test.
- Release result: 42 tests passed, 0 failed; solution build completed with 0 errors.

### Known issues

- Breakpoint and current-instruction markers await verified debugger state.
- The renderer uses fixed initial column geometry; interactive resizing and persisted column widths remain deferred.
- Branch-flow lanes are intentionally simple and show only targets inside the currently rendered rows.

### Next recommended task

Phase 6: establish debug-session/thread/register state contracts and expose only verified paused snapshots.

## 2026-07-18 — Phase 4 expression navigation and layout

### Features added or repaired

- Added an asynchronous address-expression parser/evaluator with hexadecimal literals, modules, arithmetic, parentheses, optional registers/symbols, and up to four `[expression]` pointer dereferences.
- Added bounded Back/Forward history with forward-stack truncation and selected-instruction restoration.
- Added stale-navigation generation checks in addition to cancellation.
- Added recent-expression history and versioned JSON persistence for window placement and primary pane sizes.
- Added toolbar Back/Forward actions, Alt+Left/Alt+Right shortcuts, and an editable recent-address dropdown.

### Tests added

- Valid and invalid expression forms, module/register/symbol resolution, nested pointers, unreadable pointers, arithmetic errors, history semantics, capacity bounds, and layout persistence recovery.
- Release result: 39 tests passed, 0 failed; solution build completed with 0 errors.

### Known issues

- Register expressions remain unavailable in the running preview until a verified paused-thread register source is connected.
- Symbol expressions remain unavailable until a symbol provider is implemented.
- Navigation history is session-scoped; only recent expression text and layout are persisted.

### Next recommended task

Phase 5: extract the disassembly service and replace the interim DataGrid with a bounded custom virtualized renderer.

## 2026-07-18 — Phase 3 process-inspection boundaries

### Features added or repaired

- Added typed process identity, module, region, memory-location, and memory-read result models.
- Added `IProcessMemoryService`, `IModuleService`, and `IMemoryMetadataService` with `ProcessAccess` compatibility adapters.
- Made complete, partial, unreadable, unavailable-process, and cancelled reads explicit.
- Refactored the WPF preview to consume services instead of invoking process-memory and metadata APIs directly.
- Added module-name plus RVA metadata locations while retaining the existing runtime-address managers as the compatibility backend.

### Tests added

- Unopened-process result behavior, partial reads, unreadable code, module-relative metadata context, and supported address forms.
- Release result: 21 tests passed, 0 failed; solution build completed with 0 errors.

### Known issues

- Legacy comment/bookmark persistence still stores runtime addresses; durable module identity migration is deferred until a versioned metadata store is introduced.
- This phase adds read-only boundaries only; safe writes are gated behind `IPatchService`.

### Next recommended task

Phase 4: replace the basic address resolver with a tested expression parser and navigation history.

## 2026-07-18 — Phase 2 WPF preview shell

### Files added

- WPF MemoryView theme dictionaries, preview models, read-only data service, view model, and window.
- WPF view-model and compiled-resource smoke tests.

### Features added or repaired

- Added a separately labelled `Memory View (WPF Preview)` route under Tools while preserving the legacy viewer.
- Added real attached-process identity, module context, bounded Iced disassembly, memory-region information, comments, bookmarks, and read-only hex rows.
- Added asynchronous navigation, refresh, cancellation, process-exit errors, and deterministic view-model cleanup.
- Recreated the reference hierarchy with a dense dark debugger toolbar, disassembly workspace, bottom tools, register placeholder, information pane, splitters, and status bar.
- Disabled every debugger and memory-mutation command that does not yet have a verified service path.

### Features removed

- None. The legacy MemoryView remains the default and unchanged.

### Tests added

- View-model initialization and unavailable-address behavior.
- WPF compiled resource-dictionary loading on an STA thread.
- Release result: 14 tests passed, 0 failed; solution build completed with 0 errors.

### Known issues

- The preview disassembly and hex tables use WPF `DataGrid` as an interim renderer.
- Address expressions support only absolute hexadecimal and `module+offset` in this phase.
- Debugger state, registers, stack, watches, breakpoints, call stack, memory map, assembly, and patching remain intentionally disabled.

### Next recommended task

Phase 3: extract typed process-memory, module, region, and metadata service boundaries before adding more interactive behavior.

## 2026-07-18 — Phase 1 audit

### Files added

- `01_EXISTING_IMPLEMENTATION_AUDIT.md`
- `02_TARGET_ARCHITECTURE.md`
- `03_IMPLEMENTATION_PLAN.md`
- `04_UI_COMPONENT_MAP.md`
- `05_DEBUG_ENGINE_REPAIR_LOG.md`
- `06_TEST_PLAN.md`
- `07_KNOWN_LIMITATIONS.md`
- `CHANGELOG.md`

### Features added or repaired

- None. This phase intentionally changes no production behavior.

### Features removed

- None.

### Tests added

- None. The audit defines the required test matrix for subsequent phases.

### Findings

- Existing MemoryView is a hybrid WPF-to-WinForms subsystem.
- Useful Iced decoding, visible-row GDI rendering, memory access, bookmark/comment, and module-resolution code can be reused behind services.
- Unsafe longer-instruction patching, permanent page-protection changes, unreliable register polling, fabricated stack data, and missing write verification are correctness blockers.
- `DebugMonitor` is an injected access-monitor feature, not an attached debugger engine.

### Known issues

See `01_EXISTING_IMPLEMENTATION_AUDIT.md` and `07_KNOWN_LIMITATIONS.md`.

### Next recommended task

Phase 2: create centralized MemoryView WPF theme resources and a real-data static shell matching the supplied reference. Keep debug and memory-mutation commands disabled until their service paths are implemented and tested.
