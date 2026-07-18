using System.IO;
using System.Text.Json;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record PersistedPatchEntry(
    Guid Id,
    ModuleIdentity ModuleIdentity,
    ulong ModuleRelativeAddress,
    byte[] OriginalBytes,
    byte[] PatchedBytes,
    string? Description,
    DateTimeOffset CreatedAt);

public sealed class PersistedPatchDocument
{
    public int Version { get; set; } = 1;
    public List<PersistedPatchEntry> Entries { get; set; } = new();
}

public interface IPatchHistoryStore
{
    IReadOnlyList<PersistedPatchEntry> Load();
    void Save(IReadOnlyList<PersistedPatchEntry> entries);
}

public sealed class JsonPatchHistoryStore : IPatchHistoryStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public JsonPatchHistoryStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrxMem", "MemoryView.patches.v1.json");

    public IReadOnlyList<PersistedPatchEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<PersistedPatchEntry>();
            PersistedPatchDocument? document = JsonSerializer.Deserialize<PersistedPatchDocument>(File.ReadAllText(_path), Options);
            return document?.Version == 1 && document.Entries != null
                ? document.Entries.Where(IsValid).ToArray()
                : Array.Empty<PersistedPatchEntry>();
        }
        catch
        {
            return Array.Empty<PersistedPatchEntry>();
        }
    }

    public void Save(IReadOnlyList<PersistedPatchEntry> entries)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Patch-history path has no directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(new PersistedPatchDocument { Entries = entries.Where(IsValid).ToList() }, Options);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, true);
    }

    private static bool IsValid(PersistedPatchEntry entry) =>
        entry.Id != Guid.Empty && entry.ModuleIdentity != null &&
        !string.IsNullOrWhiteSpace(entry.ModuleIdentity.Name) &&
        entry.OriginalBytes is { Length: > 0 } && entry.PatchedBytes is { Length: > 0 } &&
        entry.OriginalBytes.Length == entry.PatchedBytes.Length;
}
