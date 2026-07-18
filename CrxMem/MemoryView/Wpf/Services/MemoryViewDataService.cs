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
    private readonly ProcessAccess _process;

    public MemoryViewDataService(ProcessAccess process)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
    }

    public ulong GetDefaultAddress()
    {
        try
        {
            IntPtr entryPoint = _process.Target?.MainModule?.EntryPointAddress ?? IntPtr.Zero;
            if (entryPoint != IntPtr.Zero) return unchecked((ulong)entryPoint.ToInt64());
        }
        catch
        {
            // Module enumeration below is the permission-tolerant fallback.
        }

        ModuleInfo? module = _process.GetModules().FirstOrDefault();
        return module == null ? 0UL : unchecked((ulong)module.BaseAddress.ToInt64());
    }

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
            ModuleInfo? module = _process.GetModules().FirstOrDefault(item =>
                item.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
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

            address = unchecked((ulong)module.BaseAddress.ToInt64()) + offset;
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

    public Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken) =>
        Task.Run(() => LoadSnapshot(address, cancellationToken), cancellationToken);

    private MemoryViewSnapshot LoadSnapshot(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_process.IsOpen || _process.Target == null || _process.Target.HasExited)
        {
            throw new InvalidOperationException("The selected process is no longer available.");
        }

        var runtimeAddress = new IntPtr(unchecked((long)address));
        ModuleInfo? module = _process.GetModuleForAddress(runtimeAddress);
        MemoryRegion? region = MemoryRegion.Query(_process.Handle, runtimeAddress);
        byte[] code = _process.Read(runtimeAddress, DecodeByteCount) ?? Array.Empty<byte>();
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<DisassemblyPreviewRow> instructions = Decode(address, code, module, cancellationToken);
        IReadOnlyList<HexPreviewRow> hexRows = BuildHexRows(address, cancellationToken);
        string processName = _process.Target.ProcessName;
        string moduleName = module?.ModuleName ?? "—";
        ulong moduleBase = module == null ? 0 : unchecked((ulong)module.BaseAddress.ToInt64());
        string offset = module == null ? "—" : $"{address - moduleBase:X}";
        string protection = region == null ? "Unavailable" : FormatProtection(region.Protection);
        string regionText = region == null
            ? "Unavailable"
            : $"{unchecked((ulong)region.BaseAddress.ToInt64()):X16} – {unchecked((ulong)(region.BaseAddress.ToInt64() + region.Size)):X16}";
        string status = code.Length == 0
            ? "Address is unreadable."
            : $"Inspection mode · decoded {instructions.Count} rows from {code.Length:N0} bytes";

        return new MemoryViewSnapshot(
            address,
            processName,
            _process.Target.Id,
            moduleName,
            moduleBase,
            _process.Is64Bit ? "x64" : "x86",
            offset,
            protection,
            regionText,
            instructions,
            hexRows,
            status);
    }

    private IReadOnlyList<DisassemblyPreviewRow> Decode(
        ulong address, byte[] code, ModuleInfo? module, CancellationToken cancellationToken)
    {
        var rows = new List<DisassemblyPreviewRow>();
        if (code.Length == 0) return rows;

        var decoder = IcedDecoder.Create(_process.Is64Bit ? 64 : 32, code);
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
            string comment = CommentManager.Get(unchecked((long)runtimeAddress)) ?? ResolveFlowTarget(instruction, module);
            string marker = BookmarkManager.Exists(unchecked((long)runtimeAddress)) ? "◆" : string.Empty;
            rows.Add(new DisassemblyPreviewRow(
                marker,
                runtimeAddress.ToString(_process.Is64Bit ? "X16" : "X8"),
                Convert.ToHexString(instructionBytes).Chunk(2).Select(chars => new string(chars)).Aggregate((left, right) => left + " " + right),
                instructionText,
                comment,
                runtimeAddress));

            if (instruction.IsInvalid) decoder.IP = runtimeAddress + 1;
        }

        return rows;
    }

    private IReadOnlyList<HexPreviewRow> BuildHexRows(ulong address, CancellationToken cancellationToken)
    {
        const int bytesPerRow = 16;
        ulong alignedAddress = address & ~0xFUL;
        byte[] data = _process.Read(new IntPtr(unchecked((long)alignedAddress)), bytesPerRow * HexRowCount) ?? Array.Empty<byte>();
        var rows = new List<HexPreviewRow>();

        for (int offset = 0; offset < data.Length; offset += bytesPerRow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> row = data.AsSpan(offset, Math.Min(bytesPerRow, data.Length - offset));
            string bytes = string.Join(" ", row.ToArray().Select(value => value.ToString("X2")));
            var ascii = new StringBuilder(row.Length);
            foreach (byte value in row) ascii.Append(value is >= 32 and < 127 ? (char)value : '.');
            rows.Add(new HexPreviewRow((alignedAddress + (ulong)offset).ToString(_process.Is64Bit ? "X16" : "X8"), bytes, ascii.ToString()));
        }

        return rows;
    }

    private static string ResolveFlowTarget(Instruction instruction, ModuleInfo? module)
    {
        ulong target = instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
            ? instruction.NearBranchTarget
            : instruction.IsIPRelativeMemoryOperand ? instruction.IPRelativeMemoryAddress : 0;
        if (target == 0 || module == null) return string.Empty;

        ulong moduleBase = unchecked((ulong)module.BaseAddress.ToInt64());
        ulong moduleEnd = moduleBase + unchecked((uint)module.Size);
        return target >= moduleBase && target < moduleEnd
            ? $"{module.ModuleName}+{target - moduleBase:X}"
            : $"0x{target:X}";
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
