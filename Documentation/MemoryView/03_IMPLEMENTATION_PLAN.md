# MemoryView Incremental Implementation Plan

## Delivery rules

- Every phase ends with a successful Release build and relevant tests.
- The legacy viewer remains launchable until the final parity decision.
- No active-looking command is added before its service path works.
- Each phase updates `CHANGELOG.md`, `05_DEBUG_ENGINE_REPAIR_LOG.md`, and `07_KNOWN_LIMITATIONS.md` when applicable.
- Mutating-memory work is gated behind tests and explicit review.

## Phase 1: Audit and baseline (current)

Files:

- `Documentation/MemoryView/01_EXISTING_IMPLEMENTATION_AUDIT.md`
- `Documentation/MemoryView/02_TARGET_ARCHITECTURE.md`
- `Documentation/MemoryView/03_IMPLEMENTATION_PLAN.md`
- `Documentation/MemoryView/04_UI_COMPONENT_MAP.md`
- `Documentation/MemoryView/05_DEBUG_ENGINE_REPAIR_LOG.md`
- `Documentation/MemoryView/06_TEST_PLAN.md`
- `Documentation/MemoryView/07_KNOWN_LIMITATIONS.md`
- `Documentation/MemoryView/CHANGELOG.md`

Exit criteria: repository builds; no production behavior changed; command/safety findings are recorded.

## Phase 2: Theme foundation and static WPF shell

Add:

- `MemoryView/Wpf/Themes/MemoryView.Colors.xaml`
- `MemoryView/Wpf/Themes/MemoryView.Typography.xaml`
- `MemoryView/Wpf/Themes/MemoryView.Controls.xaml`
- `MemoryView/Wpf/Themes/MemoryView.Toolbar.xaml`
- `MemoryView/Wpf/Themes/MemoryView.Tabs.xaml`
- `MemoryView/Wpf/Themes/MemoryView.DataGrid.xaml`
- `MemoryView/Wpf/Views/MemoryViewWindow.xaml(.cs)`
- `MemoryView/Wpf/ViewModels/MemoryViewViewModel.cs`
- minimal observable/command infrastructure only if an existing implementation cannot be reused.

Modify:

- `CrxMem.csproj` only if explicit XAML resource declarations are required.
- `MainWindow.xaml` and `MainWindow.xaml.cs` to add a clearly labelled WPF preview command while retaining the legacy command.

The static shell uses real attached-process identity and selected address, never sample registers or instructions. Unsupported debug buttons are disabled with tooltips.

## Phase 3: Process-memory and metadata boundaries

Add:

- `MemoryView/Wpf/Services/IProcessMemoryService.cs`
- `MemoryView/Wpf/Services/ProcessMemoryService.cs`
- structured read/write/region result models;
- module/thread service interfaces and ProcessAccess adapters.

Modify:

- `ProcessAccess.cs` only through compatible additions; existing callers remain valid.

Tests:

- partial reads, error propagation, process exit, cancellation, cache invalidation, protection restoration.

## Phase 4: Address expressions and navigation

Add:

- tokenizer/parser/evaluator for absolute, module-relative, register, arithmetic, and bounded dereference expressions;
- navigation history service with back/forward and state restoration;
- unit tests using fake module/register/memory sources.

Integrate the toolbar address box, validation, recent history, Go, Back, and Forward.

## Phase 5: Disassembly service and renderer

Extract reusable Iced logic from `DisassemblerViewControl` into:

- immutable row/token models;
- `IDisassemblyService`;
- bounded async decode implementation;
- custom WPF `VirtualizedDisassemblyControl`;
- `BreakpointGutter` and `BranchFlowOverlay` render layers.

Do not move the legacy control; copy/refactor algorithms behind services while it remains intact.

Tests cover x86/x64 boundaries, invalid bytes, page boundaries, RIP-relative targets, token classification, cancellation, and cache invalidation.

## Phase 6: Register, state, and information panels

Add `IThreadService` and `IRegisterService`. Correctness requirements:

- snapshot is tied to a selected thread and paused/suspended state;
- context buffers are correctly initialized for x86/x64;
- failures produce unavailable state, never demo data;
- previous snapshot/delta calculation is deterministic;
- register writes require paused state and verification.

Build WPF register, flags, debug-state, and information panels matching the reference.

## Phase 7: Hex and stack

Build page-backed `HexDumpViewModel` and virtualized panel. First release supports navigation, selection, copy, read-error display, configured bytes/row, and safe edit transactions. Add interpretations incrementally.

Build stack from the selected thread snapshot. Remove all demo/guessed values from the WPF path. Heuristic return-address labels are marked as heuristic.

## Phase 8: Safe patch editor and history

Implement `IPatchService` before enabling UI mutation commands. Reject longer replacements. Add NOP padding, original-byte verification, protection restoration, instruction-cache flush, read-back verification, cache invalidation, module-relative history, undo/redo, restore, and stale detection.

The legacy assembler remains available only through the legacy viewer and is documented unsafe until repaired.

## Phase 9: Watch, memory map, and metadata

- Watch expressions reuse the address-expression service.
- Memory map uses a virtualized WPF `DataGrid` with filters and navigation.
- Module/thread lists use typed view models.
- Bookmark/comment storage moves to module identity + RVA with a compatible import.

## Phase 10: Debug session and breakpoints

Implement or adapt a real attached debug-session loop. `DebugMonitor` remains a separate access-monitor feature. Required order:

1. attach/detach and process-exit handling;
2. paused/running state and event continuation;
3. software breakpoint transaction and original-byte restoration;
4. single-step reinsertion;
5. thread creation/exit;
6. step into;
7. step over temporary breakpoint;
8. step out/run until return;
9. hardware breakpoint propagation;
10. conditions/hit counts/persistence.

Commands are enabled individually only after their paths pass integration tests.

## Phase 11: Breakpoints, call stack, and advanced panels

Add breakpoint manager synchronization, heuristic call stack with truthful labelling, watch groups, patch history, and optional threads/modules tabs.

## Phase 12: Performance, accessibility, and parity

- profile navigation, scrolling, memory allocations, and refresh;
- verify 100/125/150/200% DPI;
- add font/row-size settings and keyboard coverage;
- persist splitter/column/tab state;
- run a feature-by-feature parity checklist against the legacy viewer and visual reference.

## Phase 13: Default-route and legacy decision

When the WPF viewer passes the acceptance gate:

- make it the default Memory View command;
- keep `Legacy Memory Viewer` for at least one release;
- migrate user metadata/layout;
- document any deliberately deferred feature;
- create `08_FINAL_HANDOFF.md`.

Legacy code is removed only in a later explicit change with user approval and recovery notes.

## First implementation slice after audit

The recommended next commit is Phase 2 only: centralized MemoryView theme resources plus a real-data WPF shell. It should not include debug repair or memory writes. This gives an immediately reviewable visual milestone without hiding correctness work behind a large rewrite.
