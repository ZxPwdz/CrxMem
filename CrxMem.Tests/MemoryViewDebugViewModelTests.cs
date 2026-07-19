using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryViewDebugViewModelTests
{
    [Fact]
    public async Task PausedEvent_PopulatesVerifiedRegistersAndCommandStates()
    {
        var debug = new FakeDebugSessionService();
        var registers = new FakeRegisterService();
        using var viewModel = new MemoryViewViewModel(
            new EmptyMemoryViewDataService(), debugSession: debug, registerService: registers);

        Assert.True(viewModel.AttachCommand.CanExecute(null));
        debug.Publish(new DebugSessionSnapshot(DebugSessionState.Paused, 42, 7, "Breakpoint"));
        await WaitUntilAsync(() => viewModel.HasRegisterSnapshot);

        Assert.Equal("Paused", viewModel.DebugStateText);
        Assert.Equal("Breakpoint", viewModel.DebugStopReason);
        Assert.Equal("0x00000246", viewModel.FlagsText);
        Assert.Equal(2, viewModel.Registers.Count);
        Assert.Equal("RIP", viewModel.Registers[0].Name);
        Assert.True(viewModel.ResumeCommand.CanExecute(null));
        Assert.False(viewModel.PauseCommand.CanExecute(null));
    }

    [Fact]
    public async Task PausedEvent_PrefersNativeUnwindFramesOverHeuristicProjection()
    {
        var debug = new FakeDebugSessionService();
        using var viewModel = new MemoryViewViewModel(
            new EmptyMemoryViewDataService(),
            debugSession: debug,
            registerService: new FakeRegisterService(),
            stackService: new FakeStackService(),
            callStackService: new FakeCallStackService());

        debug.Publish(new DebugSessionSnapshot(DebugSessionState.Paused, 42, 7, "Breakpoint"));
        await WaitUntilAsync(() => viewModel.CallStackFrames.Count == 1);

        CallStackPanelRow frame = Assert.Single(viewModel.CallStackFrames);
        Assert.Equal("Target!Update+0x12", frame.Label);
        Assert.Equal("Native unwind", frame.Confidence);
        Assert.Equal(0x140001000UL, frame.RuntimeAddress);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class EmptyMemoryViewDataService : IMemoryViewDataService
    {
        public ulong GetDefaultAddress() => 0;
        public Task<AddressExpressionResult> ResolveAddressAsync(string expression, CancellationToken cancellationToken) =>
            Task.FromResult(AddressExpressionResult.Failed("Unavailable"));
        public Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDebugSessionService : IDebugSessionService
    {
        public DebugSessionSnapshot Snapshot { get; private set; } =
            new(DebugSessionState.Detached, 42, null);
        public event EventHandler<DebugSessionSnapshot>? StateChanged;
        public Task AttachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DetachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Publish(DebugSessionSnapshot snapshot)
        {
            Snapshot = snapshot;
            StateChanged?.Invoke(this, snapshot);
        }
    }

    private sealed class FakeRegisterService : IRegisterService
    {
        public Task<RegisterCaptureResult> CaptureAsync(
            DebugSessionSnapshot session,
            RegisterSnapshot? previous,
            CancellationToken cancellationToken)
        {
            RegisterSnapshot snapshot = new(
                session.SelectedThreadId!.Value,
                true,
                new[]
                {
                    new RegisterValue("RIP", 0x140001000),
                    new RegisterValue("RSP", 0x7FFFFFF0)
                },
                0x140001000,
                0x7FFFFFF0,
                0x246,
                DateTimeOffset.UtcNow);
            return Task.FromResult(RegisterCaptureResult.Captured(snapshot));
        }
    }

    private sealed class FakeStackService : IStackService
    {
        public Task<IReadOnlyList<StackPreviewRow>> ReadAsync(
            RegisterSnapshot registers, int entryCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StackPreviewRow>>(new[]
            {
                new StackPreviewRow("000000007FFFFFF0", "0000000140002000", "Target+2000 (heuristic)",
                    0x7FFFFFF0, 0x140002000, true)
            });
    }

    private sealed class FakeCallStackService : ICallStackService
    {
        public Task<UnwindResult> UnwindAsync(
            DebugSessionSnapshot session, RegisterSnapshot registers, int maximumFrames,
            CancellationToken cancellationToken) => Task.FromResult(new UnwindResult(true, new[]
            {
                new CallStackFrame(0, 0x140001000, 0x7FFFFFF0,
                    new SymbolResolution(0x140001000, "Target!Update+0x12", "Target.exe", 0x1000))
            }));
    }
}
