using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class BreakpointServiceTests
{
    [Fact]
    public async Task PersistentBreakpoint_RestoresCorrectsIpAndReinsertsAfterSingleStep()
    {
        using var fixture = new Fixture();
        BreakpointRecord breakpoint = (await fixture.Service.ToggleAsync(0x1000))!;
        Assert.Equal(0xCC, fixture.Memory[0x1000]);
        fixture.Context.Value = fixture.Context.Value with { InstructionPointer = 0x1001, Flags = 0 };

        DebugEventDecision hit = fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_BREAKPOINT));

        Assert.True(hit.ShouldPause);
        Assert.Equal(0x90, fixture.Memory[0x1000]);
        Assert.Equal(0x1000UL, fixture.Context.Value.InstructionPointer);
        Assert.Equal(0x100UL, fixture.Context.Value.Flags & 0x100);
        Assert.Equal(1, fixture.Service.Breakpoints.Single().HitCount);

        await fixture.Service.StepIntoAsync();
        DebugEventDecision stepped = fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_SINGLE_STEP));

        Assert.True(stepped.ShouldPause);
        Assert.Equal(0xCC, fixture.Memory[0x1000]);
        Assert.Equal(0UL, fixture.Context.Value.Flags & 0x100);
        Assert.True(fixture.Service.Breakpoints.Single(item => item.Id == breakpoint.Id).IsEnabled);
    }

    [Fact]
    public async Task ResumeAfterBreakpoint_AutoContinuesInternalReinsertionStep()
    {
        using var fixture = new Fixture();
        await fixture.Service.ToggleAsync(0x1000);
        fixture.Context.Value = fixture.Context.Value with { InstructionPointer = 0x1001 };
        fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_BREAKPOINT));

        DebugEventDecision decision = fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_SINGLE_STEP));

        Assert.True(decision.IsHandled);
        Assert.False(decision.ShouldPause);
        Assert.Equal(0xCC, fixture.Memory[0x1000]);
    }

    [Fact]
    public async Task OneShotBreakpoint_DisablesWithoutTrapFlagOrReinsertion()
    {
        using var fixture = new Fixture();
        fixture.Memory[0x2000] = 0x55;
        await fixture.Service.AddTemporaryAsync(0x2000);
        fixture.Context.Value = fixture.Context.Value with { InstructionPointer = 0x2001 };

        DebugEventDecision decision = fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_BREAKPOINT));

        Assert.True(decision.ShouldPause);
        Assert.Equal(0x55, fixture.Memory[0x2000]);
        Assert.Equal(0UL, fixture.Context.Value.Flags & 0x100);
        Assert.False(fixture.Service.Breakpoints.Single(item => item.Address == 0x2000).IsEnabled);
    }

    [Fact]
    public async Task StepOverCall_SetsTemporaryBreakpointAtNextInstructionAndResumes()
    {
        using var fixture = new Fixture();
        fixture.Memory[0x1005] = 0x48;

        await fixture.Service.StepOverAsync(0x1000, 5, isCall: true);

        Assert.Equal(0xCC, fixture.Memory[0x1005]);
        Assert.True(fixture.Session.ResumeCalled);
        Assert.True(fixture.Service.Breakpoints.Single(item => item.Address == 0x1005).OneShot);
    }

    [Fact]
    public async Task StepOutReadsPointerSizedReturnAddressAndRunsToIt()
    {
        using var fixture = new Fixture();
        fixture.Context.Value = fixture.Context.Value with { StackPointer = 0x3000 };
        fixture.Memory.SetBytes(0x3000, BitConverter.GetBytes(0x4000UL));
        fixture.Memory[0x4000] = 0xC3;

        await fixture.Service.StepOutAsync();

        Assert.Equal(0xCC, fixture.Memory[0x4000]);
        Assert.True(fixture.Session.ResumeCalled);
    }

    [Fact]
    public async Task DetachRestoresEveryEnabledOwnedBreakpoint()
    {
        using var fixture = new Fixture();
        fixture.Memory[0x2000] = 0x55;
        await fixture.Service.ToggleAsync(0x1000);
        await fixture.Service.ToggleAsync(0x2000);

        fixture.Service.RestoreOwnedStateBeforeDetach();

        Assert.Equal(0x90, fixture.Memory[0x1000]);
        Assert.Equal(0x55, fixture.Memory[0x2000]);
        Assert.All(fixture.Service.Breakpoints, item => Assert.False(item.IsEnabled));
    }

    [Fact]
    public async Task HardwareBreakpoint_PropagatesToNewThreadAndReportsDr6Hit()
    {
        using var fixture = new Fixture();
        HardwareBreakpointRecord breakpoint = await fixture.Service.SetHardwareAsync(
            0x2000, HardwareBreakpointAccess.Write, HardwareBreakpointSize.Byte4);
        fixture.Service.HandleEvent(new NativeDebugEvent(
            ProcessAccess.CREATE_THREAD_DEBUG_EVENT, 42, 99));
        fixture.Context.Value = fixture.Context.Value with { DebugStatus = 1UL << breakpoint.Slot };

        DebugEventDecision hit = fixture.Service.HandleException(Exception(ProcessAccess.EXCEPTION_SINGLE_STEP));

        Assert.Contains("Hardware breakpoint", hit.Reason);
        Assert.Contains(fixture.Hardware.AppliedThreads, item => item.ThreadId == 99 && item.Slot == breakpoint.Slot);
        Assert.Equal(1, fixture.Service.HardwareBreakpoints.Single().HitCount);
        Assert.Equal(0UL, fixture.Context.Value.DebugStatus & 0xF);
    }

    [Fact]
    public async Task HardwareBreakpoint_ValidatesAlignmentAndClearsOnDetach()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetHardwareAsync(
            0x2001, HardwareBreakpointAccess.Write, HardwareBreakpointSize.Byte4));
        HardwareBreakpointRecord record = await fixture.Service.SetHardwareAsync(
            0x2000, HardwareBreakpointAccess.ReadWrite, HardwareBreakpointSize.Byte4);

        fixture.Service.RestoreOwnedStateBeforeDetach();

        Assert.Contains(record.Slot, fixture.Hardware.ClearedSlots);
        Assert.Empty(fixture.Service.HardwareBreakpoints);
    }

    private static NativeDebugEvent Exception(uint code) =>
        new(ProcessAccess.EXCEPTION_DEBUG_EVENT, 42, 7, code, true);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Memory[0x1000] = 0x90;
            Patches = new PatchService(Memory);
            Service = new BreakpointService(Memory, Patches, Context, Session, Hardware);
        }

        public FakePatchMemory Memory { get; } = new();
        public PatchService Patches { get; }
        public FakeContext Context { get; } = new();
        public FakeSession Session { get; } = new();
        public FakeHardware Hardware { get; } = new();
        public BreakpointService Service { get; }
        public void Dispose() => Patches.Dispose();
    }

    private sealed class FakeHardware : IHardwareBreakpointAdapter
    {
        public List<(uint ThreadId, int Slot)> AppliedThreads { get; } = new();
        public List<int> ClearedSlots { get; } = new();
        public bool SetAll(int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size) => true;
        public bool ClearAll(int slot) { ClearedSlots.Add(slot); return true; }
        public bool ApplyToThread(uint threadId, int slot, ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size)
        { AppliedThreads.Add((threadId, slot)); return true; }
    }

    private sealed class FakePatchMemory : IPatchMemoryAdapter
    {
        private readonly Dictionary<ulong, byte> _bytes = new();
        public bool IsAvailable => true;
        public byte this[ulong address] { get => _bytes[address]; set => _bytes[address] = value; }
        public byte[]? Read(ulong address, int size) => Enumerable.Range(0, size)
            .Select(offset => _bytes.TryGetValue(address + (ulong)offset, out byte value) ? value : (byte)0)
            .ToArray();
        public bool ChangeProtection(ulong address, int size, uint protection, out uint previousProtection)
        { previousProtection = 0x20; return true; }
        public bool Write(ulong address, byte[] bytes) { SetBytes(address, bytes); return true; }
        public bool FlushInstructionCache(ulong address, int size) => true;
        public void InvalidateReadCache() { }
        public void SetBytes(ulong address, byte[] bytes)
        {
            for (int index = 0; index < bytes.Length; index++) _bytes[address + (ulong)index] = bytes[index];
        }
    }

    private sealed class FakeContext : IThreadExecutionContextService
    {
        public ThreadExecutionContext Value { get; set; } = new(7, true, 0x1000, 0x3000, 0);
        public ThreadExecutionContext Capture(int threadId) => Value with { ThreadId = threadId };
        public void SetControl(ThreadExecutionContext context) => Value = context;
    }

    private sealed class FakeSession : IDebugSessionService
    {
        public DebugSessionSnapshot Snapshot { get; private set; } =
            new(DebugSessionState.Paused, 42, 7, "Breakpoint");
        public bool ResumeCalled { get; private set; }
        public event EventHandler<DebugSessionSnapshot>? StateChanged;
        public Task AttachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DetachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalled = true;
            Snapshot = Snapshot with { State = DebugSessionState.Running, SelectedThreadId = null };
            StateChanged?.Invoke(this, Snapshot);
            return Task.CompletedTask;
        }
    }
}
