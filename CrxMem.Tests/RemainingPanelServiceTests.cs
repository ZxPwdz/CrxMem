using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using Xunit;

namespace CrxMem.Tests;

public sealed class RemainingPanelServiceTests
{
    [Fact]
    public async Task InitializeLoadsModuleMemoryMapAndThreadPanelsOnce()
    {
        var map = new FakeMemoryMap();
        using var viewModel = new MemoryViewViewModel(
            new SnapshotService(),
            moduleService: new FakeModules(),
            threadService: new FakeThreads(),
            memoryMapService: map);

        await viewModel.InitializeAsync(0x140001000);
        await viewModel.NavigateToAddressAsync(0x140001010);

        Assert.Single(viewModel.Modules);
        Assert.Single(viewModel.MemoryMap);
        Assert.Single(viewModel.Threads);
        Assert.Equal(1, map.Calls);
    }

    [Fact]
    public void CallStackProjectionClearlyLabelsHeuristicEntries()
    {
        StackPreviewRow[] stack =
        {
            new("1000", "140001000", "Target.exe+1000", 0x1000, 0x140001000, true),
            new("1008", "0", string.Empty, 0x1008, 0, false)
        };

        CallStackPanelRow frame = Assert.Single(stack.ToHeuristicCallStack());

        Assert.Contains("Heuristic", frame.Confidence);
        Assert.Equal(0x140001000UL, frame.RuntimeAddress);
    }

    [Fact]
    public void PatchProjectionShowsAppliedRestoredAndStaleState()
    {
        var patch = new PatchRecord(
            Guid.NewGuid(), 0x1000, new byte[] { 0x90 }, new byte[] { 0xCC },
            "break", "Target.exe", 0x1000, DateTimeOffset.UtcNow, true, false);

        Assert.Equal("Applied", patch.ToPanelRow().State);
        Assert.Equal("Restored", (patch with { IsApplied = false }).ToPanelRow().State);
        Assert.Equal("Stale", (patch with { IsStale = true }).ToPanelRow().State);
    }

    private sealed class SnapshotService : IMemoryViewDataService
    {
        public ulong GetDefaultAddress() => 0x140001000;
        public Task<AddressExpressionResult> ResolveAddressAsync(string expression, CancellationToken cancellationToken) =>
            Task.FromResult(AddressExpressionResult.Resolved(Convert.ToUInt64(expression, 16)));
        public Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryViewSnapshot(
                address, "Target", 42, "Target.exe", 0x140000000, "x64", "1000", "RX", "region",
                new[] { new DisassemblyPreviewRow("", address.ToString("X16"), "90", "nop", "", address) },
                Array.Empty<HexPreviewRow>(), "Ready"));
    }

    private sealed class FakeModules : IModuleService
    {
        private static readonly ModuleDescriptor Module = new("Target.exe", 0x140000000, 0x10000);
        public IReadOnlyList<ModuleDescriptor> GetModules() => new[] { Module };
        public ModuleDescriptor? FindByAddress(ulong address) => Module.Contains(address) ? Module : null;
        public ModuleDescriptor? FindByName(string moduleName) => Module;
        public ulong GetDefaultAddress() => Module.BaseAddress;
    }

    private sealed class FakeThreads : IThreadService
    {
        public IReadOnlyList<ThreadDescriptor> GetThreads() =>
            new[] { new ThreadDescriptor(7, 0x140001000, 0, "Waiting") };
    }

    private sealed class FakeMemoryMap : IMemoryMapService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<MemoryMapRow>> GetRegionsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<MemoryMapRow>>(new[]
            {
                new MemoryMapRow("140000000", "140010000", "0x10000", "R/X", "Commit", "Image", 0x140000000)
            });
        }
    }
}
