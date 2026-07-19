using System.ComponentModel;
using System.Runtime.InteropServices;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public enum DebugSessionState
{
    Detached,
    Attaching,
    Running,
    Paused,
    Detaching,
    Exited,
    Faulted
}

public sealed record DebugSessionSnapshot(
    DebugSessionState State,
    int ProcessId,
    int? SelectedThreadId,
    string? StopReason = null,
    string? Error = null)
{
    public bool CanReadThreadContext => State == DebugSessionState.Paused && SelectedThreadId.HasValue;
}

public interface IDebugSessionService
{
    DebugSessionSnapshot Snapshot { get; }
    event EventHandler<DebugSessionSnapshot>? StateChanged;
    Task AttachAsync(CancellationToken cancellationToken);
    Task DetachAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
}

public sealed record ThreadDescriptor(int ThreadId, ulong StartAddress, int Priority, string State);

public interface IThreadService
{
    IReadOnlyList<ThreadDescriptor> GetThreads();
}

public sealed class ProcessThreadService : IThreadService
{
    private readonly ProcessAccess _process;
    public ProcessThreadService(ProcessAccess process) => _process = process;

    public IReadOnlyList<ThreadDescriptor> GetThreads()
    {
        try
        {
            return _process.GetThreads()
                .Select(thread => new ThreadDescriptor(
                    thread.Id,
                    unchecked((ulong)thread.StartAddress.ToInt64()),
                    thread.Priority,
                    thread.State))
                .ToArray();
        }
        catch { return Array.Empty<ThreadDescriptor>(); }
    }
}

public sealed record RegisterValue(string Name, ulong Value, ulong? PreviousValue = null)
{
    public long? SignedDelta => PreviousValue.HasValue
        ? unchecked((long)(Value - PreviousValue.Value))
        : null;
    public bool HasChanged => PreviousValue.HasValue && PreviousValue.Value != Value;
}

public sealed record RegisterSnapshot(
    int ThreadId,
    bool Is64Bit,
    IReadOnlyList<RegisterValue> Registers,
    ulong InstructionPointer,
    ulong StackPointer,
    uint Flags,
    DateTimeOffset CapturedAt);

public sealed record RegisterCaptureResult(
    bool IsAvailable,
    RegisterSnapshot? Snapshot,
    string? Error)
{
    public static RegisterCaptureResult Unavailable(string error) => new(false, null, error);
    public static RegisterCaptureResult Captured(RegisterSnapshot snapshot) => new(true, snapshot, null);
}

public enum RegisterWriteError
{
    None,
    InvalidSession,
    ProcessMismatch,
    ThreadMismatch,
    ArchitectureMismatch,
    StaleSnapshot,
    UnknownRegister,
    ReadOnlyRegister,
    InvalidValue,
    NativeFailure,
    VerificationFailure,
    Cancelled
}

public sealed record RegisterWriteRequest(
    DebugSessionSnapshot Session,
    RegisterSnapshot Snapshot,
    string RegisterName,
    ulong RequestedValue);

public sealed record RegisterWriteValidation(bool IsValid, RegisterWriteError Error, string? Diagnostic)
{
    public static RegisterWriteValidation Valid() => new(true, RegisterWriteError.None, null);
    public static RegisterWriteValidation Invalid(RegisterWriteError error, string diagnostic) => new(false, error, diagnostic);
}

public sealed record RegisterWriteResult(
    bool Success,
    RegisterWriteError Error,
    ulong? OriginalValue,
    ulong RequestedValue,
    ulong? VerifiedValue,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    RegisterSnapshot? Snapshot,
    string? Diagnostic)
{
    public static RegisterWriteResult Failed(RegisterWriteRequest request, RegisterWriteError error, string diagnostic,
        ulong? original = null, ulong? verified = null, bool rollbackAttempted = false, bool rollbackSucceeded = false) =>
        new(false, error, original, request.RequestedValue, verified, rollbackAttempted, rollbackSucceeded, null, diagnostic);
}

public interface IRegisterService
{
    Task<RegisterCaptureResult> CaptureAsync(
        DebugSessionSnapshot session,
        RegisterSnapshot? previous,
        CancellationToken cancellationToken);
    RegisterWriteValidation ValidateWrite(RegisterWriteRequest request);
    Task<RegisterWriteResult> WriteAsync(RegisterWriteRequest request, CancellationToken cancellationToken);
}

