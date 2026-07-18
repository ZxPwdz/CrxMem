using System.ComponentModel;
using System.Runtime.InteropServices;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record NativeDebugEvent(
    uint EventCode,
    uint ProcessId,
    uint ThreadId,
    uint ExceptionCode = 0,
    bool FirstChance = false,
    IReadOnlyList<IntPtr>? HandlesToClose = null);

public interface IDebuggerNativeApi
{
    bool Attach(uint processId, out int error);
    bool Detach(uint processId, out int error);
    bool SetKillOnExit(bool killOnExit, out int error);
    bool WaitForEvent(uint timeoutMilliseconds, out NativeDebugEvent? debugEvent, out int error);
    bool ContinueEvent(uint processId, uint threadId, uint status, out int error);
    bool BreakProcess(IntPtr processHandle, out int error);
    void CloseEventHandles(NativeDebugEvent debugEvent);
}

public sealed class WindowsDebuggerNativeApi : IDebuggerNativeApi
{
    private const int ErrorSemTimeout = 121;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDebugEvent
    {
        public uint EventCode;
        public uint ProcessId;
        public uint ThreadId;
        // DEBUG_EVENT's union is pointer-aligned in a 64-bit debugger process.
        public uint AlignmentPadding;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
        public byte[] Data;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WaitForDebugEvent(out RawDebugEvent debugEvent, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DebugBreakProcess(IntPtr processHandle);

    public bool Attach(uint processId, out int error) => Invoke(
        () => ProcessAccess.DebugActiveProcess(processId), out error);

    public bool Detach(uint processId, out int error) => Invoke(
        () => ProcessAccess.DebugActiveProcessStop(processId), out error);

    public bool SetKillOnExit(bool killOnExit, out int error) => Invoke(
        () => ProcessAccess.DebugSetProcessKillOnExit(killOnExit), out error);

    public bool ContinueEvent(uint processId, uint threadId, uint status, out int error) => Invoke(
        () => ProcessAccess.ContinueDebugEvent(processId, threadId, status), out error);

    public bool BreakProcess(IntPtr processHandle, out int error) => Invoke(
        () => DebugBreakProcess(processHandle), out error);

    public bool WaitForEvent(
        uint timeoutMilliseconds,
        out NativeDebugEvent? debugEvent,
        out int error)
    {
        if (!WaitForDebugEvent(out RawDebugEvent raw, timeoutMilliseconds))
        {
            error = Marshal.GetLastWin32Error();
            debugEvent = null;
            return error == ErrorSemTimeout;
        }

        error = 0;
        uint exceptionCode = 0;
        bool firstChance = false;
        var handles = new List<IntPtr>();
        byte[] data = raw.Data ?? Array.Empty<byte>();
        if (raw.EventCode == ProcessAccess.EXCEPTION_DEBUG_EVENT && data.Length >= 156)
        {
            exceptionCode = BitConverter.ToUInt32(data, 0);
            firstChance = BitConverter.ToUInt32(data, 152) != 0;
        }
        else if (raw.EventCode == ProcessAccess.CREATE_PROCESS_DEBUG_EVENT && data.Length >= 24)
        {
            AddHandle(handles, ReadPointer(data, 0));
            AddHandle(handles, ReadPointer(data, 8));
            AddHandle(handles, ReadPointer(data, 16));
        }
        else if (raw.EventCode is ProcessAccess.CREATE_THREAD_DEBUG_EVENT or ProcessAccess.LOAD_DLL_DEBUG_EVENT && data.Length >= 8)
        {
            AddHandle(handles, ReadPointer(data, 0));
        }

        debugEvent = new NativeDebugEvent(
            raw.EventCode, raw.ProcessId, raw.ThreadId, exceptionCode, firstChance, handles);
        return true;
    }

    public void CloseEventHandles(NativeDebugEvent debugEvent)
    {
        if (debugEvent.HandlesToClose == null) return;
        foreach (IntPtr handle in debugEvent.HandlesToClose)
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) ProcessAccess.CloseHandle(handle);
    }

    private static bool Invoke(Func<bool> action, out int error)
    {
        bool success = action();
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }

    private static IntPtr ReadPointer(byte[] data, int offset) =>
        IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(data, offset))
            : new IntPtr(BitConverter.ToInt32(data, offset));

    private static void AddHandle(List<IntPtr> handles, IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) handles.Add(handle);
    }
}

