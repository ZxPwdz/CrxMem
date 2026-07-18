namespace CrxMem.MemoryView.Wpf.Services;

public enum MemoryReadStatus
{
    Complete,
    Partial,
    Unreadable,
    ProcessUnavailable,
    Cancelled
}

public sealed record ProcessIdentity(
    string Name,
    int ProcessId,
    bool Is64Bit,
    bool IsAvailable);

public sealed record MemoryReadResult(
    ulong Address,
    int RequestedSize,
    byte[] Data,
    MemoryReadStatus Status,
    string? Error = null)
{
    public bool HasData => Data.Length > 0;
}

public sealed record MemoryRegionDescriptor(
    ulong BaseAddress,
    ulong Size,
    uint Protection,
    uint State,
    uint Type);

public sealed record ModuleDescriptor(
    string Name,
    ulong BaseAddress,
    ulong Size,
    ulong EntryPoint = 0,
    string? FilePath = null)
{
    public ulong EndAddress => BaseAddress + Size;
    public bool Contains(ulong address) => address >= BaseAddress && address < EndAddress;
}

public sealed record MemoryLocation(
    ulong RuntimeAddress,
    string? ModuleName,
    ulong? RelativeVirtualAddress)
{
    public static MemoryLocation Create(ulong address, ModuleDescriptor? module) =>
        module == null
            ? new MemoryLocation(address, null, null)
            : new MemoryLocation(address, module.Name, address - module.BaseAddress);
}
