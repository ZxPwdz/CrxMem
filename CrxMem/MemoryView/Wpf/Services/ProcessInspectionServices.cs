using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public interface IProcessMemoryService
{
    ProcessIdentity Identity { get; }
    Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken);
    MemoryRegionDescriptor? QueryRegion(ulong address);
}

public interface IModuleService
{
    IReadOnlyList<ModuleDescriptor> GetModules();
    ModuleDescriptor? FindByAddress(ulong address);
    ModuleDescriptor? FindByName(string moduleName);
    ulong GetDefaultAddress();
}

public interface IMemoryMetadataService
{
    string? GetComment(MemoryLocation location);
    bool HasBookmark(MemoryLocation location);
}

public sealed class ProcessMemoryService : IProcessMemoryService
{
    private readonly ProcessAccess _process;

    public ProcessMemoryService(ProcessAccess process) =>
        _process = process ?? throw new ArgumentNullException(nameof(process));

    public ProcessIdentity Identity
    {
        get
        {
            try
            {
                bool available = _process.IsOpen && _process.Target != null && !_process.Target.HasExited;
                return new ProcessIdentity(
                    available ? _process.Target!.ProcessName : string.Empty,
                    available ? _process.Target!.Id : 0,
                    _process.Is64Bit,
                    available);
            }
            catch
            {
                return new ProcessIdentity(string.Empty, 0, _process.Is64Bit, false);
            }
        }
    }

    public Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        return Task.Run(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return new MemoryReadResult(address, size, Array.Empty<byte>(), MemoryReadStatus.Cancelled);

            if (!Identity.IsAvailable)
                return new MemoryReadResult(address, size, Array.Empty<byte>(),
                    MemoryReadStatus.ProcessUnavailable, "The selected process is no longer available.");

            try
            {
                byte[]? data = _process.Read(new IntPtr(unchecked((long)address)), size);
                if (cancellationToken.IsCancellationRequested)
                    return new MemoryReadResult(address, size, Array.Empty<byte>(), MemoryReadStatus.Cancelled);
                if (data == null || data.Length == 0)
                    return new MemoryReadResult(address, size, Array.Empty<byte>(),
                        MemoryReadStatus.Unreadable, "The requested memory is unreadable.");

                MemoryReadStatus status = data.Length == size
                    ? MemoryReadStatus.Complete
                    : MemoryReadStatus.Partial;
                return new MemoryReadResult(address, size, data, status,
                    status == MemoryReadStatus.Partial ? $"Read {data.Length} of {size} requested bytes." : null);
            }
            catch (Exception ex)
            {
                return new MemoryReadResult(address, size, Array.Empty<byte>(),
                    Identity.IsAvailable ? MemoryReadStatus.Unreadable : MemoryReadStatus.ProcessUnavailable,
                    ex.Message);
            }
        });
    }

    public MemoryRegionDescriptor? QueryRegion(ulong address)
    {
        if (!Identity.IsAvailable) return null;
        try
        {
            MemoryRegion? region = MemoryRegion.Query(_process.Handle, new IntPtr(unchecked((long)address)));
            return region == null
                ? null
                : new MemoryRegionDescriptor(
                    unchecked((ulong)region.BaseAddress.ToInt64()),
                    unchecked((ulong)region.Size),
                    region.Protection,
                    region.State,
                    region.Type);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class ModuleService : IModuleService
{
    private readonly ProcessAccess _process;

    public ModuleService(ProcessAccess process) =>
        _process = process ?? throw new ArgumentNullException(nameof(process));

    public IReadOnlyList<ModuleDescriptor> GetModules()
    {
        try
        {
            return _process.GetModules()
                .Select(module => new ModuleDescriptor(
                    module.ModuleName,
                    unchecked((ulong)module.BaseAddress.ToInt64()),
                    unchecked((uint)module.Size)))
                .ToArray();
        }
        catch
        {
            return Array.Empty<ModuleDescriptor>();
        }
    }

    public ModuleDescriptor? FindByAddress(ulong address) =>
        GetModules().FirstOrDefault(module => module.Contains(address));

    public ModuleDescriptor? FindByName(string moduleName) =>
        GetModules().FirstOrDefault(module =>
            module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

    public ulong GetDefaultAddress()
    {
        try
        {
            IntPtr entryPoint = _process.Target?.MainModule?.EntryPointAddress ?? IntPtr.Zero;
            if (entryPoint != IntPtr.Zero) return unchecked((ulong)entryPoint.ToInt64());
        }
        catch
        {
            // Fall back to the first safely enumerated module.
        }

        return GetModules().FirstOrDefault()?.BaseAddress ?? 0;
    }
}

public sealed class LegacyMemoryMetadataService : IMemoryMetadataService
{
    public string? GetComment(MemoryLocation location) =>
        CommentManager.Get(unchecked((long)location.RuntimeAddress));

    public bool HasBookmark(MemoryLocation location) =>
        BookmarkManager.Exists(unchecked((long)location.RuntimeAddress));
}
