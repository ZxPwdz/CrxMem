using System.Text;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;

namespace CrxMem.MemoryView.Wpf.Services;

public interface IMemoryViewDataService
{
    ulong GetDefaultAddress();
    Task<AddressExpressionResult> ResolveAddressAsync(string expression, CancellationToken cancellationToken);
    Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken);
}

public sealed class MemoryViewDataService : IMemoryViewDataService
{
    private const int DecodeByteCount = 64 * 1024;
    private const int PreferredBytesBeforeAddress = 8 * 1024;
    private const int MaximumDecodedInstructions = 12_000;
    private const int HexRowCount = 24;
    private readonly IProcessMemoryService _memory;
    private readonly IModuleService _modules;
    private readonly IMemoryMetadataService _metadata;
    private readonly IAddressExpressionService _addresses;
    private readonly IDisassemblyService _disassembly;

    public MemoryViewDataService(ProcessAccess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _memory = new ProcessMemoryService(process);
        _modules = new ModuleService(process);
        _metadata = new LegacyMemoryMetadataService();
        _addresses = new AddressExpressionService(_memory, _modules);
        _disassembly = new IcedDisassemblyService(_metadata);
    }

    public MemoryViewDataService(
        IProcessMemoryService memory,
        IModuleService modules,
        IMemoryMetadataService metadata,
        IAddressExpressionService? addresses = null,
        IDisassemblyService? disassembly = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _addresses = addresses ?? new AddressExpressionService(_memory, _modules);
        _disassembly = disassembly ?? new IcedDisassemblyService(_metadata);
    }

    public ulong GetDefaultAddress() => _modules.GetDefaultAddress();

    public Task<AddressExpressionResult> ResolveAddressAsync(
        string expression,
        CancellationToken cancellationToken) =>
        _addresses.EvaluateAsync(expression, cancellationToken);

    public async Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessIdentity identity = _memory.Identity;
        if (!identity.IsAvailable)
        {
            throw new InvalidOperationException("The selected process is no longer available.");
        }

        ModuleDescriptor? module = _modules.FindByAddress(address);
        MemoryRegionDescriptor? region = _memory.QueryRegion(address);
        (ulong decodeAddress, int decodeByteCount) = GetDecodeWindow(address, region);
        ulong alignedHexAddress = address & ~0xFUL;
        Task<MemoryReadResult> codeTask = _memory.ReadAsync(decodeAddress, decodeByteCount, cancellationToken);
        Task<MemoryReadResult> hexTask = _memory.ReadAsync(
            alignedHexAddress, 16 * HexRowCount, cancellationToken);
        await Task.WhenAll(codeTask, hexTask);
        cancellationToken.ThrowIfCancellationRequested();

        MemoryReadResult codeRead = await codeTask;
        MemoryReadResult hexRead = await hexTask;
        ThrowForTerminalReadState(codeRead, cancellationToken);
        ThrowForTerminalReadState(hexRead, cancellationToken, allowUnreadable: true);

        int requestedOffset = address >= decodeAddress
            ? checked((int)Math.Min((ulong)codeRead.Data.Length, address - decodeAddress))
            : 0;
        byte[] precedingCode = codeRead.Data[..requestedOffset];
        byte[] followingCode = codeRead.Data[requestedOffset..];
        int precedingLimit = MaximumDecodedInstructions - 3_000;
        IReadOnlyList<DisassemblyPreviewRow> precedingInstructions = precedingCode.Length == 0
            ? Array.Empty<DisassemblyPreviewRow>()
            : await _disassembly.DecodeAsync(
                new DisassemblyRequest(decodeAddress, precedingCode, identity.Is64Bit, module, precedingLimit),
                cancellationToken);
        int followingLimit = Math.Max(3_000, MaximumDecodedInstructions - precedingInstructions.Count);
        IReadOnlyList<DisassemblyPreviewRow> followingInstructions = followingCode.Length == 0
            ? Array.Empty<DisassemblyPreviewRow>()
            : await _disassembly.DecodeAsync(
                new DisassemblyRequest(address, followingCode, identity.Is64Bit, module, followingLimit),
                cancellationToken);
        IReadOnlyList<DisassemblyPreviewRow> instructions = precedingInstructions
            .Where(row => row.RuntimeAddress < address &&
                (ulong)CountInstructionBytes(row.Bytes) <= address - row.RuntimeAddress)
            .Concat(followingInstructions)
            .Take(MaximumDecodedInstructions)
            .ToArray();
        IReadOnlyList<HexPreviewRow> hexRows = BuildHexRows(
            alignedHexAddress, hexRead.Data, identity.Is64Bit, cancellationToken);
        string moduleName = module?.Name ?? "—";
        ulong moduleBase = module?.BaseAddress ?? 0;
        string offset = module == null ? "—" : $"{address - moduleBase:X}";
        string protection = region == null ? "Unavailable" : FormatProtection(region.Protection);
        string regionText = region == null
            ? "Unavailable"
            : $"{region.BaseAddress:X16} – {region.BaseAddress + region.Size:X16}";
        string readQualifier = codeRead.Status == MemoryReadStatus.Partial ? "partial read · " : string.Empty;
        string status = codeRead.Data.Length == 0
            ? codeRead.Error ?? "Address is unreadable."
            : $"Inspection mode · {readQualifier}decoded {instructions.Count:N0} rows from {codeRead.Data.Length:N0} bytes around 0x{address:X}";

