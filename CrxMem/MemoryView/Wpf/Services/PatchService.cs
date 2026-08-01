using System.Runtime.InteropServices;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public enum PatchStatus
{
    Applied,
    Restored,
    Rejected,
    Stale,
    Cancelled,
    Failed
}

public enum PatchFailureStage
{
    None,
    Validation,
    ReadOriginal,
    ChangeProtection,
    Write,
    RestoreProtection,
    FlushInstructionCache,
    VerifyReadBack,
    Rollback
}

public sealed record PatchRequest(
    ulong Address,
    byte[] ExpectedOriginalBytes,
    byte[] ReplacementBytes,
    bool PadWithNops = false,
    string? Description = null,
    string? ModuleName = null,
    ulong? ModuleRelativeAddress = null,
    ModuleIdentity? ModuleIdentity = null);

public sealed record PatchRecord(
    Guid Id,
    ulong Address,
    byte[] OriginalBytes,
    byte[] PatchedBytes,
    string? Description,
    string? ModuleName,
    ulong? ModuleRelativeAddress,
    DateTimeOffset CreatedAt,
    bool IsApplied,
    bool IsStale,
    ModuleIdentity? ModuleIdentity = null);

public sealed record PatchResult(
    PatchStatus Status,
    PatchFailureStage FailureStage,
    string Message,
    PatchRecord? Record = null,
    bool? RollbackSucceeded = null)
{
    public bool Succeeded => Status is PatchStatus.Applied or PatchStatus.Restored;
}

public interface IPatchMemoryAdapter
{
    bool IsAvailable { get; }
    byte[]? Read(ulong address, int size);
    bool ChangeProtection(ulong address, int size, uint protection, out uint previousProtection);
    bool Write(ulong address, byte[] bytes);
    bool FlushInstructionCache(ulong address, int size);
    void InvalidateReadCache();
}

public interface IPatchService
{
    IReadOnlyList<PatchRecord> History { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    Task<PatchResult> ApplyAsync(PatchRequest request, CancellationToken cancellationToken = default);
    Task<PatchResult> RestoreAsync(Guid patchId, CancellationToken cancellationToken = default);
    Task<PatchResult> ReapplyAsync(Guid patchId, CancellationToken cancellationToken = default);
    Task<PatchResult> UndoAsync(CancellationToken cancellationToken = default);
    Task<PatchResult> RedoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the given entries from the patch history WITHOUT touching target memory.
    /// Returns the number of entries removed.
    /// </summary>
    int RemoveFromHistory(IReadOnlyCollection<Guid> patchIds);

    /// <summary>
    /// Clears the entire patch history (including the persisted store) WITHOUT touching
    /// target memory. Returns the number of entries removed.
    /// </summary>
    int ClearHistory();
}

public sealed class ProcessPatchMemoryAdapter : IPatchMemoryAdapter
{
    private readonly ProcessAccess _process;

    public ProcessPatchMemoryAdapter(ProcessAccess process) =>
        _process = process ?? throw new ArgumentNullException(nameof(process));

    public bool IsAvailable
    {
        get
        {
            try { return _process.IsOpen && _process.Target is { HasExited: false }; }
            catch { return false; }
        }
    }

    public byte[]? Read(ulong address, int size)
    {
        _process.InvalidateReadCache();
        return _process.Read(new IntPtr(unchecked((long)address)), size);
    }

    public bool ChangeProtection(ulong address, int size, uint protection, out uint previousProtection) =>
        _process.ProtectMemory(new IntPtr(unchecked((long)address)), checked((uint)size), protection, out previousProtection);

    public bool Write(ulong address, byte[] bytes) =>
        _process.Write(new IntPtr(unchecked((long)address)), bytes);

    public bool FlushInstructionCache(ulong address, int size) =>
        NativeMethods.FlushInstructionCache(
            _process.Handle,
            new IntPtr(unchecked((long)address)),
            checked((UIntPtr)(uint)size));

    public void InvalidateReadCache() => _process.InvalidateReadCache();

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, UIntPtr size);
    }
}

public sealed class PatchService : IPatchService, IDisposable
{
    internal const uint PageExecuteReadWrite = 0x40;

    private readonly IPatchMemoryAdapter _memory;
    private readonly IPatchHistoryStore? _historyStore;
    private readonly IModuleIdentityService? _moduleIdentity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<PatchRecord> _history = new();
    private readonly Stack<Guid> _redo = new();