public sealed class WindowsRegisterService : IRegisterService
{
    private const uint WritableFlagsMask = 0x00250FD5;
    private readonly ProcessAccess _process;
    private readonly object _snapshotGate = new();
    private RegisterSnapshot? _latestSnapshot;
    public WindowsRegisterService(ProcessAccess process) => _process = process;

    public Task<RegisterCaptureResult> CaptureAsync(
        DebugSessionSnapshot session,
        RegisterSnapshot? previous,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.CanReadThreadContext)
            return RegisterCaptureResult.Unavailable("Registers require a selected thread in a verified paused debug session.");
        if (!_process.IsOpen || _process.Target == null)
            return RegisterCaptureResult.Unavailable("The selected process is unavailable.");
        if (session.ProcessId != _process.Target.Id)
            return RegisterCaptureResult.Unavailable("The debug session does not belong to the selected process.");

        int threadId = session.SelectedThreadId!.Value;
        IntPtr threadHandle = ProcessAccess.OpenThread(
            ProcessAccess.THREAD_GET_CONTEXT | ProcessAccess.THREAD_SET_CONTEXT,
            false,
            unchecked((uint)threadId));
        if (threadHandle == IntPtr.Zero)
            return RegisterCaptureResult.Unavailable(
                $"Unable to open thread {threadId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        try
        {
            RegisterCaptureResult result = _process.Is64Bit
                ? Capture64(threadHandle, threadId, previous)
                : Capture32(threadHandle, threadId, previous);
            if (result.Snapshot != null)
            {
                lock (_snapshotGate) _latestSnapshot = result.Snapshot;
            }
            return result;
        }
        finally
        {
            ProcessAccess.CloseHandle(threadHandle);
        }
    }, cancellationToken);

    public RegisterWriteValidation ValidateWrite(RegisterWriteRequest request)
    {
        if (!request.Session.CanReadThreadContext)
            return RegisterWriteValidation.Invalid(RegisterWriteError.InvalidSession, "Register editing requires a verified paused debugger session.");
        if (!_process.IsOpen || _process.Target == null || request.Session.ProcessId != _process.Target.Id)
            return RegisterWriteValidation.Invalid(RegisterWriteError.ProcessMismatch, "The register snapshot belongs to a different or terminated process.");
        if (request.Session.SelectedThreadId != request.Snapshot.ThreadId)
            return RegisterWriteValidation.Invalid(RegisterWriteError.ThreadMismatch, "The selected thread no longer matches the register snapshot.");
        if (_process.Is64Bit != request.Snapshot.Is64Bit)
            return RegisterWriteValidation.Invalid(RegisterWriteError.ArchitectureMismatch, "The target architecture changed after this snapshot was captured.");
        lock (_snapshotGate)
        {
            if (_latestSnapshot == null || _latestSnapshot.ThreadId != request.Snapshot.ThreadId ||
                _latestSnapshot.Is64Bit != request.Snapshot.Is64Bit ||
                _latestSnapshot.CapturedAt != request.Snapshot.CapturedAt)
                return RegisterWriteValidation.Invalid(RegisterWriteError.StaleSnapshot,
                    "The displayed register snapshot is no longer the current capture generation.");
        }
        string name = request.RegisterName.Trim().ToUpperInvariant();
        if (name.StartsWith("DR", StringComparison.Ordinal))
            return RegisterWriteValidation.Invalid(RegisterWriteError.ReadOnlyRegister, "Debug registers are owned by the breakpoint service and are read-only here.");
        if (!SupportedRegisters(request.Snapshot.Is64Bit).Contains(name, StringComparer.OrdinalIgnoreCase))
            return RegisterWriteValidation.Invalid(RegisterWriteError.UnknownRegister, $"{name} is not an editable register.");
        if (!request.Snapshot.Is64Bit && request.RequestedValue > uint.MaxValue)
            return RegisterWriteValidation.Invalid(RegisterWriteError.InvalidValue, "The value does not fit in a 32-bit register.");
        return RegisterWriteValidation.Valid();
    }

    public Task<RegisterWriteResult> WriteAsync(RegisterWriteRequest request, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        RegisterWriteValidation validation = ValidateWrite(request);
        if (!validation.IsValid)
            return RegisterWriteResult.Failed(request, validation.Error, validation.Diagnostic ?? "Register write rejected.");

        IntPtr thread = ProcessAccess.OpenThread(ProcessAccess.THREAD_GET_CONTEXT | ProcessAccess.THREAD_SET_CONTEXT,
            false, unchecked((uint)request.Snapshot.ThreadId));
        if (thread == IntPtr.Zero)
            return RegisterWriteResult.Failed(request, RegisterWriteError.NativeFailure,
                $"Unable to open thread {request.Snapshot.ThreadId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        try
        {
            return request.Snapshot.Is64Bit
                ? Write64(thread, request, cancellationToken)
                : Write32(thread, request, cancellationToken);
        }
        finally { ProcessAccess.CloseHandle(thread); }
    }, cancellationToken);

    private RegisterWriteResult Write64(IntPtr thread, RegisterWriteRequest request, CancellationToken cancellationToken)
    {
        CONTEXT64 context = CreateContext64();
        if (!ProcessAccess.GetThreadContext(thread, ref context)) return NativeFailure(request, "GetThreadContext");
        string name = request.RegisterName.Trim().ToUpperInvariant();
        if (!TryGet64(context, name, out ulong original))
            return RegisterWriteResult.Failed(request, RegisterWriteError.UnknownRegister, $"{name} is not editable.");
        if (!SnapshotMatches(request.Snapshot, name, original))
            return RegisterWriteResult.Failed(request, RegisterWriteError.StaleSnapshot,
                $"{name} changed after the displayed snapshot. Refresh registers before editing.", original);

        ulong requested = name == "RFLAGS" ? MergeFlags(original, request.RequestedValue) : request.RequestedValue;
        Set64(ref context, name, requested);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProcessAccess.SetThreadContext(thread, ref context)) return NativeFailure(request, "SetThreadContext", original);
        CONTEXT64 verifiedContext = CreateContext64();
        ulong verified = 0;
        if (!ProcessAccess.GetThreadContext(thread, ref verifiedContext) || !TryGet64(verifiedContext, name, out verified) || verified != requested)
            return Rollback64(thread, request, name, original, verified);
        RegisterCaptureResult capture = Capture64(thread, request.Snapshot.ThreadId, request.Snapshot);
        if (capture.Snapshot != null) { lock (_snapshotGate) _latestSnapshot = capture.Snapshot; }
        return new RegisterWriteResult(true, RegisterWriteError.None, original, requested, verified, false, false,
            capture.Snapshot, $"{name} updated and verified.");
    }

    private RegisterWriteResult Write32(IntPtr thread, RegisterWriteRequest request, CancellationToken cancellationToken)
    {
        CONTEXT32 context = CreateContext32();
        if (!ProcessAccess.Wow64GetThreadContext(thread, ref context)) return NativeFailure(request, "Wow64GetThreadContext");
        string name = request.RegisterName.Trim().ToUpperInvariant();
        if (!TryGet32(context, name, out uint original32))
            return RegisterWriteResult.Failed(request, RegisterWriteError.UnknownRegister, $"{name} is not editable.");
        ulong original = original32;
        if (!SnapshotMatches(request.Snapshot, name, original))
            return RegisterWriteResult.Failed(request, RegisterWriteError.StaleSnapshot,
                $"{name} changed after the displayed snapshot. Refresh registers before editing.", original);
        uint requested = name == "EFLAGS" ? (uint)MergeFlags(original, request.RequestedValue) : checked((uint)request.RequestedValue);
        Set32(ref context, name, requested);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProcessAccess.Wow64SetThreadContext(thread, ref context)) return NativeFailure(request, "Wow64SetThreadContext", original);
        CONTEXT32 verifiedContext = CreateContext32();
        uint verified32 = 0;
        if (!ProcessAccess.Wow64GetThreadContext(thread, ref verifiedContext) || !TryGet32(verifiedContext, name, out verified32) || verified32 != requested)
            return Rollback32(thread, request, name, original32, verified32);
        RegisterCaptureResult capture = Capture32(thread, request.Snapshot.ThreadId, request.Snapshot);
        if (capture.Snapshot != null) { lock (_snapshotGate) _latestSnapshot = capture.Snapshot; }
        return new RegisterWriteResult(true, RegisterWriteError.None, original, requested, verified32, false, false,
            capture.Snapshot, $"{name} updated and verified.");
    }

    private static string[] SupportedRegisters(bool is64Bit) => is64Bit
        ? ["RAX", "RBX", "RCX", "RDX", "RSI", "RDI", "RBP", "RSP", "RIP", "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15", "RFLAGS"]
        : ["EAX", "EBX", "ECX", "EDX", "ESI", "EDI", "EBP", "ESP", "EIP", "EFLAGS"];

    private static CONTEXT64 CreateContext64() => new()
    {
        ContextFlags = ProcessAccess.CONTEXT_ALL,
        FltSave = new byte[512],
        VectorRegister = new M128A[26]
    };

    private static CONTEXT32 CreateContext32() => new()
    {
        ContextFlags = ProcessAccess.CONTEXT32_ALL,
        FloatSave = new byte[112],
        ExtendedRegisters = new byte[512]
    };

    private static ulong MergeFlags(ulong original, ulong requested) =>
        (original & ~WritableFlagsMask) | (requested & WritableFlagsMask) | 0x2;

    private static bool SnapshotMatches(RegisterSnapshot snapshot, string name, ulong current)
    {
        if (name is "RFLAGS" or "EFLAGS") return snapshot.Flags == (uint)current;
        RegisterValue? value = snapshot.Registers.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return value != null && value.Value == current;
    }

    private static bool TryGet64(CONTEXT64 c, string name, out ulong value)
    {
        value = name switch
        {
            "RAX" => c.Rax, "RBX" => c.Rbx, "RCX" => c.Rcx, "RDX" => c.Rdx,
            "RSI" => c.Rsi, "RDI" => c.Rdi, "RBP" => c.Rbp, "RSP" => c.Rsp, "RIP" => c.Rip,
            "R8" => c.R8, "R9" => c.R9, "R10" => c.R10, "R11" => c.R11,
            "R12" => c.R12, "R13" => c.R13, "R14" => c.R14, "R15" => c.R15,
            "RFLAGS" => c.EFlags, _ => 0
        };
        return SupportedRegisters(true).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static void Set64(ref CONTEXT64 c, string name, ulong value)
    {
        switch (name)
        {
            case "RAX": c.Rax = value; break; case "RBX": c.Rbx = value; break;
            case "RCX": c.Rcx = value; break; case "RDX": c.Rdx = value; break;
            case "RSI": c.Rsi = value; break; case "RDI": c.Rdi = value; break;
            case "RBP": c.Rbp = value; break; case "RSP": c.Rsp = value; break;
            case "RIP": c.Rip = value; break; case "R8": c.R8 = value; break;
            case "R9": c.R9 = value; break; case "R10": c.R10 = value; break;
            case "R11": c.R11 = value; break; case "R12": c.R12 = value; break;
            case "R13": c.R13 = value; break; case "R14": c.R14 = value; break;
            case "R15": c.R15 = value; break; case "RFLAGS": c.EFlags = checked((uint)value); break;
        }
    }

    private static bool TryGet32(CONTEXT32 c, string name, out uint value)
    {
        value = name switch
        {
            "EAX" => c.Eax, "EBX" => c.Ebx, "ECX" => c.Ecx, "EDX" => c.Edx,
            "ESI" => c.Esi, "EDI" => c.Edi, "EBP" => c.Ebp, "ESP" => c.Esp,
            "EIP" => c.Eip, "EFLAGS" => c.EFlags, _ => 0
        };
        return SupportedRegisters(false).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static void Set32(ref CONTEXT32 c, string name, uint value)
    {
        switch (name)
        {
            case "EAX": c.Eax = value; break; case "EBX": c.Ebx = value; break;
            case "ECX": c.Ecx = value; break; case "EDX": c.Edx = value; break;
            case "ESI": c.Esi = value; break; case "EDI": c.Edi = value; break;
            case "EBP": c.Ebp = value; break; case "ESP": c.Esp = value; break;
            case "EIP": c.Eip = value; break; case "EFLAGS": c.EFlags = value; break;
        }
    }

    private static RegisterWriteResult NativeFailure(RegisterWriteRequest request, string operation, ulong? original = null) =>
        RegisterWriteResult.Failed(request, RegisterWriteError.NativeFailure,
            $"{operation} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", original);

    private static RegisterWriteResult Rollback64(IntPtr thread, RegisterWriteRequest request, string name, ulong original, ulong verified)
    {
        CONTEXT64 rollback = CreateContext64();
        bool restored = ProcessAccess.GetThreadContext(thread, ref rollback);
        if (restored) { Set64(ref rollback, name, original); restored = ProcessAccess.SetThreadContext(thread, ref rollback); }
        if (restored)
        {
            CONTEXT64 check = CreateContext64();
            restored = ProcessAccess.GetThreadContext(thread, ref check) && TryGet64(check, name, out ulong value) && value == original;
        }
        return RegisterWriteResult.Failed(request, RegisterWriteError.VerificationFailure,
            restored ? "Register verification failed; the original context was restored." : "Register verification failed and rollback could not be verified.",
            original, verified, true, restored);
    }

    private static RegisterWriteResult Rollback32(IntPtr thread, RegisterWriteRequest request, string name, uint original, uint verified)
    {
        CONTEXT32 rollback = CreateContext32();
        bool restored = ProcessAccess.Wow64GetThreadContext(thread, ref rollback);
        if (restored) { Set32(ref rollback, name, original); restored = ProcessAccess.Wow64SetThreadContext(thread, ref rollback); }
        if (restored)
        {
            CONTEXT32 check = CreateContext32();
            restored = ProcessAccess.Wow64GetThreadContext(thread, ref check) && TryGet32(check, name, out uint value) && value == original;
        }
        return RegisterWriteResult.Failed(request, RegisterWriteError.VerificationFailure,
            restored ? "Register verification failed; the original context was restored." : "Register verification failed and rollback could not be verified.",
            original, verified, true, restored);
    }

    private static RegisterCaptureResult Capture64(
        IntPtr threadHandle,
        int threadId,
        RegisterSnapshot? previous)
    {
        CONTEXT64 context = CreateContext64();
        if (!ProcessAccess.GetThreadContext(threadHandle, ref context))
            return RegisterCaptureResult.Unavailable(
                $"GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var values = new (string Name, ulong Value)[]
        {
            ("RAX", context.Rax), ("RBX", context.Rbx), ("RCX", context.Rcx), ("RDX", context.Rdx),
            ("RSI", context.Rsi), ("RDI", context.Rdi), ("RBP", context.Rbp), ("RSP", context.Rsp),
            ("RIP", context.Rip), ("R8", context.R8), ("R9", context.R9), ("R10", context.R10),
            ("R11", context.R11), ("R12", context.R12), ("R13", context.R13), ("R14", context.R14),
            ("R15", context.R15), ("RFLAGS", context.EFlags), ("DR0", context.Dr0), ("DR1", context.Dr1), ("DR2", context.Dr2),
            ("DR3", context.Dr3), ("DR6", context.Dr6), ("DR7", context.Dr7)
        };
        return RegisterCaptureResult.Captured(CreateSnapshot(
            threadId, true, values, context.Rip, context.Rsp, context.EFlags, previous));
    }

    private static RegisterCaptureResult Capture32(
        IntPtr threadHandle,
        int threadId,
        RegisterSnapshot? previous)
    {
        CONTEXT32 context = CreateContext32();
        if (!ProcessAccess.Wow64GetThreadContext(threadHandle, ref context))
            return RegisterCaptureResult.Unavailable(
                $"Wow64GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var values = new (string Name, ulong Value)[]
        {
            ("EAX", context.Eax), ("EBX", context.Ebx), ("ECX", context.Ecx), ("EDX", context.Edx),
            ("ESI", context.Esi), ("EDI", context.Edi), ("EBP", context.Ebp), ("ESP", context.Esp),
            ("EIP", context.Eip), ("EFLAGS", context.EFlags), ("DR0", context.Dr0), ("DR1", context.Dr1), ("DR2", context.Dr2),
            ("DR3", context.Dr3), ("DR6", context.Dr6), ("DR7", context.Dr7)
        };
        return RegisterCaptureResult.Captured(CreateSnapshot(
            threadId, false, values, context.Eip, context.Esp, context.EFlags, previous));
    }

    private static RegisterSnapshot CreateSnapshot(
        int threadId,
        bool is64Bit,
        IEnumerable<(string Name, ulong Value)> values,
        ulong instructionPointer,
        ulong stackPointer,
        uint flags,
        RegisterSnapshot? previous)
    {
        Dictionary<string, ulong> previousValues = previous?.Registers.ToDictionary(
            value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        RegisterValue[] registers = values.Select(value => new RegisterValue(
            value.Name,
            value.Value,
            previousValues.TryGetValue(value.Name, out ulong oldValue) ? oldValue : null)).ToArray();
        return new RegisterSnapshot(
            threadId, is64Bit, registers, instructionPointer, stackPointer, flags, DateTimeOffset.UtcNow);
    }
}
