using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class DebugStateServiceTests
{
    [Theory]
    [InlineData(DebugSessionState.Detached)]
    [InlineData(DebugSessionState.Attaching)]
    [InlineData(DebugSessionState.Running)]
    [InlineData(DebugSessionState.Detaching)]
    [InlineData(DebugSessionState.Exited)]
    [InlineData(DebugSessionState.Faulted)]
    public async Task CaptureAsync_RejectsEveryNonPausedState(DebugSessionState state)
    {
        using var process = new ProcessAccess();
        var service = new WindowsRegisterService(process);
        var session = new DebugSessionSnapshot(state, 42, 7);

        RegisterCaptureResult result = await service.CaptureAsync(session, null, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Snapshot);
        Assert.Contains("paused", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsync_PausedButUnavailableProcessReturnsUnavailableWithoutDemoData()
    {
        using var process = new ProcessAccess();
        var service = new WindowsRegisterService(process);

        RegisterCaptureResult result = await service.CaptureAsync(
            new DebugSessionSnapshot(DebugSessionState.Paused, 42, 7), null, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Null(result.Snapshot);
        Assert.Contains("unavailable", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RegisterValue_ChangedAndSignedDeltaAreDeterministic()
    {
        var increased = new RegisterValue("RAX", 0x1010, 0x1000);
        var decreased = new RegisterValue("RBX", 0x0FF0, 0x1000);
        var first = new RegisterValue("RCX", 1);

        Assert.True(increased.HasChanged);
        Assert.Equal(16, increased.SignedDelta);
        Assert.Equal(-16, decreased.SignedDelta);
        Assert.False(first.HasChanged);
        Assert.Null(first.SignedDelta);
    }

    [Fact]
    public void DebugSnapshot_ContextInvariantRequiresPausedSelectedThread()
    {
        Assert.False(new DebugSessionSnapshot(DebugSessionState.Paused, 42, null).CanReadThreadContext);
        Assert.False(new DebugSessionSnapshot(DebugSessionState.Running, 42, 7).CanReadThreadContext);
        Assert.True(new DebugSessionSnapshot(DebugSessionState.Paused, 42, 7).CanReadThreadContext);
    }
}
