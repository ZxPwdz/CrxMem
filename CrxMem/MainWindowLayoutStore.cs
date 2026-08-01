using System.Text.Json;
using System.IO;

namespace CrxMem;

internal sealed class MainWindowLayoutSettings
{
    public int SchemaVersion { get; set; } = 3;
    public double WindowWidth { get; set; } = 1152;
    public double WindowHeight { get; set; } = 768;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool Maximized { get; set; }
    public double AddressListHeight { get; set; } = 190;
}

internal sealed class MainWindowLayoutStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal MainWindowLayoutStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrxMem", "MainWindow.layout.json");
    }

    internal MainWindowLayoutSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new MainWindowLayoutSettings();
            MainWindowLayoutSettings? settings = JsonSerializer.Deserialize<MainWindowLayoutSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (settings == null || settings.SchemaVersion is not (1 or 2 or 3)) return new MainWindowLayoutSettings();

            if (settings.SchemaVersion == 1)
            {
                bool usesPreviousDefaultBounds =
                    NearlyEqual(settings.WindowWidth, 1280) &&
                    NearlyEqual(settings.WindowHeight, 860);
                if (usesPreviousDefaultBounds)
                {
                    settings.WindowWidth = 1152;
                    settings.WindowHeight = 768;
                }
                if (NearlyEqual(settings.AddressListHeight, 215))
                    settings.AddressListHeight = 190;
                settings.SchemaVersion = 3;
            }
            else if (settings.SchemaVersion == 2)
            {
                // Schema v2 could retain the previous tall layout and oversized Address List,
                // making the compact theme appear unchanged. Normalize it once while keeping
                // the saved position and maximized state.
                settings.WindowWidth = Math.Min(settings.WindowWidth, 1152);
                settings.WindowHeight = Math.Min(settings.WindowHeight, 768);
                settings.AddressListHeight = Math.Min(settings.AddressListHeight, 190);
                settings.SchemaVersion = 3;
            }

            if (!IsFinite(settings.WindowWidth) || settings.WindowWidth < 960) settings.WindowWidth = 1152;
            if (!IsFinite(settings.WindowHeight) || settings.WindowHeight < 640) settings.WindowHeight = 768;
            if (!IsFinite(settings.AddressListHeight)) settings.AddressListHeight = 190;
            settings.AddressListHeight = Math.Clamp(settings.AddressListHeight, 145, 520);
            if (settings.WindowLeft is double left && !IsFinite(left)) settings.WindowLeft = null;
            if (settings.WindowTop is double top && !IsFinite(top)) settings.WindowTop = null;
            return settings;
        }
        catch
        {
            return new MainWindowLayoutSettings();
        }
    }

    internal void Save(MainWindowLayoutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.5;
}
