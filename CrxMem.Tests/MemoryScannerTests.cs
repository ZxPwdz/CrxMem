using CrxMem.Core;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryScannerTests
{
    private static readonly IntPtr BaseAddress = new(0x10000);

    [Fact]
    public void FirstScan_FindsExactAlignedInt32()
    {
        byte[] memory = new byte[32];
        BitConverter.GetBytes(123456).CopyTo(memory, 8);
        var scanner = new MemoryScanner(new FakeMemoryAccessor(BaseAddress, memory));

        scanner.FirstScan(
            ScanType.Exact,
            ScanValueType.Int32,
            "123456",
            fastScan: true,
            startAddress: BaseAddress,
            endAddress: IntPtr.Add(BaseAddress, memory.Length));

        ScanResult result = Assert.Single(scanner.Results);
        Assert.Equal(IntPtr.Add(BaseAddress, 8), result.Address);
    }

    [Fact]
    public void NextScan_ChangedFiltersResultsAndUndoRestoresThem()
    {
        byte[] memory = new byte[32];
        BitConverter.GetBytes(100).CopyTo(memory, 4);
        BitConverter.GetBytes(100).CopyTo(memory, 12);
        var accessor = new FakeMemoryAccessor(BaseAddress, memory);
        var scanner = new MemoryScanner(accessor);

        scanner.FirstScan(
            ScanType.Exact,
            ScanValueType.Int32,
            "100",
            fastScan: true,
            startAddress: BaseAddress,
            endAddress: IntPtr.Add(BaseAddress, memory.Length));

        accessor.WriteInt32(12, 75);
        scanner.NextScan(ScanType.Changed, ScanValueType.Int32, string.Empty);

        ScanResult changed = Assert.Single(scanner.Results);
        Assert.Equal(IntPtr.Add(BaseAddress, 12), changed.Address);

        scanner.UndoScan();
        Assert.Equal(2, scanner.ResultCount);
    }

    [Fact]
    public void FirstScan_RespectsWritableFilter()
    {
        byte[] memory = BitConverter.GetBytes(42);
        var accessor = new FakeMemoryAccessor(
            BaseAddress,
            memory,
            MemoryRegion.PAGE_READONLY);
        var scanner = new MemoryScanner(accessor);

        scanner.FirstScan(
            ScanType.Exact,
            ScanValueType.Int32,
            "42",
            fastScan: true,
            writableOnly: true,
            startAddress: BaseAddress,
            endAddress: IntPtr.Add(BaseAddress, memory.Length));

        Assert.Empty(scanner.Results);
    }

    [Fact]
    public void ArrayOfBytesScan_SupportsWildcards()
    {
        byte[] memory = [0x90, 0x48, 0x8B, 0x11, 0x22, 0xC3];
        var scanner = new MemoryScanner(new FakeMemoryAccessor(BaseAddress, memory));

        List<IntPtr> results = scanner.ScanArrayOfBytes("48 8B ?? ?? C3");

        IntPtr result = Assert.Single(results);
        Assert.Equal(IntPtr.Add(BaseAddress, 1), result);
    }

    private sealed class FakeMemoryAccessor : IMemoryAccessor
    {
        private readonly IntPtr _baseAddress;
        private readonly byte[] _memory;
        private readonly MemoryRegion _region;

        public FakeMemoryAccessor(IntPtr baseAddress, byte[] memory, uint protection = MemoryRegion.PAGE_READWRITE)
        {
            _baseAddress = baseAddress;
            _memory = memory;
            _region = new MemoryRegion
            {
                BaseAddress = baseAddress,
                AllocationBase = baseAddress,
                Size = memory.Length,
                Protection = protection,
                State = MemoryRegion.MEM_COMMIT
            };
        }

        public bool Is64Bit => true;

        public byte[] Read(IntPtr address, int size)
        {
            long offset = address.ToInt64() - _baseAddress.ToInt64();
            if (offset < 0 || offset >= _memory.Length || size <= 0)
                return [];

            int count = Math.Min(size, _memory.Length - (int)offset);
            return _memory.AsSpan((int)offset, count).ToArray();
        }

        public IReadOnlyList<MemoryRegion> EnumerateMemoryRegions(IntPtr startAddress, IntPtr endAddress)
        {
            long regionEnd = _baseAddress.ToInt64() + _memory.Length;
            bool overlaps = startAddress.ToInt64() < regionEnd && endAddress.ToInt64() > _baseAddress.ToInt64();
            return overlaps ? [_region] : [];
        }

        public void WriteInt32(int offset, int value)
        {
            BitConverter.GetBytes(value).CopyTo(_memory, offset);
        }
    }
}
