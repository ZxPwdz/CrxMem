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

public interface IRegisterService
{
    Task<RegisterCaptureResult> CaptureAsync(
        DebugSessionSnapshot session,
        RegisterSnapshot? previous,
        CancellationToken cancellationToken);
}

public sealed class WindowsRegisterService : IRegisterService
{
    private readonly ProcessAccess _process;
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
            return _process.Is64Bit
                ? Capture64(threadHandle, threadId, previous)
                : Capture32(threadHandle, threadId, previous);
        }
        finally
        {
            ProcessAccess.CloseHandle(threadHandle);
        }
    }, cancellationToken);

    private static RegisterCaptureResult Capture64(
        IntPtr threadHandle,
        int threadId,
        RegisterSnapshot? previous)
    {
        var context = new CONTEXT64
        {
            ContextFlags = ProcessAccess.CONTEXT_ALL,
            FltSave = new byte[512],
            VectorRegister = new M128A[26]
        };
        if (!ProcessAccess.GetThreadContext(threadHandle, ref context))
            return RegisterCaptureResult.Unavailable(
                $"GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var values = new (string Name, ulong Value)[]
        {
            ("RAX", context.Rax), ("RBX", context.Rbx), ("RCX", context.Rcx), ("RDX", context.Rdx),
            ("RSI", context.Rsi), ("RDI", context.Rdi), ("RBP", context.Rbp), ("RSP", context.Rsp),
            ("RIP", context.Rip), ("R8", context.R8), ("R9", context.R9), ("R10", context.R10),
            ("R11", context.R11), ("R12", context.R12), ("R13", context.R13), ("R14", context.R14),
            ("R15", context.R15), ("DR0", context.Dr0), ("DR1", context.Dr1), ("DR2", context.Dr2),
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
        var context = new CONTEXT32
        {
            ContextFlags = ProcessAccess.CONTEXT32_ALL,
            FloatSave = new byte[112],
            ExtendedRegisters = new byte[512]
        };
        if (!ProcessAccess.Wow64GetThreadContext(threadHandle, ref context))
            return RegisterCaptureResult.Unavailable(
                $"Wow64GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        var values = new (string Name, ulong Value)[]
        {
            ("EAX", context.Eax), ("EBX", context.Ebx), ("ECX", context.Ecx), ("EDX", context.Edx),
            ("ESI", context.Esi), ("EDI", context.Edi), ("EBP", context.Ebp), ("ESP", context.Esp),
            ("EIP", context.Eip), ("DR0", context.Dr0), ("DR1", context.Dr1), ("DR2", context.Dr2),
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
