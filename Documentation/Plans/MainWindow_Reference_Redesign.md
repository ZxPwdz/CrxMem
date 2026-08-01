# CrxMem Main Window Reference Redesign

## Objective

Redesign the primary scanner window around the supplied dark-blue reference without changing scanner, process, table, driver, MemoryView, Watchdog Hunter, Lua, or settings behavior.

## Recovery point

The complete pre-redesign source snapshot is stored at:

`C:\Users\admin\Documents\CrxMem_Backups\CrxMem-pre-main-ui-20260721-234416`

It contains the source tree (excluding generated/build directories), the tracked worktree patch, Git status, untracked-file list, SHA-256 manifest, and focused copies of the original MainWindow and theme files.

## Implemented scope

- Native `WindowChrome` with custom vector title-bar controls.
- Truthful version/process/PID/architecture presentation and disconnected state.
- Reference hierarchy: title bar, menu, compact toolbar, scanner workspace, address list, and status bar.
- Responsive 62/38 scanner split and persisted address-list height.
- Scan Results and Address List converted to virtualized, extended-selection WPF DataGrids.
- MainWindow-only dark resource dictionary for cards, inputs, dropdowns, menus, checkboxes, tables, focus, selection, and disabled states.
- Existing named controls and event-handler paths retained.
- Atomic, recoverable window placement and splitter persistence.

## Visual targets

- Default: 1152 x 768 (balanced compact density)
- Minimum: 960 x 640
- Window: `#071421`
- Card: `#0B1826`
- Raised surface: `#101E2D`
- Table header: `#152437`
- Border: `#2A3D52`
- Accent: `#2588F8`
- Primary text: `#ECF3FB`
- Success: `#43D66C`

## Verification

- Release build and test suite.
- MainWindow resource/load checks.
- Extended selection and virtualization checks for both tables.
- Layout-store round-trip and corrupt-file recovery.
- Layout measurement at 960 x 640, 1024 x 700, 1152 x 768, and 1280 x 860.
- Manual verification of process selection, scans, address transfer/edit/freeze, tables, MemoryView, Lua, Watchdog Hunter, driver tools, Settings, and window chrome.

## Deferred milestone

The MemoryView formatting, bookmark provenance, disassembly number formatting, and remaining dropdown cleanup are intentionally deferred and are not part of this MainWindow-only visual milestone.
