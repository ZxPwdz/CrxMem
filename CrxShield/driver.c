/*
CrxShield - Kernel Memory Access Research Driver
Purpose: Cheat Engine-style research driver providing kernel-assisted
process memory read/write and process base address lookup.
Features:
- Process memory read/write (MmCopyVirtualMemory with MDL fallback)
- Process base address resolution
- Thread enumeration (ZwQuerySystemInformation SystemProcessInformation walk)
- Hardware breakpoint set/clear on all threads of a process
  (Dr0-Dr3/Dr7 written directly with __writedr inside each target thread via
  a queued special kernel APC; KTHREAD DebugActive is set so the context
  switcher saves/restores the DR image. PsGetContextThread/PsSetContextThread
  return STATUS_UNSUCCESSFUL on this Windows 11 build in every calling
  context, so the context APIs are not used)
- Device restricted to SYSTEM/Administrators via SDDL
NOTE: No driver hiding and no callback enumeration/removal - those
paths were removed during Phase 0 stabilization. The freed IOCTL slots
+0x4/+0x5/+0x6 are reused by the Phase 2 thread/hw-breakpoint IOCTLs.
WARNING: Kernel mode code can cause system instability (BSOD).
Always test on a system you can afford to crash.
*/

#include <ntifs.h>      // Provides kernel APIs and types
#include <wdmsec.h>     // IoCreateDeviceSecure
#include <intrin.h>     // __readdr/__writedr

// GUID_DEVCLASS_UNKNOWN is declared in devguid.h but its definition lives in
// uuid.lib (user-mode only). Define it locally for kernel linking.
// {4D36E97E-E325-11CE-BFC1-08002BE10318}
static const GUID CRXSHIELD_DEVCLASS_UNKNOWN =
    { 0x4d36e97eL, 0xe325, 0x11ce, { 0xbf, 0xc1, 0x08, 0x00, 0x2b, 0xe1, 0x03, 0x18 } };

// Logging Macro (compiled out in release builds)
#if DBG
#define LOG(fmt, ...) DbgPrintEx(DPFLTR_SYSTEM_ID, DPFLTR_ERROR_LEVEL, "[CrxMemKernel] " fmt "\n", __VA_ARGS__)
#else
#define LOG(...) do { } while (0)
#endif

// Obfuscated strings (simple XOR encryption, key 0xAA)
#define XOR_KEY 0xAA
#define OBFUSCATE_STRING(str) { \
    char* p = str; \
    while (*p) { *p ^= XOR_KEY; p++; } \
}

char g_ObfDeviceName[] = { '\\' ^ XOR_KEY, 'D' ^ XOR_KEY, 'e' ^ XOR_KEY, 'v' ^ XOR_KEY, 'i' ^ XOR_KEY, 'c' ^ XOR_KEY, 'e' ^ XOR_KEY, '\\' ^ XOR_KEY, 'C' ^ XOR_KEY, 'r' ^ XOR_KEY, 'x' ^ XOR_KEY, 'S' ^ XOR_KEY, 'h' ^ XOR_KEY, 'i' ^ XOR_KEY, 'e' ^ XOR_KEY, 'l' ^ XOR_KEY, 'd' ^ XOR_KEY, 0 };
char g_ObfSymLinkName[] = { '\\' ^ XOR_KEY, 'D' ^ XOR_KEY, 'o' ^ XOR_KEY, 's' ^ XOR_KEY, 'D' ^ XOR_KEY, 'e' ^ XOR_KEY, 'v' ^ XOR_KEY, 'i' ^ XOR_KEY, 'c' ^ XOR_KEY, 'e' ^ XOR_KEY, 's' ^ XOR_KEY, '\\' ^ XOR_KEY, 'C' ^ XOR_KEY, 'r' ^ XOR_KEY, 'x' ^ XOR_KEY, 'S' ^ XOR_KEY, 'h' ^ XOR_KEY, 'i' ^ XOR_KEY, 'e' ^ XOR_KEY, 'l' ^ XOR_KEY, 'd' ^ XOR_KEY, 0 };

// IOCTL codes (randomized base to avoid signatures)
// NOTE: FILE_ANY_ACCESS is kept on all codes so the C# CTL_CODE computation
// stays compatible. The SDDL on the device object is the real protection.
// TODO: On a future protocol bump, tighten these to FILE_READ_DATA |
// FILE_WRITE_DATA and update the managed client accordingly.
#define IOCTL_BASE 0x1337
#define IOCTL_CRXSHIELD_GET_VERSION     CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x0, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_READ_MEMORY     CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x1, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_WRITE_MEMORY    CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x2, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_GET_PROCESS_BASE CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x3, METHOD_BUFFERED, FILE_ANY_ACCESS)
// Phase 2 IOCTLs - reuse slots freed when the callback IOCTLs were removed
#define IOCTL_CRXSHIELD_ENUM_THREADS    CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x4, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_SET_HWBP        CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x5, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_CRXSHIELD_CLEAR_HWBP      CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x6, METHOD_BUFFERED, FILE_ANY_ACCESS)

// Driver version
#define CRXSHIELD_VERSION_MAJOR 1
#define CRXSHIELD_VERSION_MINOR 5
#define CRXSHIELD_VERSION_BUILD 0

// Maximum bytes allowed in a single read/write transfer
#define CRXSHIELD_MAX_TRANSFER (16*1024*1024)

// SDDL: only SYSTEM and built-in Administrators may open the device
#define CRXSHIELD_DEVICE_SDDL L"D:P(A;;GA;;;SY)(A;;GA;;;BA)"

// Structures for IOCTL communication
#pragma pack(push, 1)
typedef struct _CRXSHIELD_VERSION {
    ULONG Major;
    ULONG Minor;
    ULONG Build;
} CRXSHIELD_VERSION, * PCRXSHIELD_VERSION;

typedef struct _CRXSHIELD_READ_REQUEST {
    ULONG ProcessId;
    ULONG64 Address;
    ULONG Size;
} CRXSHIELD_READ_REQUEST, * PCRXSHIELD_READ_REQUEST;

typedef struct _CRXSHIELD_WRITE_REQUEST {
    ULONG ProcessId;
    ULONG64 Address;
    ULONG Size;
} CRXSHIELD_WRITE_REQUEST, * PCRXSHIELD_WRITE_REQUEST;

typedef struct _CRXSHIELD_PROCESS_BASE_REQUEST {
    ULONG ProcessId;
    ULONG64 BaseAddress;
} CRXSHIELD_PROCESS_BASE_REQUEST, * PCRXSHIELD_PROCESS_BASE_REQUEST;

// --- Phase 2: thread enumeration + hardware breakpoints ---

#define CRXSHIELD_MAX_THREADS 256

// Hardware breakpoint request constants
#define CRXSHIELD_HWBP_SLOT_ALL        0xFFFFFFFF  // CLEAR_HWBP: wipe all debug registers
#define CRXSHIELD_HWBP_TYPE_EXECUTE    0
#define CRXSHIELD_HWBP_TYPE_WRITE      1
#define CRXSHIELD_HWBP_TYPE_READWRITE  2

typedef struct _CRXSHIELD_THREAD_INFO {
    ULONG ThreadId;
    ULONG64 StartAddress;   // Win32 start address; 0 when the kernel reports NULL
} CRXSHIELD_THREAD_INFO, * PCRXSHIELD_THREAD_INFO;

