using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryToolServiceTests
{
    [Fact]
    public async Task StackService_UsesSelectedSnapshotAndLabelsModulePointersAsHeuristic()
    {
        byte[] stack = BitConverter.GetBytes(0x140001000UL)
            .Concat(BitConverter.GetBytes(0x1234UL))
            .ToArray();
        var memory = new ToolMemoryService(stack);
        var service = new StackService(memory, new ToolModuleService());
        var registers = new RegisterSnapshot(
            7, true, Array.Empty<RegisterValue>(), 0x140002000, 0x70000000, 0x246, DateTimeOffset.UtcNow);

        IReadOnlyList<StackPreviewRow> rows = await service.ReadAsync(registers, 2, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Target.exe+1000 (heuristic)", rows[0].Label);
        Assert.True(rows[0].IsHeuristic);
        Assert.False(rows[1].IsHeuristic);
        Assert.Equal(0x70000008UL, rows[1].RuntimeAddress);
    }

    [Fact]
    public async Task WatchService_TracksPreviousValueAndErrorsTruthfully()
    {
        var memory = new ToolMemoryService(BitConverter.GetBytes(0x12345678UL));
        var service = new WatchService(new FixedAddressService(0x70000000), memory);

        WatchPreviewRow first = await service.EvaluateAsync("Target.exe+10", null, CancellationToken.None);
        memory.Data = BitConverter.GetBytes(0x12345679UL);
        WatchPreviewRow second = await service.EvaluateAsync("Target.exe+10", first, CancellationToken.None);

        Assert.Equal("0x12345678", first.Value);
        Assert.True(second.HasChanged);
        Assert.Equal(0x12345678UL, second.PreviousValue);

        WatchPreviewRow invalid = await new WatchService(
            new FailingAddressService(), memory).EvaluateAsync("missing", null, CancellationToken.None);
        Assert.Null(invalid.RuntimeAddress);
        Assert.Contains("Unknown", invalid.Status);
    }

    private sealed class ToolMemoryService(byte[] data) : IProcessMemoryService
    {
        public byte[] Data { get; set; } = data;
        public ProcessIdentity Identity { get; } = new("Target", 42, true, true);
        public Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken)
        {
            byte[] result = Data.Take(size).ToArray();
            return Task.FromResult(new MemoryReadResult(
                address, size, result,
                result.Length == size ? MemoryReadStatus.Complete : MemoryReadStatus.Partial));
        }
        public MemoryRegionDescriptor? QueryRegion(ulong address) => null;
    }

    private sealed class ToolModuleService : IModuleService
    {
        private static readonly ModuleDescriptor Module = new("Target.exe", 0x140000000, 0x100000);
        public IReadOnlyList<ModuleDescriptor> GetModules() => new[] { Module };
        public ModuleDescriptor? FindByAddress(ulong address) => Module.Contains(address) ? Module : null;
        public ModuleDescriptor? FindByName(string moduleName) => Module;
        public ulong GetDefaultAddress() => Module.BaseAddress;
    }

    private sealed class FixedAddressService(ulong address) : IAddressExpressionService
    {
        public Task<AddressExpressionResult> EvaluateAsync(string expression, CancellationToken cancellationToken) =>
            Task.FromResult(AddressExpressionResult.Resolved(address));
    }

    private sealed class FailingAddressService : IAddressExpressionService
    {
        public Task<AddressExpressionResult> EvaluateAsync(string expression, CancellationToken cancellationToken) =>
            Task.FromResult(AddressExpressionResult.Failed("Unknown symbol."));
    }
}
