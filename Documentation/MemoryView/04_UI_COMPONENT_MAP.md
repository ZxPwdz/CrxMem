# MemoryView UI Component Map

## Reference-to-WPF map

| Reference region | WPF component | Initial data source | Phase | Initial command policy |
|---|---|---|---|---|
| Menu bar | `MemoryViewWindow` menu | `MemoryViewViewModel` | 2 | Navigation/view commands only; debug commands disabled |
| Debug toolbar | Styled `ToolBar` | Session/navigation commands | 2/10 | Attach state shown; run/step omitted or disabled until tested |
| Address box | Address expression control | `IAddressExpressionService` | 4 | Enabled after parser/evaluator tests |
| Process tab | Single-session tab header | Existing attached `ProcessAccess.Target` | 2 | One session initially; no fake multi-session |
| Context header | Metadata strip | module/region/disassembly services | 3/5 | Copyable real values; unavailable shown as `—` |
| Disassembly | `VirtualizedDisassemblyControl` | `DisassemblyViewModel` | 5 | Navigation/copy/bookmark/comment first |
| State gutter | `BreakpointGutter` drawing layer | row marker collection | 5/10 | Bookmarks and RIP when real; breakpoints after service |
| Branch overlay | `BranchFlowOverlay` | decoded branch models | 5 | Visible-window branches only |
| Registers | `RegisterPanel` | `RegistersViewModel` | 6 | Read-only until paused verified edit path |
| Flags/state | `RegisterPanel` sections | snapshot/session state | 6/10 | No misleading last-error fields |
| Information | `InformationPanel` | selected row + module/region | 6 | Real values only |
| Bottom tabs | compact `TabControl` | child view models | 2+ | Empty future tabs omitted, not faked |
| Hex Dump | `HexDumpPanel` | page-backed hex VM | 7 | Read/copy first; edits after patch-memory safety |
| Stack | `StackPanelView` | selected thread snapshot | 7 | Unavailable on failure; never demo data |
| Watch | `WatchPanel` | address expressions | 9 | Added when expressions work |
| Breakpoints | `BreakpointsPanel` | breakpoint service | 10/11 | Added when manager model works |
| Call Stack | `CallStackPanel` | call-stack service | 11 | Heuristic status visible |
| Memory Map | WPF virtualized `DataGrid` | memory-map service | 9 | Sort/filter/follow |
| Patch editor | `PatchEditorPanel` | patch service | 8 | Apply disabled until validation succeeds |
| Status bar | `DebuggerStatusBar` | status/session/notification | 2/10 | Explicit `Attached (inspection only)` vs debugger state |

## Visual tokens

Theme resources will use the specification tokens under the `MemoryView.*` prefix. The initial reference-derived values are:

| Semantic use | Value |
|---|---|
| Window | `#101419` |
| Panel | `#151A20` |
| Raised/toolbar | `#1A2027` / `#171C22` |
| Header | `#1B2128` |
| Border | `#2B333C` |
| Selection | `#174B75` |
| Primary/secondary text | `#D8DEE5` / `#9AA6B2` |
| Mnemonic/register/immediate | blue/purple/yellow semantic brushes |
| Breakpoint | `#F25B5B` |
| Instruction pointer | `#E2B84B` plus arrow shape |
| Bookmark | `#4DA8FF` |
| Patch | `#D98CFF` |
| Changed register | `#F17C68` |

No production control should hardcode these colors.

## Reference density targets

- Menu: approximately 28 logical pixels high.
- Toolbar: approximately 40 logical pixels high.
- Process tabs: approximately 28 logical pixels high.
- Metadata: 24–28 logical pixels high.
- Disassembly rows: configurable 18–22 logical pixels.
- Right inspector: approximately 25% of initial width, resizable.
- Bottom tools: approximately 35% of initial content height, resizable.
- Patch editor: approximately 35% of bottom width, resizable.
- General UI font: Segoe UI 12–13 px equivalent.
- Dense data font: Cascadia Mono with Consolas fallback, 12–13 px equivalent.

## Interaction map

| Input | Target behavior |
|---|---|
| `Ctrl+G` | Focus address expression box |
| `Enter` in address box | Validate and navigate |
| `Alt+Left` / `Alt+Right` | Navigation back/forward |
| Arrow/Page keys | Move disassembly selection |
| `Enter`/`Space` | Follow valid selected target |
| `Ctrl+C` | Copy context-sensitive selection |
| `Ctrl+D` | Toggle bookmark |
| `;` | Edit comment |
| `F2` | Toggle breakpoint only after breakpoint service is active |
| Gutter click | Toggle breakpoint only after breakpoint service is active |
| Splitter drag | Resize and persist pane layout |

## Legacy parity map

| Legacy feature | WPF replacement | Parity gate |
|---|---|---|
| Hex/module+offset navigation | Address expression service | Absolute and module+RVA tests pass |
| Bounded disassembly | Async disassembly service/control | Same or better valid rows around target |
| Branch lines | Branch overlay | Correct target mapping within viewport |
| Copy formats | Disassembly commands | All current formats retained |
| Comments/bookmarks | Metadata service | Existing JSON imports without loss |
| Hex view/copy | Hex panel | Read errors and selection are truthful |
| Registers | Register panel | No demo values; selected-thread snapshot |
| Stack | Stack panel | No guessed/demo fallback |
| Modules/map/threads | Typed WPF panels | Sort/filter/follow replaces text dialogs |
| AOB search | Shared scanner/search results | Cancellation and progress added |
| NOP/assemble/restore | Patch editor/service | Verification and rollback pass tests |
| Find what accesses | Access-monitor window/service | Remains explicitly experimental |