typedef struct _CRXSHIELD_ENUM_THREADS_REQUEST {
    ULONG ProcessId;
} CRXSHIELD_ENUM_THREADS_REQUEST, * PCRXSHIELD_ENUM_THREADS_REQUEST;

typedef struct _CRXSHIELD_ENUM_THREADS_RESPONSE {
    ULONG Count;            // total threads in the process (may exceed entries returned)
    CRXSHIELD_THREAD_INFO Threads[CRXSHIELD_MAX_THREADS];
} CRXSHIELD_ENUM_THREADS_RESPONSE, * PCRXSHIELD_ENUM_THREADS_RESPONSE;

typedef struct _CRXSHIELD_SET_HWBP_REQUEST {
    ULONG ProcessId;
    ULONG Slot;             // 0-3 (Dr0-Dr3)
    ULONG64 Address;
    ULONG Type;             // 0=execute, 1=write, 2=read/write
    ULONG Length;           // 1/2/4/8 (must be 1 for execute)
} CRXSHIELD_SET_HWBP_REQUEST, * PCRXSHIELD_SET_HWBP_REQUEST;

typedef struct _CRXSHIELD_CLEAR_HWBP_REQUEST {
    ULONG ProcessId;
    ULONG Slot;             // 0-3 clears one slot; 0xFFFFFFFF clears all debug registers
} CRXSHIELD_CLEAR_HWBP_REQUEST, * PCRXSHIELD_CLEAR_HWBP_REQUEST;

typedef struct _CRXSHIELD_HWBP_RESPONSE {
    ULONG ThreadsUpdated;
    ULONG ThreadsFailed;
    ULONG LookupFailed;     // PsLookupThreadByThreadId failures
    ULONG GetCtxFailed;     // APC queue failure / APC timeout / get-context failures
    ULONG SetCtxFailed;     // set-context failures inside the target thread
    LONG FirstErrorStatus;  // first failing per-thread NTSTATUS (0 if none failed)
} CRXSHIELD_HWBP_RESPONSE, * PCRXSHIELD_HWBP_RESPONSE;
#pragma pack(pop)

// Undocumented APIs - Function Pointer Definitions
typedef NTSTATUS(NTAPI* PFN_MmCopyVirtualMemory)(
    PEPROCESS SourceProcess,
    PVOID SourceAddress,
    PEPROCESS TargetProcess,
    PVOID TargetAddress,
    SIZE_T BufferSize,
    KPROCESSOR_MODE PreviousMode,
    PSIZE_T ReturnSize
);

typedef PVOID(NTAPI* PFN_PsGetProcessSectionBaseAddress)(
    PEPROCESS Process
);

typedef PVOID(NTAPI* PFN_PsGetProcessWow64Process)(
    PEPROCESS Process
);

// Exported by ntoskrnl but not declared in the WDK headers - declare locally.
NTSYSAPI
NTSTATUS
NTAPI
ZwQuerySystemInformation(
    _In_ ULONG SystemInformationClass,
    _Out_writes_bytes_opt_(SystemInformationLength) PVOID SystemInformation,
    _In_ ULONG SystemInformationLength,
    _Out_opt_ PULONG ReturnLength
);

NTKERNELAPI
NTSTATUS
NTAPI
PsGetContextThread(
    _In_ PETHREAD Thread,
    _Inout_ PCONTEXT ThreadContext,
    _In_ KPROCESSOR_MODE Mode
);

NTKERNELAPI
NTSTATUS
NTAPI
PsSetContextThread(
    _In_ PETHREAD Thread,
    _In_ PCONTEXT ThreadContext,
    _In_ KPROCESSOR_MODE Mode
);

// APC support: KeInitializeApc/KeInsertQueueApc and their routine types are
// declared in wdm.h, which cannot be included alongside ntifs.h - declare
// them locally (same pattern as PsGetContextThread above).
typedef enum _CRXSHIELD_KAPC_ENVIRONMENT {
    CrxOriginalApcEnvironment = 0,
    CrxAttachedApcEnvironment = 1,
    CrxCurrentApcEnvironment = 2,
    CrxInsertApcEnvironment = 3
} CRXSHIELD_KAPC_ENVIRONMENT;
#define OriginalApcEnvironment CrxOriginalApcEnvironment

typedef
_Function_class_(KNORMAL_ROUTINE)
_IRQL_requires_(PASSIVE_LEVEL)
_IRQL_requires_same_
VOID
NTAPI
KNORMAL_ROUTINE(
    _In_opt_ PVOID NormalContext,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2
);
typedef KNORMAL_ROUTINE* PKNORMAL_ROUTINE;

typedef
_Function_class_(KKERNEL_ROUTINE)
_IRQL_requires_(APC_LEVEL)
_IRQL_requires_same_
VOID
NTAPI
KKERNEL_ROUTINE(
    _In_ PKAPC Apc,
    _Inout_ PKNORMAL_ROUTINE* NormalRoutine,
    _Inout_ PVOID* NormalContext,
    _Inout_ PVOID* SystemArgument1,
    _Inout_ PVOID* SystemArgument2
);
typedef KKERNEL_ROUTINE* PKKERNEL_ROUTINE;

typedef
_Function_class_(KRUNDOWN_ROUTINE)
_IRQL_requires_same_
VOID
NTAPI
KRUNDOWN_ROUTINE(
    _In_ PKAPC Apc
);
typedef KRUNDOWN_ROUTINE* PKRUNDOWN_ROUTINE;

NTKERNELAPI
VOID
NTAPI
KeInitializeApc(
    _Out_ PKAPC Apc,
    _In_ PETHREAD Thread,
    _In_ CRXSHIELD_KAPC_ENVIRONMENT Environment,
    _In_ PKKERNEL_ROUTINE KernelRoutine,
    _In_opt_ PKRUNDOWN_ROUTINE RundownRoutine,
    _In_opt_ PKNORMAL_ROUTINE NormalRoutine,
    _In_opt_ KPROCESSOR_MODE Mode,
    _In_opt_ PVOID NormalContext
);

NTKERNELAPI
BOOLEAN
NTAPI
KeInsertQueueApc(
    _Inout_ PKAPC Apc,
    _In_opt_ PVOID SystemArgument1,
    _In_opt_ PVOID SystemArgument2,
    _In_ KPRIORITY Increment
);

// SystemProcessInformation class for ZwQuerySystemInformation
#define CRXSHIELD_SYSTEM_PROCESS_INFORMATION_CLASS 5

// Private layout mirrors of the undocumented system-information structs.
// Prefixed to avoid any collision with future WDK headers. Field order
// matches the documented Vista+ layout so FIELD_OFFSET(..., Threads) is the
// correct offset of the variable-length thread array.
typedef struct _CRXSHIELD_SYS_CLIENT_ID {
    HANDLE UniqueProcess;
    HANDLE UniqueThread;
} CRXSHIELD_SYS_CLIENT_ID, * PCRXSHIELD_SYS_CLIENT_ID;

typedef struct _CRXSHIELD_SYS_THREAD_INFORMATION {
    LARGE_INTEGER KernelTime;
    LARGE_INTEGER UserTime;
    LARGE_INTEGER CreateTime;
    ULONG WaitTime;
    PVOID StartAddress;             // Win32 start address (NULL for some threads)
    CRXSHIELD_SYS_CLIENT_ID ClientId;
    LONG Priority;
    LONG BasePriority;
    ULONG ContextSwitches;
    ULONG ThreadState;
    ULONG WaitReason;
} CRXSHIELD_SYS_THREAD_INFORMATION, * PCRXSHIELD_SYS_THREAD_INFORMATION;

