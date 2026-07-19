using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public enum AutoAssemblerSection { Common, Enable, Disable }
public enum AutoAssemblerSeverity { Info, Warning, Error }
public sealed record AutoAssemblerSpan(int Line, int Column, int Length);
public sealed record AutoAssemblerDiagnostic(AutoAssemblerSeverity Severity, string Code, string Message, AutoAssemblerSpan Span);
public sealed record AutoAssemblerLine(AutoAssemblerSection Section, string Text, AutoAssemblerSpan Span);
public sealed record AutoAssemblerDocument(IReadOnlyList<AutoAssemblerLine> Lines, int? ForcedBitness);
public sealed record AutoAssemblerAllocation(string Name, ulong Address, uint Size, uint Protection, ulong? NearAddress = null);
public sealed record AutoAssemblerAssertion(ulong Address, byte[] ExpectedBytes, AutoAssemblerSpan Span);
public sealed record AutoAssemblerWrite(ulong Address, byte[] Bytes, AutoAssemblerSpan Span, string Description);
public sealed record AutoAssemblerPlan(
    Guid Id, string ScriptHash, int ProcessId, DateTime ProcessStartTimeUtc, int Bitness, bool IsEnable,
    IReadOnlyList<AutoAssemblerAllocation> Allocations, IReadOnlyList<AutoAssemblerAssertion> Assertions,
    IReadOnlyList<AutoAssemblerWrite> Writes, IReadOnlyDictionary<string, ulong> Symbols,
    IReadOnlyList<string> SymbolsToRemove, IReadOnlyList<string> AllocationsToFree,
    IReadOnlyList<AutoAssemblerDiagnostic> Diagnostics)
{
    public bool CanApply => Diagnostics.All(item => item.Severity != AutoAssemblerSeverity.Error);
}
public sealed record AutoAssemblerCompileRequest(string Source, bool Enable, int Bitness, int ProcessId, DateTime ProcessStartTimeUtc,
    bool SyntaxCheck = true);
public sealed record AutoAssemblerCompileResult(AutoAssemblerPlan? Plan, IReadOnlyList<AutoAssemblerDiagnostic> Diagnostics)
{
    public bool Succeeded => Plan?.CanApply == true;
}
public sealed record AutoAssemblerSession(Guid Id, Guid PlanId, string ScriptHash, int ProcessId,
    DateTime ProcessStartTimeUtc, IReadOnlyList<PatchRecord> Patches, IReadOnlyList<AutoAssemblerAllocation> Allocations,
    IReadOnlyDictionary<string, ulong> Symbols, bool IsEnabled, string? RecoveryError = null);
public sealed record AutoAssemblerExecutionResult(bool Succeeded, string Message, AutoAssemblerSession? Session = null,
    bool RollbackAttempted = false, bool RollbackSucceeded = false);

public interface IAutoAssemblerParser { AutoAssemblerDocument Parse(string source, IList<AutoAssemblerDiagnostic> diagnostics); }
public interface IAutoAssemblerPreprocessor { AutoAssemblerDocument Process(AutoAssemblerDocument document, IList<AutoAssemblerDiagnostic> diagnostics); }
public interface IAutoAssemblerBinder { Task<AutoAssemblerCompileResult> BindAsync(AutoAssemblerCompileRequest request, CancellationToken cancellationToken); }
public interface IAutoAssemblerCompiler { Task<AutoAssemblerCompileResult> CompileAsync(AutoAssemblerCompileRequest request, CancellationToken cancellationToken); }
public interface INearAllocationPlanner { Task<AutoAssemblerAllocation?> ProposeAsync(string name, uint size, uint protection, ulong? nearAddress, CancellationToken cancellationToken); }
public interface IAutoAssemblerTransactionExecutor
{
    Task<AutoAssemblerExecutionResult> EnableAsync(AutoAssemblerPlan plan, CancellationToken cancellationToken);
    Task<AutoAssemblerExecutionResult> DisableAsync(AutoAssemblerPlan plan, AutoAssemblerSession session, CancellationToken cancellationToken);
}
public interface IAutoAssemblerSessionStore { AutoAssemblerSession? Get(string scriptHash); void Set(AutoAssemblerSession session); void Remove(string scriptHash); }
public interface IAutoAssemblerService
{
    AutoAssemblerCapability TrustedLuaCapabilities { get; }
    string LuaCapabilitySummary { get; }
    void SetTrustedLuaCapabilities(AutoAssemblerCapability capabilities);
    void RegisterLuaCommand(string name, Func<IReadOnlyList<string>, CancellationToken, Task<string>> command);
    Task<AutoAssemblerCompileResult> CheckAsync(string source, bool enable, CancellationToken cancellationToken);
    Task<AutoAssemblerExecutionResult> EnableAsync(string source, CancellationToken cancellationToken);
    Task<AutoAssemblerExecutionResult> DisableAsync(string source, CancellationToken cancellationToken);
}

