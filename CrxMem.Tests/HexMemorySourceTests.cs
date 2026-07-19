using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class HexMemorySourceTests
{
    [Fact]
    public async Task SourceRepresentsWholeRegionAndLoadsOnlyRequestedPages()
    {
        var memory = new RecordingMemoryService(0x100000, 1024 * 1024);
        using var source = new PagedHexMemorySource(memory, 0x180000);

        Assert.Equal(65_536, source.Count);
        Assert.Empty(source[0].Data!);
        Assert.Empty(source[32_000].Data!);

        await WaitUntilAsync(() => memory.ReadCalls >= 2);
        Assert.InRange(memory.ReadCalls, 2, 2);
        await WaitUntilAsync(() => source[0].Data?.Length == 16);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), source[0].Data);
    }

    [Fact]
    public async Task ConcurrentRowsSharePageAndInvalidationReloadsIt()
    {
        var memory = new RecordingMemoryService(0x200000, 0x8000);
        using var source = new PagedHexMemorySource(memory, 0x201000);

        _ = source[0];
        _ = source[1];
        _ = source[100];
        await WaitUntilAsync(() => memory.ReadCalls == 1);
        await WaitUntilAsync(() => source[0].Data?.Length == 16);
        Assert.Equal(1, memory.ReadCalls);

        source.Invalidate(0x200010, 4);
        _ = source[1];
        await WaitUntilAsync(() => memory.ReadCalls == 2);
        Assert.Equal(2, memory.ReadCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class RecordingMemoryService : IProcessMemoryService
    {
        private readonly ulong _base;
        private readonly ulong _size;
        private int _readCalls;
        public RecordingMemoryService(ulong @base, ulong size) { _base = @base; _size = size; }
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public ProcessIdentity Identity => new("HexTarget", 42, true, true);

        public Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCalls);
            byte[] data = Enumerable.Range(0, size).Select(index => unchecked((byte)(address + (ulong)index))).ToArray();
            return Task.FromResult(new MemoryReadResult(address, size, data, MemoryReadStatus.Complete));
        }

        public MemoryRegionDescriptor? QueryRegion(ulong address) =>
            address >= _base && address < _base + _size
                ? new MemoryRegionDescriptor(_base, _size, 0x04, 0x1000, 0x20000)
                : null;
    }
}
