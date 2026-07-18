using System.ComponentModel;
using System.Runtime.InteropServices;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record DebugEventDecision(bool IsHandled, bool ShouldPause, string? Reason = null)
{
    public static DebugEventDecision Unhandled { get; } = new(false, false);
    public static DebugEventDecision Continue { get; } = new(true, false);
    public static DebugEventDecision Pause(string reason) => new(true, true, reason);
}

public interface IDebugEventCoordinator
{
    void HandleEvent(NativeDebugEvent debugEvent) { }
    DebugEventDecision HandleException(NativeDebugEvent debugEvent);
    void RestoreOwnedStateBeforeDetach();
}

public sealed record ThreadExecutionContext(
    int ThreadId,
    bool Is64Bit,
    ulong InstructionPointer,
    ulong StackPointer,
    ulong Flags,
    ulong DebugStatus = 0);

public interface IThreadExecutionContextService
{
    ThreadExecutionContext Capture(int threadId);
    void SetControl(ThreadExecutionContext context);
}

public sealed class WindowsThreadExecutionContextService : IThreadExecutionContextService
{
    private readonly ProcessAccess _process;
    public WindowsThreadExecutionContextService(ProcessAccess process) => _process = process;

    public ThreadExecutionContext Capture(int threadId)
    {
        IntPtr thread = Open(threadId);
        try
        {
            if (_process.Is64Bit)
            {
                var context = new CONTEXT64
                {
                    ContextFlags = ProcessAccess.CONTEXT_ALL,
                    FltSave = new byte[512],
                    VectorRegister = new M128A[26]
                };
                if (!ProcessAccess.GetThreadContext(thread, ref context)) ThrowNative("GetThreadContext");
                return new ThreadExecutionContext(threadId, true, context.Rip, context.Rsp, context.EFlags, context.Dr6);
            }

            var context32 = new CONTEXT32
            {
                ContextFlags = ProcessAccess.CONTEXT32_ALL,
                FloatSave = new byte[112],
                ExtendedRegisters = new byte[512]
            };
            if (!ProcessAccess.Wow64GetThreadContext(thread, ref context32)) ThrowNative("Wow64GetThreadContext");
            return new ThreadExecutionContext(threadId, false, context32.Eip, context32.Esp, context32.EFlags, context32.Dr6);
        }
        finally { ProcessAccess.CloseHandle(thread); }
    }

    public void SetControl(ThreadExecutionContext value)
    {
        IntPtr thread = Open(value.ThreadId);
        try
        {
            if (value.Is64Bit)
            {
                var context = new CONTEXT64
                {
                    ContextFlags = ProcessAccess.CONTEXT_ALL,
                    FltSave = new byte[512],
                    VectorRegister = new M128A[26]
                };
                if (!ProcessAccess.GetThreadContext(thread, ref context)) ThrowNative("GetThreadContext");
                context.Rip = value.InstructionPointer;
                context.Rsp = value.StackPointer;
                context.EFlags = checked((uint)value.Flags);
                context.Dr6 = value.DebugStatus;
                if (!ProcessAccess.SetThreadContext(thread, ref context)) ThrowNative("SetThreadContext");
            }
            else
            {
                var context = new CONTEXT32
                {
                    ContextFlags = ProcessAccess.CONTEXT32_ALL,
                    FloatSave = new byte[112],
                    ExtendedRegisters = new byte[512]
                };
                if (!ProcessAccess.Wow64GetThreadContext(thread, ref context)) ThrowNative("Wow64GetThreadContext");
                context.Eip = checked((uint)value.InstructionPointer);
                context.Esp = checked((uint)value.StackPointer);
                context.EFlags = checked((uint)value.Flags);
                context.Dr6 = checked((uint)value.DebugStatus);
                if (!ProcessAccess.Wow64SetThreadContext(thread, ref context)) ThrowNative("Wow64SetThreadContext");
            }
        }
        finally { ProcessAccess.CloseHandle(thread); }
    }

