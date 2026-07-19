# Cheat Engine Auto Assembler Research and CrxMem Design

## Executive conclusion

Cheat Engine Auto Assembler (AA) is a stateful script compiler and loader. Lua is not its core language. The AA engine parses directives and assembly, resolves symbols and forward labels, allocates target memory, produces machine-code write blocks, applies them, and retains enable-time state so a later `[DISABLE]` run can restore code, unregister symbols, and free allocations.

Lua is an optional host-side metaprogramming and extension layer. Exact feature parity eventually requires Lua, but a correct AA core must exist independently first.

CrxMem already references NLua 1.7.3 and exposes a Lua engine. However, `LuaScripting/LuaAssemblerFunctions.cs::AutoAssemble` is currently a placeholder: it recognizes only a few constructs, always chooses the enable section, writes directly through `ProcessAccess.WriteWithProtection`, does not retain disable state, does not verify expected bytes, and suppresses failures behind a Boolean. It must not be treated as a production AA implementation.

## Primary references

- Local Delphi reference: `C:\Users\admin\source\repos\CrxMem\CE_Source\ProcessHelper\autoassembler.pas`
- Local code-block compiler: `C:\Users\admin\source\repos\CrxMem\CE_Source\ProcessHelper\autoassemblercode.pas`
- Local enable/disable owner: `C:\Users\admin\source\repos\CrxMem\CE_Source\ProcessHelper\MemoryRecordUnit.pas`
- Local template generator: `C:\Users\admin\source\repos\CrxMem\CE_Source\ProcessHelper\frmautoinjectunit.pas`
- Current upstream implementation: <https://github.com/cheat-engine/cheat-engine/blob/master/Cheat%20Engine/autoassembler.pas>
- Official command catalog: <https://wiki.cheatengine.org/index.php?title=Auto_Assembler:Commands>
- Official language overview: <https://wiki.cheatengine.org/index.php?title=Cheat_Engine:Auto_Assembler>
- Official Lua block behavior: <https://wiki.cheatengine.org/index.php?title=Auto_Assembler:LUA_ASM>

The implementation should reproduce documented behavior through CrxMem-owned C# services and tests. Source text should not be copied mechanically without first resolving licensing compatibility with CrxMem's MIT license.

## What happens when an AA script is toggled

### Section selection

A table script normally has `[ENABLE]` and `[DISABLE]` sections. Lines outside both sections run in either direction. Enabling selects common plus enable lines; disabling selects common plus disable lines. Duplicate or missing section markers are errors. `[32-BIT]` and `[64-BIT]` blocks are filtered for the target architecture.

The memory-record owner retains a disable-state object between toggles. Cheat Engine calls the same engine with an enable/disable flag and passes that state back during disable.

### Preprocessing

The reference implementation performs these operations before normal assembly:

1. Attach original source line numbers for diagnostics.
2. Select enable or disable content and target architecture.
3. Run registered prologue callbacks.
4. Evaluate `{$LUA}` blocks. The Lua chunk receives `syntaxcheck` and `memrec`; a returned string is inserted back into the AA source.
5. Lower `{$LUACODE}`, `{$C}`, and `{$CCODE}` blocks into generated allocations, labels, callbacks, or compiled code.
6. Remove comments, parse structures, discover implicit labels when not in strict mode, and process `{$TRY}`/`{$EXCEPT}` regions.
7. Run AOB scans early and replace successful results with definitions.

Lua blocks also run during syntax checking. A compatible host must provide the `syntaxcheck` flag so scripts can avoid permanent side effects while still returning enough generated AA text to validate.

### First pass

The first pass builds the symbol and operation model:

- `define` performs token substitution.
- `label` declares forward labels; `name:` either selects a known address or defines a declared label at the current output position.
- `alloc`, `allocnx`, and `allocxo` describe target allocations, optionally near a preferred address.
- `globalalloc` and `sharedalloc` have longer-lived/shared identity semantics.
- `registersymbol` and `unregistersymbol` are queued symbol-table operations.
- `assert` reads target memory and aborts if the expected byte pattern does not match.
- `readmem`, `loadbinary`, `reassemble`, `fullaccess`, `include`, thread creation, and library loading are lowered into later operations.
- Normal instructions and data directives are retained as assembler lines.

On x64, preferred/near allocation is important because a normal relative jump has a ±2 GB reach. If a suitable near allocation cannot be obtained, templates or encodings may require a larger absolute jump sequence.

### Allocation and second pass

After declarations are known, the engine allocates memory and substitutes actual addresses. The second pass walks the assembler lines with a current address:

