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
    public int SelectedToolTab { get; set; }
    public int SelectedBreakpointTab { get; set; }
    public bool InspectorVisible { get; set; } = true;
    public bool PatchEditorVisible { get; set; } = true;
    public double CodeFontSize { get; set; } = 12;
    public double RowHeight { get; set; } = 21;
    public Dictionary<string, List<double>> ColumnWidths { get; set; } = new();
    public List<string> RecentExpressions { get; set; } = new();
    public List<string> RecentHexExpressions { get; set; } = new();
    public bool MicrosoftSymbolServerEnabled { get; set; }
    public bool BookmarkManagerSelected { get; set; }
    public string BookmarkSearch { get; set; } = string.Empty;
    public int BookmarkTypeFilter { get; set; }
    public int BookmarkStateFilter { get; set; }
    public bool BookmarkPinnedFirst { get; set; }
    public string BookmarkGroupFilter { get; set; } = string.Empty;
    public string BookmarkTagFilter { get; set; } = string.Empty;
    public string BookmarkColorFilter { get; set; } = string.Empty;
    public List<double> BookmarkColumnWidths { get; set; } = new();
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
            settings.ColumnWidths ??= new Dictionary<string, List<double>>();
            settings.RecentHexExpressions = (settings.RecentHexExpressions ?? new List<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
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