public sealed class NativeDebugSessionService : IDebugSessionService, IDisposable
{
    private const int PollMilliseconds = 50;
    private readonly ProcessAccess _process;
    private readonly IDebuggerNativeApi _native;
    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _continueSignal = new(false);
    private DebugSessionSnapshot _snapshot;
    private Task? _eventLoop;
    private TaskCompletionSource<bool>? _attachCompletion;
    private volatile bool _detachRequested;
    private bool _disposed;

    public NativeDebugSessionService(ProcessAccess process, IDebuggerNativeApi? native = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _native = native ?? new WindowsDebuggerNativeApi();
        int processId = process.Target?.Id ?? 0;
        _snapshot = new DebugSessionSnapshot(DebugSessionState.Detached, processId, null);
    }

    public DebugSessionSnapshot Snapshot
    {
        get { lock (_stateLock) return _snapshot; }
    }

    public event EventHandler<DebugSessionSnapshot>? StateChanged;

    public async Task AttachAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_process.IsOpen || _process.Target == null || _process.Target.HasExited)
            throw new InvalidOperationException("Select a live process before attaching the debugger.");
        lock (_stateLock)
        {
            if (_snapshot.State != DebugSessionState.Detached)
                throw new InvalidOperationException($"Cannot attach while the debug session is {_snapshot.State}.");
            _detachRequested = false;
            _continueSignal.Reset();
            _attachCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetSnapshotLocked(new DebugSessionSnapshot(
                DebugSessionState.Attaching, _process.Target.Id, null));
            _eventLoop = Task.Factory.StartNew(
                DebugLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        try
        {
            await _attachCompletion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _detachRequested = true;
            _continueSignal.Set();
            if (_eventLoop != null) await _eventLoop;
            throw;
        }
    }

    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Task? eventLoop;
        lock (_stateLock)
        {
            if (_snapshot.State == DebugSessionState.Detached) return;
            if (_snapshot.State == DebugSessionState.Faulted && _eventLoop?.IsCompleted != false)
            {
                SetSnapshotLocked(_snapshot with
                {
                    State = DebugSessionState.Detached,
                    SelectedThreadId = null,
                    Error = null
                });
                return;
            }
            if (_snapshot.State == DebugSessionState.Exited)
            {
                SetSnapshotLocked(_snapshot with { State = DebugSessionState.Detached, SelectedThreadId = null });
                return;
            }
            _detachRequested = true;
            SetSnapshotLocked(_snapshot with { State = DebugSessionState.Detaching });
            eventLoop = _eventLoop;
        }
        _continueSignal.Set();
        if (eventLoop != null) await eventLoop.WaitAsync(cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Snapshot.State != DebugSessionState.Running)
            throw new InvalidOperationException("Pause is available only while the debug session is running.");
        if (!_native.BreakProcess(_process.Handle, out int error))
            throw new Win32Exception(error, "Unable to request a debugger break.");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (Snapshot.State != DebugSessionState.Paused)
            throw new InvalidOperationException("Resume is available only while the debug session is paused.");
        _continueSignal.Set();
        return Task.CompletedTask;
    }

    private void DebugLoop()
    {
        NativeDebugEvent? pendingEvent = null;
        bool attached = false;
        try
        {
            uint processId = unchecked((uint)_process.Target.Id);
            if (!_native.Attach(processId, out int attachError))
                throw new Win32Exception(attachError, "DebugActiveProcess failed.");
            attached = true;
            if (!_native.SetKillOnExit(false, out int killError))
                throw new Win32Exception(killError, "Unable to disable debugger kill-on-exit behavior.");

            Publish(new DebugSessionSnapshot(DebugSessionState.Running, (int)processId, null));
            _attachCompletion?.TrySetResult(true);

            while (attached)
            {
                if (pendingEvent != null)
                {
                    if (!_detachRequested && !_continueSignal.Wait(PollMilliseconds)) continue;
                    _continueSignal.Reset();
                    uint continueStatus = ContinueStatus(pendingEvent);
                    if (!_native.ContinueEvent(pendingEvent.ProcessId, pendingEvent.ThreadId, continueStatus, out int continueError))
                        throw new Win32Exception(continueError, "ContinueDebugEvent failed.");
                    _native.CloseEventHandles(pendingEvent);
                    pendingEvent = null;
                    if (!_detachRequested)
                        Publish(new DebugSessionSnapshot(DebugSessionState.Running, (int)processId, null));
                }

                if (_detachRequested)
                {
                    DetachAfterDrainingEvents(processId);
                    attached = false;
                    Publish(new DebugSessionSnapshot(DebugSessionState.Detached, (int)processId, null));
                    break;
                }

                if (!_native.WaitForEvent((uint)PollMilliseconds, out NativeDebugEvent? debugEvent, out int waitError))
                    throw new Win32Exception(waitError, "WaitForDebugEvent failed.");
                if (debugEvent == null) continue;

                if (debugEvent.EventCode == ProcessAccess.EXIT_PROCESS_DEBUG_EVENT)
                {
                    _native.ContinueEvent(debugEvent.ProcessId, debugEvent.ThreadId, ProcessAccess.DBG_CONTINUE, out _);
                    _native.CloseEventHandles(debugEvent);
                    attached = false;
                    Publish(new DebugSessionSnapshot(DebugSessionState.Exited, (int)processId, null, "Process exited"));
                    break;
                }

                if (debugEvent.EventCode == ProcessAccess.EXCEPTION_DEBUG_EVENT &&
                    debugEvent.ExceptionCode is ProcessAccess.EXCEPTION_BREAKPOINT or ProcessAccess.EXCEPTION_SINGLE_STEP)
                {
                    pendingEvent = debugEvent;
                    string reason = debugEvent.ExceptionCode == ProcessAccess.EXCEPTION_SINGLE_STEP
                        ? "Single step"
                        : "Breakpoint";
                    Publish(new DebugSessionSnapshot(
                        DebugSessionState.Paused, (int)processId, (int)debugEvent.ThreadId, reason));
                    continue;
                }

                uint status = ContinueStatus(debugEvent);
                if (!_native.ContinueEvent(debugEvent.ProcessId, debugEvent.ThreadId, status, out int eventError))
                    throw new Win32Exception(eventError, "ContinueDebugEvent failed.");
                _native.CloseEventHandles(debugEvent);
            }
        }
        catch (Exception ex)
        {
            _attachCompletion?.TrySetException(ex);
            Publish(Snapshot with
            {
                State = DebugSessionState.Faulted,
                SelectedThreadId = null,
                Error = ex.Message
            });
        }
        finally
        {
            if (pendingEvent != null)
            {
                _native.ContinueEvent(pendingEvent.ProcessId, pendingEvent.ThreadId, ContinueStatus(pendingEvent), out _);
                _native.CloseEventHandles(pendingEvent);
            }
            if (attached) _native.Detach(unchecked((uint)_process.Target.Id), out _);
        }
    }

    private static uint ContinueStatus(NativeDebugEvent debugEvent) =>
        debugEvent.EventCode != ProcessAccess.EXCEPTION_DEBUG_EVENT ||
        debugEvent.ExceptionCode is ProcessAccess.EXCEPTION_BREAKPOINT or ProcessAccess.EXCEPTION_SINGLE_STEP
            ? ProcessAccess.DBG_CONTINUE
            : ProcessAccess.DBG_EXCEPTION_NOT_HANDLED;

    private void DetachAfterDrainingEvents(uint processId)
    {
        int lastError = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            DrainQueuedEvents();
            if (_native.Detach(processId, out lastError)) return;
            if (lastError != 5) break;
            Thread.Sleep(20);
        }

        throw new Win32Exception(
            lastError,
            $"DebugActiveProcessStop failed ({lastError}: {new Win32Exception(lastError).Message}).");
    }

    private void DrainQueuedEvents()
    {
        int quietPolls = 0;
        for (int eventCount = 0; eventCount < 256 && quietPolls < 3; eventCount++)
        {
            if (!_native.WaitForEvent(10, out NativeDebugEvent? debugEvent, out int waitError))
                throw new Win32Exception(waitError, "WaitForDebugEvent failed while detaching.");
            if (debugEvent == null)
            {
                quietPolls++;
                continue;
            }

            quietPolls = 0;
            _native.ContinueEvent(
                debugEvent.ProcessId,
                debugEvent.ThreadId,
                ContinueStatus(debugEvent),
                out _);
            _native.CloseEventHandles(debugEvent);
        }
    }

    private void Publish(DebugSessionSnapshot snapshot)
    {
        lock (_stateLock) SetSnapshotLocked(snapshot);
    }

    private void SetSnapshotLocked(DebugSessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (Snapshot.State is not DebugSessionState.Detached and not DebugSessionState.Exited)
                DetachAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).GetAwaiter().GetResult();
        }
        catch
        {
            _detachRequested = true;
            _continueSignal.Set();
        }
        _continueSignal.Dispose();
        _disposed = true;
    }
}
