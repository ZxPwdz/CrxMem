using System.Collections.Concurrent;
using System.Text;
using NLua;

namespace CrxMem.MemoryView.Wpf.Services;

[Flags]
public enum AutoAssemblerCapability
{
    None = 0,
    FileSystem = 1,
    HostCallbacks = 2,
    ProcessMutation = 4,
    RemoteThread = 8,
    DllLoading = 16
}

public sealed record AutoAssemblerLuaResult(bool Succeeded, string GeneratedSource,
    IReadOnlyDictionary<int, int> GeneratedToOriginalLine, string? Error = null);

public interface IAutoAssemblerLuaHost
{
    AutoAssemblerCapability TrustedCapabilities { get; }
    void SetTrustedCapabilities(AutoAssemblerCapability capabilities);
    void AttachService(IAutoAssemblerService service);
    void RegisterCommand(string name, Func<IReadOnlyList<string>, CancellationToken, Task<string>> command);
    Task<AutoAssemblerLuaResult> PreprocessAsync(string source, bool syntaxCheck, CancellationToken cancellationToken);
}

public sealed class RestrictedAutoAssemblerLuaHost : IAutoAssemblerLuaHost
{
    private const int MaximumGeneratedCharacters = 1024 * 1024;
    private static readonly TimeSpan MaximumExecutionTime = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<string, Func<IReadOnlyList<string>, CancellationToken, Task<string>>> _commands =
        new(StringComparer.OrdinalIgnoreCase);
    private IAutoAssemblerService? _service;
    private int _callbackDepth;
    public AutoAssemblerCapability TrustedCapabilities { get; private set; }
    public void SetTrustedCapabilities(AutoAssemblerCapability capabilities) => TrustedCapabilities = capabilities;
    public void AttachService(IAutoAssemblerService service) => _service = service;
    public void RegisterCommand(string name, Func<IReadOnlyList<string>, CancellationToken, Task<string>> command)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Command name is required.", nameof(name));
        _commands[name] = command ?? throw new ArgumentNullException(nameof(command));
    }

    public async Task<AutoAssemblerLuaResult> PreprocessAsync(string source, bool syntaxCheck, CancellationToken cancellationToken)
    {
        if (source.IndexOf("{$LUA}", StringComparison.OrdinalIgnoreCase) < 0)
            return new(true, source, IdentityMap(source));
        try
        {
            return await Task.Run(() => Process(source, syntaxCheck, cancellationToken), cancellationToken)
                .WaitAsync(MaximumExecutionTime, cancellationToken);
        }
        catch (TimeoutException) { return new(false, string.Empty, new Dictionary<int, int>(), "AA Lua exceeded the two-second execution limit."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, string.Empty, new Dictionary<int, int>(), "AA Lua failed: " + ex.Message); }
    }

    private AutoAssemblerLuaResult Process(string source, bool syntaxCheck, CancellationToken cancellationToken)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(Math.Min(source.Length, MaximumGeneratedCharacters));
        var map = new Dictionary<int, int>();
        int outputLine = 1;
        for (int index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!lines[index].Trim().Equals("{$LUA}", StringComparison.OrdinalIgnoreCase))
            {
                Append(lines[index], index + 1); continue;
            }
            int blockStart = index + 2;
            var block = new StringBuilder();
            while (++index < lines.Length && !lines[index].Trim().Equals("{$ASM}", StringComparison.OrdinalIgnoreCase))
                block.AppendLine(lines[index]);
            if (index >= lines.Length)
                return new(false, string.Empty, map, $"Unterminated {{$LUA}} block beginning at line {blockStart - 1}.");
            string generated = ExecuteBlock(block.ToString(), syntaxCheck, blockStart, cancellationToken);
            if (output.Length + generated.Length > MaximumGeneratedCharacters)
                return new(false, string.Empty, map, "AA Lua generated more than 1 MB of assembler text.");
            foreach (string generatedLine in generated.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                Append(generatedLine, blockStart);
        }
        return new(true, output.ToString(), map);

        void Append(string text, int sourceLine)
        {
            output.AppendLine(text); map[outputLine++] = sourceLine;
        }
    }

    private string ExecuteBlock(string code, bool syntaxCheck, int sourceLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lua = new Lua();
        // This state never calls LoadCLRPackage. Remove libraries that provide filesystem, module loading,
        // native loading, process execution, or introspection before user code can run.
        lua.DoString("local __aa_steps=0; debug.sethook(function() __aa_steps=__aa_steps+1; if __aa_steps>1000 then error('AA Lua instruction limit exceeded') end end,'',1000)");
        lua.DoString("os=nil; io=nil; package=nil; debug=nil; dofile=nil; loadfile=nil; require=nil; collectgarbage=nil");
        lua["syntaxcheck"] = syntaxCheck;
        var bridge = new LuaBridge(this, cancellationToken);
        lua.RegisterFunction("autoAssembleCheck", bridge, typeof(LuaBridge).GetMethod(nameof(LuaBridge.AutoAssembleCheck))!);
        lua.RegisterFunction("autoAssemble", bridge, typeof(LuaBridge).GetMethod(nameof(LuaBridge.AutoAssemble))!);
        lua.RegisterFunction("aaCommand", bridge, typeof(LuaBridge).GetMethod(nameof(LuaBridge.Command))!);
        object[] results = lua.DoString(code, $"AA Lua line {sourceLine}");
        if (results.Length == 0 || results[0] == null) return string.Empty;
        return results[0] as string ?? throw new InvalidOperationException("{$LUA} must return assembler source text or nil.");
    }

    private static IReadOnlyDictionary<int, int> IdentityMap(string source)
    {
        int count = source.Count(value => value == '\n') + 1;
        return Enumerable.Range(1, count).ToDictionary(line => line, line => line);
    }

    private sealed class LuaBridge
    {
        private readonly RestrictedAutoAssemblerLuaHost _host;
        private readonly CancellationToken _cancellationToken;
        public LuaBridge(RestrictedAutoAssemblerLuaHost host, CancellationToken cancellationToken)
        { _host = host; _cancellationToken = cancellationToken; }

        public bool AutoAssembleCheck(string script)
        {
            if (_host._service == null || Interlocked.Increment(ref _host._callbackDepth) > 3)
            { Interlocked.Decrement(ref _host._callbackDepth); return false; }
            try { return _host._service.CheckAsync(script, true, _cancellationToken).GetAwaiter().GetResult().Succeeded; }
            finally { Interlocked.Decrement(ref _host._callbackDepth); }
        }

        public bool AutoAssemble(string script)
        {
            if ((_host.TrustedCapabilities & AutoAssemblerCapability.ProcessMutation) == 0 || _host._service == null) return false;
            return _host._service.EnableAsync(script, _cancellationToken).GetAwaiter().GetResult().Succeeded;
        }

        public string Command(string name, params object[] args)
        {
            if ((_host.TrustedCapabilities & AutoAssemblerCapability.HostCallbacks) == 0 || !_host._commands.TryGetValue(name, out var command))
                throw new InvalidOperationException($"AA command '{name}' is not registered or trusted.");
            return command(args.Select(value => value?.ToString() ?? string.Empty).ToArray(), _cancellationToken).GetAwaiter().GetResult();
        }
    }
}