    private static IntPtr Open(int threadId)
    {
        IntPtr thread = ProcessAccess.OpenThread(
            ProcessAccess.THREAD_GET_CONTEXT | ProcessAccess.THREAD_SET_CONTEXT,
            false,
            unchecked((uint)threadId));
        if (thread == IntPtr.Zero) ThrowNative("OpenThread");
        return thread;
    }

    private static void ThrowNative(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"{operation} failed.");
}

public enum BreakpointKind
{
    Software,
    Temporary
}

public sealed record BreakpointRecord(
    Guid Id,
    ulong Address,
    BreakpointKind Kind,
    bool OneShot,
    bool IsEnabled,
    long HitCount,
    Guid PatchId,
    string? ModuleName = null,
    ulong? ModuleRelativeAddress = null);

public enum HardwareBreakpointAccess { Execute, Write, ReadWrite }
public enum HardwareBreakpointSize { Byte1 = 1, Byte2 = 2, Byte4 = 4, Byte8 = 8 }

public sealed record HardwareBreakpointRecord(
    int Slot,
    ulong Address,
    HardwareBreakpointAccess Access,
    HardwareBreakpointSize Size,
    bool IsEnabled,
    long HitCount);

public interface IHardwareBreakpointAdapter
{
    bool SetAll(int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size);
    bool ClearAll(int slot);
    bool ApplyToThread(uint threadId, int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size);
}

