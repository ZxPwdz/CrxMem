using System;
using System.Collections.Generic;

namespace CrxMem.Core
{
    /// <summary>
    /// Minimal process-memory surface required by the scanner.
    /// Keeping this contract UI- and Win32-independent makes scan behavior testable.
    /// </summary>
    public interface IMemoryAccessor
    {
        bool Is64Bit { get; }

        byte[] Read(IntPtr address, int size);

        IReadOnlyList<MemoryRegion> EnumerateMemoryRegions(IntPtr startAddress, IntPtr endAddress);
    }
}