typedef struct _CRXSHIELD_SYS_PROCESS_INFORMATION {
    ULONG NextEntryOffset;
    ULONG NumberOfThreads;
    LARGE_INTEGER WorkingSetPrivateSize;
    ULONG HardFaultCount;
    ULONG NumberOfThreadsHighWatermark;
    ULONG64 CycleTime;
    LARGE_INTEGER CreateTime;
    LARGE_INTEGER UserTime;
    LARGE_INTEGER KernelTime;
    UNICODE_STRING ImageName;
    LONG BasePriority;
    HANDLE UniqueProcessId;
    HANDLE InheritedFromUniqueProcessId;
    ULONG HandleCount;
    ULONG SessionId;
    ULONG_PTR UniqueProcessKey;
    SIZE_T PeakVirtualSize;
    SIZE_T VirtualSize;
    ULONG PageFaultCount;
    SIZE_T PeakWorkingSetSize;
    SIZE_T WorkingSetSize;
    SIZE_T QuotaPeakPagedPoolUsage;
    SIZE_T QuotaPagedPoolUsage;
    SIZE_T QuotaPeakNonPagedPoolUsage;
    SIZE_T QuotaNonPagedPoolUsage;
    SIZE_T PagefileUsage;
    SIZE_T PeakPagefileUsage;
    SIZE_T PrivatePageCount;
    LARGE_INTEGER ReadOperationCount;
    LARGE_INTEGER WriteOperationCount;
    LARGE_INTEGER OtherOperationCount;
    LARGE_INTEGER ReadTransferCount;
    LARGE_INTEGER WriteTransferCount;
    LARGE_INTEGER OtherTransferCount;
    CRXSHIELD_SYS_THREAD_INFORMATION Threads[1];
} CRXSHIELD_SYS_PROCESS_INFORMATION, * PCRXSHIELD_SYS_PROCESS_INFORMATION;

#define CRXSHIELD_POOL_TAG 'hTxC'   // "CxTh"

// Globals
PDEVICE_OBJECT g_DeviceObject = NULL;
PFN_MmCopyVirtualMemory g_MmCopyVirtualMemory = NULL;
PFN_PsGetProcessSectionBaseAddress g_PsGetProcessSectionBaseAddress = NULL;
PFN_PsGetProcessWow64Process g_PsGetProcessWow64Process = NULL;

// Forward declarations with correct macros
DRIVER_INITIALIZE DriverEntry;
DRIVER_UNLOAD DriverUnload;

_Dispatch_type_(IRP_MJ_CREATE)
_Dispatch_type_(IRP_MJ_CLOSE)
DRIVER_DISPATCH CreateClose;

_Dispatch_type_(IRP_MJ_DEVICE_CONTROL)
DRIVER_DISPATCH DeviceControl;

// Helper: XOR deobfuscate
VOID DeobfuscateString(char* str) {
    OBFUSCATE_STRING(str);
}

// ANSI → UNICODE helper
NTSTATUS AnsiToUnicode(const char* ansiStr, PUNICODE_STRING uniStr) {
    ANSI_STRING ansi;
    RtlInitAnsiString(&ansi, ansiStr);
    return RtlAnsiStringToUnicodeString(uniStr, &ansi, TRUE);
}

// IRP handlers
NTSTATUS CreateClose(PDEVICE_OBJECT DeviceObject, PIRP Irp) {
    UNREFERENCED_PARAMETER(DeviceObject);
    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

NTSTATUS ReadProcessMemory(ULONG ProcessId, PVOID SourceAddress, PVOID Buffer, SIZE_T Size, PSIZE_T BytesRead) {
    if (!g_MmCopyVirtualMemory) return STATUS_NOT_IMPLEMENTED;

    PEPROCESS target;
    NTSTATUS status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)ProcessId, &target);
    if (!NT_SUCCESS(status)) return status;

    // NOTE: No ProbeForRead on SourceAddress here. This is a research tool
    // with a trusted caller, and MmCopyVirtualMemory with KernelMode
    // performs no probing of either address - probing here would check the
    // caller's address space, not the target's, and is meaningless.
    status = g_MmCopyVirtualMemory(target, SourceAddress, PsGetCurrentProcess(), Buffer, Size, KernelMode, BytesRead);
    ObDereferenceObject(target);
    return status;
}

NTSTATUS WriteProcessMemory(ULONG ProcessId, PVOID TargetAddress, PVOID Buffer, SIZE_T Size, PSIZE_T BytesWritten) {
    if (!g_MmCopyVirtualMemory) return STATUS_NOT_IMPLEMENTED;

    PEPROCESS target;
    NTSTATUS status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)ProcessId, &target);
    if (!NT_SUCCESS(status)) {
        LOG("WriteProcessMemory: PsLookupProcessByProcessId failed with 0x%X", status);
        return status;
    }

    // First try normal MmCopyVirtualMemory - works for writable pages
    status = g_MmCopyVirtualMemory(PsGetCurrentProcess(), Buffer, target, TargetAddress, Size, KernelMode, BytesWritten);

    if (NT_SUCCESS(status)) {
        LOG("WriteProcessMemory: MmCopyVirtualMemory succeeded, wrote %llu bytes", (ULONG64)*BytesWritten);
        ObDereferenceObject(target);
        return status;
    }

    LOG("WriteProcessMemory: MmCopyVirtualMemory failed with 0x%X, trying MDL approach", status);

    // MmCopyVirtualMemory failed - likely a protected page (code section)
    // Use MDL to bypass page protection
    if (Size == 0 || Size > CRXSHIELD_MAX_TRANSFER) {
        ObDereferenceObject(target);
        return STATUS_INVALID_PARAMETER;
    }

    // Defensive: IoAllocateMdl takes a ULONG length. Reject anything that
    // would truncate (the 16MB cap above already guarantees this fits).
    if (Size > MAXULONG) {
        ObDereferenceObject(target);
        return STATUS_INVALID_PARAMETER;
    }

    KAPC_STATE apcState;
    KeStackAttachProcess(target, &apcState);

    PMDL mdl = NULL;
    PVOID mapped = NULL;
    BOOLEAN pagesLocked = FALSE;
    BOOLEAN pagesMapped = FALSE;

    __try {
        // Create an MDL for the target address
        mdl = IoAllocateMdl(TargetAddress, (ULONG)Size, FALSE, FALSE, NULL);
        if (!mdl) {
            LOG("WriteProcessMemory: IoAllocateMdl failed");
            status = STATUS_INSUFFICIENT_RESOURCES;
            __leave;
        }

        __try {
            // Lock the pages - this may fail if address is invalid
            MmProbeAndLockPages(mdl, KernelMode, IoReadAccess);
            pagesLocked = TRUE;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            LOG("WriteProcessMemory: MmProbeAndLockPages failed");
            status = STATUS_ACCESS_VIOLATION;
        }
        if (!pagesLocked) __leave;

        // Map the pages as writable in system space
        mapped = MmMapLockedPagesSpecifyCache(mdl, KernelMode, MmNonCached, NULL, FALSE, NormalPagePriority);
        if (!mapped) {
            LOG("WriteProcessMemory: MmMapLockedPagesSpecifyCache failed");
            status = STATUS_INSUFFICIENT_RESOURCES;
            __leave;
        }
        pagesMapped = TRUE;

        // Change protection to allow writes
        status = MmProtectMdlSystemAddress(mdl, PAGE_READWRITE);
        if (!NT_SUCCESS(status)) {
            LOG("WriteProcessMemory: MmProtectMdlSystemAddress failed with 0x%X", status);
            __leave;
        }

        // Copy the data
        RtlCopyMemory(mapped, Buffer, Size);
        *BytesWritten = Size;
        LOG("WriteProcessMemory: MDL write succeeded, wrote %llu bytes", (ULONG64)Size);
        status = STATUS_SUCCESS;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        LOG("WriteProcessMemory: Exception during MDL write");
        status = GetExceptionCode();
    }

    // Unified cleanup - ALWAYS runs, including on the exception path
    if (pagesMapped) MmUnmapLockedPages(mapped, mdl);
    if (pagesLocked) MmUnlockPages(mdl);
    if (mdl) IoFreeMdl(mdl);

    KeUnstackDetachProcess(&apcState);
    ObDereferenceObject(target);
    return status;
}

