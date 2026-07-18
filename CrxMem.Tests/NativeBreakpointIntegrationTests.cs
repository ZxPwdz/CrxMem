using System.Globalization;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class NativeBreakpointIntegrationTests
{
    [Fact]
    public async Task OptIn_WindowsSoftwareBreakpointHitStepReinsertAndDetach()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_BREAKPOINT_TESTS"), "1", StringComparison.Ordinal))
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
            string ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            ulong probe = ParseAddress(ready, "PROBE=");
            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            var adapter = new ProcessPatchMemoryAdapter(process);
            using var patches = new PatchService(adapter);
            using var session = new NativeDebugSessionService(process);
            var contexts = new WindowsThreadExecutionContextService(process);
            var breakpoints = new BreakpointService(adapter, patches, contexts, session);
            session.EventCoordinator = breakpoints;

            await session.AttachAsync(CancellationToken.None);
            await WaitForPausedAsync(session, TimeSpan.FromSeconds(5));
            await breakpoints.ToggleAsync(probe);
            await ResumeUntilOwnedBreakpointAsync(session, probe, TimeSpan.FromSeconds(8));

            ThreadExecutionContext hitContext = contexts.Capture(session.Snapshot.SelectedThreadId!.Value);
            Assert.Equal(probe, hitContext.InstructionPointer);
            Assert.Equal(1, breakpoints.Breakpoints.Single().HitCount);

            await breakpoints.StepIntoAsync();
            await WaitForPausedReasonAsync(session, "Single step", TimeSpan.FromSeconds(5));
            Assert.Equal(0xCC, adapter.Read(probe, 1)![0]);

            await breakpoints.ToggleAsync(probe);
            await session.DetachAsync(CancellationToken.None);
            Assert.Equal(DebugSessionState.Detached, session.Snapshot.State);
            Assert.False(target.HasExited);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task OptIn_WindowsHardwareBreakpointPropagatesAndClears()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_BREAKPOINT_TESTS"), "1", StringComparison.Ordinal))
            return;

        string targetPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"));
        using var target = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(targetPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            }
        };
        Assert.True(target.Start());
        try
        {
            string ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            ulong probe = ParseAddress(ready, "PROBE=");
            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            var adapter = new ProcessPatchMemoryAdapter(process);
            using var patches = new PatchService(adapter);
            using var session = new NativeDebugSessionService(process);
            var breakpoints = new BreakpointService(
                adapter, patches, new WindowsThreadExecutionContextService(process), session,
                new WindowsHardwareBreakpointAdapter(process));
            session.EventCoordinator = breakpoints;

            await session.AttachAsync(CancellationToken.None);
            await WaitForPausedAsync(session, TimeSpan.FromSeconds(5));
            HardwareBreakpointRecord hardware = await breakpoints.SetHardwareAsync(
                probe, HardwareBreakpointAccess.Execute, HardwareBreakpointSize.Byte1);
            await ResumeUntilReasonPrefixAsync(session, "Hardware breakpoint", TimeSpan.FromSeconds(8));

            Assert.Equal(1, breakpoints.HardwareBreakpoints.Single().HitCount);
            await breakpoints.ClearHardwareAsync(hardware.Slot);
            await session.DetachAsync(CancellationToken.None);
            Assert.Empty(breakpoints.HardwareBreakpoints);
            Assert.False(target.HasExited);
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    private static ulong ParseAddress(string line, string name)
    {
        string text = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith(name, StringComparison.Ordinal))[(name.Length + 2)..];
        return ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static async Task ResumeUntilOwnedBreakpointAsync(
        NativeDebugSessionService session,
        ulong address,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.Snapshot.State == DebugSessionState.Faulted)
                throw new InvalidOperationException(session.Snapshot.Error);
            if (session.Snapshot.State == DebugSessionState.Paused)
            {
                if (session.Snapshot.StopReason == $"Breakpoint at 0x{address:X}") return;
                await session.ResumeAsync(cancellation.Token);
            }
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static async Task ResumeUntilReasonPrefixAsync(
        NativeDebugSessionService session,
        string prefix,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            DebugSessionSnapshot snapshot = session.Snapshot;
            if (snapshot.State == DebugSessionState.Faulted)
                throw new InvalidOperationException(snapshot.Error);
            if (snapshot.State == DebugSessionState.Paused)
            {
                if (snapshot.StopReason?.StartsWith(prefix, StringComparison.Ordinal) == true) return;
                await session.ResumeAsync(cancellation.Token);
            }
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static Task WaitForPausedAsync(NativeDebugSessionService session, TimeSpan timeout) =>
        WaitForPausedReasonAsync(session, null, timeout);

    private static async Task WaitForPausedReasonAsync(
        NativeDebugSessionService session,
        string? reason,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (session.Snapshot.State != DebugSessionState.Paused ||
               (reason != null && session.Snapshot.StopReason != reason))
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (session.Snapshot.State == DebugSessionState.Faulted)
                throw new InvalidOperationException(session.Snapshot.Error);
            await Task.Delay(10, cancellation.Token);
        }
    }
}