        return new MemoryViewSnapshot(
            address,
            identity.Name,
            identity.ProcessId,
            moduleName,
            moduleBase,
            identity.Is64Bit ? "x64" : "x86",
            offset,
            protection,
            regionText,
            instructions,
            hexRows,
            status);
    }

    private static (ulong Address, int ByteCount) GetDecodeWindow(
        ulong requestedAddress,
        MemoryRegionDescriptor? region)
    {
        ulong lowerBound = region?.BaseAddress ?? 0;
        ulong upperBound = region == null
            ? ulong.MaxValue
            : region.BaseAddress > ulong.MaxValue - region.Size
                ? ulong.MaxValue
                : region.BaseAddress + region.Size;
        ulong availableBefore = requestedAddress >= lowerBound ? requestedAddress - lowerBound : 0;
        ulong bytesBefore = Math.Min((ulong)PreferredBytesBeforeAddress, availableBefore);
        ulong start = requestedAddress - bytesBefore;
        ulong available = upperBound > start ? upperBound - start : 0;
        int count = checked((int)Math.Min((ulong)DecodeByteCount, available));
        return (start, Math.Max(1, count));
    }

    private static int CountInstructionBytes(string bytes) =>
        bytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static IReadOnlyList<HexPreviewRow> BuildHexRows(
        ulong alignedAddress,
        byte[] data,
        bool is64Bit,
        CancellationToken cancellationToken)
    {
        const int bytesPerRow = 16;
        var rows = new List<HexPreviewRow>();

        for (int offset = 0; offset < data.Length; offset += bytesPerRow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> row = data.AsSpan(offset, Math.Min(bytesPerRow, data.Length - offset));
            string bytes = string.Join(" ", row.ToArray().Select(value => value.ToString("X2")));
            var ascii = new StringBuilder(row.Length);
            foreach (byte value in row) ascii.Append(value is >= 32 and < 127 ? (char)value : '.');
            rows.Add(new HexPreviewRow(
                (alignedAddress + (ulong)offset).ToString(is64Bit ? "X16" : "X8"),
                bytes,
                ascii.ToString(),
                alignedAddress + (ulong)offset,
                row.ToArray()));
        }

        return rows;
    }

    private static void ThrowForTerminalReadState(
        MemoryReadResult result,
        CancellationToken cancellationToken,
        bool allowUnreadable = false)
    {
        if (result.Status == MemoryReadStatus.Cancelled)
            throw new OperationCanceledException(cancellationToken);
        if (result.Status == MemoryReadStatus.ProcessUnavailable)
            throw new InvalidOperationException(result.Error ?? "The selected process is no longer available.");
        if (!allowUnreadable && result.Status == MemoryReadStatus.Unreadable)
            throw new InvalidOperationException(result.Error ?? "The requested memory is unreadable.");
    }

    private static string FormatProtection(uint protection)
    {
        var parts = new List<string>();
        if ((protection & (MemoryRegion.PAGE_READONLY | MemoryRegion.PAGE_READWRITE | MemoryRegion.PAGE_EXECUTE_READ | MemoryRegion.PAGE_EXECUTE_READWRITE)) != 0) parts.Add("Read");
        if ((protection & (MemoryRegion.PAGE_READWRITE | MemoryRegion.PAGE_EXECUTE_READWRITE)) != 0) parts.Add("Write");
        if ((protection & (MemoryRegion.PAGE_EXECUTE | MemoryRegion.PAGE_EXECUTE_READ | MemoryRegion.PAGE_EXECUTE_READWRITE | MemoryRegion.PAGE_EXECUTE_WRITECOPY)) != 0) parts.Add("Execute");
        if ((protection & MemoryRegion.PAGE_GUARD) != 0) parts.Add("Guard");
        return parts.Count == 0 ? $"0x{protection:X}" : string.Join(" / ", parts);
    }

}
