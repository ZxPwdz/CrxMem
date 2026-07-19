using System.Collections.Concurrent;
using System.ComponentModel;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class NativeDebugSessionServiceTests
{
    [Fact]
    public async Task OptIn_WindowsAttachPauseResumeDetach_AgainstIsolatedTarget()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_DEBUG_TESTS"), "1", StringComparison.Ordinal))
            return;

        string targetPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"));
        Assert.True(File.Exists(targetPath), $"Debug target was not built: {targetPath}");
        using var target = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(targetPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        Assert.True(target.Start());
        try
        {
            string? ready = await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("READY", ready);
            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            using var service = new NativeDebugSessionService(process);

            await service.AttachAsync(CancellationToken.None);
            if (service.Snapshot.State == DebugSessionState.Running)
                await service.PauseAsync(CancellationToken.None);
            await WaitForStateAsync(service, DebugSessionState.Paused);
            await ResumeThroughStartupBreakpointsAsync(service);
            await service.PauseAsync(CancellationToken.None);
            await WaitForStateAsync(service, DebugSessionState.Paused);

            var registerService = new WindowsRegisterService(process);
            RegisterCaptureResult registers = await registerService.CaptureAsync(
                service.Snapshot, null, CancellationToken.None);
            Assert.True(registers.IsAvailable, registers.Error);
            using (var dbgHelp = new WindowsDbgHelpService(process, new ModuleService(process)))
            {
                UnwindResult unwind = await dbgHelp.UnwindAsync(
                    service.Snapshot, registers.Snapshot!, 64, CancellationToken.None);
                Assert.True(unwind.IsAvailable, unwind.Error);
                Assert.NotEmpty(unwind.Frames);
                Assert.All(unwind.Frames, frame => Assert.NotEqual(0UL, frame.InstructionAddress));
            }
            await service.DetachAsync(CancellationToken.None);

            Assert.True(
                service.Snapshot.State == DebugSessionState.Detached,
                $"Detach ended in {service.Snapshot.State}: {service.Snapshot.Error}");
            Assert.False(target.HasExited);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task AttachPauseResumeDetach_FollowsEventOwnershipAndContinuesPendingEvent()
    {
        using var process = new ProcessAccess();
        Assert.True(process.Open(Environment.ProcessId));
        var native = new FakeDebuggerNativeApi((uint)Environment.ProcessId);
        using var service = new NativeDebugSessionService(process, native);

        await service.AttachAsync(CancellationToken.None);
        await WaitForStateAsync(service, DebugSessionState.Paused);
        Assert.Equal(7, service.Snapshot.SelectedThreadId);
        Assert.Equal("Breakpoint", service.Snapshot.StopReason);

        await service.ResumeAsync(CancellationToken.None);
        await WaitForStateAsync(service, DebugSessionState.Running);
        Assert.Contains(native.ContinuedEvents, item => item.ThreadId == 7 && item.Status == ProcessAccess.DBG_CONTINUE);

        await service.PauseAsync(CancellationToken.None);
        await WaitForStateAsync(service, DebugSessionState.Paused);
        await service.DetachAsync(CancellationToken.None);

        Assert.Equal(DebugSessionState.Detached, service.Snapshot.State);
        Assert.True(native.DetachCalled);
        Assert.True(native.KillOnExitDisabled);
        Assert.True(native.BreakCalled);
    }

    [Fact]
    public async Task AttachFailure_TransitionsToFaultedAndReturnsNativeError()
    {
        using var process = new ProcessAccess();
        Assert.True(process.Open(Environment.ProcessId));
        var native = new FakeDebuggerNativeApi((uint)Environment.ProcessId) { AttachError = 5 };
        using var service = new NativeDebugSessionService(process, native);

        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(
            () => service.AttachAsync(CancellationToken.None));

        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(DebugSessionState.Faulted, service.Snapshot.State);
        Assert.Contains("DebugActiveProcess", service.Snapshot.Error!);
    }

    [Fact]
    public async Task HandledInternalSingleStep_IsContinuedWithoutPublishingPausedState()
    {
        using var process = new ProcessAccess();
        Assert.True(process.Open(Environment.ProcessId));
        var native = new FakeDebuggerNativeApi((uint)Environment.ProcessId);
        var coordinator = new AutoContinueCoordinator();
        using var service = new NativeDebugSessionService(process, native, coordinator);

        await service.AttachAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (coordinator.HandledEvents == 0) await Task.Delay(10, timeout.Token);

        Assert.Equal(DebugSessionState.Running, service.Snapshot.State);
        await service.DetachAsync(CancellationToken.None);
        Assert.True(coordinator.RestoreCalls > 0);
    }

    private static async Task WaitForStateAsync(
        NativeDebugSessionService service,
        DebugSessionState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (service.Snapshot.State != state)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (service.Snapshot.State == DebugSessionState.Faulted)
                throw new InvalidOperationException(service.Snapshot.Error);
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task ResumeThroughStartupBreakpointsAsync(NativeDebugSessionService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            DebugSessionSnapshot snapshot = service.Snapshot;
            if (snapshot.State == DebugSessionState.Faulted)
                throw new InvalidOperationException(snapshot.Error);
            if (snapshot.State == DebugSessionState.Paused)
                await service.ResumeAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            if (service.Snapshot.State == DebugSessionState.Running)
            {
                await Task.Delay(150, timeout.Token);
                if (service.Snapshot.State == DebugSessionState.Running) return;
            }
        }
    }

    private sealed class FakeDebuggerNativeApi(uint processId) : IDebuggerNativeApi
    {
        private readonly ConcurrentQueue<NativeDebugEvent> _events = new();
        public int AttachError { get; init; }
        public bool DetachCalled { get; private set; }
        public bool KillOnExitDisabled { get; private set; }
        public bool BreakCalled { get; private set; }
        public ConcurrentBag<(uint ThreadId, uint Status)> ContinuedEvents { get; } = new();

        public bool Attach(uint requestedProcessId, out int error)
        {
            error = AttachError;
            if (error != 0) return false;
            Assert.Equal(processId, requestedProcessId);
            _events.Enqueue(new NativeDebugEvent(ProcessAccess.CREATE_PROCESS_DEBUG_EVENT, processId, 7));
            _events.Enqueue(new NativeDebugEvent(
                ProcessAccess.EXCEPTION_DEBUG_EVENT, processId, 7,
                ProcessAccess.EXCEPTION_BREAKPOINT, true));
            return true;
        }

        public bool Detach(uint requestedProcessId, out int error)
        {
            error = 0;
            DetachCalled = true;
            return requestedProcessId == processId;
        }

        public bool SetKillOnExit(bool killOnExit, out int error)
        {
            error = 0;
            KillOnExitDisabled = !killOnExit;
            return true;
        }

        public bool WaitForEvent(uint timeoutMilliseconds, out NativeDebugEvent? debugEvent, out int error)
        {
            error = 0;
            if (_events.TryDequeue(out NativeDebugEvent? item))
            {
                debugEvent = item;
                return true;
            }
            Thread.Sleep(Math.Min((int)timeoutMilliseconds, 5));
            debugEvent = null;
            return true;
        }

        public bool ContinueEvent(uint requestedProcessId, uint threadId, uint status, out int error)
        {
            error = 0;
            ContinuedEvents.Add((threadId, status));
            return requestedProcessId == processId;
        }

        public bool BreakProcess(IntPtr processHandle, out int error)
        {
            error = 0;
            BreakCalled = true;
            _events.Enqueue(new NativeDebugEvent(
                ProcessAccess.EXCEPTION_DEBUG_EVENT, processId, 9,
                ProcessAccess.EXCEPTION_BREAKPOINT, true));
            return true;
        }

        public void CloseEventHandles(NativeDebugEvent debugEvent) { }
    }

    private sealed class AutoContinueCoordinator : IDebugEventCoordinator
    {
        public int HandledEvents { get; private set; }
        public int RestoreCalls { get; private set; }
        public DebugEventDecision HandleException(NativeDebugEvent debugEvent)
        {
            HandledEvents++;
            return DebugEventDecision.Continue;
        }
        public void RestoreOwnedStateBeforeDetach() => RestoreCalls++;
    }
}
