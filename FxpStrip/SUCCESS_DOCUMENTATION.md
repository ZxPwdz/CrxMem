# CurseX Overclock DLL - Successful Build and Crash-Isolation Report

## 1. Final Result

The `STRIPPED` project now builds a stable 32-bit injectable DLL for the current
32-bit `Gunz.exe` client. The tested build opens a console-only interface, keeps
the fake `EhSvc.dll` behavior, supports Ground Slash and Overclock, and no longer
crashes when entering the lobby after login.

Successful output:

```text
E:\Git.Projects\Curse-X\Overclock\STRIPPED\Release\fxp.dll
```

Verified binary format:

```text
Machine:         0x014C
Optional header: 0x010B
Architecture:    PE32 / x86
```

The successful Release build completed with zero compiler warnings and zero
errors.

## 2. Final User Interface

The previous custom GDI trainer window was removed. Injection now creates only a
styled Windows console with the visible header:

```text
CurseX - ZxPwds Overclock
```

The Windows console title is randomized on every launch. The interface does not
show raw addresses or raw float values.

### Controls

| Key | Action |
| --- | --- |
| `F1` | Toggle Ground Slash on or off globally |
| `F2` | Toggle Overclock on or off globally |
| `-` | Decrease the selected Overclock level by 5% and enable it |
| `+` | Increase the selected Overclock level by 5% and enable it |
| Numpad `-` / `+` | Same Overclock adjustments |
| `Q` | Close only the console; keep the DLL loaded |

When Overclock is disabled, the selected percentage is remembered. Pressing
`F2` again restores that level. The initial remembered level is 100%.

## 3. Active Memory Features

### Ground Slash

```text
Address: Gunz.exe+7909EC
Type:    float
Off:     80.0
On:      0.0
```

The live target was verified as the bytes `00 00 A0 42`, which represents the
32-bit float `80.0f`.

### Overclock

```text
Address: Gunz.exe+79FC90
Type:    float
Default: 4.800000191
Maximum: 7.0
```

The percentage mapping is:

```text
0%   = 4.800000191
100% = 7.0
```

Both targets are resolved relative to the live `Gunz.exe` module base. The DLL
validates the executable as PE32 and confirms that the target offsets fit inside
the image before enabling controls.

## 4. Memory Write Process

The target values reside in protected image memory. The DLL uses this sequence
for every float write:

1. Query the target with `VirtualQuery`.
2. Confirm that the region is committed, readable, and not guarded.
3. Select `PAGE_EXECUTE_WRITECOPY` for a `MEM_IMAGE` target.
4. Temporarily change the page protection.
5. Write the float using `WriteProcessMemory` on the current process.
6. Read the value back and verify the write.
7. Restore the exact original page protection.
8. Report the precise failing stage and Windows error if any operation fails.

The DLL also repairs the first five bytes of `ntdll!NtProtectVirtualMemory` to:

```text
B8 50 00 00 00
```

This restores the expected 32-bit syscall-number load (`mov eax, 0x50`) after
the game has redirected that entry point. The console waits until this repair is
verified before accepting memory controls.

## 5. Fake EhSvc Behavior Retained

The fake `EhSvc.dll` behavior remains active as requested:

1. `PatchHackshield()` creates `shader\fake` under the game directory.
2. The embedded fake DLL bytes are written to `shader\fake\EhSvc.dll`.
3. The only enabled MinHook API hook is `LoadLibraryA`.
4. Requests for `EhSvc.dll` are redirected to `fake\EhSvc.dll`.

The stable build proves that this remaining `LoadLibraryA` hook and the fake DLL
path can coexist with login and lobby entry in the tested client.

## 6. HWID Bypass Removal

All identified HWID spoofing behavior was removed:

- No `MachineGuid` registry substitution.
- No `LastValue` registry substitution.
- No generated UUID identity.
- No base64-encoded fake identity.
- No display-device name randomization.
- No `RegQueryValueExA` HWID hook.
- No `EnumDisplayDevicesA` spoofing hook.
- No RPC UUID functions or `rpcrt4` dependency.

The final DLL import audit found no HWID or RPC imports associated with the
removed implementation.

## 7. Lobby Crash Investigation

### Original symptom

The DLL injected and the game allowed login, but the process crashed while
entering the lobby.

### Isolation test

The project originally installed a group of legacy API hooks through MinHook.
For the isolation build, every API hook except the fake-DLL `LoadLibraryA` hook
was disabled. The GDI trainer had already been removed, while the console,
memory writes, fake DLL, and direct `NtProtectVirtualMemory` repair remained.

Result:

```text
Login succeeded.
Lobby entry succeeded.
The crash no longer occurred.
```

