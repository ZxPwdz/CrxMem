# MemoryView Target Architecture

## Architectural goals

- Keep the existing `MemoryViewForm` available until WPF feature parity is accepted.
- Move every Win32/process/debug operation behind a service boundary.
- Use WPF + MVVM for state, commands, selection, async loading, and enablement.
- Use custom WPF drawing only for dense disassembly/gutter/branch-flow and, if profiling requires it, the hex surface.
- Never display fabricated target state.
- Every mutation returns a structured, verified result and records enough information to roll back safely.
- All expensive reads support cancellation and stale-request rejection.

## Proposed dependency direction

```text
MemoryViewWindow.xaml
  -> MemoryViewViewModel
       -> INavigationHistoryService
       -> IAddressExpressionService
       -> IDisassemblyService
       -> IRegisterService
       -> IThreadService
       -> IMemoryMapService
       -> IPatchService
       -> IBreakpointService
       -> IDebugSessionService
       -> IUserMetadataService
            -> IProcessMemoryService
                 -> ProcessAccess adapter

Views and controls depend on view models/models only.
Services may depend on lower-level services, never on WPF or WinForms types.
```

## Initial folder structure

Only create files when a phase needs them.

```text
CrxMem/MemoryView/Wpf/
|-- Views/
|   |-- MemoryViewWindow.xaml
|   |-- RegisterPanel.xaml
|   |-- InformationPanel.xaml
|   |-- HexDumpPanel.xaml
|   `-- PatchEditorPanel.xaml
|-- ViewModels/
|   |-- MemoryViewViewModel.cs
|   |-- DisassemblyViewModel.cs
|   |-- RegistersViewModel.cs
|   |-- HexDumpViewModel.cs
|   `-- PatchEditorViewModel.cs
|-- Controls/
|   |-- VirtualizedDisassemblyControl.cs
|   |-- BreakpointGutter.cs
|   `-- BranchFlowOverlay.cs
|-- Models/
|   |-- DisassemblyRow.cs
|   |-- InstructionToken.cs
|   |-- RegisterDisplayItem.cs
|   |-- DebuggerStateSnapshot.cs
|   `-- PatchRecord.cs
|-- Services/
|   |-- ProcessMemoryService.cs
|   |-- DisassemblyService.cs
|   |-- AddressExpressionService.cs
|   |-- NavigationHistoryService.cs
|   |-- PatchService.cs
|   |-- RegisterService.cs
|   |-- ThreadService.cs
|   |-- MemoryMapService.cs
|   |-- UserMetadataService.cs
|   `-- DebugSessionService.cs
`-- Themes/
    |-- MemoryView.Colors.xaml
    |-- MemoryView.Typography.xaml
    |-- MemoryView.Controls.xaml
    |-- MemoryView.Toolbar.xaml
    |-- MemoryView.Tabs.xaml
    `-- MemoryView.DataGrid.xaml
```

The `Wpf` segment avoids name collisions with the existing `CrxMem.MemoryView` WinForms classes during migration.

## Core service contracts

### Process memory

`IProcessMemoryService` should expose structured results rather than nullable arrays or booleans:

```csharp
public interface IProcessMemoryService
{
    ValueTask<MemoryReadResult> ReadAsync(
        ulong address, int length, CancellationToken cancellationToken);

    ValueTask<MemoryWriteResult> WriteVerifiedAsync(
        ulong address, ReadOnlyMemory<byte> bytes,
        MemoryWriteOptions options,
        CancellationToken cancellationToken);

    ValueTask<MemoryRegionInfo?> QueryRegionAsync(
        ulong address, CancellationToken cancellationToken);