    public PatchService(
        IPatchMemoryAdapter memory,
        IPatchHistoryStore? historyStore = null,
        IModuleIdentityService? moduleIdentity = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _historyStore = historyStore;
        _moduleIdentity = moduleIdentity;
        LoadPersistedHistory();
    }

    public IReadOnlyList<PatchRecord> History => _history.ToArray();
    public bool CanUndo => _history.Any(item => item.IsApplied);
    public bool CanRedo => _redo.Count > 0;

    public async Task<PatchResult> ApplyAsync(PatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[]? patch = ValidateAndBuildPatch(request, out string? error);
        if (patch == null)
            return Failed(PatchStatus.Rejected, PatchFailureStage.Validation, error!);

        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return Failed(PatchStatus.Cancelled, PatchFailureStage.None, "The patch operation was cancelled.");
        }
        try
        {
            PatchResult result = ExecuteTransaction(
                request.Address,
                request.ExpectedOriginalBytes,
                patch,
                PatchStatus.Applied,
                cancellationToken);
            if (!result.Succeeded) return result;

            ModuleIdentity? identity = request.ModuleIdentity ??
                (request.ModuleName == null ? null : _moduleIdentity?.GetCurrent(request.ModuleName));
            var record = new PatchRecord(
                Guid.NewGuid(), request.Address, request.ExpectedOriginalBytes.ToArray(), patch,
                request.Description, request.ModuleName, request.ModuleRelativeAddress,
                DateTimeOffset.UtcNow, true, false, identity);
            _history.Add(record);
            _redo.Clear();
            SavePersistedHistory();
            return result with { Record = record };
        }
        catch (OperationCanceledException)
        {
            return Failed(PatchStatus.Cancelled, PatchFailureStage.None, "The patch operation was cancelled.");
        }
        finally { _gate.Release(); }
    }

    public Task<PatchResult> RestoreAsync(Guid patchId, CancellationToken cancellationToken = default) =>
        SetAppliedStateAsync(patchId, false, addToRedo: false, cancellationToken);

    public Task<PatchResult> ReapplyAsync(Guid patchId, CancellationToken cancellationToken = default) =>
        SetAppliedStateAsync(patchId, true, addToRedo: false, cancellationToken);

    public Task<PatchResult> UndoAsync(CancellationToken cancellationToken = default)
    {
        PatchRecord? record = _history.LastOrDefault(item => item.IsApplied);
        return record == null
            ? Task.FromResult(Failed(PatchStatus.Rejected, PatchFailureStage.Validation, "There is no applied patch to undo."))
            : SetAppliedStateAsync(record.Id, false, addToRedo: true, cancellationToken);
    }

    public Task<PatchResult> RedoAsync(CancellationToken cancellationToken = default)
    {
        if (_redo.Count == 0)
            return Task.FromResult(Failed(PatchStatus.Rejected, PatchFailureStage.Validation, "There is no patch to redo."));
        return SetAppliedStateAsync(_redo.Peek(), true, addToRedo: false, cancellationToken);
    }

    private async Task<PatchResult> SetAppliedStateAsync(
        Guid patchId,
        bool apply,
        bool addToRedo,
        CancellationToken cancellationToken)
    {
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            return Failed(PatchStatus.Cancelled, PatchFailureStage.None, "The patch operation was cancelled.");
        }
        try
        {
            int index = _history.FindIndex(item => item.Id == patchId);
            if (index < 0)
                return Failed(PatchStatus.Rejected, PatchFailureStage.Validation, "The patch history entry no longer exists.");

            PatchRecord record = _history[index];
            if (record.IsApplied == apply)
                return Failed(PatchStatus.Rejected, PatchFailureStage.Validation,
                    apply ? "The patch is already applied." : "The patch is already restored.");

            byte[] expected = apply ? record.OriginalBytes : record.PatchedBytes;
            byte[] replacement = apply ? record.PatchedBytes : record.OriginalBytes;
            PatchResult result = ExecuteTransaction(
                record.Address, expected, replacement,
                apply ? PatchStatus.Applied : PatchStatus.Restored,
                cancellationToken);

            if (!result.Succeeded)
            {
                if (result.Status == PatchStatus.Stale)
                {
                    _history[index] = record with { IsStale = true };
                    SavePersistedHistory();
                }
                return result with { Record = _history[index] };
            }

            PatchRecord updated = record with { IsApplied = apply, IsStale = false };
            _history[index] = updated;
            if (apply)
            {
                if (_redo.Count > 0 && _redo.Peek() == patchId) _redo.Pop();
            }
            else if (addToRedo)
            {
                _redo.Push(patchId);
            }
            SavePersistedHistory();
            return result with { Record = updated };
        }
        catch (OperationCanceledException)
        {
            return Failed(PatchStatus.Cancelled, PatchFailureStage.None, "The patch operation was cancelled.");
        }
        finally { _gate.Release(); }
    }

    public int RemoveFromHistory(IReadOnlyCollection<Guid> patchIds)
    {
        ArgumentNullException.ThrowIfNull(patchIds);
        if (patchIds.Count == 0) return 0;
        _gate.Wait();
        try
        {
            var ids = new HashSet<Guid>(patchIds);
            int removed = _history.RemoveAll(item => ids.Contains(item.Id));
            if (removed > 0)
            {
                // Purge redo entries that point at deleted records (Stack<T> enumerates top-first).
                if (_redo.Count > 0)
                {
                    Guid[] kept = _redo.Where(id => !ids.Contains(id)).ToArray();
                    _redo.Clear();
                    foreach (Guid id in kept.Reverse())
                        _redo.Push(id);
                }
                SavePersistedHistory();
            }
            return removed;
        }
        finally { _gate.Release(); }
    }

    public int ClearHistory()
    {
        _gate.Wait();
        try
        {
            int removed = _history.Count;
            _history.Clear();
            _redo.Clear();
            SavePersistedHistory();
            return removed;
        }
        finally { _gate.Release(); }
    }

    private void LoadPersistedHistory()
    {
        if (_historyStore == null || _moduleIdentity == null) return;
        foreach (PersistedPatchEntry persisted in _historyStore.Load())
        {
            ModuleIdentityResolution resolution = _moduleIdentity.Resolve(
                persisted.ModuleIdentity, persisted.ModuleRelativeAddress);
            ulong address = resolution.RuntimeAddress ?? 0;
            bool applied = false;
            bool stale = !resolution.IsMatch || address == 0;
            if (!stale && _memory.IsAvailable)
            {
                try
                {
                    byte[]? current = _memory.Read(address, persisted.OriginalBytes.Length);
                    if (current?.SequenceEqual(persisted.PatchedBytes) == true) applied = true;
                    else if (current?.SequenceEqual(persisted.OriginalBytes) != true) stale = true;
                }
                catch { stale = true; }
            }
            else if (!stale)
            {
                stale = true;
            }

            _history.Add(new PatchRecord(
                persisted.Id, address, persisted.OriginalBytes, persisted.PatchedBytes,
                persisted.Description, persisted.ModuleIdentity.Name, persisted.ModuleRelativeAddress,
                persisted.CreatedAt, applied, stale, persisted.ModuleIdentity));
        }
    }

    private void SavePersistedHistory()
    {
        if (_historyStore == null) return;
        try
        {
            PersistedPatchEntry[] entries = _history
                .Where(record => record.ModuleIdentity != null && record.ModuleRelativeAddress.HasValue)
                .Select(record => new PersistedPatchEntry(
                    record.Id, record.ModuleIdentity!, record.ModuleRelativeAddress!.Value,
                    record.OriginalBytes, record.PatchedBytes, record.Description, record.CreatedAt))
                .ToArray();
            _historyStore.Save(entries);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PatchService] Patch history persistence failed: {ex.Message}");
        }
    }

    private PatchResult ExecuteTransaction(
        ulong address,
        byte[] expected,
        byte[] replacement,
        PatchStatus successStatus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_memory.IsAvailable)
            return Failed(PatchStatus.Failed, PatchFailureStage.ReadOriginal, "The target process is unavailable.");

        byte[]? current;
        try { current = _memory.Read(address, expected.Length); }
        catch (Exception ex)
        {
            return Failed(PatchStatus.Failed, PatchFailureStage.ReadOriginal, $"Reading original bytes failed: {ex.Message}");
        }
        if (current == null || current.Length != expected.Length)
            return Failed(PatchStatus.Failed, PatchFailureStage.ReadOriginal, "Could not read all expected bytes.");
        if (!current.AsSpan().SequenceEqual(expected))
            return Failed(PatchStatus.Stale, PatchFailureStage.ReadOriginal, "The target bytes no longer match the expected bytes.");

        cancellationToken.ThrowIfCancellationRequested();
        uint oldProtection;
        try
        {
            if (!_memory.ChangeProtection(address, replacement.Length, PageExecuteReadWrite, out oldProtection))
                return Failed(PatchStatus.Failed, PatchFailureStage.ChangeProtection, "Could not enable temporary write access.");
        }
        catch (Exception ex)
        {
            return Failed(PatchStatus.Failed, PatchFailureStage.ChangeProtection, $"Changing memory protection failed: {ex.Message}");
        }

        bool wrote = false;
        bool restored = false;
        string? writeError = null;
        string? restoreError = null;
        try
        {
            wrote = _memory.Write(address, replacement);
        }
        catch (Exception ex) { writeError = ex.Message; }
        finally
        {
            try { restored = _memory.ChangeProtection(address, replacement.Length, oldProtection, out _); }
            catch (Exception ex) { restoreError = ex.Message; }
        }

        if (!restored)
            return PostWriteFailure(address, expected, replacement, oldProtection,
                PatchFailureStage.RestoreProtection,
                restoreError == null ? "The original memory protection could not be restored." : $"Restoring memory protection failed: {restoreError}");

        if (!wrote)
            return PostWriteFailure(address, expected, replacement, oldProtection,
                PatchFailureStage.Write,
                writeError == null ? "The target process rejected all or part of the write." : $"Writing target memory failed: {writeError}");

        bool flushed;
        try { flushed = _memory.FlushInstructionCache(address, replacement.Length); }
        catch { flushed = false; }
        if (!flushed)
            return PostWriteFailure(address, expected, replacement, oldProtection,
                PatchFailureStage.FlushInstructionCache, "The target instruction cache could not be flushed.");

        byte[]? verified;
        try
        {
            _memory.InvalidateReadCache();
            verified = _memory.Read(address, replacement.Length);
        }
        catch { verified = null; }
        if (verified == null || !verified.AsSpan().SequenceEqual(replacement))
            return PostWriteFailure(address, expected, replacement, oldProtection,
                PatchFailureStage.VerifyReadBack, "Read-back verification failed.");

        return new PatchResult(successStatus, PatchFailureStage.None,
            successStatus == PatchStatus.Applied ? "Patch applied and verified." : "Original bytes restored and verified.");
    }

    private PatchResult PostWriteFailure(
        ulong address,
        byte[] original,
        byte[] written,
        uint originalProtection,
        PatchFailureStage stage,
        string message)
    {
        bool rollback = TryRollback(address, original, written.Length, originalProtection);
        return new PatchResult(PatchStatus.Failed, rollback ? stage : PatchFailureStage.Rollback,
            rollback ? $"{message} The write was rolled back." : $"{message} Automatic rollback also failed.",
            RollbackSucceeded: rollback);
    }

    private bool TryRollback(ulong address, byte[] original, int size, uint finalProtection)
    {
        try
        {
            if (!_memory.ChangeProtection(address, size, PageExecuteReadWrite, out _)) return false;
            bool wrote = false;
            bool restored = false;
            try { wrote = _memory.Write(address, original); }
            finally
            {
                try { restored = _memory.ChangeProtection(address, size, finalProtection, out _); }
                catch { restored = false; }
            }
            if (!wrote || !restored || !_memory.FlushInstructionCache(address, size)) return false;
            _memory.InvalidateReadCache();
            byte[]? verified = _memory.Read(address, original.Length);
            return verified != null && verified.AsSpan().SequenceEqual(original);
        }
        catch { return false; }
    }

    private static byte[]? ValidateAndBuildPatch(PatchRequest request, out string? error)
    {
        error = null;
        if (request.Address == 0) error = "A non-zero target address is required.";
        else if (request.ExpectedOriginalBytes is not { Length: > 0 }) error = "Expected original bytes are required.";
        else if (request.ReplacementBytes is not { Length: > 0 }) error = "Replacement bytes are required.";
        else if (request.ReplacementBytes.Length > request.ExpectedOriginalBytes.Length) error = "A replacement cannot be longer than the original instruction bytes.";
        else if (request.ReplacementBytes.Length < request.ExpectedOriginalBytes.Length && !request.PadWithNops) error = "A shorter replacement requires explicit NOP padding.";
        if (error != null) return null;

        byte[] result = Enumerable.Repeat((byte)0x90, request.ExpectedOriginalBytes.Length).ToArray();
        request.ReplacementBytes.CopyTo(result, 0);
        return result;
    }

    private static PatchResult Failed(PatchStatus status, PatchFailureStage stage, string message) =>
        new(status, stage, message);

    public void Dispose() => _gate.Dispose();
}
