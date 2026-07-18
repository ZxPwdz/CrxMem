using System.Text;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace CrxMem.MemoryView.Wpf.Services;

public interface IMemoryViewDataService
{
    ulong GetDefaultAddress();
    bool TryResolveAddress(string expression, out ulong address, out string? error);
    Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken);
}

public sealed class MemoryViewDataService : IMemoryViewDataService
{
    private const int DecodeByteCount = 4096;
    private const int MaximumInstructions = 300;
    private const int HexRowCount = 24;
    private readonly IProcessMemoryService _memory;
    private readonly IModuleService _modules;
    private readonly IMemoryMetadataService _metadata;

    public MemoryViewDataService(ProcessAccess process)
        : this(
            new ProcessMemoryService(process),
            new ModuleService(process),
            new LegacyMemoryMetadataService())
    {
    }

    public MemoryViewDataService(
        IProcessMemoryService memory,
        IModuleService modules,
        IMemoryMetadataService metadata)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public ulong GetDefaultAddress() => _modules.GetDefaultAddress();

    public bool TryResolveAddress(string expression, out ulong address, out string? error)
    {
        address = 0;
        error = null;
        string value = expression.Trim();
        if (string.IsNullOrEmpty(value))
        {
            error = "Enter a hexadecimal address or module+offset.";
            return false;
        }

        int plusIndex = value.IndexOf('+');
        if (plusIndex > 0 && plusIndex < value.Length - 1)
        {
            string moduleName = value[..plusIndex].Trim();
            string offsetText = RemoveHexPrefix(value[(plusIndex + 1)..].Trim());
            ModuleDescriptor? module = _modules.FindByName(moduleName);
            if (module == null)
            {
                error = $"Module '{moduleName}' is not loaded.";
                return false;
            }

            if (!ulong.TryParse(offsetText, System.Globalization.NumberStyles.HexNumber, null, out ulong offset))
            {
                error = "The module offset is not valid hexadecimal.";
                return false;
            }

            address = module.BaseAddress + offset;
            return true;
        }

        if (!ulong.TryParse(RemoveHexPrefix(value).Replace("`", string.Empty),
                System.Globalization.NumberStyles.HexNumber, null, out address))
        {
            error = "The address is not valid hexadecimal.";
            return false;
        }

        return true;
    }

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
        ulong alignedHexAddress = address & ~0xFUL;
        Task<MemoryReadResult> codeTask = _memory.ReadAsync(address, DecodeByteCount, cancellationToken);
        Task<MemoryReadResult> hexTask = _memory.ReadAsync(
            alignedHexAddress, 16 * HexRowCount, cancellationToken);
        await Task.WhenAll(codeTask, hexTask);
        cancellationToken.ThrowIfCancellationRequested();

        MemoryReadResult codeRead = await codeTask;
        MemoryReadResult hexRead = await hexTask;
        ThrowForTerminalReadState(codeRead, cancellationToken);
        ThrowForTerminalReadState(hexRead, cancellationToken, allowUnreadable: true);

        IReadOnlyList<DisassemblyPreviewRow> instructions = await Task.Run(
            () => Decode(address, codeRead.Data, module, identity.Is64Bit, cancellationToken),
            cancellationToken);
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
            : $"Inspection mode · {readQualifier}decoded {instructions.Count} rows from {codeRead.Data.Length:N0} bytes";

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

    private IReadOnlyList<DisassemblyPreviewRow> Decode(
        ulong address,
        byte[] code,
        ModuleDescriptor? module,
        bool is64Bit,
        CancellationToken cancellationToken)
    {
        var rows = new List<DisassemblyPreviewRow>();
        if (code.Length == 0) return rows;

        var decoder = IcedDecoder.Create(is64Bit ? 64 : 32, code);
        decoder.IP = address;
        var formatter = new NasmFormatter();
        formatter.Options.HexPrefix = "0x";

        ulong decodeEnd = address + (ulong)code.Length;
        while (decoder.IP < decodeEnd && rows.Count < MaximumInstructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decoder.Decode(out Instruction instruction);
            int offset = checked((int)(instruction.IP - address));
            int length = instruction.IsInvalid ? 1 : Math.Max(1, instruction.Length);
            if (offset < 0 || offset >= code.Length) break;
            length = Math.Min(length, code.Length - offset);

            byte[] instructionBytes = code.AsSpan(offset, length).ToArray();
            string instructionText;
            if (instruction.IsInvalid)
            {
                instructionText = $"db 0x{instructionBytes[0]:X2}";
            }
            else
            {
                var output = new PlainFormatterOutput();
                formatter.Format(instruction, output);
                instructionText = output.ToString();
            }

            ulong runtimeAddress = instruction.IsInvalid ? address + (ulong)offset : instruction.IP;
            MemoryLocation location = MemoryLocation.Create(runtimeAddress, module);
            string comment = _metadata.GetComment(location) ?? ResolveFlowTarget(instruction, module);
            string marker = _metadata.HasBookmark(location) ? "◆" : string.Empty;
            rows.Add(new DisassemblyPreviewRow(
                marker,
                runtimeAddress.ToString(is64Bit ? "X16" : "X8"),
                Convert.ToHexString(instructionBytes).Chunk(2).Select(chars => new string(chars)).Aggregate((left, right) => left + " " + right),
                instructionText,
                comment,
                runtimeAddress));

            if (instruction.IsInvalid) decoder.IP = runtimeAddress + 1;
        }

        return rows;
    }

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
                ascii.ToString()));
        }

        return rows;
    }

    private static string ResolveFlowTarget(Instruction instruction, ModuleDescriptor? module)
    {
        ulong target = instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
            ? instruction.NearBranchTarget
            : instruction.IsIPRelativeMemoryOperand ? instruction.IPRelativeMemoryAddress : 0;
        if (target == 0 || module == null) return string.Empty;

        return module.Contains(target)
            ? $"{module.Name}+{target - module.BaseAddress:X}"
            : $"0x{target:X}";
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

    private static string RemoveHexPrefix(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;

    private sealed class PlainFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _builder = new();
        public override void Write(string text, FormatterTextKind kind) => _builder.Append(text);
        public override string ToString() => _builder.ToString();
    }
}