NTSTATUS GetProcessBaseAddress(ULONG ProcessId, PULONG64 BaseAddress) {
    PEPROCESS target;
    NTSTATUS status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)ProcessId, &target);
    if (!NT_SUCCESS(status)) return status;

    // WoW64 (32-bit) process? PsGetProcessSectionBaseAddress returns the
    // 64-bit loader section base for WoW64 processes, NOT the 32-bit image
    // base (observed: 0x242000 for a process whose true image base was
    // 0x170000). The WOW64_PROCESS structure's first field is the 32-bit
    // PEB pointer, and PEB32+0x8 holds ImageBaseAddress (ULONG32).
    PVOID wow64Process = g_PsGetProcessWow64Process ? g_PsGetProcessWow64Process(target) : NULL;
    if (wow64Process) {
        // Kernel-side structure, stable while we hold the process reference.
        ULONG64 peb32 = (ULONG64)(ULONG_PTR)(*(PVOID*)wow64Process);
        ObDereferenceObject(target);
        if (!peb32) return STATUS_NOT_FOUND;

        if (!g_MmCopyVirtualMemory) return STATUS_NOT_IMPLEMENTED;
        ULONG imageBase32 = 0;
        SIZE_T read = 0;
        // PEB32 lives in the target's user address space - reuse the same
        // safe cross-process read as IOCTL_READ_MEMORY.
        status = ReadProcessMemory(ProcessId, (PVOID)(peb32 + 8), &imageBase32, sizeof(imageBase32), &read);
        if (!NT_SUCCESS(status)) return status;
        if (read != sizeof(imageBase32) || imageBase32 == 0) return STATUS_NOT_FOUND;
        *BaseAddress = imageBase32;
        return STATUS_SUCCESS;
    }

    if (!g_PsGetProcessSectionBaseAddress) {
        ObDereferenceObject(target);
        return STATUS_NOT_IMPLEMENTED;
    }
    PVOID base = g_PsGetProcessSectionBaseAddress(target);
    ObDereferenceObject(target);

    if (!base) return STATUS_NOT_FOUND;

    *BaseAddress = (ULONG64)base;
    return STATUS_SUCCESS;
}

// Enumerate threads of a process via ZwQuerySystemInformation(SystemProcessInformation).
// Reliable and uses no undocumented ETHREAD offsets. Returns the total thread
// count in *TotalCount and fills up to Capacity entries into Entries (which may
// point into the METHOD_BUFFERED SystemBuffer). All work runs at PASSIVE_LEVEL.
static NTSTATUS CrxEnumerateProcessThreads(
    _In_ ULONG ProcessId,
    _Out_writes_opt_(Capacity) PCRXSHIELD_THREAD_INFO Entries,
    _In_ ULONG Capacity,
    _Out_ PULONG TotalCount,
    _Out_ PULONG ReturnedCount
) {
    *TotalCount = 0;
    *ReturnedCount = 0;

    // Confirm the process exists before walking the system process list.
    PEPROCESS process = NULL;
    NTSTATUS status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)ProcessId, &process);
    if (!NT_SUCCESS(status)) return status;
    ObDereferenceObject(process);

    // Size query first; add slack because processes/threads can spawn
    // between the sizing call and the real query.
    ULONG bufSize = 0;
    ZwQuerySystemInformation(CRXSHIELD_SYSTEM_PROCESS_INFORMATION_CLASS, NULL, 0, &bufSize);
    if (bufSize == 0) bufSize = 1024 * 1024;
    bufSize += 64 * 1024;

    // ExAllocatePool2 requires Windows 10 2004+ (acceptable for this tool).
    PVOID buffer = ExAllocatePool2(POOL_FLAG_PAGED, bufSize, CRXSHIELD_POOL_TAG);
    if (!buffer) return STATUS_INSUFFICIENT_RESOURCES;

    ULONG attempt = 0;
    for (;;) {
        ULONG needed = 0;
        status = ZwQuerySystemInformation(CRXSHIELD_SYSTEM_PROCESS_INFORMATION_CLASS, buffer, bufSize, &needed);
        if (NT_SUCCESS(status)) break;
        if (status == STATUS_INFO_LENGTH_MISMATCH && attempt < 3) {
            attempt++;
            ExFreePoolWithTag(buffer, CRXSHIELD_POOL_TAG);
            bufSize = needed + 64 * 1024;
            buffer = ExAllocatePool2(POOL_FLAG_PAGED, bufSize, CRXSHIELD_POOL_TAG);
            if (!buffer) return STATUS_INSUFFICIENT_RESOURCES;
            continue;
        }
        ExFreePoolWithTag(buffer, CRXSHIELD_POOL_TAG);
        return status;
    }

    ULONG total = 0;
    ULONG returned = 0;
    BOOLEAN found = FALSE;
    PUCHAR bufStart = (PUCHAR)buffer;
    PUCHAR bufEnd = bufStart + bufSize;
    PUCHAR cursor = bufStart;
    ULONG guard = 0;

    while (guard++ < 0x10000) {  // iteration guard against a corrupt chain
        PCRXSHIELD_SYS_PROCESS_INFORMATION proc = (PCRXSHIELD_SYS_PROCESS_INFORMATION)cursor;

        if (proc->UniqueProcessId == (HANDLE)(ULONG_PTR)ProcessId) {
            found = TRUE;
            ULONG threadCount = proc->NumberOfThreads;
            PCRXSHIELD_SYS_THREAD_INFORMATION th =
                (PCRXSHIELD_SYS_THREAD_INFORMATION)(cursor + FIELD_OFFSET(CRXSHIELD_SYS_PROCESS_INFORMATION, Threads));

            // Defensive: clamp threadCount to what actually fits in the buffer.
            while (threadCount > 0 &&
                   (SIZE_T)((PUCHAR)(th + threadCount) - bufStart) > bufSize) {
                threadCount--;
            }

            for (ULONG i = 0; i < threadCount; i++) {
                total++;
                if (returned < Capacity) {
                    Entries[returned].ThreadId = (ULONG)(ULONG_PTR)th[i].ClientId.UniqueThread;
                    // Win32 start address may be NULL for some threads -> report 0
                    Entries[returned].StartAddress = (ULONG64)(ULONG_PTR)th[i].StartAddress;
                    returned++;
                }
            }
            break;
        }

        ULONG next = proc->NextEntryOffset;
        if (next == 0) break;
        // Sanity: aligned, forward-moving, and leaves room for a header
        if ((next & 7) != 0) break;
        if (cursor + next >= bufEnd || cursor + next <= cursor) break;
        cursor += next;
    }

    ExFreePoolWithTag(buffer, CRXSHIELD_POOL_TAG);

    if (!found) return STATUS_NOT_FOUND;  // raced with process exit

    *TotalCount = total;
    *ReturnedCount = returned;
    return STATUS_SUCCESS;
}

