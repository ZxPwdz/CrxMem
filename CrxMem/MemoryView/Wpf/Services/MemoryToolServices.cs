using CrxMem.MemoryView.Wpf.Models;

namespace CrxMem.MemoryView.Wpf.Services;

public interface IStackService
{
    Task<IReadOnlyList<StackPreviewRow>> ReadAsync(
        RegisterSnapshot registers,
        int maximumRows,
        CancellationToken cancellationToken);
}

public interface IWatchService
{
    Task<WatchPreviewRow> EvaluateAsync(
        string expression,
        WatchPreviewRow? previous,
        CancellationToken cancellationToken);
}

public sealed class StackService : IStackService
{
    private readonly IProcessMemoryService _memory;
    private readonly IModuleService _modules;

    public StackService(IProcessMemoryService memory, IModuleService modules)
    {
        _memory = memory;
        _modules = modules;
    }

    public async Task<IReadOnlyList<StackPreviewRow>> ReadAsync(
        RegisterSnapshot registers,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        if (maximumRows is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        int pointerSize = registers.Is64Bit ? 8 : 4;
        MemoryReadResult read = await _memory.ReadAsync(
            registers.StackPointer, pointerSize * maximumRows, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!read.HasData) return Array.Empty<StackPreviewRow>();

        var rows = new List<StackPreviewRow>();
        for (int offset = 0; offset + pointerSize <= read.Data.Length; offset += pointerSize)
        {
            ulong pointer = pointerSize == 8
                ? BitConverter.ToUInt64(read.Data, offset)
                : BitConverter.ToUInt32(read.Data, offset);
            ulong address = registers.StackPointer + (ulong)offset;
            ModuleDescriptor? module = _modules.FindByAddress(pointer);
            string label = module == null ? string.Empty : $"{module.Name}+{pointer - module.BaseAddress:X} (heuristic)";
            rows.Add(new StackPreviewRow(
                address.ToString(registers.Is64Bit ? "X16" : "X8"),
                pointer.ToString(registers.Is64Bit ? "X16" : "X8"),
                label,
                address,
                pointer,
                module != null));
        }
        return rows;
    }
}

public sealed class WatchService : IWatchService
{
    private readonly IAddressExpressionService _addresses;
    private readonly IProcessMemoryService _memory;

    public WatchService(IAddressExpressionService addresses, IProcessMemoryService memory)
    {
        _addresses = addresses;
        _memory = memory;
    }

    public async Task<WatchPreviewRow> EvaluateAsync(
        string expression,
        WatchPreviewRow? previous,
        CancellationToken cancellationToken)
    {
        AddressExpressionResult resolved = await _addresses.EvaluateAsync(expression, cancellationToken);
        if (!resolved.Success)
            return new WatchPreviewRow(expression, "—", "—", resolved.Error ?? "Invalid expression", null, null);

        int pointerSize = _memory.Identity.Is64Bit ? 8 : 4;
        MemoryReadResult read = await _memory.ReadAsync(resolved.Address, pointerSize, cancellationToken);
        if (read.Data.Length < pointerSize)
            return new WatchPreviewRow(
                expression, $"0x{resolved.Address:X}", "—", read.Error ?? "Unreadable", resolved.Address, null,
                previous?.NumericValue);

        ulong value = pointerSize == 8
            ? BitConverter.ToUInt64(read.Data, 0)
            : BitConverter.ToUInt32(read.Data, 0);
        return new WatchPreviewRow(
            expression,
            $"0x{resolved.Address:X}",
            $"0x{value:X}",
            read.Status == MemoryReadStatus.Partial ? "Partial read" : "OK",
            resolved.Address,
            value,
            previous?.NumericValue);
    }
}