public sealed class AutoAssemblerParser : IAutoAssemblerParser
{
    public AutoAssemblerDocument Parse(string source, IList<AutoAssemblerDiagnostic> diagnostics)
    {
        var result = new List<AutoAssemblerLine>();
        AutoAssemblerSection section = AutoAssemblerSection.Common;
        int? bitness = null;
        string[] lines = (source ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string text = StripComment(lines[index]).Trim();
            var span = new AutoAssemblerSpan(index + 1, 1, Math.Max(1, lines[index].Length));
            if (text.Length == 0) continue;
            if (text.Equals("[ENABLE]", StringComparison.OrdinalIgnoreCase)) { section = AutoAssemblerSection.Enable; continue; }
            if (text.Equals("[DISABLE]", StringComparison.OrdinalIgnoreCase)) { section = AutoAssemblerSection.Disable; continue; }
            if (text.Equals("[32-BIT]", StringComparison.OrdinalIgnoreCase)) { bitness = 32; continue; }
            if (text.Equals("[64-BIT]", StringComparison.OrdinalIgnoreCase)) { bitness = 64; continue; }
            if (text.StartsWith("{$", StringComparison.Ordinal))
                diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1001", "Lua and other preprocessor blocks require the restricted Phase 19 host.", span));
            result.Add(new AutoAssemblerLine(section, text, span));
        }
        return new AutoAssemblerDocument(result, bitness);
    }

    private static string StripComment(string line)
    {
        int slash = line.IndexOf("//", StringComparison.Ordinal);
        int semicolon = line.IndexOf(';');
        int cut = slash < 0 ? semicolon : semicolon < 0 ? slash : Math.Min(slash, semicolon);
        return cut < 0 ? line : line[..cut];
    }
}