- Known-address `name:` lines move the output cursor.
- Unknown declared labels bind to the current cursor.
- Assembly instructions and `db`/`dw`/`dd`/`dq` data generate bytes.
- Forward label references are initially sized conservatively, then reassembled after the label is known.
- If the final encoding is shorter than the reserved size, the remainder is filled with architecture-appropriate NOPs; a larger-than-reserved result is an error.
- Adjacent output blocks are coalesced before writing.

This is why a line-at-a-time assembler cannot implement AA correctly. Allocation addresses and forward labels affect final instruction sizes and branch encodings.

### Apply and retained disable state

The reference engine changes protection, writes generated blocks, restores protection, performs queued actions, updates symbols, and handles deallocation last. Enable-time state includes:

- allocation name, address, size, preferred address, and protection;
- registered symbols;
- all resolved labels/allocations/address-like definitions needed by disable code;
- exception regions and compiled-code symbol/source information.

The disable section can therefore resolve enable-time allocation names, unregister symbols, restore original instructions supplied by the script, and deallocate the correct live addresses after ASLR.

CrxMem should improve on the reference write boundary: compile a complete plan first, verify every expected target range, apply a multi-range transaction, restore all protections, flush the instruction cache, read back every block, and roll back already-written blocks if a later block fails.

## Language and directive surface

### Core syntax

- Case-insensitive commands and identifiers.
- `//`, semicolon, and block comments as supported by the parser.
- Hex by default; `#value`, `(int)value`, `(float)value`, and `(double)value` typed literals.
- Address expressions, quoted module names, labels, symbols, and `name:` cursor/definition syntax.
- Assembly plus data directives such as `db`, `dw`, `dd`, `dq`, strings, and alignment/NOP directives.
- `[ENABLE]`, `[DISABLE]`, `[32-BIT]`, `[64-BIT]`, `{$STRICT}`, `{$TRY}`, and `{$EXCEPT}`.

### Core AA directives

- Symbols and substitution: `define`, `label`, `registersymbol`, `unregistersymbol`, `struct`/`endstruct`.
- Discovery: `aobscan`, `aobscanmodule`, `aobscanregion`, with wildcard patterns.
- Memory: `alloc`, `allocnx`, `allocxo`, `dealloc`, `globalalloc`, `sharedalloc`, `fullaccess`, `readmem`, `loadbinary`.
- Validation/relocation: `assert`, `reassemble`, `include`, `align`.
- Execution: `createthread`, `createthreadandwait`, `loadlibrary`.
- Extensibility: `luacall`, `{$LUA}`/`{$ASM}`, registered AA commands and prologues.
- Advanced runtime blocks: `{$LUACODE}`, `{$C}`, and `{$CCODE}`.
- Kernel-only behavior such as `kalloc` is a separate capability and should not be implied by user-mode parity.

## Lua's actual roles

### Host preprocessing: `{$LUA}`

Runs in CrxMem/CE before assembly. It can validate conditions, calculate values, perform host actions, or return AA source that replaces the block. This is the first Lua feature CrxMem should support.

### Immediate host execution: `luacall(...)`

Executes a host Lua statement while compiling/executing the AA script. It is not target machine code.

### Extension commands

Cheat Engine lets Lua register custom AA commands. A matching design should expose a registry whose callbacks accept parameters and syntax-check state and return replacement AA text. Core directives must not depend on Lua callbacks.

### Target-triggered Lua: `{$LUACODE}`

This is substantially different. Generated target code transfers register values to a helper/client bridge so Lua runs in the host when the target reaches that block, then optionally copies changed values back. Upstream build instructions identify a separate `luaclient` component for this capability. It should be a late, opt-in feature with strict reentrancy and debugger/process-exit handling.

### C blocks

`{$C}` and `{$CCODE}` depend on a C compiler/runtime path and generated symbol/reference plumbing. They are not Lua features and should be deferred until the AA core is stable.

## Recommended CrxMem architecture

```text
AA editor / memory record / Lua autoAssemble API
                    |
              IAutoAssemblerService
                    |
        parse -> bind -> plan -> validate
          |       |       |        |
         AST   symbols  writes   assertions
                    |
          AutoAssemblerExecutionPlan
                    |
      IAutoAssemblerTransactionExecutor
       |          |          |          |
 allocations  patch batch  symbols  queued actions
                    |
        AutoAssemblerSessionState
        (retained for [DISABLE])
```

Recommended contracts:

- `IAutoAssemblerParser`: source to syntax tree with exact spans and diagnostics.
- `IAutoAssemblerPreprocessor`: sections, architecture, includes, defines, optional Lua expansion.
- `IAutoAssemblerBinder`: labels, modules, symbols, AOB results, allocation requests, forward references.
- `IAutoAssemblerCompiler`: immutable write blocks and queued operations; no target mutation.
- `IAutoAssemblerTransactionExecutor`: allocation plus verified multi-range mutation and rollback.
- `IAutoAssemblerSessionStore`: enable-time state associated with a table record and target identity.
- `IAutoAssemblerLuaHost`: optional trusted preprocessing API built on the existing NLua engine.
- `IAutoAssemblerCommandRegistry`: extension callbacks that return source/AST nodes.
- `IAutoAssemblerTemplateService`: code injection, AOB injection, full injection, API hook, relocation, and table-framework generation.

The compiler must return a preview containing resolved addresses, allocated ranges, original bytes, replacement bytes, protection changes, symbols, external actions, and diagnostics before Apply is allowed.

## Safety and trust model

AA scripts can modify executable memory, allocate executable pages, spawn remote threads, load DLLs, execute Lua, access files through includes, and potentially invoke CLR APIs through CrxMem's current `LoadCLRPackage` configuration. Treat downloaded scripts as executable code, not data.

Required controls:

- Trusted-script prompt and a capability summary before first execution.
- Lua disabled by default for untrusted AA scripts; CLR/package loading, filesystem, network, process launch, and unrestricted reflection must not be silently inherited.
- Explicit consent for remote-thread creation, DLL loading, C compilation, or host callbacks.
- Syntax Check and Preview modes must not mutate the target or host.
- Includes use canonical paths, bounded size/depth, cycle detection, and a defined trusted root policy.
- AOB scans are bounded/cancellable and require unambiguous-match policy where the script expects one result.
- Enable plans bind to process start time, architecture, module fingerprint, and RVA so they cannot be disabled against a different process instance or binary.
- A failed enable must roll back and must not publish a usable disable state.
- Process exit disposes allocations/symbol state locally without attempting writes to a reused PID.

## Compatibility delivery phases

### Phase 18 — AA core and safe enable/disable

- Editor, diagnostics, syntax check, preview, and memory-record lifecycle.
- Sections, architecture blocks, comments, typed literals, address expressions, `define`, labels, assembly, and data directives.
- `assert`, AOB scan variants, near `alloc`, `dealloc`, `registersymbol`, `unregistersymbol`, `readmem`, and alignment.
- Immutable execution plan, verified batch writes, rollback, read-back, instruction-cache flush, and retained session state.
- Code-injection, AOB-injection, and full-injection templates.

### Phase 19 — AA compatibility and trusted Lua

- `include`, `globalalloc`, `sharedalloc`, scoped `fullaccess`, `reassemble`, and carefully gated queued actions.
- `{$LUA}` preprocessing, `luacall`, `autoAssemble`/`autoAssembleCheck`, and custom-command registration through NLua.
- Trust/capability UI and a restricted AA Lua environment.
- `createthread`, `createthreadandwait`, and `loadlibrary` only after explicit policy and integration tests.

### Deferred advanced parity

- `{$LUACODE}` host/target bridge.
- `{$C}`/`{$CCODE}` and compiler toolchain.
- Kernel allocations and driver-specific directives.
- Platform architectures other than the supported x86/x64 Windows targets.

These features must fail with explicit unsupported-capability diagnostics until implemented.

## Verification strategy

- Parser golden tests for every directive, comments, quoting, typed values, sections, and diagnostics.
- Binder tests for forward/backward labels, duplicate definitions, unresolved symbols, ASLR, x86/x64, and ±2 GB branch limits.
- Compiler tests for instruction-size stabilization, NOP fill, data directives, and adjacent-block coalescing.
- Fake-service tests for ambiguous AOBs, cancellation, allocation failure, assertion mismatch, partial write, protection failure, read-back mismatch, and rollback.
- Benign native target tests for enable, observable behavior, disable restoration, symbols, allocation cleanup, process exit, and restart/PID reuse.
- Lua tests proving syntax-check behavior, generated-source substitution, capability denial, cancellation, and error line mapping.
- Differential compatibility corpus: run non-destructive scripts through Cheat Engine syntax checking and CrxMem compilation and compare resolved symbols/bytes. Never execute comparison scripts against third-party processes.

## Immediate corrective action before Phase 18

The current Lua `autoAssemble` placeholder should be marked unsupported or routed to a future `IAutoAssemblerService`. It must not claim compatibility or be used as a fallback because it bypasses CrxMem's verified patch service and cannot correctly disable what it enables.