// --- DR7 helpers ---
// DR7 layout: L0-G3 enable bits at 2*slot (Ln local, Gn global),
// RWn (2 bits) at 16+4*slot, LENn (2 bits) at 18+4*slot.
// RW: 00=execute, 01=write, 11=read/write. LEN: 00=1, 01=2, 11=4, 10=8 bytes.

#define CRXSHIELD_DR7_RESERVED_BITS (1ULL << 10)  // bit 10 always reads/writes as 1

static ULONG64 CrxDr7ClearSlot(_In_ ULONG64 dr7, _In_ ULONG slot) {
    dr7 &= ~(3ULL << (slot * 2));           // Ln + Gn enable bits
    dr7 &= ~(0xFULL << (16 + slot * 4));    // RWn + LENn
    return dr7;
}

static ULONG64 CrxDr7SetSlot(_In_ ULONG64 dr7, _In_ ULONG slot, _In_ ULONG type, _In_ ULONG length) {
    ULONG64 rwEnc;   // 0=execute, 1=write, 3=read/write
    ULONG64 lenEnc;  // 0=1 byte, 1=2 bytes, 2=8 bytes, 3=4 bytes

    switch (type) {
    case CRXSHIELD_HWBP_TYPE_EXECUTE: rwEnc = 0; break;
    case CRXSHIELD_HWBP_TYPE_WRITE:   rwEnc = 1; break;
    default:                          rwEnc = 3; break;  // read/write
    }
    switch (length) {
    case 1: lenEnc = 0; break;
    case 2: lenEnc = 1; break;
    case 8: lenEnc = 2; break;
    default: lenEnc = 3; break;  // 4 bytes
    }

    dr7 = CrxDr7ClearSlot(dr7, slot);
    dr7 |= (1ULL << (slot * 2));            // Ln local enable
    dr7 |= (rwEnc << (16 + slot * 4));
    dr7 |= (lenEnc << (18 + slot * 4));
    dr7 |= CRXSHIELD_DR7_RESERVED_BITS;
    return dr7;
}

// --- Cross-thread debug-register access via per-thread special kernel APC ---
//
// HISTORY/WHY: on this Windows 11 build, PsGetContextThread/PsSetContextThread
// return STATUS_UNSUCCESSFUL (0xC0000001) in EVERY calling context: both
// cross-thread from the IOCTL dispatch (v1.2.0: 8/8 threads) and for the
// current thread from a normal kernel APC normal routine (v1.3.0: 7/7 failed
// at the get stage with 0xC0000001 - note the APC itself ran fine; normal
// kernel APCs do NOT require alertable waits, only user APCs do). The context
// APIs are therefore unusable here. The classic robust method (used by public
// hwbp drivers) queues a SPECIAL kernel APC (NormalRoutine == NULL) to each
// target thread: its kernel routine preempts the thread at APC_LEVEL with no
// alertability requirement and writes DR0-DR7 directly with __writedr.
//
// PERSISTENCE across context switches (x64 Windows 10/11):
// - KTHREAD+0x03 (last _DISPATCHER_HEADER byte) is DebugActive (UCHAR
//   boolean; the same byte carries ActiveDR7 as bit 0 in the bitfield
//   overlay). Layout verified against public symbol data for Win11 24H2.
// - The context switcher saves the CPU's current DR0-3/DR6/DR7 as the
//   outgoing thread's debug image when its DebugActive is set, restores the
//   image when switching such a thread back in, and clears the registers
//   when switching from a DebugActive thread to one without it, so state
//   never leaks across processes.
// - Therefore: programming the CPU debug registers inside the target thread
//   and setting DebugActive makes the breakpoint survive rescheduling,
//   exactly like the official PsSetContextThread path. DebugActive is set
//   only while DR7 has at least one Ln/Gn enable bit and cleared when the
//   last slot is removed.

#define CRXSHIELD_HWBP_APC_TAG   'pAhC'   // "ChAp"
#define CRXSHIELD_HWBP_STAGE_GET 1        // queue/timeout/rundown stage
#define CRXSHIELD_HWBP_STAGE_SET 2        // DR write stage inside the APC

#define CRXSHIELD_DEBUG_ACTIVE_OFFSET 0x03  // KTHREAD.Header.DebugActive (x64)
#define CRXSHIELD_DEBUG_ACTIVE_MASK   0x01
#define CRXSHIELD_DR7_ENABLE_MASK     0xFF  // L0..L3 + G0..G3 enable bits

static BOOLEAN CrxGetDebugActive(_In_ PETHREAD Thread) {
    return (*(volatile PUCHAR)((PUCHAR)Thread + CRXSHIELD_DEBUG_ACTIVE_OFFSET) &
            CRXSHIELD_DEBUG_ACTIVE_MASK) != 0;
}

static VOID CrxSetDebugActive(_In_ PETHREAD Thread, _In_ BOOLEAN Active) {
    volatile PUCHAR flags = (volatile PUCHAR)((PUCHAR)Thread + CRXSHIELD_DEBUG_ACTIVE_OFFSET);
    if (Active) *flags |= CRXSHIELD_DEBUG_ACTIVE_MASK;
    else        *flags &= (UCHAR)~CRXSHIELD_DEBUG_ACTIVE_MASK;
}

// One nonpaged block per queued APC. Lifetime is shared between the
// dispatching thread and the APC path via RefCount (starts at 2); the last
// one out frees it. Exactly one of the kernel routine (delivered) or the
// rundown routine (thread exited with the APC still queued) runs, so each
// decrements once; the dispatcher decrements after its wait. If the thread
// exits before delivery and the dispatcher already timed out, the rundown
// routine performs the final free - no leak, no UAF.
typedef struct _CRXSHIELD_HWBP_APC_BLOCK {
    KAPC Apc;
    KEVENT Completed;           // signaled when the APC path finishes
    volatile LONG RefCount;     // 2 = dispatcher + APC (kernel or rundown routine)
    volatile NTSTATUS Status;   // result inside the target thread
    volatile ULONG Stage;       // CRXSHIELD_HWBP_STAGE_* when Status is a failure
    BOOLEAN Set;
    ULONG Slot;
    ULONG64 Address;
    ULONG Type;
    ULONG Length;
} CRXSHIELD_HWBP_APC_BLOCK, * PCRXSHIELD_HWBP_APC_BLOCK;

