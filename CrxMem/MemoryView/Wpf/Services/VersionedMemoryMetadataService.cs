using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record PersistedMemoryMetadataEntry(
    ModuleIdentity ModuleIdentity, ulong ModuleRelativeAddress, string? Comment, bool IsBookmarked,
    string? Label = null, DateTimeOffset? CreatedAt = null);

public sealed class PersistedMemoryMetadataDocument
{
    public int Version { get; set; } = 1;
    public List<PersistedMemoryMetadataEntry> Entries { get; set; } = new();
}

public sealed class PersistedMemoryAnnotationDocument
{
    public int Version { get; set; } = 2;
    public List<MemoryAnnotationRecord> Entries { get; set; } = new();
}

public sealed class VersionedMemoryMetadataService : IEnumerableMemoryMetadataService
{
    private readonly IModuleService _modules;
    private readonly IModuleIdentityService _identities;
    private readonly string _path;
    private readonly List<MemoryAnnotationRecord> _entries = new();
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly bool? _is64Bit;

    public VersionedMemoryMetadataService(IModuleService modules, IModuleIdentityService identities,
        string? path = null, bool migrateLegacy = true, bool? is64Bit = null)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _is64Bit = is64Bit;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrxMem", "MemoryView.metadata.v2.json");
        Load();
        if (migrateLegacy) _ = Task.Run(MigrateLegacyRecords);
    }

    public event EventHandler<MemoryAnnotationsChangedEventArgs>? Changed;

    public string? GetComment(MemoryLocation location) => FindResolved(location)?.Record.Comment;
    public bool HasBookmark(MemoryLocation location) => FindResolved(location)?.Record.IsBookmarked == true;

    public bool SetComment(MemoryLocation location, string? comment)
    {
        EditableMemoryMetadata current = GetMetadata(location);
        return SetMetadata(location, current with { Comment = comment }).Succeeded;
    }

    public bool SetBookmark(MemoryLocation location, bool isBookmarked, string? label = null)
    {
        EditableMemoryMetadata current = GetMetadata(location);
        return SetMetadata(location, current with { IsBookmarked = isBookmarked, Label = label ?? current.Label }).Succeeded;
    }

    public EditableMemoryMetadata GetMetadata(MemoryLocation location)
    {
        MemoryAnnotationRecord? entry = FindResolved(location)?.Record;
        return entry == null ? new(null, false) : new(entry.Comment, entry.IsBookmarked, entry.Label);
    }

    public MetadataMutationResult SetMetadata(MemoryLocation location, EditableMemoryMetadata metadata)
    {
        ResolvedMemoryAnnotation? existing = FindResolved(location);
        AnnotationMutationResult result = existing == null
            ? Create(location, new(metadata.Label, metadata.Comment, null, Array.Empty<string>(), null, false, metadata.IsBookmarked))
            : Update(existing.Id, new(metadata.Label, metadata.Comment, existing.Record.Group, existing.Record.Tags,
                existing.Record.Color, existing.Record.IsPinned, metadata.IsBookmarked));
        if (result.Succeeded) return MetadataMutationResult.Success(location);
        MetadataMutationError error = result.Error switch
        {
            AnnotationMutationError.StaleIdentity => MetadataMutationError.StaleLocation,
            AnnotationMutationError.Persistence => MetadataMutationError.PersistenceFailed,
            AnnotationMutationError.InvalidAddress => MetadataMutationError.UnsupportedAddress,
            _ => MetadataMutationError.UnsupportedAddress
        };
        return MetadataMutationResult.Failure(location, error, result.Message);
    }

    public IReadOnlyList<ResolvedMemoryAnnotation> GetAllAnnotations()
    {
        lock (_sync) return _entries.Select(Resolve).ToArray();
    }

    public ResolvedMemoryAnnotation? GetAnnotation(MemoryAnnotationId id)
    {
        lock (_sync)
        {
            MemoryAnnotationRecord? entry = _entries.FirstOrDefault(item => item.Id == id);
            return entry == null ? null : Resolve(entry);
        }
    }

    public ResolvedMemoryAnnotation? GetAnnotation(MemoryLocation location) => FindResolved(location);

    public AnnotationMutationResult Create(MemoryLocation location, MemoryAnnotationEdit edit)
    {
        if (!TryCreateRecord(location, edit, out MemoryAnnotationRecord? record, out string? error) || record == null)
            return AnnotationMutationResult.Failure(AnnotationMutationError.InvalidAddress, error ?? "Invalid annotation address.");
        lock (_sync)
        {
            ResolvedMemoryAnnotation? conflict = FindResolvedLocked(location);
            if (conflict != null) return AnnotationMutationResult.Failure(AnnotationMutationError.Conflict,
                "An annotation already exists at that address.", conflict);
            _entries.Add(record);
            if (!TrySaveLocked(out error)) { _entries.Remove(record); return PersistenceFailure(error); }
        }
        OnChanged(record.Id);
        return AnnotationMutationResult.Success(Resolve(record));
    }

    public AnnotationMutationResult Update(MemoryAnnotationId id, MemoryAnnotationEdit edit)
    {
        MemoryAnnotationRecord updated;
        lock (_sync)
        {
            int index = _entries.FindIndex(item => item.Id == id);
            if (index < 0) return AnnotationMutationResult.Failure(AnnotationMutationError.NotFound, "Annotation no longer exists.");
            MemoryAnnotationRecord previous = _entries[index];
            updated = previous with
            {
                Label = Clean(edit.Label), Comment = Clean(edit.Comment), Group = Clean(edit.Group),
                Tags = NormalizeTags(edit.Tags), Color = NormalizeColor(edit.Color), IsPinned = edit.IsPinned,
                IsBookmarked = edit.IsBookmarked, UpdatedAt = DateTimeOffset.UtcNow
            };
            if (!updated.IsBookmarked && string.IsNullOrWhiteSpace(updated.Comment)) _entries.RemoveAt(index);
            else _entries[index] = updated;
            if (!TrySaveLocked(out string? error))
            {
                _entries.RemoveAll(item => item.Id == id);
                _entries.Insert(Math.Min(index, _entries.Count), previous);
                return PersistenceFailure(error);
            }
        }
        OnChanged(id);
        return AnnotationMutationResult.Success(Resolve(updated));
    }

    public AnnotationMutationResult Move(MemoryAnnotationId id, MemoryLocation destination, AnnotationConflictResolution resolution)
    {
        MemoryAnnotationRecord? source;
        MemoryAnnotationRecord? result;
        List<MemoryAnnotationRecord> backup;
        lock (_sync)
        {
            source = _entries.FirstOrDefault(item => item.Id == id);
            if (source == null) return AnnotationMutationResult.Failure(AnnotationMutationError.NotFound, "Annotation no longer exists.");
            if (!TryLocation(destination, source, out MemoryAnnotationRecord? relocated, out string? error) || relocated == null)
                return AnnotationMutationResult.Failure(AnnotationMutationError.InvalidAddress, error ?? "Invalid destination.");
            ResolvedMemoryAnnotation? conflict = FindResolvedLocked(destination);
            if (conflict != null && conflict.Id != id && resolution == AnnotationConflictResolution.Cancel)
                return AnnotationMutationResult.Failure(AnnotationMutationError.Conflict,
                    "The destination already contains an annotation.", conflict);

            backup = _entries.ToList();
            _entries.RemoveAll(item => item.Id == id);
            if (conflict != null && conflict.Id != id)
            {
                _entries.RemoveAll(item => item.Id == conflict.Id);
                result = resolution == AnnotationConflictResolution.Merge
                    ? Merge(source, conflict.Record, relocated)
                    : relocated;
            }
            else result = relocated;
            _entries.Add(result);
            if (!TrySaveLocked(out error)) { _entries.Clear(); _entries.AddRange(backup); return PersistenceFailure(error); }
        }
        OnChanged(id);
        return AnnotationMutationResult.Success(Resolve(result), "Annotation address updated.");
    }

    public AnnotationMutationResult Delete(IReadOnlyCollection<MemoryAnnotationId> ids)
    {
        if (ids.Count == 0) return AnnotationMutationResult.Success(null, "Nothing selected.");
        List<MemoryAnnotationRecord> removed;
        lock (_sync)
        {
            removed = _entries.Where(item => ids.Contains(item.Id)).ToList();
            _entries.RemoveAll(item => ids.Contains(item.Id));
            if (!TrySaveLocked(out string? error)) { _entries.AddRange(removed); return PersistenceFailure(error); }
        }
        OnChanged(ids);
        return AnnotationMutationResult.Success(null, $"Deleted {removed.Count} annotation(s).");
    }

    public AnnotationMutationResult RebindAbsolute(MemoryAnnotationId id, ulong address)
    {
        MemoryAnnotationRecord updated;
        lock (_sync)
        {
            int index = _entries.FindIndex(item => item.Id == id);
            if (index < 0) return AnnotationMutationResult.Failure(AnnotationMutationError.NotFound, "Annotation no longer exists.");
            MemoryAnnotationRecord previous = _entries[index];
            if (previous.LocationKind != MemoryAnnotationLocationKind.Absolute)
                return AnnotationMutationResult.Failure(AnnotationMutationError.InvalidAddress, "Only absolute annotations can be rebound.");
            ModuleIdentity? executable = CurrentExecutableIdentity();
            if (executable == null) return AnnotationMutationResult.Failure(AnnotationMutationError.StaleIdentity,
                "The selected executable could not be fingerprinted.");
            updated = previous with { ExecutableIdentity = executable, Is64Bit = CurrentIs64Bit(), AbsoluteAddress = address, UpdatedAt = DateTimeOffset.UtcNow };
            _entries[index] = updated;
            if (!TrySaveLocked(out string? error)) { _entries[index] = previous; return PersistenceFailure(error); }
        }
        OnChanged(id);
        return AnnotationMutationResult.Success(Resolve(updated), "Absolute annotation rebound to the selected executable.");
    }

    public AnnotationMutationResult BulkUpdate(IReadOnlyCollection<MemoryAnnotationId> ids, MemoryAnnotationBulkEdit edit)
    {
        List<MemoryAnnotationRecord> backup;
        lock (_sync)
        {
            backup = _entries.ToList();
            for (int i = 0; i < _entries.Count; i++)
            {
                MemoryAnnotationRecord current = _entries[i];
                if (!ids.Contains(current.Id)) continue;
                _entries[i] = current with
                {
                    Group = edit.Group == null ? current.Group : Clean(edit.Group),
                    Tags = edit.AddTags == null ? current.Tags : NormalizeTags(current.Tags.Concat(edit.AddTags)),
                    Color = edit.Color == null ? current.Color : NormalizeColor(edit.Color),
                    IsPinned = edit.IsPinned ?? current.IsPinned,
                    IsBookmarked = edit.IsBookmarked ?? current.IsBookmarked,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            if (!TrySaveLocked(out string? error)) { _entries.Clear(); _entries.AddRange(backup); return PersistenceFailure(error); }
        }
        OnChanged(ids);
        return AnnotationMutationResult.Success(null, $"Updated {ids.Count} annotation(s).");
    }

    public string ExportJson(IReadOnlyCollection<MemoryAnnotationId>? ids = null)
    {
        lock (_sync)
        {
            List<MemoryAnnotationRecord> selected = ids == null ? _entries.ToList() : _entries.Where(x => ids.Contains(x.Id)).ToList();
            return JsonSerializer.Serialize(new PersistedMemoryAnnotationDocument { Entries = selected }, _json);
        }
    }

    public string ExportCsv(IReadOnlyCollection<MemoryAnnotationId>? ids = null)
    {
        var text = new StringBuilder("Id,Type,Expression,ResolvedAddress,State,Label,Comment,Group,Tags,Color,Pinned,Created,Updated\r\n");
        IEnumerable<ResolvedMemoryAnnotation> rows = GetAllAnnotations();
        if (ids != null) rows = rows.Where(row => ids.Contains(row.Id));
        foreach (ResolvedMemoryAnnotation row in rows)
        {
            MemoryAnnotationRecord r = row.Record;
            text.AppendLine(string.Join(',', AnnotationCsv.Escape(r.Id.ToString()), r.LocationKind,
                AnnotationCsv.Escape(row.Expression), row.RuntimeAddress?.ToString("X") ?? string.Empty, row.State,
                AnnotationCsv.Escape(r.Label), AnnotationCsv.Escape(r.Comment), AnnotationCsv.Escape(r.Group),
                AnnotationCsv.Escape(AnnotationCsv.JoinTags(r.Tags)), AnnotationCsv.Escape(r.Color), r.IsPinned,
                r.CreatedAt.ToString("O"), r.UpdatedAt.ToString("O")));
        }
        return text.ToString();
    }

    public AnnotationMutationResult ImportJson(string json, AnnotationConflictResolution resolution)
    {
        List<MemoryAnnotationRecord>? incoming;
        try
        {
            using JsonDocument probe = JsonDocument.Parse(json);
            if (probe.RootElement.ValueKind == JsonValueKind.Array)
            {
                List<Bookmark>? legacy = JsonSerializer.Deserialize<List<Bookmark>>(json, _json);
                incoming = legacy?.Select(TryConvertLegacyImport).Where(item => item != null).Cast<MemoryAnnotationRecord>().ToList();
            }
            else
            {
                PersistedMemoryAnnotationDocument? document = JsonSerializer.Deserialize<PersistedMemoryAnnotationDocument>(json, _json);
                incoming = document?.Version == 2 ? document.Entries : null;
            }
            if (incoming == null) return AnnotationMutationResult.Failure(AnnotationMutationError.UnsupportedExpression,
                "The file is neither a CrxMem annotation schema v2 document nor a legacy bookmarks.json file.");
        }
        catch (Exception ex) { return AnnotationMutationResult.Failure(AnnotationMutationError.UnsupportedExpression, ex.Message); }

        List<MemoryAnnotationRecord> backup;
        var changed = new List<MemoryAnnotationId>();
        lock (_sync)
        {
            backup = _entries.ToList();
            foreach (MemoryAnnotationRecord raw in incoming)
            {
                MemoryAnnotationRecord item = Sanitize(raw with { Id = raw.Id.Value == Guid.Empty ? MemoryAnnotationId.New() : raw.Id });
                int index = _entries.FindIndex(existing => SamePersistedLocation(existing, item));
                if (index >= 0)
                {
                    if (resolution == AnnotationConflictResolution.Cancel)
                    {
                        _entries.Clear(); _entries.AddRange(backup);
                        return AnnotationMutationResult.Failure(AnnotationMutationError.Conflict,
                            $"Import conflicts with {_entries[index].Id}.", Resolve(_entries[index]));
                    }
                    if (resolution == AnnotationConflictResolution.Merge) item = Merge(item, _entries[index], item);
                    _entries.RemoveAt(index);
                }
                _entries.Add(item); changed.Add(item.Id);
            }
            if (!TrySaveLocked(out string? error)) { _entries.Clear(); _entries.AddRange(backup); return PersistenceFailure(error); }
        }
        OnChanged(changed);
        return AnnotationMutationResult.Success(null, $"Imported {incoming.Count} annotation(s).");
    }

    private MemoryAnnotationRecord? TryConvertLegacyImport(Bookmark bookmark)
    {
        ulong address = unchecked((ulong)bookmark.Address);
        MemoryLocation location = MemoryLocation.Create(address, _modules.FindByAddress(address));
        string expression = bookmark.OriginalAddressString ?? string.Empty;
        int plus = expression.IndexOf('+');
        if (plus > 0 && _modules.FindByName(expression[..plus].Trim()) is { } module &&
            ulong.TryParse(expression[(plus + 1)..].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong rva))
            location = MemoryLocation.Create(module.BaseAddress + rva, module);
        var edit = new MemoryAnnotationEdit(bookmark.Label, bookmark.Comment, null, Array.Empty<string>(), null, false, true);
        if (!TryCreateRecord(location, edit, out MemoryAnnotationRecord? record, out _) || record == null) return null;
        return record with { CreatedAt = new DateTimeOffset(bookmark.Created), UpdatedAt = new DateTimeOffset(bookmark.Created) };
    }

    private ResolvedMemoryAnnotation? FindResolved(MemoryLocation location)
    {
        lock (_sync) return FindResolvedLocked(location);
    }

    private ResolvedMemoryAnnotation? FindResolvedLocked(MemoryLocation location)
    {
        if (location.ModuleName != null && location.RelativeVirtualAddress.HasValue)
        {
            ModuleIdentity? current = _identities.GetCurrent(location.ModuleName);
            if (current == null) return null;
            MemoryAnnotationRecord? moduleEntry = _entries.FirstOrDefault(entry =>
                entry.LocationKind == MemoryAnnotationLocationKind.ModuleRelative &&
                entry.ModuleRelativeAddress == location.RelativeVirtualAddress && entry.ModuleIdentity != null &&
                SameIdentity(entry.ModuleIdentity, current));
            return moduleEntry == null ? null : Resolve(moduleEntry);
        }
        MemoryAnnotationRecord? absoluteEntry = _entries.FirstOrDefault(entry =>
            entry.LocationKind == MemoryAnnotationLocationKind.Absolute && entry.AbsoluteAddress == location.RuntimeAddress);
        if (absoluteEntry == null) return null;
        ResolvedMemoryAnnotation resolved = Resolve(absoluteEntry);
        return resolved.State == MemoryAnnotationResolutionState.Resolved ? resolved : null;
    }

    private ResolvedMemoryAnnotation Resolve(MemoryAnnotationRecord entry)
    {
        if (entry.LocationKind == MemoryAnnotationLocationKind.ModuleRelative && entry.ModuleIdentity != null && entry.ModuleRelativeAddress.HasValue)
        {
            ModuleIdentity? current = _identities.GetCurrent(entry.ModuleIdentity.Name);
            if (current == null || !SameIdentity(entry.ModuleIdentity, current))
                return new(entry, null, $"{entry.ModuleIdentity.Name}+{entry.ModuleRelativeAddress.Value:X}",
                    MemoryAnnotationResolutionState.StaleModule,
                    $"Module '{entry.ModuleIdentity.Name}' does not match the persisted binary fingerprint.");
            ModuleIdentityResolution resolution = _identities.Resolve(entry.ModuleIdentity, entry.ModuleRelativeAddress.Value);
            return new(entry, resolution.RuntimeAddress,
                $"{entry.ModuleIdentity.Name}+{entry.ModuleRelativeAddress.Value:X}",
                resolution.IsMatch ? MemoryAnnotationResolutionState.Resolved : MemoryAnnotationResolutionState.StaleModule,
                resolution.Reason);
        }

        string expression = entry.AbsoluteAddress?.ToString("X") ?? "—";
        ModuleIdentity? currentExecutable = CurrentExecutableIdentity();
        if (entry.ExecutableIdentity == null || currentExecutable == null)
            return new(entry, null, expression, MemoryAnnotationResolutionState.Unavailable,
                "The selected executable identity is unavailable.");
        if (!SameIdentity(entry.ExecutableIdentity, currentExecutable) || entry.Is64Bit != CurrentIs64Bit())
            return new(entry, null, expression, MemoryAnnotationResolutionState.StaleExecutable,
                "This absolute address belongs to a different executable build or architecture. Rebind it explicitly before navigating.");
        return new(entry, entry.AbsoluteAddress, expression, MemoryAnnotationResolutionState.Resolved,
            "Executable identity verified. Absolute addresses do not follow ASLR.");
    }

    private bool TryCreateRecord(MemoryLocation location, MemoryAnnotationEdit edit,
        out MemoryAnnotationRecord? record, out string? error)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var seed = new MemoryAnnotationRecord(MemoryAnnotationId.New(), MemoryAnnotationLocationKind.Absolute,
            null, null, null, CurrentIs64Bit(), null, Clean(edit.Label), Clean(edit.Comment), Clean(edit.Group),
            NormalizeTags(edit.Tags), NormalizeColor(edit.Color), edit.IsPinned, edit.IsBookmarked, now, now);
        return TryLocation(location, seed, out record, out error);
    }

    private bool TryLocation(MemoryLocation location, MemoryAnnotationRecord seed,
        out MemoryAnnotationRecord? record, out string? error)
    {
        record = null;
        error = null;
        ModuleDescriptor? module = location.ModuleName == null ? _modules.FindByAddress(location.RuntimeAddress) : _modules.FindByName(location.ModuleName);
        if (module != null)
        {
            ulong rva = location.RelativeVirtualAddress ?? location.RuntimeAddress - module.BaseAddress;
            ModuleIdentity? identity = _identities.GetCurrent(module.Name);
            if (identity == null) { error = $"Module '{module.Name}' could not be fingerprinted."; return false; }
            if (rva >= identity.ImageSize) { error = "Address lies outside the current module image."; return false; }
            record = seed with { LocationKind = MemoryAnnotationLocationKind.ModuleRelative, ModuleIdentity = identity,
                ModuleRelativeAddress = rva, ExecutableIdentity = null, AbsoluteAddress = null, UpdatedAt = DateTimeOffset.UtcNow };
            return true;
        }

        ModuleIdentity? executable = CurrentExecutableIdentity();
        if (executable == null) { error = "The selected executable could not be fingerprinted for an absolute bookmark."; return false; }
        record = seed with { LocationKind = MemoryAnnotationLocationKind.Absolute, ModuleIdentity = null,
            ModuleRelativeAddress = null, ExecutableIdentity = executable, Is64Bit = CurrentIs64Bit(),
            AbsoluteAddress = location.RuntimeAddress, UpdatedAt = DateTimeOffset.UtcNow };
        return true;
    }

    private ModuleIdentity? CurrentExecutableIdentity()
    {
        ModuleDescriptor? main = _modules.FindByAddress(_modules.GetDefaultAddress()) ?? _modules.GetModules().FirstOrDefault();
        return main == null ? null : _identities.GetCurrent(main.Name);
    }

    private bool CurrentIs64Bit() => _is64Bit ?? _modules.GetModules().Any(module => module.BaseAddress > uint.MaxValue);

    private static MemoryAnnotationRecord Merge(MemoryAnnotationRecord moving, MemoryAnnotationRecord destination,
        MemoryAnnotationRecord relocated)
    {
        return relocated with
        {
            Id = moving.Id,
            Label = First(moving.Label, destination.Label), Comment = First(moving.Comment, destination.Comment),
            Group = First(moving.Group, destination.Group), Color = First(moving.Color, destination.Color),
            Tags = NormalizeTags(moving.Tags.Concat(destination.Tags)),
            IsPinned = moving.IsPinned || destination.IsPinned,
            IsBookmarked = moving.IsBookmarked || destination.IsBookmarked,
            CreatedAt = moving.CreatedAt < destination.CreatedAt ? moving.CreatedAt : destination.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                using JsonDocument probe = JsonDocument.Parse(json);
                int version = probe.RootElement.TryGetProperty("Version", out JsonElement element) ? element.GetInt32() : 1;
                if (version == 2)
                {
                    PersistedMemoryAnnotationDocument? document = JsonSerializer.Deserialize<PersistedMemoryAnnotationDocument>(json, _json);
                    if (document?.Entries != null) _entries.AddRange(document.Entries.Select(Sanitize).Where(IsValid));
                    return;
                }
                PersistedMemoryMetadataDocument? old = JsonSerializer.Deserialize<PersistedMemoryMetadataDocument>(json, _json);
                if (old?.Entries != null) _entries.AddRange(old.Entries.Where(IsValidV1).Select(FromV1));
                return;
            }
            string v1 = Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, "MemoryView.metadata.v1.json");
            if (!v1.Equals(_path, StringComparison.OrdinalIgnoreCase) && File.Exists(v1))
            {
                PersistedMemoryMetadataDocument? old = JsonSerializer.Deserialize<PersistedMemoryMetadataDocument>(File.ReadAllText(v1), _json);
                if (old?.Entries != null) _entries.AddRange(old.Entries.Where(IsValidV1).Select(FromV1));
                TrySaveLocked(out _);
            }
        }
        catch { _entries.Clear(); }
    }

    private void MigrateLegacyRecords()
    {
        var changed = new List<MemoryAnnotationId>();
        lock (_sync)
        {
            foreach ((long runtimeAddress, string comment) in CommentManager.GetAll())
                MergeLegacy(unchecked((ulong)runtimeAddress), comment, false, null, null, changed);
            foreach (Bookmark bookmark in BookmarkManager.GetAll())
                MergeLegacy(unchecked((ulong)bookmark.Address), bookmark.Comment, true, bookmark.Label, bookmark.Created, changed,
                    bookmark.OriginalAddressString);
            if (changed.Count > 0) TrySaveLocked(out _);
        }
        if (changed.Count > 0) OnChanged(changed);
    }

    private void MergeLegacy(ulong address, string? comment, bool bookmarked, string? label, DateTime? created,
        ICollection<MemoryAnnotationId> changed, string? expression = null)
    {
        MemoryLocation location = MemoryLocation.Create(address, _modules.FindByAddress(address));
        if (expression?.Contains('+') == true)
        {
            int plus = expression.IndexOf('+');
            if (ulong.TryParse(expression[(plus + 1)..].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong rva) && _modules.FindByName(expression[..plus].Trim()) is { } module)
                location = MemoryLocation.Create(module.BaseAddress + rva, module);
        }
        ResolvedMemoryAnnotation? existing = FindResolvedLocked(location);
        if (existing != null)
        {
            int index = _entries.FindIndex(x => x.Id == existing.Id);
            _entries[index] = existing.Record with
            {
                Comment = First(existing.Record.Comment, comment), Label = First(existing.Record.Label, label),
                IsBookmarked = existing.Record.IsBookmarked || bookmarked
            };
            changed.Add(existing.Id);
            return;
        }
        var edit = new MemoryAnnotationEdit(label, comment, null, Array.Empty<string>(), null, false, bookmarked);
        if (TryCreateRecord(location, edit, out MemoryAnnotationRecord? record, out _) && record != null)
        {
            if (created.HasValue) record = record with { CreatedAt = new DateTimeOffset(created.Value) };
            _entries.Add(record); changed.Add(record.Id);
        }
    }

    private bool TrySaveLocked(out string? error)
    {
        try
        {
            string directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Metadata path has no directory.");
            Directory.CreateDirectory(directory);
            string temporary = _path + ".tmp";
            string backup = _path + ".bak";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PersistedMemoryAnnotationDocument { Entries = _entries.ToList() }, _json));
            if (File.Exists(_path)) File.Copy(_path, backup, true);
            File.Move(temporary, _path, true);
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static AnnotationMutationResult PersistenceFailure(string? error) =>
        AnnotationMutationResult.Failure(AnnotationMutationError.Persistence, error ?? "Annotations could not be written to disk.");
    private void OnChanged(MemoryAnnotationId id) => OnChanged([id]);
    private void OnChanged(IEnumerable<MemoryAnnotationId> ids) => Changed?.Invoke(this, new MemoryAnnotationsChangedEventArgs(ids));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? First(string? preferred, string? fallback) => Clean(preferred) ?? Clean(fallback);
    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) => tags == null ? Array.Empty<string>() :
        tags.SelectMany(tag => tag.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)).Select(x => x.Trim())
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    private static string? NormalizeColor(string? color)
    {
        color = Clean(color);
        if (color == null) return null;
        if (!color.StartsWith('#')) color = "#" + color;
        return color.Length is 7 or 9 && color.Skip(1).All(char.IsAsciiHexDigit) ? color.ToUpperInvariant() : null;
    }
    private static bool SameIdentity(ModuleIdentity a, ModuleIdentity b) => a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) &&
        a.ImageSize == b.ImageSize && a.FileLength == b.FileLength && a.Sha256.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase);
    private static bool SamePersistedLocation(MemoryAnnotationRecord a, MemoryAnnotationRecord b) =>
        a.LocationKind == b.LocationKind && (a.LocationKind == MemoryAnnotationLocationKind.ModuleRelative
            ? a.ModuleRelativeAddress == b.ModuleRelativeAddress && a.ModuleIdentity != null && b.ModuleIdentity != null && SameIdentity(a.ModuleIdentity, b.ModuleIdentity)
            : a.AbsoluteAddress == b.AbsoluteAddress && a.ExecutableIdentity != null && b.ExecutableIdentity != null && SameIdentity(a.ExecutableIdentity, b.ExecutableIdentity));
    private static MemoryAnnotationRecord FromV1(PersistedMemoryMetadataEntry old)
    {
        DateTimeOffset created = old.CreatedAt ?? DateTimeOffset.UtcNow;
        return new(MemoryAnnotationId.New(), MemoryAnnotationLocationKind.ModuleRelative, old.ModuleIdentity,
            old.ModuleRelativeAddress, null, false, null, old.Label, old.Comment, null, Array.Empty<string>(),
            null, false, old.IsBookmarked, created, created);
    }
    private static MemoryAnnotationRecord Sanitize(MemoryAnnotationRecord item) => item with
    {
        Label = Clean(item.Label), Comment = Clean(item.Comment), Group = Clean(item.Group),
        Tags = NormalizeTags(item.Tags), Color = NormalizeColor(item.Color),
        CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
        UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt
    };
    private static bool IsValid(MemoryAnnotationRecord item) => item.Id.Value != Guid.Empty &&
        (!string.IsNullOrWhiteSpace(item.Comment) || item.IsBookmarked) &&
        (item.LocationKind == MemoryAnnotationLocationKind.ModuleRelative
            ? item.ModuleIdentity != null && item.ModuleRelativeAddress < item.ModuleIdentity.ImageSize
            : item.ExecutableIdentity != null && item.AbsoluteAddress.HasValue);
    private static bool IsValidV1(PersistedMemoryMetadataEntry entry) => entry.ModuleIdentity != null &&
        !string.IsNullOrWhiteSpace(entry.ModuleIdentity.Name) && entry.ModuleRelativeAddress < entry.ModuleIdentity.ImageSize &&
        (!string.IsNullOrWhiteSpace(entry.Comment) || entry.IsBookmarked);
}
