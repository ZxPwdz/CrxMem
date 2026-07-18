using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record MemoryMapRow(
    string BaseAddress,
    string EndAddress,
    string Size,
    string Protection,
    string State,
    string Type,
    ulong RuntimeAddress);

public interface IMemoryMapService
{
    Task<IReadOnlyList<MemoryMapRow>> GetRegionsAsync(CancellationToken cancellationToken);
}

public sealed class ProcessMemoryMapService : IMemoryMapService
{
    private readonly ProcessAccess _process;
    public ProcessMemoryMapService(ProcessAccess process) => _process = process;

    public Task<IReadOnlyList<MemoryMapRow>> GetRegionsAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<MemoryMapRow>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _process.GetMemoryRegions().Select(region =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong start = unchecked((ulong)region.BaseAddress.ToInt64());
            ulong size = unchecked((ulong)region.Size);
            ulong end = ulong.MaxValue - start < size ? ulong.MaxValue : start + size;
            return new MemoryMapRow(
                start.ToString("X16"),
                end.ToString("X16"),
                $"0x{size:X}",
                FormatProtection(region.Protection),
                $"0x{region.State:X}",
                $"0x{region.Type:X}",
                start);
        }).ToArray();
    }, cancellationToken);

    private static string FormatProtection(uint value)
    {
        var parts = new List<string>();
        if ((value & (MemoryRegion.PAGE_READONLY | MemoryRegion.PAGE_READWRITE | MemoryRegion.PAGE_EXECUTE_READ | MemoryRegion.PAGE_EXECUTE_READWRITE)) != 0) parts.Add("R");
        if ((value & (MemoryRegion.PAGE_READWRITE | MemoryRegion.PAGE_EXECUTE_READWRITE)) != 0) parts.Add("W");
        if ((value & (MemoryRegion.PAGE_EXECUTE | MemoryRegion.PAGE_EXECUTE_READ | MemoryRegion.PAGE_EXECUTE_READWRITE | MemoryRegion.PAGE_EXECUTE_WRITECOPY)) != 0) parts.Add("X");
        if ((value & MemoryRegion.PAGE_GUARD) != 0) parts.Add("Guard");
        return parts.Count == 0 ? $"0x{value:X}" : string.Join('/', parts);
    }
}

public sealed record ModulePanelRow(string Name, string BaseAddress, string EndAddress, string Size, ulong RuntimeAddress);
public sealed record PatchHistoryRow(
    Guid Id,
    string Address,
    string Original,
    string Patched,
    string State,
    string Description,
    ulong RuntimeAddress);
public sealed record CallStackPanelRow(
    int Index,
    string Address,
    string Label,
    string Confidence,
    ulong RuntimeAddress);

public static class PanelProjection
{
    public static ModulePanelRow ToPanelRow(this ModuleDescriptor module) => new(
        module.Name,
        module.BaseAddress.ToString("X16"),
        (module.BaseAddress + module.Size).ToString("X16"),
        $"0x{module.Size:X}",
        module.BaseAddress);

    public static PatchHistoryRow ToPanelRow(this PatchRecord patch) => new(
        patch.Id,
        patch.Address.ToString("X16"),
        Convert.ToHexString(patch.OriginalBytes),
        Convert.ToHexString(patch.PatchedBytes),
        patch.IsStale ? "Stale" : patch.IsApplied ? "Applied" : "Restored",
        patch.Description ?? string.Empty,
        patch.Address);

    public static IReadOnlyList<CallStackPanelRow> ToHeuristicCallStack(
        this IEnumerable<StackPreviewRow> stackRows) => stackRows
        .Where(row => row.PointerValue != 0 && !string.IsNullOrWhiteSpace(row.Label))
        .Select((row, index) => new CallStackPanelRow(
            index,
            row.Value,
            row.Label,
            "Heuristic — stack scan, not unwind data",
            row.PointerValue))
        .ToArray();
}