public sealed class AutoAssemblerPreprocessor : IAutoAssemblerPreprocessor
{
    private const int MaximumIncludeDepth = 8;
    private const int MaximumIncludedCharacters = 1024 * 1024;
    private readonly string[] _approvedRoots;
    public AutoAssemblerPreprocessor(IEnumerable<string>? approvedRoots = null)
    {
        _approvedRoots = (approvedRoots ?? DefaultRoots()).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public AutoAssemblerDocument Process(AutoAssemblerDocument document, IList<AutoAssemblerDiagnostic> diagnostics)
    {
        var defines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<AutoAssemblerLine>();
        foreach (AutoAssemblerLine line in ExpandIncludes(document.Lines, diagnostics, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0))
        {
            if (TryCall(line.Text, "define", out string[] args) && args.Length == 2)
            {
                defines[args[0].Trim()] = args[1].Trim();
                continue;
            }
            string text = line.Text;
            foreach ((string name, string value) in defines.OrderByDescending(item => item.Key.Length))
                text = ReplaceIdentifier(text, name, value);
            output.Add(line with { Text = text });
        }
        return new AutoAssemblerDocument(output, document.ForcedBitness);
    }

    private IReadOnlyList<AutoAssemblerLine> ExpandIncludes(IEnumerable<AutoAssemblerLine> lines,
        IList<AutoAssemblerDiagnostic> diagnostics, HashSet<string> stack, int depth)
    {
        var output = new List<AutoAssemblerLine>();
        foreach (AutoAssemblerLine line in lines)
        {
            if (!TryCall(line.Text, "include", out string[] args)) { output.Add(line); continue; }
            if (args.Length != 1 || depth >= MaximumIncludeDepth)
            { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1910", "Include depth or syntax is invalid.", line.Span)); continue; }
            string requested = args[0].Trim().Trim('"', '\'');
            string? path = ResolveApprovedPath(requested);
            if (path == null || !File.Exists(path))
            { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1911", $"Include '{requested}' is outside approved roots or does not exist.", line.Span)); continue; }
            if (!stack.Add(path))
            { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1912", $"Include cycle detected at '{requested}'.", line.Span)); continue; }
            string text = File.ReadAllText(path);
            if (text.Length > MaximumIncludedCharacters)
            { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1913", $"Include '{requested}' exceeds 1 MB.", line.Span)); stack.Remove(path); continue; }
            var nested = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select((value, index) => new AutoAssemblerLine(line.Section, value.Trim(), new(index + 1, 1, Math.Max(1, value.Length))));
            output.AddRange(ExpandIncludes(nested, diagnostics, stack, depth + 1));
            stack.Remove(path);
        }
        return output;
    }

    private string? ResolveApprovedPath(string requested)
    {
        foreach (string root in _approvedRoots)
        {
            string candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> DefaultRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrxMem", "AutoAssembler", "Includes");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CrxMem", "AutoAssembler", "Includes");
    }

    internal static bool TryCall(string text, string name, out string[] args)
    {
        args = Array.Empty<string>();
        if (!text.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase) || !text.EndsWith(')')) return false;
        args = text[(name.Length + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        return true;
    }

    private static string ReplaceIdentifier(string source, string name, string value)
    {
        var output = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length;)
        {
            if (i + name.Length <= source.Length && source.AsSpan(i, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase) &&
                (i == 0 || !IsIdentifier(source[i - 1])) && (i + name.Length == source.Length || !IsIdentifier(source[i + name.Length])))
            { output.Append(value); i += name.Length; }
            else output.Append(source[i++]);
        }
        return output.ToString();
    }
    private static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value is '_' or '.';
}

public sealed class InMemoryAutoAssemblerSessionStore : IAutoAssemblerSessionStore
{
    private readonly Dictionary<string, AutoAssemblerSession> _sessions = new(StringComparer.Ordinal);
    public AutoAssemblerSession? Get(string scriptHash) => _sessions.GetValueOrDefault(scriptHash);
    public void Set(AutoAssemblerSession session) => _sessions[session.ScriptHash] = session;
    public void Remove(string scriptHash) => _sessions.Remove(scriptHash);
}

public sealed class AutoAssemblerCompiler : IAutoAssemblerCompiler, IAutoAssemblerBinder
{
    private readonly IAutoAssemblerParser _parser;
    private readonly IAutoAssemblerPreprocessor _preprocessor;
    private readonly IAddressExpressionService _addresses;
    private readonly IAssemblerService _assembler;
    private readonly INearAllocationPlanner _allocations;
    private readonly IAobScanService? _aobScanner;
    private readonly IProcessMemoryService? _memory;
    private readonly IAutoAssemblerLuaHost? _luaHost;

    public AutoAssemblerCompiler(IAddressExpressionService addresses, IAssemblerService assembler, INearAllocationPlanner allocations,
        IAutoAssemblerParser? parser = null, IAutoAssemblerPreprocessor? preprocessor = null,
        IAobScanService? aobScanner = null, IProcessMemoryService? memory = null,
        IAutoAssemblerLuaHost? luaHost = null)
    {
        _addresses = addresses; _assembler = assembler; _allocations = allocations;
        _parser = parser ?? new AutoAssemblerParser(); _preprocessor = preprocessor ?? new AutoAssemblerPreprocessor();
        _aobScanner = aobScanner;
        _memory = memory;
        _luaHost = luaHost;
    }

    public Task<AutoAssemblerCompileResult> CompileAsync(AutoAssemblerCompileRequest request, CancellationToken cancellationToken) =>
        BindAsync(request, cancellationToken);

    public async Task<AutoAssemblerCompileResult> BindAsync(AutoAssemblerCompileRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = new List<AutoAssemblerDiagnostic>();
        string expandedSource = request.Source;
        IReadOnlyDictionary<int, int>? luaSourceMap = null;
        if (_luaHost != null)
        {
            AutoAssemblerLuaResult lua = await _luaHost.PreprocessAsync(request.Source, request.SyntaxCheck, cancellationToken);
            if (!lua.Succeeded)
            {
                var error = new AutoAssemblerDiagnostic(AutoAssemblerSeverity.Error, "AA1901", lua.Error ?? "AA Lua preprocessing failed.", new(1, 1, 1));
                return new AutoAssemblerCompileResult(null, [error]);
            }
            expandedSource = lua.GeneratedSource;
            luaSourceMap = lua.GeneratedToOriginalLine;
        }
        AutoAssemblerDocument document = _preprocessor.Process(_parser.Parse(expandedSource, diagnostics), diagnostics);
        int bitness = document.ForcedBitness ?? request.Bitness;
        if (bitness is not (32 or 64)) diagnostics.Add(Error("AA2001", "Bitness must be 32 or 64.", 1));
        AutoAssemblerSection wanted = request.Enable ? AutoAssemblerSection.Enable : AutoAssemblerSection.Disable;
        AutoAssemblerLine[] lines = document.Lines.Where(line => line.Section is AutoAssemblerSection.Common || line.Section == wanted).ToArray();
        var proposed = new List<AutoAssemblerAllocation>();
        var symbols = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var unregister = new List<string>();
        var dealloc = new List<string>();

        foreach (AutoAssemblerLine line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAllocation(line, out string name, out uint size, out uint protection, out string? nearExpression))
            {
                ulong? near = nearExpression == null ? null : await ResolveAsync(nearExpression, line, diagnostics, cancellationToken);
                AutoAssemblerAllocation? allocation = await _allocations.ProposeAsync(name, size, protection, near, cancellationToken);
                if (allocation == null) diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2002", $"No preview allocation is available for {name}.", line.Span));
                else { proposed.Add(allocation); symbols[name] = allocation.Address; }
            }
        }

        var assertions = new List<AutoAssemblerAssertion>();
        var writes = new List<AutoAssemblerWrite>();
        ulong? cursor = null;
        foreach (AutoAssemblerLine line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text = line.Text;
            if (TryAllocation(line, out _, out _, out _, out _)) continue;
            if (AutoAssemblerPreprocessor.TryCall(text, "assert", out string[] assertArgs))
            {
                if (assertArgs.Length != 2) { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2101", "assert requires address and bytes.", line.Span)); continue; }
                ulong? address = await ResolveAsync(assertArgs[0], line, diagnostics, cancellationToken, symbols);
                if (!TryBytes(assertArgs[1], 1, out byte[] bytes, out string error)) diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2102", error, line.Span));
                else if (address.HasValue) assertions.Add(new(address.Value, bytes, line.Span));
                continue;
            }
            if (AutoAssemblerPreprocessor.TryCall(text, "registersymbol", out string[] registerArgs))
            {
                foreach (string symbol in registerArgs) if (symbols.TryGetValue(symbol, out ulong address)) symbols[symbol] = address;
                continue;
            }
            if (AutoAssemblerPreprocessor.TryCall(text, "unregistersymbol", out string[] unregisterArgs)) { unregister.AddRange(unregisterArgs); continue; }
            if (AutoAssemblerPreprocessor.TryCall(text, "dealloc", out string[] deallocArgs)) { dealloc.AddRange(deallocArgs); continue; }
            if (text.EndsWith(':'))
            {
                string label = text[..^1].Trim();
                if (symbols.TryGetValue(label, out ulong allocated)) cursor = allocated;
                else
                {
                    ulong? address = await ResolveAsync(label, line, diagnostics, cancellationToken, symbols);
                    if (address.HasValue) { cursor = address; symbols[label] = address.Value; }
                }
                continue;
            }
            if (text.StartsWith("label(", StringComparison.OrdinalIgnoreCase)) continue;
            if (text.StartsWith("fullaccess(", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(AutoAssemblerSeverity.Info, "AA1902", "fullaccess is scoped automatically to verified write transactions; page protection is never left changed.", line.Span));
                continue;
            }
            if (text.StartsWith("createthread", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("loadlibrary", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("luacall", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1903", $"'{text.Split('(')[0]}' requires an explicitly trusted executable capability and an enabled native integration gate.", line.Span));
                continue;
            }
            if (text.StartsWith("{$LUACODE}", StringComparison.OrdinalIgnoreCase) || text.StartsWith("{$C}", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("{$CCODE}", StringComparison.OrdinalIgnoreCase) || text.StartsWith("kalloc", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA1904", "Embedded code and driver/kernel allocation directives are unsupported capabilities.", line.Span));
                continue;
            }
            if (text.StartsWith("aobscan", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryAobArguments(text, out string symbol, out string pattern, out string? module, out string? startText, out string? endText))
                { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2201", "Invalid AOB directive syntax.", line.Span)); continue; }
                if (_aobScanner == null)
                { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2201", "AOB directives require a bounded scan source.", line.Span)); continue; }
                ulong? start = startText == null ? null : await ResolveAsync(startText, line, diagnostics, cancellationToken, symbols);
                ulong? end = endText == null ? null : await ResolveAsync(endText, line, diagnostics, cancellationToken, symbols);
                AobScanResult scan = await _aobScanner.ScanAsync(pattern, module, start, end, cancellationToken);
                if (!scan.Succeeded) diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2203", scan.Error!, line.Span));
                else if (scan.Matches.Count == 0) diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2204", $"AOB scan '{symbol}' found no matches.", line.Span));
                else if (scan.Matches.Count != 1) diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2205", $"AOB scan '{symbol}' found {scan.Matches.Count} matches; exactly one is required.", line.Span));
                else symbols[symbol] = scan.Matches[0];
                continue;
            }
            if (!cursor.HasValue) { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2103", "Set an address or allocation label before emitting bytes.", line.Span)); continue; }
            byte[] emitted;
            if (TryData(text, out int width, out string data))
            {
                if (!TryBytes(data, width, out emitted, out string error)) { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2104", error, line.Span)); continue; }
            }
            else if (AutoAssemblerPreprocessor.TryCall(text, "readmem", out string[] readArgs))
            {
                if (_memory == null || readArgs.Length != 2 || !TryHexOrDecimal(readArgs[1], out ulong count) || count is 0 or > 1024 * 1024)
                { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2202", "readmem requires an address, a size up to 1 MB, and a live memory source.", line.Span)); continue; }
                ulong? source = await ResolveAsync(readArgs[0], line, diagnostics, cancellationToken, symbols);
                if (!source.HasValue) continue;
                MemoryReadResult read = await _memory.ReadAsync(source.Value, checked((int)count), cancellationToken);
                if (read.Data.Length != (int)count) { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2206", "readmem could not read the complete requested range.", line.Span)); continue; }
                emitted = read.Data;
            }
            else if (AutoAssemblerPreprocessor.TryCall(text, "align", out string[] alignArgs))
            {
                if (alignArgs.Length is < 1 or > 2 || !TryHexOrDecimal(alignArgs[0], out ulong boundary) || boundary == 0 || (boundary & (boundary - 1)) != 0 || boundary > 0x10000)
                { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2207", "align requires a power-of-two boundary up to 0x10000.", line.Span)); continue; }
                byte fill = 0;
                ulong fillValue = 0;
                if (alignArgs.Length == 2 && (!TryHexOrDecimal(alignArgs[1], out fillValue) || fillValue > byte.MaxValue))
                { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2208", "align fill must be one byte.", line.Span)); continue; }
                else if (alignArgs.Length == 2) fill = (byte)fillValue;
                int padding = checked((int)((boundary - (cursor.Value & (boundary - 1))) & (boundary - 1)));
                if (padding == 0) continue;
                emitted = Enumerable.Repeat(fill, padding).ToArray();
            }
            else
            {
                string instruction = ReplaceSymbols(text, symbols);
                AssemblyResult assembly = _assembler.Assemble(new(instruction, cursor.Value, bitness));
                if (!assembly.Succeeded) { diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2105", assembly.Error ?? "Assembly failed.", line.Span)); continue; }
                emitted = assembly.Bytes;
            }
            writes.Add(new(cursor.Value, emitted, line.Span, text));
            cursor = checked(cursor.Value + (ulong)emitted.Length);
        }

        if (luaSourceMap != null)
        {
            for (int index = 0; index < diagnostics.Count; index++)
            {
                AutoAssemblerDiagnostic diagnostic = diagnostics[index];
                if (luaSourceMap.TryGetValue(diagnostic.Span.Line, out int originalLine))
                    diagnostics[index] = diagnostic with { Span = diagnostic.Span with { Line = originalLine } };
            }
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Source ?? string.Empty)));
        var plan = new AutoAssemblerPlan(Guid.NewGuid(), hash, request.ProcessId, request.ProcessStartTimeUtc, bitness, request.Enable,
            proposed, assertions, Coalesce(writes), symbols, unregister, dealloc, diagnostics.ToArray());
        return new AutoAssemblerCompileResult(plan, diagnostics);
    }

    private async Task<ulong?> ResolveAsync(string expression, AutoAssemblerLine line, IList<AutoAssemblerDiagnostic> diagnostics,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, ulong>? symbols = null)
    {
        string expanded = ReplaceSymbols(expression, symbols ?? new Dictionary<string, ulong>());
        AddressExpressionResult result = await _addresses.EvaluateAsync(expanded, cancellationToken);
        if (result.Success) return result.Address;
        diagnostics.Add(new(AutoAssemblerSeverity.Error, "AA2301", result.Error ?? $"Cannot resolve {expression}.", line.Span));
        return null;
    }

    private static bool TryAllocation(AutoAssemblerLine line, out string name, out uint size, out uint protection, out string? near)
    {
        name = string.Empty; size = 0; near = null; protection = 0x40;
        string directive = line.Text.StartsWith("allocnx", StringComparison.OrdinalIgnoreCase) ? "allocnx" :
            line.Text.StartsWith("allocxo", StringComparison.OrdinalIgnoreCase) ? "allocxo" :
            line.Text.StartsWith("globalalloc", StringComparison.OrdinalIgnoreCase) ? "globalalloc" :
            line.Text.StartsWith("sharedalloc", StringComparison.OrdinalIgnoreCase) ? "sharedalloc" : "alloc";
        if (!AutoAssemblerPreprocessor.TryCall(line.Text, directive, out string[] args) || args.Length < 2) return false;
        name = args[0];
        if (!TryHexOrDecimal(args[1], out ulong parsed) || parsed is 0 or > uint.MaxValue) return false;
        size = (uint)parsed; near = args.Length > 2 ? args[2] : null;
        protection = directive == "allocnx" ? 0x04U : directive == "allocxo" ? 0x20U : 0x40U;
        return true;
    }

    private static bool TryAobArguments(string text, out string symbol, out string pattern, out string? module, out string? start, out string? end)
    {
        symbol = pattern = string.Empty; module = start = end = null;
        string directive = text.StartsWith("aobscanmodule", StringComparison.OrdinalIgnoreCase) ? "aobscanmodule" :
            text.StartsWith("aobscanregion", StringComparison.OrdinalIgnoreCase) ? "aobscanregion" : "aobscan";
        if (!AutoAssemblerPreprocessor.TryCall(text, directive, out string[] args)) return false;
        if (directive == "aobscan" && args.Length == 2) { symbol = args[0]; pattern = args[1]; return true; }
        if (directive == "aobscanmodule" && args.Length == 3) { symbol = args[0]; module = args[1]; pattern = args[2]; return true; }
        if (directive == "aobscanregion" && args.Length == 4) { symbol = args[0]; start = args[1]; end = args[2]; pattern = args[3]; return true; }
        return false;
    }

    private static bool TryData(string text, out int width, out string data)
    {
        width = 0; data = string.Empty;
        string[] parts = text.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        width = parts[0].ToLowerInvariant() switch { "db" => 1, "dw" => 2, "dd" => 4, "dq" => 8, _ => 0 };
        data = parts[1]; return width != 0;
    }

    private static bool TryBytes(string text, int width, out byte[] bytes, out string error)
    {
        var output = new List<byte>(); error = string.Empty;
        foreach (string token in text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryHexOrDecimal(token, out ulong value) || (width < 8 && value >= (1UL << (width * 8))))
            { bytes = Array.Empty<byte>(); error = $"Invalid {width * 8}-bit data value '{token}'."; return false; }
            for (int index = 0; index < width; index++) output.Add((byte)(value >> (index * 8)));
        }
        bytes = output.ToArray();
        if (bytes.Length == 0) { error = "A data directive requires at least one value."; return false; }
        return true;
    }

    private static bool TryHexOrDecimal(string text, out ulong value)
    {
        string input = text.Trim(); bool dec = input.StartsWith('#');
        if (dec) input = input[1..]; else if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) input = input[2..];
        else if (input.EndsWith("h", StringComparison.OrdinalIgnoreCase)) input = input[..^1];
        return ulong.TryParse(input, dec ? NumberStyles.Integer : NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string ReplaceSymbols(string text, IReadOnlyDictionary<string, ulong> symbols)
    {
        foreach ((string name, ulong address) in symbols.OrderByDescending(item => item.Key.Length))
            text = text.Replace(name, $"0x{address:X}", StringComparison.OrdinalIgnoreCase);
        return text;
    }
    private static IReadOnlyList<AutoAssemblerWrite> Coalesce(IReadOnlyList<AutoAssemblerWrite> writes)
    {
        var result = new List<AutoAssemblerWrite>();
        foreach (AutoAssemblerWrite write in writes)
        {
            if (result.LastOrDefault() is { } last && last.Address + (ulong)last.Bytes.Length == write.Address)
            { result[^1] = last with { Bytes = last.Bytes.Concat(write.Bytes).ToArray(), Description = last.Description + " | " + write.Description }; }
            else result.Add(write);
        }
        return result;
    }
    private static AutoAssemblerDiagnostic Error(string code, string message, int line) => new(AutoAssemblerSeverity.Error, code, message, new(line, 1, 1));
}