    void Invalidate(ulong address, int length);
}
```

Results carry requested/actual length, Win32/driver error, partial-read status, source path, protection restoration status, flush status, and read-back verification.

### Disassembly

`IDisassemblyService.DecodeWindowAsync` accepts address, architecture, row/byte limits, and cancellation. It returns immutable rows and context metadata. It owns Iced decoding and tokenization but never draws.

The initial window should decode a bounded page near the requested address. The renderer requests older/newer pages as the viewport approaches an edge. A request generation ID prevents obsolete results from replacing newer navigation.

### Address expressions

`IAddressExpressionService` parses and evaluates:

- absolute hex;
- `module+RVA`;
- supported register names from a captured snapshot;
- arithmetic;
- bounded pointer dereferences;
- symbols when a symbol provider is available.

Parsing and evaluation are separate so syntax errors can be shown before memory access. Dereference depth and read count are bounded.

### Registers and threads

`IRegisterService` returns immutable snapshots tied to process, thread, timestamp, architecture, and a proven paused/suspended state. It never returns demo values. Editing requires an explicit paused session and returns a verification result.

`IThreadService` owns thread enumeration, safe suspend/resume bookkeeping, context retrieval, and handles thread-exit races. UI refresh does not open handles directly.

### Patch service

`IPatchService` is the only code path allowed to alter executable memory from MemoryView. Its transaction must:

1. resolve a runtime address and module identity;
2. read and verify expected original bytes;
3. reject unsafe longer replacements unless a separate code-cave workflow is used;
4. optionally NOP-pad shorter encodings;
5. change protection;
6. write all bytes;
7. restore protection;
8. flush instruction cache;
9. read back and verify;
10. invalidate caches;
11. record module + RVA, original and patched bytes, state, and time;
12. expose rollback and stale-record outcomes.

No view or control calls `VirtualProtectEx`, `WriteProcessMemory`, or `FlushInstructionCache` directly.

### Breakpoints and debug session

`IDebugSessionService` owns the session state machine:

```text
Detached -> Attaching -> Paused <-> Running -> Detaching -> Detached
                         |   |
                         |   `-> Exited/Faulted
                         `-> Stepping
```

Commands derive `CanExecute` from state. `IBreakpointService` owns breakpoint definitions independently from the current process and maps persistent module-relative records to runtime bindings.

`DebugMonitor` can be adapted behind a separate access-monitor interface. It must not be presented as the attached debugger service.

## View-model boundaries

### `MemoryViewViewModel`

- current process/session identity;
- selected address/thread;
- navigation and debug commands;
- pane layout state;
- status notifications;
- child view models;
- command enablement.

### `DisassemblyViewModel`

- immutable visible window;
- selection;
- bounded async page loading;
- token and marker state;
- follow/copy/bookmark/comment commands.

### `RegistersViewModel`

- current and previous snapshots;
- changed/delta calculation;
- flag decoding;
- edit/follow/copy commands enabled only when valid.

### `HexDumpViewModel`

- page-addressed data source;
- selection and interpretation;
- changed/read-error ranges;
- edit transaction commands;
- synchronized navigation events.

### `PatchEditorViewModel`

- address/original/new instruction;
- validation/encoding preview;
- safe apply/restore/undo commands;
- patch history and stale state.

## Rendering architecture

`VirtualizedDisassemblyControl` is a `FrameworkElement` using `DrawingContext`. It receives rows, selection, column geometry, and semantic marker collections. It does no memory access, decoding, or breakpoint mutation.

Rendering layers:

1. row background/selection/current-IP highlight;
2. breakpoint/bookmark/patch gutter;
3. branch-flow overlay clipped to viewport;
4. address and byte columns;
5. tokenized instruction runs;
6. symbol/comment column;
7. focus and hover adorners.

Only visible rows plus a small buffer are formatted. Frozen brushes, typefaces, and geometries are reused. Column widths and row height live in layout settings.

## Threading and refresh model

- View-model commands use `async` APIs.
- Each navigation owns a linked cancellation token; new navigation cancels the prior request.
- Background services never read WPF/WinForms controls.
- UI updates are dispatched once per completed batch.
- Register refresh is state-driven and throttled; paused snapshots refresh on stop/thread selection, not at 10 Hz unconditionally.
- Running memory refresh is opt-in and uses bounded page batches.
- Process exit cancels all work and transitions the session once.
- Cache invalidation is published after verified writes and module changes.

## Error model

Expected failures use typed results with machine-readable codes and user-facing summaries. Unexpected failures are logged with context. View models expose a nonmodal notification collection and status text. Destructive failures may use confirmation/error dialogs.

Never silently replace failures with fabricated values.

## Layout and visual architecture

The WPF shell follows the reference image:

```text
Menu
Debug/navigation toolbar
Single-session process tab
Context metadata row
Disassembly + right register/state/information inspector
Bottom tools + patch editor
Status bar
```

Nested grids and `GridSplitter` provide four resizable divisions. Initial layout persistence stores logical sizes, selected bottom tab, columns, fonts, and compact/focused preset in the versioned settings JSON.

The first shell exposes only real navigation/view commands. Debug and mutation commands remain omitted or disabled with a reason until their services pass tests.

## Compatibility and fallback

- Keep `MemoryViewForm` and its existing entry path available as `Legacy Memory Viewer`.
- Add the WPF window alongside it.
- Share `ProcessAccess` initially through an adapter; do not duplicate process handles.
- Migrate bookmark/comment data with module-relative identities before changing storage.
- Do not migrate active in-memory NOP records silently; the legacy viewer remains responsible for patches it created.
- Switch default routing only after WPF navigation, disassembly, hex, register truthfulness, and metadata meet the parity gate.
