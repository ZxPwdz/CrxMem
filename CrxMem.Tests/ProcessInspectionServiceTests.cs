using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class ProcessInspectionServiceTests
{
    [Fact]
    public async Task ProcessMemoryService_UnopenedProcess_ReturnsUnavailableResult()
    {
        using var process = new ProcessAccess();
        var service = new ProcessMemoryService(process);

        MemoryReadResult result = await service.ReadAsync(0x1000, 16, CancellationToken.None);

        Assert.Equal(MemoryReadStatus.ProcessUnavailable, result.Status);
        Assert.Empty(result.Data);
        Assert.False(service.Identity.IsAvailable);
    }

    [Fact]
    public async Task MemoryViewDataService_PartialReadsRemainVisibleAndTruthfullyLabelled()
    {
        var memory = new FakeProcessMemoryService();
        var modules = new FakeModuleService();
        var metadata = new RecordingMetadataService();
        var service = new MemoryViewDataService(memory, modules, metadata);

        var snapshot = await service.LoadSnapshotAsync(0x140001000, CancellationToken.None);

        Assert.Contains("partial read", snapshot.Status);
        Assert.NotEmpty(snapshot.Instructions);
        Assert.Single(snapshot.HexRows);
        Assert.Equal("Target.exe", snapshot.ModuleName);
        Assert.Equal("entry", snapshot.Instructions[0].Comment);
        Assert.Equal("◆", snapshot.Instructions[0].Marker);
        Assert.Equal("Target.exe", metadata.LastLocation?.ModuleName);
        Assert.Equal(0x1000UL, metadata.LastLocation?.RelativeVirtualAddress);
    }

    [Fact]
    public async Task MemoryViewDataService_UnreadableCode_ReportsError()
    {
        var memory = new FakeProcessMemoryService { CodeStatus = MemoryReadStatus.Unreadable };
        var service = new MemoryViewDataService(memory, new FakeModuleService(), new RecordingMetadataService());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LoadSnapshotAsync(0x140001000, CancellationToken.None));

        Assert.Contains("unreadable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Target.exe+1000", 0x140001000UL)]
    [InlineData("target.EXE+0x20", 0x140000020UL)]
    [InlineData("0x140001000", 0x140001000UL)]
    [InlineData("00000001`40001000", 0x140001000UL)]
    public void TryResolveAddress_ResolvesSupportedPhaseThreeForms(string expression, ulong expected)
    {
        var service = new MemoryViewDataService(
            new FakeProcessMemoryService(), new FakeModuleService(), new RecordingMetadataService());

        bool resolved = service.TryResolveAddress(expression, out ulong address, out string? error);

        Assert.True(resolved, error);
        Assert.Equal(expected, address);
    }

    private sealed class FakeProcessMemoryService : IProcessMemoryService
    {
        public MemoryReadStatus CodeStatus { get; init; } = MemoryReadStatus.Partial;
        public ProcessIdentity Identity { get; } = new("Target", 42, true, true);

        public Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            MemoryReadStatus status;
            if (size == 4096)
            {
                status = CodeStatus;
                data = status == MemoryReadStatus.Unreadable ? Array.Empty<byte>() : new byte[] { 0x48, 0x89, 0xD8 };
            }
            else
            {
                status = MemoryReadStatus.Partial;
                data = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
            }

            return Task.FromResult(new MemoryReadResult(
                address, size, data, status,
                status == MemoryReadStatus.Unreadable ? "The requested memory is unreadable." : null));
        }

        public MemoryRegionDescriptor? QueryRegion(ulong address) =>
            new(0x140000000, 0x100000, MemoryRegion.PAGE_EXECUTE_READ, MemoryRegion.MEM_COMMIT, 0x1000000);
    }

    private sealed class FakeModuleService : IModuleService
    {
        private static readonly ModuleDescriptor Module =
            new("Target.exe", 0x140000000, 0x100000, 0x140001000, "C:\\Target.exe");

        public IReadOnlyList<ModuleDescriptor> GetModules() => new[] { Module };
        public ModuleDescriptor? FindByAddress(ulong address) => Module.Contains(address) ? Module : null;
        public ModuleDescriptor? FindByName(string moduleName) =>
            Module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ? Module : null;
        public ulong GetDefaultAddress() => Module.EntryPoint;
    }

    private sealed class RecordingMetadataService : IMemoryMetadataService
    {
        public MemoryLocation? LastLocation { get; private set; }

        public string? GetComment(MemoryLocation location)
        {
            LastLocation = location;
            return "entry";
        }

        public bool HasBookmark(MemoryLocation location)
        {
            LastLocation = location;
            return true;
        }
    }
}
