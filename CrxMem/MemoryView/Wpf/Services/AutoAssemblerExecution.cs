using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed class ProcessNearAllocationPlanner : INearAllocationPlanner
{
    private const uint MemFree = 0x10000;
    private readonly ProcessAccess _process;
    public ProcessNearAllocationPlanner(ProcessAccess process) => _process = process;

    public Task<AutoAssemblerAllocation?> ProposeAsync(string name, uint size, uint protection, ulong? nearAddress,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ulong granularity = 0x10000;
        uint roundedSize = checked((uint)(((ulong)size + 0xFFF) & ~0xFFFUL));
        IEnumerable<MemoryRegion> free = _process.GetMemoryRegions().Where(region => region.State == MemFree && region.Size >= roundedSize);
        if (nearAddress.HasValue)
            free = free.OrderBy(region => Distance(unchecked((ulong)region.BaseAddress.ToInt64()), nearAddress.Value));
        foreach (MemoryRegion region in free)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong start = (unchecked((ulong)region.BaseAddress.ToInt64()) + granularity - 1) & ~(granularity - 1);
            if (start == 0) continue;
            if (start + roundedSize > unchecked((ulong)region.BaseAddress.ToInt64()) + unchecked((ulong)region.Size)) continue;
            if (nearAddress.HasValue && Distance(start, nearAddress.Value) > int.MaxValue) continue;
            return new AutoAssemblerAllocation(name, start, roundedSize, protection, nearAddress);
        }
        return null;
    }, cancellationToken);

    private static ulong Distance(ulong left, ulong right) => left >= right ? left - right : right - left;
}

public sealed class AutoAssemblerTransactionExecutor : IAutoAssemblerTransactionExecutor
{
    private readonly ProcessAccess _process;
    private readonly IPatchService _patches;
    private readonly IAutoAssemblerSessionStore _sessions;

    public AutoAssemblerTransactionExecutor(ProcessAccess process, IPatchService patches, IAutoAssemblerSessionStore sessions)
    { _process = process; _patches = patches; _sessions = sessions; }