public sealed class WindowsHardwareBreakpointAdapter : IHardwareBreakpointAdapter
{
    private readonly HardwareBreakpointManager _manager;
    public WindowsHardwareBreakpointAdapter(ProcessAccess process) => _manager = new HardwareBreakpointManager(process);
    public bool SetAll(int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size) =>
        _manager.SetBreakpoint(slot, address, Map(access), Map(size));
    public bool ClearAll(int slot)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (_manager.ClearBreakpointWhileDebugPaused(slot)) return true;
            Thread.Yield();
        }
        return false;
    }
    public bool ApplyToThread(uint threadId, int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size) =>
        _manager.ApplyBreakpointToThread(threadId, slot, address, Map(access), Map(size), suspendThread: false);
    private static HardwareBreakpointManager.BreakpointType Map(HardwareBreakpointAccess value) => value switch
    {
        HardwareBreakpointAccess.Execute => HardwareBreakpointManager.BreakpointType.Execute,
        HardwareBreakpointAccess.Write => HardwareBreakpointManager.BreakpointType.Write,
        _ => HardwareBreakpointManager.BreakpointType.ReadWrite
    };
    private static HardwareBreakpointManager.BreakpointSize Map(HardwareBreakpointSize value) => value switch
    {
        HardwareBreakpointSize.Byte1 => HardwareBreakpointManager.BreakpointSize.Byte1,
        HardwareBreakpointSize.Byte2 => HardwareBreakpointManager.BreakpointSize.Byte2,
        HardwareBreakpointSize.Byte4 => HardwareBreakpointManager.BreakpointSize.Byte4,
        HardwareBreakpointSize.Byte8 => HardwareBreakpointManager.BreakpointSize.Byte8,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public interface IBreakpointService
{
    IReadOnlyList<BreakpointRecord> Breakpoints { get; }
    IReadOnlyList<HardwareBreakpointRecord> HardwareBreakpoints { get; }
    Task<BreakpointRecord?> ToggleAsync(
        ulong address,
        string? moduleName = null,
        ulong? moduleRelativeAddress = null,
        CancellationToken cancellationToken = default);
    Task<BreakpointRecord> AddTemporaryAsync(ulong address, CancellationToken cancellationToken = default);
    bool HasEnabledBreakpoint(ulong address);
    Task<HardwareBreakpointRecord> SetHardwareAsync(
        ulong address,
        HardwareBreakpointAccess access,
        HardwareBreakpointSize size,
        CancellationToken cancellationToken = default);
    Task ClearHardwareAsync(int slot, CancellationToken cancellationToken = default);
}

public interface IDebugExecutionService
{
    Task StepIntoAsync(CancellationToken cancellationToken = default);
    Task StepOverAsync(ulong address, int instructionLength, bool isCall, CancellationToken cancellationToken = default);
    Task StepOutAsync(CancellationToken cancellationToken = default);
    Task RunToAsync(ulong address, CancellationToken cancellationToken = default);
    Task RunUntilReturnAsync(CancellationToken cancellationToken = default);
}

public sealed class BreakpointService :
    IBreakpointService,
    IDebugExecutionService,
    IDebugEventCoordinator
{
    private const ulong TrapFlag = 0x100;
    private readonly IPatchMemoryAdapter _memory;
    private readonly IPatchService _patches;
    private readonly IThreadExecutionContextService _contexts;
    private readonly IDebugSessionService _session;
    private readonly IHardwareBreakpointAdapter? _hardware;
    private readonly object _sync = new();
    private readonly List<BreakpointRecord> _breakpoints = new();
    private readonly Dictionary<int, Guid> _pendingReinsertions = new();
    private readonly HashSet<int> _pauseAfterReinsertion = new();
    private readonly List<HardwareBreakpointRecord> _hardwareBreakpoints = new();

    public BreakpointService(
        IPatchMemoryAdapter memory,
        IPatchService patches,
        IThreadExecutionContextService contexts,
        IDebugSessionService session,
        IHardwareBreakpointAdapter? hardware = null)
    {
        _memory = memory;
        _patches = patches;
        _contexts = contexts;
        _session = session;
        _hardware = hardware;
    }

    public IReadOnlyList<BreakpointRecord> Breakpoints
    {
        get { lock (_sync) return _breakpoints.ToArray(); }
    }

    public IReadOnlyList<HardwareBreakpointRecord> HardwareBreakpoints
    {
        get { lock (_sync) return _hardwareBreakpoints.ToArray(); }
    }

    public bool HasEnabledBreakpoint(ulong address)
    {
        lock (_sync) return _breakpoints.Any(item => item.Address == address && item.IsEnabled);
    }

    public Task<HardwareBreakpointRecord> SetHardwareAsync(
        ulong address,
        HardwareBreakpointAccess access,
        HardwareBreakpointSize size,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequirePausedSession();
        if (_hardware == null) throw new InvalidOperationException("Hardware breakpoint support is unavailable.");
        int byteSize = (int)size;
        if (access == HardwareBreakpointAccess.Execute && size != HardwareBreakpointSize.Byte1)
            throw new InvalidOperationException("Execution breakpoints must use a one-byte length.");
        if (address % (ulong)byteSize != 0)
            throw new InvalidOperationException($"The address must be aligned to {byteSize} bytes.");
        int slot;
        lock (_sync) slot = Enumerable.Range(0, 4).FirstOrDefault(
            candidate => _hardwareBreakpoints.All(item => item.Slot != candidate), -1);
        if (slot < 0) throw new InvalidOperationException("All four hardware breakpoint slots are in use.");
        if (!_hardware.SetAll(slot, address, access, size))
        {
            _hardware.ClearAll(slot);
            throw new InvalidOperationException($"DR{slot} could not be applied to every current thread.");
        }
        var record = new HardwareBreakpointRecord(slot, address, access, size, true, 0);
        lock (_sync) _hardwareBreakpoints.Add(record);
        return Task.FromResult(record);
    }

    public Task ClearHardwareAsync(int slot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HardwareBreakpointRecord? record;
        lock (_sync) record = _hardwareBreakpoints.FirstOrDefault(item => item.Slot == slot);
        if (record == null) return Task.CompletedTask;
        if (_hardware == null || !_hardware.ClearAll(slot))
            throw new InvalidOperationException($"DR{slot} could not be cleared from every current thread.");
        lock (_sync) _hardwareBreakpoints.RemoveAll(item => item.Slot == slot);
        return Task.CompletedTask;
    }

    public void HandleEvent(NativeDebugEvent debugEvent)
    {
        if (debugEvent.EventCode == ProcessAccess.EXIT_THREAD_DEBUG_EVENT)
        {
            lock (_sync)
            {
                _pendingReinsertions.Remove(checked((int)debugEvent.ThreadId));
                _pauseAfterReinsertion.Remove(checked((int)debugEvent.ThreadId));
            }
            return;
        }
        if (debugEvent.EventCode != ProcessAccess.CREATE_THREAD_DEBUG_EVENT || _hardware == null) return;
        HardwareBreakpointRecord[] active;
        lock (_sync) active = _hardwareBreakpoints.Where(item => item.IsEnabled).ToArray();
        foreach (HardwareBreakpointRecord breakpoint in active)
        {
            if (!_hardware.ApplyToThread(debugEvent.ThreadId, breakpoint.Slot, breakpoint.Address,
                    breakpoint.Access, breakpoint.Size))
                throw new InvalidOperationException(
                    $"DR{breakpoint.Slot} could not be propagated to new thread {debugEvent.ThreadId}.");
        }
    }

    public async Task<BreakpointRecord?> ToggleAsync(
        ulong address,
        string? moduleName = null,
        ulong? moduleRelativeAddress = null,
        CancellationToken cancellationToken = default)
    {
        BreakpointRecord? existing;
        lock (_sync) existing = _breakpoints.FirstOrDefault(item => item.Address == address && item.Kind == BreakpointKind.Software);
        if (existing is { IsEnabled: true })
        {
            PatchResult restored = await _patches.RestoreAsync(existing.PatchId, cancellationToken).ConfigureAwait(false);
            if (!restored.Succeeded) throw new InvalidOperationException(restored.Message);
            lock (_sync)
            {
                int index = _breakpoints.FindIndex(item => item.Id == existing.Id);
                _breakpoints[index] = existing with { IsEnabled = false };
                return _breakpoints[index];
            }
        }
        if (existing != null)
        {
            PatchResult reapplied = await _patches.ReapplyAsync(existing.PatchId, cancellationToken).ConfigureAwait(false);
            if (!reapplied.Succeeded) throw new InvalidOperationException(reapplied.Message);
            lock (_sync)
            {
                int index = _breakpoints.FindIndex(item => item.Id == existing.Id);
                _breakpoints[index] = existing with { IsEnabled = true };
                return _breakpoints[index];
            }
        }

        return await AddAsync(address, BreakpointKind.Software, false, moduleName, moduleRelativeAddress, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BreakpointRecord> AddTemporaryAsync(ulong address, CancellationToken cancellationToken = default) =>
        AddAsync(address, BreakpointKind.Temporary, true, null, null, cancellationToken);

    private async Task<BreakpointRecord> AddAsync(
        ulong address,
        BreakpointKind kind,
        bool oneShot,
        string? moduleName,
        ulong? moduleRelativeAddress,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_breakpoints.Any(item => item.Address == address && item.IsEnabled))
                throw new InvalidOperationException($"An enabled breakpoint already owns 0x{address:X}.");
        }
        byte[]? original = _memory.Read(address, 1);
        if (original is not { Length: 1 }) throw new InvalidOperationException("The breakpoint byte is unreadable.");
        PatchResult result = await _patches.ApplyAsync(new PatchRequest(
            address, original, new byte[] { 0xCC }, false, $"{kind} breakpoint", moduleName, moduleRelativeAddress),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Record == null) throw new InvalidOperationException(result.Message);
        var record = new BreakpointRecord(
            Guid.NewGuid(), address, kind, oneShot, true, 0, result.Record.Id, moduleName, moduleRelativeAddress);
        lock (_sync) _breakpoints.Add(record);
        return record;
    }

    public DebugEventDecision HandleException(NativeDebugEvent debugEvent)
    {
        if (debugEvent.ExceptionCode == ProcessAccess.EXCEPTION_BREAKPOINT)
            return HandleBreakpoint(debugEvent);
        if (debugEvent.ExceptionCode == ProcessAccess.EXCEPTION_SINGLE_STEP)
            return HandleSingleStep(debugEvent);
        return DebugEventDecision.Unhandled;
    }

    private DebugEventDecision HandleBreakpoint(NativeDebugEvent debugEvent)
    {
        ThreadExecutionContext context = _contexts.Capture(checked((int)debugEvent.ThreadId));
        if (context.InstructionPointer == 0) return DebugEventDecision.Unhandled;
        ulong address = context.InstructionPointer - 1;
        BreakpointRecord? breakpoint;
        lock (_sync) breakpoint = _breakpoints.FirstOrDefault(item => item.Address == address && item.IsEnabled);
        if (breakpoint == null) return DebugEventDecision.Unhandled;

        PatchResult restored = _patches.RestoreAsync(breakpoint.PatchId).GetAwaiter().GetResult();
        if (!restored.Succeeded) throw new InvalidOperationException(restored.Message);
        ulong flags = breakpoint.OneShot ? context.Flags & ~TrapFlag : context.Flags | TrapFlag;
        _contexts.SetControl(context with { InstructionPointer = address, Flags = flags });
        lock (_sync)
        {
            int index = _breakpoints.FindIndex(item => item.Id == breakpoint.Id);
            BreakpointRecord updated = breakpoint with
            {
                HitCount = breakpoint.HitCount + 1,
                IsEnabled = !breakpoint.OneShot
            };
            _breakpoints[index] = updated;
            if (!breakpoint.OneShot) _pendingReinsertions[checked((int)debugEvent.ThreadId)] = breakpoint.Id;
        }
        return DebugEventDecision.Pause(
            breakpoint.OneShot ? $"Temporary breakpoint at 0x{address:X}" : $"Breakpoint at 0x{address:X}");
    }

    private DebugEventDecision HandleSingleStep(NativeDebugEvent debugEvent)
    {
        int threadId = checked((int)debugEvent.ThreadId);
        Guid breakpointId;
        bool pause;
        bool hasPending;
        lock (_sync)
        {
            hasPending = _pendingReinsertions.Remove(threadId, out breakpointId);
            pause = hasPending && _pauseAfterReinsertion.Remove(threadId);
        }
        if (!hasPending)
        {
            ThreadExecutionContext stepped = _contexts.Capture(threadId);
            HardwareBreakpointRecord? hardwareHit = null;
            lock (_sync)
            {
                hardwareHit = _hardwareBreakpoints.FirstOrDefault(item =>
                    item.IsEnabled && (stepped.DebugStatus & (1UL << item.Slot)) != 0);
                if (hardwareHit != null)
                {
                    int index = _hardwareBreakpoints.FindIndex(item => item.Slot == hardwareHit.Slot);
                    _hardwareBreakpoints[index] = hardwareHit with { HitCount = hardwareHit.HitCount + 1 };
                }
            }
            _contexts.SetControl(stepped with
            {
                Flags = stepped.Flags & ~TrapFlag,
                DebugStatus = stepped.DebugStatus & ~0xFUL
            });
            return hardwareHit == null
                ? DebugEventDecision.Pause("Single step")
                : DebugEventDecision.Pause($"Hardware breakpoint DR{hardwareHit.Slot} at 0x{hardwareHit.Address:X}");
        }

        BreakpointRecord breakpoint;
        lock (_sync) breakpoint = _breakpoints.Single(item => item.Id == breakpointId);
        PatchResult reapplied = _patches.ReapplyAsync(breakpoint.PatchId).GetAwaiter().GetResult();
        if (!reapplied.Succeeded) throw new InvalidOperationException(reapplied.Message);
        ThreadExecutionContext context = _contexts.Capture(threadId);
        _contexts.SetControl(context with { Flags = context.Flags & ~TrapFlag });
        return pause ? DebugEventDecision.Pause("Single step") : DebugEventDecision.Continue;
    }

    public async Task StepIntoAsync(CancellationToken cancellationToken = default)
    {
        DebugSessionSnapshot snapshot = RequirePausedSession();
        int threadId = snapshot.SelectedThreadId!.Value;
        bool awaitingReinsertion;
        lock (_sync)
        {
            awaitingReinsertion = _pendingReinsertions.ContainsKey(threadId);
            if (awaitingReinsertion) _pauseAfterReinsertion.Add(threadId);
        }
        if (!awaitingReinsertion)
        {
            ThreadExecutionContext context = _contexts.Capture(threadId);
            _contexts.SetControl(context with { Flags = context.Flags | TrapFlag });
        }
        await _session.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StepOverAsync(
        ulong address,
        int instructionLength,
        bool isCall,
        CancellationToken cancellationToken = default)
    {
        RequirePausedSession();
        if (!isCall)
        {
            await StepIntoAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (instructionLength <= 0 || instructionLength > 15)
            throw new InvalidOperationException("The selected instruction length is not valid for step-over.");
        await AddTemporaryAsync(checked(address + (ulong)instructionLength), cancellationToken).ConfigureAwait(false);
        await _session.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StepOutAsync(CancellationToken cancellationToken = default)
    {
        DebugSessionSnapshot snapshot = RequirePausedSession();
        ThreadExecutionContext context = _contexts.Capture(snapshot.SelectedThreadId!.Value);
        int pointerSize = context.Is64Bit ? 8 : 4;
        byte[]? bytes = _memory.Read(context.StackPointer, pointerSize);
        if (bytes == null || bytes.Length != pointerSize)
            throw new InvalidOperationException("The return address is unreadable.");
        ulong returnAddress = pointerSize == 8 ? BitConverter.ToUInt64(bytes) : BitConverter.ToUInt32(bytes);
        if (returnAddress == 0) throw new InvalidOperationException("The current stack does not contain a return address.");
        await RunToAsync(returnAddress, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunToAsync(ulong address, CancellationToken cancellationToken = default)
    {
        RequirePausedSession();
        await AddTemporaryAsync(address, cancellationToken).ConfigureAwait(false);
        await _session.ResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task RunUntilReturnAsync(CancellationToken cancellationToken = default) =>
        StepOutAsync(cancellationToken);

    public void RestoreOwnedStateBeforeDetach()
    {
        HardwareBreakpointRecord[] hardware;
        lock (_sync) hardware = _hardwareBreakpoints.Where(item => item.IsEnabled).ToArray();
        foreach (HardwareBreakpointRecord breakpoint in hardware)
        {
            if (_hardware == null || !_hardware.ClearAll(breakpoint.Slot))
                throw new InvalidOperationException($"Could not clear hardware breakpoint DR{breakpoint.Slot}.");
            lock (_sync) _hardwareBreakpoints.RemoveAll(item => item.Slot == breakpoint.Slot);
        }

        BreakpointRecord[] enabled;
        lock (_sync) enabled = _breakpoints.Where(item => item.IsEnabled).ToArray();
        foreach (BreakpointRecord breakpoint in enabled)
        {
            PatchResult result = _patches.RestoreAsync(breakpoint.PatchId).GetAwaiter().GetResult();
            if (!result.Succeeded) throw new InvalidOperationException(
                $"Could not restore breakpoint at 0x{breakpoint.Address:X}: {result.Message}");
            lock (_sync)
            {
                int index = _breakpoints.FindIndex(item => item.Id == breakpoint.Id);
                if (index >= 0) _breakpoints[index] = _breakpoints[index] with { IsEnabled = false };
            }
        }
        lock (_sync)
        {
            _pendingReinsertions.Clear();
            _pauseAfterReinsertion.Clear();
        }
    }

    private DebugSessionSnapshot RequirePausedSession()
    {
        DebugSessionSnapshot snapshot = _session.Snapshot;
        if (!snapshot.CanReadThreadContext)
            throw new InvalidOperationException("This command requires a selected thread in a paused debug session.");
        return snapshot;
    }
}
