using System.Text.Json;
using System.IO;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed class MemoryViewLayoutSettings
{
    public int SchemaVersion { get; set; } = 1;
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 800;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double InspectorWidth { get; set; } = 350;
    public double BottomToolsHeight { get; set; } = 255;
    public double PatchEditorWidth { get; set; } = 350;
    public double RegisterPanelHeight { get; set; } = 420;
    public List<string> RecentExpressions { get; set; } = new();
}

public interface IMemoryViewLayoutStore
{
    MemoryViewLayoutSettings Load();
    void Save(MemoryViewLayoutSettings settings);
}

public sealed class MemoryViewLayoutStore : IMemoryViewLayoutStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MemoryViewLayoutStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrxMem", "MemoryView.layout.json");
    }

    public MemoryViewLayoutSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new MemoryViewLayoutSettings();
            MemoryViewLayoutSettings? settings = JsonSerializer.Deserialize<MemoryViewLayoutSettings>(
                File.ReadAllText(_filePath), JsonOptions);
            if (settings?.SchemaVersion != 1) return new MemoryViewLayoutSettings();
            settings.RecentExpressions = settings.RecentExpressions
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();
            return settings;
        }
        catch
        {
            return new MemoryViewLayoutSettings();
        }
    }

    public void Save(MemoryViewLayoutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