    public async Task<AutoAssemblerExecutionResult> EnableAsync(AutoAssemblerPlan plan, CancellationToken cancellationToken)
    {
        string? identityError = ValidateIdentity(plan);
        if (identityError != null) return new(false, identityError);
        if (!plan.CanApply) return new(false, "The Auto Assembler plan contains errors and cannot be applied.");
        if (_sessions.Get(plan.ScriptHash) is { IsEnabled: true }) return new(false, "This script is already enabled for the current process instance.");

        var allocations = new List<AutoAssemblerAllocation>();
        var applied = new List<PatchRecord>();
        try
        {
            foreach (AutoAssemblerAllocation proposal in plan.Allocations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr allocated = _process.AllocateMemoryAt(new IntPtr(unchecked((long)proposal.Address)), proposal.Size, proposal.Protection);
                if (allocated == IntPtr.Zero || unchecked((ulong)allocated.ToInt64()) != proposal.Address)
                    throw new InvalidOperationException($"Previewed allocation {proposal.Name} at 0x{proposal.Address:X} is no longer available. Refresh the preview.");
                allocations.Add(proposal);
            }
            foreach (AutoAssemblerAssertion assertion in plan.Assertions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? current = _process.Read(new IntPtr(unchecked((long)assertion.Address)), assertion.ExpectedBytes.Length);
                if (current?.SequenceEqual(assertion.ExpectedBytes) != true)
                    throw new InvalidOperationException($"Assertion failed at 0x{assertion.Address:X}. No writes were made.");
            }
            foreach (AutoAssemblerWrite write in plan.Writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? original = _process.Read(new IntPtr(unchecked((long)write.Address)), write.Bytes.Length);
                if (original == null || original.Length != write.Bytes.Length)
                    throw new InvalidOperationException($"Cannot read original bytes at 0x{write.Address:X}.");
                PatchResult result = await _patches.ApplyAsync(new PatchRequest(write.Address, original, write.Bytes,
                    Description: "AA: " + write.Description), cancellationToken);
                if (!result.Succeeded || result.Record == null) throw new InvalidOperationException(result.Message);
                applied.Add(result.Record);
            }
            var session = new AutoAssemblerSession(Guid.NewGuid(), plan.Id, plan.ScriptHash, plan.ProcessId,
                plan.ProcessStartTimeUtc, applied, allocations, plan.Symbols, true);
            _sessions.Set(session);
            return new(true, $"Enabled with {applied.Count} verified write block(s) and {allocations.Count} allocation(s).", session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bool rolledBack = await RollbackAsync(applied, allocations, CancellationToken.None);
            return new(false, ex.Message, null, true, rolledBack);
        }
        catch (OperationCanceledException)
        {
            bool rolledBack = await RollbackAsync(applied, allocations, CancellationToken.None);
            return new(false, "Auto Assembler enable was cancelled.", null, true, rolledBack);
        }
    }

    public async Task<AutoAssemblerExecutionResult> DisableAsync(AutoAssemblerPlan plan, AutoAssemblerSession session,
        CancellationToken cancellationToken)
    {
        string? identityError = ValidateIdentity(plan);
        if (identityError != null) return new(false, identityError, session);
        if (session.ProcessId != plan.ProcessId || session.ProcessStartTimeUtc != plan.ProcessStartTimeUtc || session.ScriptHash != plan.ScriptHash)
            return new(false, "The enabled AA session does not match this script and process instance.", session);
        var disablePatches = new List<PatchRecord>();
        try
        {
            foreach (PatchRecord patch in session.Patches.Reverse())
            {
                PatchResult restored = await _patches.RestoreAsync(patch.Id, cancellationToken);
                if (!restored.Succeeded) throw new InvalidOperationException(restored.Message);
            }
            foreach (AutoAssemblerWrite write in plan.Writes)
            {
                byte[]? original = _process.Read(new IntPtr(unchecked((long)write.Address)), write.Bytes.Length);
                if (original == null) throw new InvalidOperationException($"Cannot read disable target 0x{write.Address:X}.");
                PatchResult applied = await _patches.ApplyAsync(new PatchRequest(write.Address, original, write.Bytes,
                    Description: "AA disable: " + write.Description), cancellationToken);
                if (!applied.Succeeded || applied.Record == null) throw new InvalidOperationException(applied.Message);
                disablePatches.Add(applied.Record);
            }
            foreach (AutoAssemblerAllocation allocation in session.Allocations.Reverse())
                if (!_process.FreeMemory(new IntPtr(unchecked((long)allocation.Address))))
                    throw new InvalidOperationException($"Could not release allocation {allocation.Name} at 0x{allocation.Address:X}.");
            _sessions.Remove(plan.ScriptHash);
            return new(true, "Disabled; original bytes, symbols, and allocations were released.", session with { IsEnabled = false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var recovery = session with { RecoveryError = ex.Message };
            _sessions.Set(recovery);
            return new(false, "Disable is incomplete and recoverable: " + ex.Message, recovery);
        }
    }

    private string? ValidateIdentity(AutoAssemblerPlan plan)
    {
        if (!_process.IsOpen || _process.Target == null || _process.Target.HasExited) return "The selected process is unavailable.";
        if (_process.Target.Id != plan.ProcessId) return "The AA preview belongs to a different process.";
        DateTime start;
        try { start = _process.Target.StartTime.ToUniversalTime(); }
        catch { return "The process start time cannot be verified."; }
        if (start != plan.ProcessStartTimeUtc) return "The process restarted after this AA preview was compiled.";
        if ((_process.Is64Bit ? 64 : 32) != plan.Bitness) return "The process architecture does not match the AA plan.";
        return null;
    }

    private async Task<bool> RollbackAsync(IReadOnlyList<PatchRecord> patches, IReadOnlyList<AutoAssemblerAllocation> allocations,
        CancellationToken cancellationToken)
    {
        bool success = true;
        foreach (PatchRecord patch in patches.Reverse())
            success &= (await _patches.RestoreAsync(patch.Id, cancellationToken)).Succeeded;
        foreach (AutoAssemblerAllocation allocation in allocations.Reverse())
            success &= _process.FreeMemory(new IntPtr(unchecked((long)allocation.Address)));
        return success;
    }
}

public sealed class AutoAssemblerService : IAutoAssemblerService
{
    private readonly ProcessAccess _process;
    private readonly IAutoAssemblerCompiler _compiler;
    private readonly IAutoAssemblerTransactionExecutor _executor;
    private readonly IAutoAssemblerSessionStore _sessions;
    private readonly IAutoAssemblerLuaHost? _luaHost;
    public AutoAssemblerService(ProcessAccess process, IAutoAssemblerCompiler compiler,
        IAutoAssemblerTransactionExecutor executor, IAutoAssemblerSessionStore sessions,
        IAutoAssemblerLuaHost? luaHost = null)
    { _process = process; _compiler = compiler; _executor = executor; _sessions = sessions; _luaHost = luaHost; }

    public AutoAssemblerCapability TrustedLuaCapabilities => _luaHost?.TrustedCapabilities ?? AutoAssemblerCapability.None;
    public string LuaCapabilitySummary =>
        "AA Lua is isolated: CLR, os, io, package, debug, file loading, and native loading are disabled. " +
        $"Trusted callbacks: {TrustedLuaCapabilities}. Remote threads and DLL loading remain disabled.";
    public void SetTrustedLuaCapabilities(AutoAssemblerCapability capabilities) => _luaHost?.SetTrustedCapabilities(capabilities);
    public void RegisterLuaCommand(string name, Func<IReadOnlyList<string>, CancellationToken, Task<string>> command)
    {
        if (_luaHost == null) throw new InvalidOperationException("The restricted AA Lua host is unavailable.");
        _luaHost.RegisterCommand(name, command);
    }

    public Task<AutoAssemblerCompileResult> CheckAsync(string source, bool enable, CancellationToken cancellationToken)
        => CompileForCurrentProcessAsync(source, enable, syntaxCheck: true, cancellationToken);

    private Task<AutoAssemblerCompileResult> CompileForCurrentProcessAsync(string source, bool enable, bool syntaxCheck,
        CancellationToken cancellationToken)
    {
        if (!_process.IsOpen || _process.Target == null || _process.Target.HasExited)
            return Task.FromResult(new AutoAssemblerCompileResult(null,
                [new(AutoAssemblerSeverity.Error, "AA3001", "Select a live process first.", new(1, 1, 1))]));
        return _compiler.CompileAsync(new(source, enable, _process.Is64Bit ? 64 : 32, _process.Target.Id,
            _process.Target.StartTime.ToUniversalTime(), syntaxCheck), cancellationToken);
    }

    public async Task<AutoAssemblerExecutionResult> EnableAsync(string source, CancellationToken cancellationToken)
    {
        AutoAssemblerCompileResult result = await CompileForCurrentProcessAsync(source, true, syntaxCheck: false, cancellationToken);
        return result.Plan == null || !result.Succeeded
            ? new(false, FormatDiagnostics(result.Diagnostics))
            : await _executor.EnableAsync(result.Plan, cancellationToken);
    }

    public async Task<AutoAssemblerExecutionResult> DisableAsync(string source, CancellationToken cancellationToken)
    {
        AutoAssemblerCompileResult result = await CompileForCurrentProcessAsync(source, false, syntaxCheck: false, cancellationToken);
        if (result.Plan == null || !result.Succeeded) return new(false, FormatDiagnostics(result.Diagnostics));
        AutoAssemblerSession? session = _sessions.Get(result.Plan.ScriptHash);
        return session == null ? new(false, "This script has no enabled session for the current process.")
            : await _executor.DisableAsync(result.Plan, session, cancellationToken);
    }

    private static string FormatDiagnostics(IEnumerable<AutoAssemblerDiagnostic> diagnostics) => string.Join(Environment.NewLine,
        diagnostics.Where(item => item.Severity == AutoAssemblerSeverity.Error).Select(item => $"Line {item.Span.Line}: {item.Message}"));
}