// Runs at APC_LEVEL in the TARGET thread's context (special kernel APC):
// read-modify-write the CPU debug registers and hand them to the context
// switcher via DebugActive. Only nonpaged memory is touched.
static VOID CrxHwBpApplyDrs(_In_ PCRXSHIELD_HWBP_APC_BLOCK block) {
    PETHREAD thread = PsGetCurrentThread();
    ULONG64 dr[4];
    ULONG64 dr7;

    // If the thread already owns a debug image, the CPU registers hold it
    // (restored at switch-in). Otherwise start from a clean image - the CPU
    // DRs may contain another thread's stale values.
    if (CrxGetDebugActive(thread)) {
        dr[0] = __readdr(0);
        dr[1] = __readdr(1);
        dr[2] = __readdr(2);
        dr[3] = __readdr(3);
        dr7 = __readdr(7);
    } else {
        dr[0] = dr[1] = dr[2] = dr[3] = 0;
        dr7 = CRXSHIELD_DR7_RESERVED_BITS;
    }

    if (block->Set) {
        dr[block->Slot] = block->Address;
        dr7 = CrxDr7SetSlot(dr7, block->Slot, block->Type, block->Length);
    } else if (block->Slot == CRXSHIELD_HWBP_SLOT_ALL) {
        dr[0] = dr[1] = dr[2] = dr[3] = 0;
        dr7 = CRXSHIELD_DR7_RESERVED_BITS;
    } else {
        dr[block->Slot] = 0;
        dr7 = CrxDr7ClearSlot(dr7, block->Slot) | CRXSHIELD_DR7_RESERVED_BITS;
    }

    // Address registers first, DR7 (enable) last.
    __writedr(0, dr[0]);
    __writedr(1, dr[1]);
    __writedr(2, dr[2]);
    __writedr(3, dr[3]);
    __writedr(6, 0);  // clear sticky status bits
    __writedr(7, dr7);

    // Persist across context switches (see the note above).
    CrxSetDebugActive(thread, (dr7 & CRXSHIELD_DR7_ENABLE_MASK) != 0);
}

static _Function_class_(KKERNEL_ROUTINE)
_IRQL_requires_(APC_LEVEL)
_IRQL_requires_same_
VOID NTAPI CrxHwBpApcKernelRoutine(
    _In_ PKAPC Apc,
    _Inout_ PKNORMAL_ROUTINE* NormalRoutine,
    _Inout_ PVOID* Ctx,
    _Inout_ PVOID* Arg1,
    _Inout_ PVOID* Arg2
) {
    UNREFERENCED_PARAMETER(NormalRoutine);
    UNREFERENCED_PARAMETER(Ctx);
    UNREFERENCED_PARAMETER(Arg1);
    UNREFERENCED_PARAMETER(Arg2);

    PCRXSHIELD_HWBP_APC_BLOCK block = CONTAINING_RECORD(Apc, CRXSHIELD_HWBP_APC_BLOCK, Apc);

    NTSTATUS st = STATUS_SUCCESS;
    __try {
        CrxHwBpApplyDrs(block);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        st = GetExceptionCode();
        block->Stage = CRXSHIELD_HWBP_STAGE_SET;
    }

    block->Status = st;
    KeSetEvent(&block->Completed, IO_NO_INCREMENT, FALSE);
    if (InterlockedDecrement(&block->RefCount) == 0) {
        ExFreePoolWithTag(block, CRXSHIELD_HWBP_APC_TAG);
    }
}

static _Function_class_(KRUNDOWN_ROUTINE)
_IRQL_requires_same_
VOID NTAPI CrxHwBpApcRundownRoutine(
    _In_ PKAPC Apc
) {
    // The thread exited with the APC still queued; the APC never ran.
    PCRXSHIELD_HWBP_APC_BLOCK block = CONTAINING_RECORD(Apc, CRXSHIELD_HWBP_APC_BLOCK, Apc);
    block->Status = STATUS_THREAD_IS_TERMINATING;
    block->Stage = CRXSHIELD_HWBP_STAGE_GET;
    KeSetEvent(&block->Completed, IO_NO_INCREMENT, FALSE);  // wake a waiting dispatcher
    if (InterlockedDecrement(&block->RefCount) == 0) {
        ExFreePoolWithTag(block, CRXSHIELD_HWBP_APC_TAG);
    }
}

// Apply a hardware breakpoint set/clear to every thread of a process by
// queueing a kernel APC to each thread (see the note above). Threads that
// fail any stage are counted per stage. Runs at PASSIVE_LEVEL.
static NTSTATUS CrxApplyHwBpToProcess(
    _In_ ULONG ProcessId,
    _In_ ULONG Slot,        // 0-3, or CRXSHIELD_HWBP_SLOT_ALL (clear only)
    _In_ BOOLEAN Set,       // TRUE = set breakpoint, FALSE = clear
    _In_ ULONG64 Address,
    _In_ ULONG Type,
    _In_ ULONG Length,
    _Out_ PCRXSHIELD_HWBP_RESPONSE Resp
) {
    RtlZeroMemory(Resp, sizeof(*Resp));

    // Validate the process exists (enumeration also re-checks via the walk).
    PEPROCESS process = NULL;
    NTSTATUS status = PsLookupProcessByProcessId((HANDLE)(ULONG_PTR)ProcessId, &process);
    if (!NT_SUCCESS(status)) return status;
    ObDereferenceObject(process);

    CRXSHIELD_THREAD_INFO threads[CRXSHIELD_MAX_THREADS];
    ULONG total = 0;
    ULONG returned = 0;
    status = CrxEnumerateProcessThreads(ProcessId, threads, CRXSHIELD_MAX_THREADS, &total, &returned);
    if (!NT_SUCCESS(status)) return status;
    if (returned < total) {
        LOG("CrxApplyHwBpToProcess: PID %lu has %lu threads, capped at %lu",
            ProcessId, total, (ULONG)CRXSHIELD_MAX_THREADS);
    }

    for (ULONG i = 0; i < returned; i++) {
        PETHREAD thread = NULL;
        NTSTATUS threadStatus = PsLookupThreadByThreadId((HANDLE)(ULONG_PTR)threads[i].ThreadId, &thread);
        if (!NT_SUCCESS(threadStatus)) {
            Resp->LookupFailed++;
            Resp->ThreadsFailed++;
            if (Resp->FirstErrorStatus == STATUS_SUCCESS) Resp->FirstErrorStatus = threadStatus;
            continue;
        }

        PCRXSHIELD_HWBP_APC_BLOCK block =
            (PCRXSHIELD_HWBP_APC_BLOCK)ExAllocatePool2(POOL_FLAG_NON_PAGED, sizeof(*block), CRXSHIELD_HWBP_APC_TAG);
        if (!block) {
            ObDereferenceObject(thread);
            Resp->GetCtxFailed++;
            Resp->ThreadsFailed++;
            if (Resp->FirstErrorStatus == STATUS_SUCCESS) Resp->FirstErrorStatus = STATUS_INSUFFICIENT_RESOURCES;
            continue;
        }
        RtlZeroMemory(block, sizeof(*block));
        KeInitializeEvent(&block->Completed, NotificationEvent, FALSE);
        block->RefCount = 2;
        block->Status = STATUS_UNSUCCESSFUL;  // overwritten if the APC runs
        block->Stage = CRXSHIELD_HWBP_STAGE_GET;
        block->Set = Set;
        block->Slot = Slot;
        block->Address = Address;
        block->Type = Type;
        block->Length = Length;

        // NormalRoutine == NULL makes this a special kernel APC: the kernel
        // routine preempts the target thread at APC_LEVEL with no alertable
        // wait required. The rundown routine handles thread-exit-before-delivery.
        KeInitializeApc(&block->Apc, thread, OriginalApcEnvironment,
                        CrxHwBpApcKernelRoutine, CrxHwBpApcRundownRoutine, NULL,
                        KernelMode, block);

        if (!KeInsertQueueApc(&block->Apc, NULL, NULL, IO_NO_INCREMENT)) {
            // Thread is exiting; the APC was not queued so no routine will
            // ever run - the block is ours to free.
            ObDereferenceObject(thread);
            ExFreePoolWithTag(block, CRXSHIELD_HWBP_APC_TAG);
            Resp->GetCtxFailed++;
            Resp->ThreadsFailed++;
            if (Resp->FirstErrorStatus == STATUS_SUCCESS) Resp->FirstErrorStatus = STATUS_THREAD_IS_TERMINATING;
            continue;
        }
        // The APC queue machinery owns the thread now; drop our reference.
        ObDereferenceObject(thread);

        LARGE_INTEGER timeout;
        timeout.QuadPart = -2LL * 10 * 1000 * 1000;  // 2 seconds, relative
        NTSTATUS waitStatus = KeWaitForSingleObject(&block->Completed, Executive, KernelMode, FALSE, &timeout);
        if (waitStatus == STATUS_SUCCESS && NT_SUCCESS(block->Status)) {
            Resp->ThreadsUpdated++;
        } else {
            Resp->ThreadsFailed++;
            if (waitStatus != STATUS_SUCCESS) {
                // APC did not run within the timeout (suspended or
                // long-waiting thread). The block stays valid for a
                // late-running APC via the refcount.
                Resp->GetCtxFailed++;
                if (Resp->FirstErrorStatus == STATUS_SUCCESS) Resp->FirstErrorStatus = STATUS_TIMEOUT;
            } else {
                if (block->Stage == CRXSHIELD_HWBP_STAGE_SET) Resp->SetCtxFailed++;
                else Resp->GetCtxFailed++;
                if (Resp->FirstErrorStatus == STATUS_SUCCESS) Resp->FirstErrorStatus = block->Status;
            }
        }

        if (InterlockedDecrement(&block->RefCount) == 0) {
            ExFreePoolWithTag(block, CRXSHIELD_HWBP_APC_TAG);
        }
    }

    return STATUS_SUCCESS;
}

