using System.Globalization;
using System.IO;
using System.Text.Json;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record PersistedMemoryMetadataEntry(
    ModuleIdentity ModuleIdentity,
    ulong ModuleRelativeAddress,
    string? Comment,
    bool IsBookmarked,
    string? Label = null,
    DateTimeOffset? CreatedAt = null);

public sealed class PersistedMemoryMetadataDocument
{
    public int Version { get; set; } = 1;
    public List<PersistedMemoryMetadataEntry> Entries { get; set; } = new();
}

public sealed class VersionedMemoryMetadataService : IMemoryMetadataService
{
    private readonly IModuleService _modules;
    private readonly IModuleIdentityService _identities;
    private readonly string _path;
    private readonly List<PersistedMemoryMetadataEntry> _entries = new();
    private readonly object _sync = new();

    public VersionedMemoryMetadataService(
        IModuleService modules,
        IModuleIdentityService identities,
        string? path = null,
        bool migrateLegacy = true)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrxMem", "MemoryView.metadata.v1.json");
        Load();
        if (migrateLegacy) _ = Task.Run(MigrateLegacyRecords);
    }

    public string? GetComment(MemoryLocation location)
    {
        PersistedMemoryMetadataEntry? entry = Find(location);
        if (entry != null) return entry.Comment;
        return location.ModuleName != null && _identities.GetCurrent(location.ModuleName) != null
            ? null
            : CommentManager.Get(unchecked((long)location.RuntimeAddress));
    }

    public bool HasBookmark(MemoryLocation location)
    {
        PersistedMemoryMetadataEntry? entry = Find(location);
        if (entry != null) return entry.IsBookmarked;
        return location.ModuleName != null && _identities.GetCurrent(location.ModuleName) != null
            ? false
            : BookmarkManager.Exists(unchecked((long)location.RuntimeAddress));
    }

    public bool SetComment(MemoryLocation location, string? comment) =>
        Upsert(location, comment, null, null);

    public bool SetBookmark(MemoryLocation location, bool isBookmarked, string? label = null) =>
        Upsert(location, null, isBookmarked, label);

    private PersistedMemoryMetadataEntry? Find(MemoryLocation location)
    {
        if (location.ModuleName == null || !location.RelativeVirtualAddress.HasValue) return null;
        ModuleIdentity? current = _identities.GetCurrent(location.ModuleName);
        if (current == null) return null;
        lock (_sync)
            return _entries.FirstOrDefault(entry => SameLocation(entry, current, location.RelativeVirtualAddress.Value));
    }

    private bool Upsert(MemoryLocation location, string? comment, bool? bookmark, string? label)
    {
        if (location.ModuleName == null || !location.RelativeVirtualAddress.HasValue) return false;
        ModuleIdentity? identity = _identities.GetCurrent(location.ModuleName);
        if (identity == null) return false;
        lock (_sync)
        {
            int index = _entries.FindIndex(entry => SameLocation(entry, identity, location.RelativeVirtualAddress.Value));
            PersistedMemoryMetadataEntry current = index >= 0
                ? _entries[index]
                : new(identity, location.RelativeVirtualAddress.Value, null, false);
            var updated = current with
            {
                Comment = comment ?? current.Comment,
                IsBookmarked = bookmark ?? current.IsBookmarked,
                Label = label ?? current.Label,
                CreatedAt = current.CreatedAt ?? DateTimeOffset.UtcNow
            };
            if (string.IsNullOrWhiteSpace(updated.Comment) && !updated.IsBookmarked)
            {
                if (index >= 0) _entries.RemoveAt(index);
            }
            else if (index >= 0) _entries[index] = updated;
            else _entries.Add(updated);
            return TrySave();
        }
    }

    private void MigrateLegacyRecords()
    {
        bool changed = false;
        lock (_sync)
        {
            foreach ((long runtimeAddress, string comment) in CommentManager.GetAll())
            {
                ulong address = unchecked((ulong)runtimeAddress);
                ModuleDescriptor? module = _modules.FindByAddress(address);
                if (module == null) continue;
                ModuleIdentity? identity = _identities.GetCurrent(module.Name);
                if (identity == null) continue;
                changed |= MergeLegacy(identity, address - module.BaseAddress, comment, false, null, null);
            }

            foreach (Bookmark bookmark in BookmarkManager.GetAll())
            {
                if (!TryResolveLegacyBookmark(bookmark, out ModuleDescriptor? module, out ulong rva) || module == null) continue;
                ModuleIdentity? identity = _identities.GetCurrent(module.Name);
                if (identity == null) continue;
                changed |= MergeLegacy(identity, rva, bookmark.Comment, true, bookmark.Label, bookmark.Created);
            }
            if (changed) TrySave();
        }
    }

    private bool MergeLegacy(ModuleIdentity identity, ulong rva, string? comment, bool bookmarked, string? label, DateTime? created)
    {
        int index = _entries.FindIndex(entry => SameLocation(entry, identity, rva));
        if (index < 0)
        {
            _entries.Add(new(identity, rva, comment, bookmarked, label,
                created.HasValue ? new DateTimeOffset(created.Value) : DateTimeOffset.UtcNow));
            return true;
        }
        PersistedMemoryMetadataEntry existing = _entries[index];
        var merged = existing with
        {
            Comment = string.IsNullOrWhiteSpace(existing.Comment) ? comment : existing.Comment,
            IsBookmarked = existing.IsBookmarked || bookmarked,
            Label = string.IsNullOrWhiteSpace(existing.Label) ? label : existing.Label
        };
        if (merged == existing) return false;
        _entries[index] = merged;
        return true;
    }

    private bool TryResolveLegacyBookmark(Bookmark bookmark, out ModuleDescriptor? module, out ulong rva)
    {
        module = null;
        rva = 0;
        string source = bookmark.OriginalAddressString ?? string.Empty;
        int plus = source.IndexOf('+');
        if (plus > 0 && ulong.TryParse(source[(plus + 1)..].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rva))
        {
            module = _modules.FindByName(source[..plus].Trim());
            return module != null && rva < module.Size;
        }
        ulong address = unchecked((ulong)bookmark.Address);
        module = _modules.FindByAddress(address);
        if (module == null) return false;
        rva = address - module.BaseAddress;
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            PersistedMemoryMetadataDocument? document = JsonSerializer.Deserialize<PersistedMemoryMetadataDocument>(File.ReadAllText(_path));
            if (document?.Version == 1 && document.Entries != null)
                _entries.AddRange(document.Entries.Where(IsValid));
        }
        catch
        {
            _entries.Clear();
        }
    }

    private void Save()
    {
        string directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Metadata path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(
            new PersistedMemoryMetadataDocument { Entries = _entries.ToList() },
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }

    private bool TrySave()
    {
        try
        {
            Save();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemoryMetadata] Persistence failed: {ex.Message}");
            return false;
        }
    }

    private static bool SameLocation(PersistedMemoryMetadataEntry entry, ModuleIdentity identity, ulong rva) =>
        entry.ModuleRelativeAddress == rva && entry.ModuleIdentity.Name.Equals(identity.Name, StringComparison.OrdinalIgnoreCase) &&
        entry.ModuleIdentity.ImageSize == identity.ImageSize && entry.ModuleIdentity.Sha256.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsValid(PersistedMemoryMetadataEntry entry) =>
        entry.ModuleIdentity != null && !string.IsNullOrWhiteSpace(entry.ModuleIdentity.Name) &&
        entry.ModuleRelativeAddress < entry.ModuleIdentity.ImageSize &&
        (!string.IsNullOrWhiteSpace(entry.Comment) || entry.IsBookmarked);
}
