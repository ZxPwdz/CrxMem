using System.Globalization;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record AobScanResult(IReadOnlyList<ulong> Matches, string? Error = null)
{
    public bool Succeeded => Error == null;
}
public interface IAobScanService
{
    Task<AobScanResult> ScanAsync(string pattern, string? moduleName, ulong? start, ulong? end, CancellationToken cancellationToken);
}

public sealed class ProcessAobScanService : IAobScanService
{
    private const ulong MaximumScannedBytes = 512UL * 1024 * 1024;
    private const int MaximumMatches = 32;
    private const int ChunkSize = 1024 * 1024;
    private readonly ProcessAccess _process;
    private readonly IProcessMemoryService _memory;
    private readonly IModuleService _modules;
    public ProcessAobScanService(ProcessAccess process, IProcessMemoryService memory, IModuleService modules)
    { _process = process; _memory = memory; _modules = modules; }

    public async Task<AobScanResult> ScanAsync(string pattern, string? moduleName, ulong? start, ulong? end,
        CancellationToken cancellationToken)
    {
        if (!TryPattern(pattern, out byte?[] bytes, out string? error)) return new(Array.Empty<ulong>(), error);
        var ranges = new List<(ulong Start, ulong End)>();
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            ModuleDescriptor? module = _modules.FindByName(moduleName);
            if (module == null) return new(Array.Empty<ulong>(), $"Module '{moduleName}' is not loaded.");
            ranges.Add((module.BaseAddress, checked(module.BaseAddress + module.Size)));
        }
        else if (start.HasValue && end.HasValue && end > start) ranges.Add((start.Value, end.Value));
        else
        {
            ranges.AddRange(_process.GetMemoryRegions().Where(region => region.IsReadable && (region.Protection & MemoryRegion.PAGE_GUARD) == 0)
                .Select(region => (unchecked((ulong)region.BaseAddress.ToInt64()),
                    checked(unchecked((ulong)region.BaseAddress.ToInt64()) + unchecked((ulong)region.Size)))));
        }

        ulong scanned = 0;
        var matches = new List<ulong>();
        foreach ((ulong rangeStart, ulong rangeEnd) in ranges)
        {
            for (ulong address = rangeStart; address < rangeEnd;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = checked((int)Math.Min((ulong)ChunkSize, rangeEnd - address));
                if (scanned + (ulong)count > MaximumScannedBytes)
                    return new(matches, $"AOB scan exceeded the {MaximumScannedBytes / (1024 * 1024)} MB safety limit.");
                MemoryReadResult read = await _memory.ReadAsync(address, count, cancellationToken);
                scanned += (ulong)count;
                if (read.Data.Length >= bytes.Length)
                    Find(read.Data, bytes, address, matches);
                if (matches.Count >= MaximumMatches) return new(matches, $"AOB scan exceeded {MaximumMatches} matches.");
                ulong advance = (ulong)Math.Max(1, count - bytes.Length + 1);
                address += advance;
            }
        }
        return new(matches);
    }

    private static void Find(byte[] data, byte?[] pattern, ulong baseAddress, List<ulong> matches)
    {
        for (int offset = 0; offset <= data.Length - pattern.Length; offset++)
        {
            bool equal = true;
            for (int index = 0; index < pattern.Length; index++)
                if (pattern[index].HasValue && pattern[index]!.Value != data[offset + index]) { equal = false; break; }
            if (equal) matches.Add(baseAddress + (ulong)offset);
        }
    }

    private static bool TryPattern(string text, out byte?[] pattern, out string? error)
    {
        var result = new List<byte?>(); error = null;
        foreach (string token in text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "?" or "??" || token.Contains('?')) { result.Add(null); continue; }
            string value = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (!byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte parsed))
            { pattern = Array.Empty<byte?>(); error = $"Invalid AOB token '{token}'."; return false; }
            result.Add(parsed);
        }
        pattern = result.ToArray();
        if (pattern.Length == 0) { error = "AOB pattern is empty."; return false; }
        return true;
    }
}
