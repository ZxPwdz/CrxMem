# MemoryView Test Plan

## Test layers

1. Pure unit tests for parsing, decoding, tokenization, state transitions, deltas, and history.
2. Service tests using fake memory/module/thread providers.
3. Windows integration tests against a small purpose-built target process.
4. WPF STA tests for resource loading, command enablement, selection synchronization, and layout serialization.
5. Manual visual/performance checks using the supplied reference image.

Kernel-driver and injected-VEH tests are isolated, opt-in, and never part of the default managed CI run.

## Disassembly

- x86 and x64 decode windows;
- exact instruction boundaries from a known entry point;
- invalid byte emitted as data without infinite loop;
- partial/page-boundary read;
- RIP-relative effective address;
- near call/jump/conditional target;
- token kind and formatted text;
- module/RVA and symbol formatting;
- bounded row/byte counts;
- cancellation and stale-generation rejection;
- cache invalidation after verified patch.

## Address expressions and navigation

- absolute hexadecimal with accepted prefixes/separators;
- case-insensitive module + RVA;
- register and arithmetic expression;
- single/multiple bounded dereferences;
- symbol resolution available/unavailable;
- overflow, invalid syntax, unknown module/register, unreadable pointer;
- back/forward stack and forward truncation after new navigation;
- restored selected row, scroll offset, dump address, and tab.

## Memory service

- complete and partial reads;
- unreadable/unmapped region result;
- process exits during operation;
- user-mode and fake-driver result consistency;
- cancellation;
- cache hit, expiry, and explicit invalidation;
- protection change and restoration on success/failure;
- instruction-cache flush result;
- read-back mismatch;
- no UI/framework dependency.

## Patch service

- equal-length patch;
- shorter patch with explicit NOP padding;
- longer patch rejected;
- stale expected original bytes rejected;
- write failure restores protection;
- flush failure reported;
- read-back mismatch reported;
- cache invalidated after success;
- undo/redo and restore all;
- module rebase and missing/wrong module identity;
- active/disabled/stale history serialization.

## Registers and threads

- x86/x64 snapshot mapping;
- selected thread exits during capture;
- capture rejected when state invariant is not met;
- current/previous and signed delta calculation;
- individual RFLAGS decoding;
- unavailable snapshot contains no demo values;
- verified register edit while paused;
- edit rejected while running;
- handle/suspend bookkeeping balances under failures.

## Breakpoints/debug session

- legal state transitions and command enablement;
- attach/detach/process exit;
- software breakpoint insertion and original-byte verification;
- breakpoint exception, IP correction, single-step, and reinsertion;
- one-shot and hit count;
- step into/over/out flows;
- hardware breakpoint mapping and thread creation propagation;
- thread exit cleanup;
- conditions and thread filters;
- detach restores owned breakpoints and target state.

## Hex, stack, watch, and memory map

- page/region boundaries and partial rows;
- selection across pages;
- high/low nibble entry state;
- paste parse, confirmation, verified write, and undo;
- interpretation endianness and size;
- changed-byte highlighting;
- selected-thread stack address and pointer width;
- no demo fallback;
- watch valid/invalid transitions and previous values;
- memory-map sorting/filtering and navigation.

## WPF UI

- every merged theme resource loads in an STA;
- 100%, 125%, 150%, and 200% DPI review;
- keyboard focus and shortcuts;
- disabled command reason/tooltips;
- splitter/column/tab persistence and reset;
- disassembly/hex/register navigation synchronization;
- bookmark/breakpoint/patch marker synchronization;
- process exit clears/locks applicable panes;
- rapid address navigation cancels obsolete work;
- high-contrast/focus indicators do not rely on color alone.

## Performance targets

Measurements use a Release build after warm-up:

- address navigation produces first useful disassembly within 250 ms for a normal local target;
- continuous disassembly scrolling maintains responsive input without unbounded element growth;
- rapid navigation does not grow pending work or display stale results;
- register snapshot processing performs no repeated polling while paused state is unchanged unless requested;
- hex cache remains bounded;
- a 100,000-region/list-item data source remains usable through virtualization;
- closing the WPF viewer releases view models, tokens, and static-event subscriptions.

Targets are initial engineering budgets, not acceptance claims until measured and recorded.

## Manual acceptance matrix

For every visible command record: target state, command enabled state, observed effect, error path, undo/cleanup path, and whether target memory/thread/protection state was restored. A screenshot comparison checklist covers layout hierarchy, density, semantic colors, resizing, minimum size, and long-value clipping.
