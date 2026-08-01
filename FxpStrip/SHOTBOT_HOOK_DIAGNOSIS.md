# Shotbot Hook Installation Diagnosis

## Summary

The message below does not mean that hooks need to be installed manually:

```text
Shotbot unavailable: Unable to install hook stubs.
```

It means that `InitializeMemoryShotbot()` attempted to install its four inline
hooks, but at least one protection, write, verification, or protection-restore
operation failed. The initialization routine then rolled the installed sites
back and displayed its generic failure message.

## Live Cheat Engine Verification

The following was verified read-only against the live 32-bit `Gunz.exe`
process:

- Process ID: `15312`
- `Gunz.exe` base: `0x00130000`
- Loaded test DLL:
  `E:\Git.Projects\Curse-X\Overclock\FXP STRIP\Release\fxp.dll`
- The four Shotbot addresses contain their expected original instructions.
- All four target pages have `PAGE_EXECUTE_READ` protection.
- None of the Shotbot sites remained partially patched after the failed attempt.

### Verified Hook Sites

| Purpose | Address | Live bytes begin with | Protection |
|---|---:|---|---|
| Crosshair write 1 | `0x002060FB` | `C7 82 C0 03 00 00 01 00 00 00` | `PAGE_EXECUTE_READ` |
| Crosshair write 0 | `0x00206291` | `C7 82 C0 03 00 00 00 00 00 00` | `PAGE_EXECUTE_READ` |
| Weapon gun state | `0x00204231` | `8B 45 F8 C6 40 08 01` | `PAGE_EXECUTE_READ` |
| Weapon sword state | `0x00208368` | `8B 95 2C FE FF FF C6 42 08 00` | `PAGE_EXECUTE_READ` |

These results confirm that the Shotbot RVAs and expected instruction signatures
are valid for the current client.

## Identified Protection-Layer Problem

The live 32-bit `ntdll!NtProtectVirtualMemory` entry begins with:

```text
E9 2A C9 A1 8C
```

This is a detour rather than the normal syscall entry used by this Windows
build.

The original `STRIPPED` implementation verified its memory-protection setup
before setting the write-ready flag. The modified `FXP STRIP` implementation
replaced that process with:

```cpp
InitScan();
InstallCompatHooks();
InterlockedExchange(&g_memoryWritePatchReady, 1);
```

As a result, `g_memoryWritePatchReady` is set to `1` unconditionally. The
console proceeds as though protected writes are available even when the
underlying protection operation has not been verified.

## Resulting Failure Flow

1. Shotbot signature validation succeeds.
2. Executable hook stubs are allocated and prepared.
3. Shotbot calls `VirtualProtect` before writing an inline jump.
4. A protection, write, readback, or restoration check fails.
5. `install_all_sites()` returns `false`.
6. Any previously installed sites are restored.
7. The console displays `Unable to install hook stubs`.

## Conclusion

The failure is not caused by stale Shotbot addresses or mismatched instruction
signatures. The modified project declares its memory-write layer ready without
verifying that the protection path works. The current Shotbot error is generic,
so it does not identify which individual site or operation failed.

No process memory was modified while collecting this diagnostic information.