NTSTATUS DeviceControl(PDEVICE_OBJECT DeviceObject, PIRP Irp) {
    UNREFERENCED_PARAMETER(DeviceObject);
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    NTSTATUS status = STATUS_SUCCESS;
    ULONG_PTR info = 0;
    PVOID buf = Irp->AssociatedIrp.SystemBuffer;
    ULONG inLen = stack->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outLen = stack->Parameters.DeviceIoControl.OutputBufferLength;

    // Only require a non-NULL SystemBuffer here; per-IOCTL in/out length
    // validation happens inside each case (GET_VERSION takes no input).
    if (!buf) {
        status = STATUS_INVALID_PARAMETER;
        goto complete;
    }

    switch (stack->Parameters.DeviceIoControl.IoControlCode) {
    case IOCTL_CRXSHIELD_GET_VERSION: {
        LOG("IOCTL_GET_VERSION");
        if (outLen < sizeof(CRXSHIELD_VERSION)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_VERSION v = (PCRXSHIELD_VERSION)buf;
        v->Major = CRXSHIELD_VERSION_MAJOR;
        v->Minor = CRXSHIELD_VERSION_MINOR;
        v->Build = CRXSHIELD_VERSION_BUILD;
        info = sizeof(CRXSHIELD_VERSION);
        break;
    }
    case IOCTL_CRXSHIELD_READ_MEMORY: {
        if (inLen < sizeof(CRXSHIELD_READ_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_READ_REQUEST req = (PCRXSHIELD_READ_REQUEST)buf;
        LOG("IOCTL_READ_MEMORY: PID: %d, Addr: 0x%llX, Size: %d", req->ProcessId, req->Address, req->Size);
        if (req->Size == 0 || req->Size > CRXSHIELD_MAX_TRANSFER) { status = STATUS_INVALID_PARAMETER; break; }
        if (outLen < req->Size) { status = STATUS_BUFFER_TOO_SMALL; break; }
        SIZE_T read = 0;
        status = ReadProcessMemory(req->ProcessId, (PVOID)req->Address, buf, req->Size, &read);
        info = read;
        break;
    }
    case IOCTL_CRXSHIELD_WRITE_MEMORY: {
        // Validate the fixed header length FIRST - req->Size must never be
        // dereferenced before we know the full header is present, and the
        // combined check must be subtraction-based so a huge Size cannot
        // wrap the addition.
        if (inLen < sizeof(CRXSHIELD_WRITE_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_WRITE_REQUEST req = (PCRXSHIELD_WRITE_REQUEST)buf;
        LOG("IOCTL_WRITE_MEMORY: PID: %d, Addr: 0x%llX, Size: %d", req->ProcessId, req->Address, req->Size);
        if (req->Size == 0 || req->Size > CRXSHIELD_MAX_TRANSFER) { status = STATUS_INVALID_PARAMETER; break; }
        if (req->Size > inLen - sizeof(CRXSHIELD_WRITE_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PVOID data = (PUCHAR)buf + sizeof(CRXSHIELD_WRITE_REQUEST);
        SIZE_T written = 0;
        status = WriteProcessMemory(req->ProcessId, (PVOID)req->Address, data, req->Size, &written);
        info = written;
        break;
    }
    case IOCTL_CRXSHIELD_GET_PROCESS_BASE: {
        if (inLen < sizeof(CRXSHIELD_PROCESS_BASE_REQUEST) || outLen < sizeof(CRXSHIELD_PROCESS_BASE_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_PROCESS_BASE_REQUEST req = (PCRXSHIELD_PROCESS_BASE_REQUEST)buf;
        LOG("IOCTL_GET_PROCESS_BASE: PID: %d", req->ProcessId);
        ULONG64 baseAddr = 0;
        status = GetProcessBaseAddress(req->ProcessId, &baseAddr);
        req->BaseAddress = baseAddr;
        info = sizeof(CRXSHIELD_PROCESS_BASE_REQUEST);
        break;
    }
    case IOCTL_CRXSHIELD_ENUM_THREADS: {
        if (inLen < sizeof(CRXSHIELD_ENUM_THREADS_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        // Count alone must always fit; thread entries fill whatever remains.
        if (outLen < sizeof(ULONG)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_ENUM_THREADS_REQUEST req = (PCRXSHIELD_ENUM_THREADS_REQUEST)buf;
        LOG("IOCTL_ENUM_THREADS: PID: %lu", req->ProcessId);
        ULONG capacity = (outLen - sizeof(ULONG)) / sizeof(CRXSHIELD_THREAD_INFO);
        if (capacity > CRXSHIELD_MAX_THREADS) capacity = CRXSHIELD_MAX_THREADS;
        // METHOD_BUFFERED aliases in/out on the same buffer: read the input
        // before any output is written.
        ULONG pid = req->ProcessId;
        PCRXSHIELD_ENUM_THREADS_RESPONSE resp = (PCRXSHIELD_ENUM_THREADS_RESPONSE)buf;
        ULONG total = 0;
        ULONG returned = 0;
        status = CrxEnumerateProcessThreads(pid, resp->Threads, capacity, &total, &returned);
        if (NT_SUCCESS(status)) {
            resp->Count = total;
            info = sizeof(ULONG) + (ULONG_PTR)returned * sizeof(CRXSHIELD_THREAD_INFO);
            if (returned < total) status = STATUS_BUFFER_OVERFLOW;  // partial data returned
        }
        break;
    }
    case IOCTL_CRXSHIELD_SET_HWBP: {
        if (inLen < sizeof(CRXSHIELD_SET_HWBP_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        if (outLen < sizeof(CRXSHIELD_HWBP_RESPONSE)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_SET_HWBP_REQUEST req = (PCRXSHIELD_SET_HWBP_REQUEST)buf;
        LOG("IOCTL_SET_HWBP: PID: %lu, Slot: %lu, Addr: 0x%llX, Type: %lu, Len: %lu",
            req->ProcessId, req->Slot, (unsigned long long)req->Address, req->Type, req->Length);
        if (req->Slot > 3) { status = STATUS_INVALID_PARAMETER; break; }
        if (req->Type > CRXSHIELD_HWBP_TYPE_READWRITE) { status = STATUS_INVALID_PARAMETER; break; }
        if (!(req->Length == 1 || req->Length == 2 || req->Length == 4 || req->Length == 8)) { status = STATUS_INVALID_PARAMETER; break; }
        if (req->Type == CRXSHIELD_HWBP_TYPE_EXECUTE && req->Length != 1) { status = STATUS_INVALID_PARAMETER; break; }
        // Copy inputs to locals: the output response aliases the same buffer.
        ULONG pid = req->ProcessId;
        ULONG slot = req->Slot;
        ULONG64 addr = req->Address;
        ULONG type = req->Type;
        ULONG length = req->Length;
        CRXSHIELD_HWBP_RESPONSE resp;
        status = CrxApplyHwBpToProcess(pid, slot, TRUE, addr, type, length, &resp);
        if (NT_SUCCESS(status)) {
            RtlCopyMemory(buf, &resp, sizeof(resp));
            info = sizeof(CRXSHIELD_HWBP_RESPONSE);
        }
        break;
    }
    case IOCTL_CRXSHIELD_CLEAR_HWBP: {
        if (inLen < sizeof(CRXSHIELD_CLEAR_HWBP_REQUEST)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        if (outLen < sizeof(CRXSHIELD_HWBP_RESPONSE)) { status = STATUS_BUFFER_TOO_SMALL; break; }
        PCRXSHIELD_CLEAR_HWBP_REQUEST req = (PCRXSHIELD_CLEAR_HWBP_REQUEST)buf;
        LOG("IOCTL_CLEAR_HWBP: PID: %lu, Slot: %lu", req->ProcessId, req->Slot);
        if (req->Slot > 3 && req->Slot != CRXSHIELD_HWBP_SLOT_ALL) { status = STATUS_INVALID_PARAMETER; break; }
        ULONG pid = req->ProcessId;
        ULONG slot = req->Slot;
        CRXSHIELD_HWBP_RESPONSE resp;
        status = CrxApplyHwBpToProcess(pid, slot, FALSE, 0, 0, 0, &resp);
        if (NT_SUCCESS(status)) {
            RtlCopyMemory(buf, &resp, sizeof(resp));
            info = sizeof(CRXSHIELD_HWBP_RESPONSE);
        }
        break;
    }
    default:
        status = STATUS_INVALID_DEVICE_REQUEST;
    }

complete:
    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = info;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

VOID DriverUnload(PDRIVER_OBJECT DriverObject) {
    LOG("Driver Unloaded");
    UNREFERENCED_PARAMETER(DriverObject);
    char symObf[sizeof(g_ObfSymLinkName)];
    RtlCopyMemory(symObf, g_ObfSymLinkName, sizeof(g_ObfSymLinkName));
    DeobfuscateString(symObf);

    UNICODE_STRING symLink;
    if (NT_SUCCESS(AnsiToUnicode(symObf, &symLink))) {
        IoDeleteSymbolicLink(&symLink);
        RtlFreeUnicodeString(&symLink);
    }

    if (g_DeviceObject) {
        IoDeleteDevice(g_DeviceObject);
    }
}

NTSTATUS DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath) {
    UNREFERENCED_PARAMETER(RegistryPath);
    NTSTATUS status;

    // Resolve APIs
    UNICODE_STRING routineName;
    RtlInitUnicodeString(&routineName, L"MmCopyVirtualMemory");
    g_MmCopyVirtualMemory = (PFN_MmCopyVirtualMemory)MmGetSystemRoutineAddress(&routineName);

    RtlInitUnicodeString(&routineName, L"PsGetProcessSectionBaseAddress");
    g_PsGetProcessSectionBaseAddress = (PFN_PsGetProcessSectionBaseAddress)MmGetSystemRoutineAddress(&routineName);

    RtlInitUnicodeString(&routineName, L"PsGetProcessWow64Process");
    g_PsGetProcessWow64Process = (PFN_PsGetProcessWow64Process)MmGetSystemRoutineAddress(&routineName);

    char devObf[sizeof(g_ObfDeviceName)];
    char symObf[sizeof(g_ObfSymLinkName)];
    RtlCopyMemory(devObf, g_ObfDeviceName, sizeof(g_ObfDeviceName));
    RtlCopyMemory(symObf, g_ObfSymLinkName, sizeof(g_ObfSymLinkName));
    DeobfuscateString(devObf);
    DeobfuscateString(symObf);

    UNICODE_STRING devName, symLink;
    status = AnsiToUnicode(devObf, &devName);
    if (!NT_SUCCESS(status)) return status;
    status = AnsiToUnicode(symObf, &symLink);
    if (!NT_SUCCESS(status)) {
        RtlFreeUnicodeString(&devName);
        return status;
    }

    // Create the device secured so only SYSTEM/Administrators can open it
    UNICODE_STRING sddl;
    RtlInitUnicodeString(&sddl, CRXSHIELD_DEVICE_SDDL);
    status = IoCreateDeviceSecure(DriverObject, 0, &devName, FILE_DEVICE_UNKNOWN, FILE_DEVICE_SECURE_OPEN, FALSE, &sddl, (LPCGUID)&CRXSHIELD_DEVCLASS_UNKNOWN, &g_DeviceObject);
    if (!NT_SUCCESS(status)) {
        RtlFreeUnicodeString(&devName);
        RtlFreeUnicodeString(&symLink);
        return status;
    }

    status = IoCreateSymbolicLink(&symLink, &devName);
    if (!NT_SUCCESS(status)) {
        IoDeleteDevice(g_DeviceObject);
        RtlFreeUnicodeString(&devName);
        RtlFreeUnicodeString(&symLink);
        return status;
    }

    RtlFreeUnicodeString(&devName);
    RtlFreeUnicodeString(&symLink);

    DriverObject->DriverUnload = DriverUnload;
    DriverObject->MajorFunction[IRP_MJ_CREATE] = CreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = CreateClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = DeviceControl;

    g_DeviceObject->Flags |= DO_BUFFERED_IO;
    g_DeviceObject->Flags &= ~DO_DEVICE_INITIALIZING;

    LOG("Driver Loaded");

    return STATUS_SUCCESS;
}