This proves that the console, both configured addresses, the fake `EhSvc.dll`
redirect, and the direct protection repair are not sufficient by themselves to
reproduce the lobby crash. At least one of the disabled API hooks, or an
interaction between them, caused the failure.

It does not yet prove which individual hook was responsible because the hooks
were disabled as a group.

## 8. Disabled Hook Risk Analysis

The old hook implementations remain in `hooks.cpp` for reference, but they are
not registered or enabled in the successful build.

### Highest-risk candidates

#### `CreateThread`

This hook examines thread start addresses and caller bytes. For selected threads
originating in GunZ code, it prevents the real thread from starting and returns
a permanently sleeping dummy thread instead. Lobby entry commonly creates or
depends on new worker threads, making this the strongest behavioral candidate.

Additional risks include reading caller memory without exception protection and
returning the same dummy handle for multiple blocked thread requests.

#### `PeekMessageA`

This hook touches a legacy chat queue on every intercepted message-loop call. It
uses `messagemutex` and `ZChatOutput`, but neither is initialized by the current
console-only code. An invalid mutex operation or an attempted call through an
unset `ZChatOutput` pointer could crash the process. Lobby entry also increases
window-message activity, so this is a strong candidate.

#### `GetCurrentThreadId`

This hook inspects bytes at `_ReturnAddress()` and conditionally performs a
double pointer dereference to return a substituted thread ID. The memory reads
are not protected by structured exception handling. A changed client call site
or invalid derived pointer could cause an access violation.

#### `SetWindowsHookExA`

This hook always returns `NULL`, preventing every ANSI Windows hook request from
succeeding. If the lobby, game UI, or a required service expects hook setup to
succeed, the forced failure can break initialization.

### Medium-risk candidates

#### `SetWindowLongA`

This hook returns failure for every window other than the stored GunZ window.
Lobby UI creation or subclassing may involve additional windows, so blocking
those calls could leave the UI in an invalid state.

#### `BitBlt`

This hook reports success without performing the operation when the caller is
inside GunZ code. It was likely intended to block screenshots, but it can also
interfere with legitimate rendering or surface-copy operations.

#### `PostMessageA`

This hook suppresses every `WM_CLOSE` message and reports success. It is less
likely to cause lobby entry failure directly, but it changes global window
lifecycle behavior.

### Lower-risk candidates

#### `SetCursorPos`

This hook blocks cursor movement unless the stored game window is foreground and
the old menu flag is false. It can cause input problems but is less likely to
produce an access violation.

#### `ClipCursor`

This hook always replaces the requested clip rectangle with `NULL`. It changes
mouse confinement but is unlikely to be the direct lobby crash source.

#### `CreateMutexA`

This hook called the memory-protection repair on its first invocation and then
forwarded to the original function. The successful build performs and verifies
the same repair directly during startup, so the repair itself has been retained
without relying on this timing-sensitive hook.

## 9. Current Enabled Hook Matrix

| API | Current state | Reason |
| --- | --- | --- |
| `LoadLibraryA` | Enabled | Required for fake `EhSvc.dll` redirect |
| `GetCurrentThreadId` | Disabled | Crash-isolation build |
| `PostMessageA` | Disabled | Crash-isolation build |
| `SetCursorPos` | Disabled | Crash-isolation build |
| `PeekMessageA` | Disabled | Contains unsafe legacy state |
| `SetWindowLongA` | Disabled | Crash-isolation build |
| `CreateMutexA` | Disabled | Protection repair now runs directly |
| `CreateThread` | Disabled | Strong lobby-crash candidate |
| `SetWindowsHookExA` | Disabled | Blocks all hook installation |
| `BitBlt` | Disabled | Legacy screenshot suppression |
| `ClipCursor` | Disabled | Legacy cursor behavior |

## 10. Build Instructions

The solution names its 32-bit platform `x86`, which maps to the project's
`Win32` configuration.

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    fxp.sln `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=x86 `
    /m
```

Expected output:

```text
Release\fxp.dll
```

## 11. Successful Configuration Summary

The known-working configuration is:

- 32-bit PE32 DLL.
- Console-only interface.
- Ground Slash at `Gunz.exe+7909EC`.
- Overclock at `Gunz.exe+79FC90`.
- Fake `EhSvc.dll` retained.
- `NtProtectVirtualMemory` repair retained.
- Only the required `LoadLibraryA` API hook enabled.
- All HWID spoofing removed.
- All legacy trainer UI and unrelated gameplay features removed.
- All suspected lobby-crash API hooks disabled.

This configuration successfully passed injection, login, and lobby entry in the
tested GunZ client.
