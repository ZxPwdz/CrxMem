using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CrxMem;

public sealed class CrxMemSettings
{
    public int Version { get; set; } = 1;
    public int UpdateInterval { get; set; } = 500;
    public int FreezeInterval { get; set; } = 100;
    public int FoundListUpdateInterval { get; set; } = 1000;
    public bool ShowValuesAsSigned { get; set; }
    public bool SimpleCopyPaste { get; set; }
    public bool SaveWindowPositions { get; set; } = true;
    public string AutoAttachProcess { get; set; } = string.Empty;
    public bool AlwaysAutoAttach { get; set; }
    public bool FastAutoAttach { get; set; } = true;
    public bool UseWmiProcessWatch { get; set; } = true;
    public int FastAutoAttachInterval { get; set; } = 1;
    public bool AutoLoadDriver { get; set; }
    public bool AutoInjectVeh { get; set; }
    public bool FastSuspendOnAttach { get; set; }
    public int SuspendInterval { get; set; }
    public int KernelModeUpdateMultiplier { get; set; } = 2;
    public int KernelCacheLifetimeMs { get; set; } = 50;
    public bool AskToClearListOnNewProcess { get; set; } = true;
    public bool AlwaysLaunchAsAdmin { get; set; }
    public bool DarkMode { get; set; } = true;
    public int ScanThreadPriority { get; set; } = 2;
    public int ScanBufferSize { get; set; } = 4096;
    public bool FastScanByDefault { get; set; }
    public bool PauseWhileScanning { get; set; }
    public bool ScanMemPrivate { get; set; } = true;
    public bool ScanMemImage { get; set; } = true;
    public bool ScanMemMapped { get; set; }
    public bool SkipPageNoCache { get; set; }
    public bool SkipPageWriteCombine { get; set; }
    public int HotkeySpeedhack { get; set; }
    public int HotkeyFreezeAll { get; set; }
    public int HotkeyUnfreezeAll { get; set; }
    public int HotkeyNewScan { get; set; }
    public int HotkeyNextScan { get; set; }
    public int HotkeyUndoScan { get; set; }
    public Dictionary<string, int> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _settingsFilePath;
    private readonly Func<CrxMemSettings?>? _legacyLoader;

    public AppSettingsStore(string settingsFilePath, Func<CrxMemSettings?>? legacyLoader = null)
    {
        _settingsFilePath = settingsFilePath;
        _legacyLoader = legacyLoader;
    }

    public CrxMemSettings Load()
    {
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                return Normalize(JsonSerializer.Deserialize<CrxMemSettings>(json, JsonOptions));
            }
            catch (JsonException)
            {
                PreserveInvalidSettingsFile();
            }
        }

        CrxMemSettings settings = Normalize(_legacyLoader?.Invoke());
        Save(settings);
        return settings;
    }

    public void Save(CrxMemSettings settings)
    {
        settings = Normalize(settings);
        string? directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _settingsFilePath + ".tmp";
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsFilePath, overwrite: true);
    }

    private void PreserveInvalidSettingsFile()
    {
        string directory = Path.GetDirectoryName(_settingsFilePath) ?? string.Empty;
        string fileName = $"settings.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json";
        File.Move(_settingsFilePath, Path.Combine(directory, fileName), overwrite: false);
    }

    private static CrxMemSettings Normalize(CrxMemSettings? settings)
    {
        settings ??= new CrxMemSettings();
        settings.Version = 1;
        settings.AutoAttachProcess ??= string.Empty;
        settings.Hotkeys = settings.Hotkeys == null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(settings.Hotkeys, StringComparer.OrdinalIgnoreCase);
        settings.UpdateInterval = Math.Clamp(settings.UpdateInterval, 50, 60_000);
        settings.FreezeInterval = Math.Clamp(settings.FreezeInterval, 10, 60_000);
        settings.FoundListUpdateInterval = Math.Clamp(settings.FoundListUpdateInterval, 50, 60_000);
        settings.FastAutoAttachInterval = Math.Clamp(settings.FastAutoAttachInterval, 1, 60_000);
        settings.SuspendInterval = Math.Clamp(settings.SuspendInterval, 0, 60_000);
        settings.KernelModeUpdateMultiplier = Math.Clamp(settings.KernelModeUpdateMultiplier, 1, 100);
        settings.KernelCacheLifetimeMs = Math.Clamp(settings.KernelCacheLifetimeMs, 0, 60_000);
        settings.ScanThreadPriority = Math.Clamp(settings.ScanThreadPriority, 0, 4);
        settings.ScanBufferSize = Math.Clamp(settings.ScanBufferSize, 1, 1_048_576);
        return settings;
    }
}

public static class SettingsManager
{
    private const string RegistryPath = @"Software\CrxMem";
    private static readonly object SyncRoot = new();
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrxMem",
        "settings.json");
    private static readonly AppSettingsStore Store = new(SettingsFilePath, LoadLegacySettings);
    private static readonly CrxMemSettings Settings = Store.Load();

    public static string FilePath => SettingsFilePath;

    public static int UpdateInterval { get => Settings.UpdateInterval; set => Settings.UpdateInterval = value; }
    public static int FreezeInterval { get => Settings.FreezeInterval; set => Settings.FreezeInterval = value; }
    public static int FoundListUpdateInterval { get => Settings.FoundListUpdateInterval; set => Settings.FoundListUpdateInterval = value; }
    public static bool ShowValuesAsSigned { get => Settings.ShowValuesAsSigned; set => Settings.ShowValuesAsSigned = value; }
    public static bool SimpleCopyPaste { get => Settings.SimpleCopyPaste; set => Settings.SimpleCopyPaste = value; }
    public static bool SaveWindowPositions { get => Settings.SaveWindowPositions; set => Settings.SaveWindowPositions = value; }
    public static string AutoAttachProcess { get => Settings.AutoAttachProcess; set => Settings.AutoAttachProcess = value ?? string.Empty; }
    public static bool AlwaysAutoAttach { get => Settings.AlwaysAutoAttach; set => Settings.AlwaysAutoAttach = value; }
    public static bool FastAutoAttach { get => Settings.FastAutoAttach; set => Settings.FastAutoAttach = value; }
    public static bool UseWmiProcessWatch { get => Settings.UseWmiProcessWatch; set => Settings.UseWmiProcessWatch = value; }
    public static int FastAutoAttachInterval { get => Settings.FastAutoAttachInterval; set => Settings.FastAutoAttachInterval = value; }
    public static bool AutoLoadDriver { get => Settings.AutoLoadDriver; set => Settings.AutoLoadDriver = value; }
    public static bool AutoInjectVEH { get => Settings.AutoInjectVeh; set => Settings.AutoInjectVeh = value; }
    public static bool FastSuspendOnAttach { get => Settings.FastSuspendOnAttach; set => Settings.FastSuspendOnAttach = value; }
    public static int SuspendInterval { get => Settings.SuspendInterval; set => Settings.SuspendInterval = value; }
    public static int KernelModeUpdateMultiplier { get => Settings.KernelModeUpdateMultiplier; set => Settings.KernelModeUpdateMultiplier = value; }
    public static int KernelCacheLifetimeMs { get => Settings.KernelCacheLifetimeMs; set => Settings.KernelCacheLifetimeMs = value; }
    public static bool AskToClearListOnNewProcess { get => Settings.AskToClearListOnNewProcess; set => Settings.AskToClearListOnNewProcess = value; }
    public static bool AlwaysLaunchAsAdmin { get => Settings.AlwaysLaunchAsAdmin; set => Settings.AlwaysLaunchAsAdmin = value; }
    public static bool DarkMode { get => Settings.DarkMode; set => Settings.DarkMode = value; }
    public static int ScanThreadPriority { get => Settings.ScanThreadPriority; set => Settings.ScanThreadPriority = value; }
    public static int ScanBufferSize { get => Settings.ScanBufferSize; set => Settings.ScanBufferSize = value; }
    public static bool FastScanByDefault { get => Settings.FastScanByDefault; set => Settings.FastScanByDefault = value; }
    public static bool PauseWhileScanning { get => Settings.PauseWhileScanning; set => Settings.PauseWhileScanning = value; }
    public static bool ScanMemPrivate { get => Settings.ScanMemPrivate; set => Settings.ScanMemPrivate = value; }
    public static bool ScanMemImage { get => Settings.ScanMemImage; set => Settings.ScanMemImage = value; }
    public static bool ScanMemMapped { get => Settings.ScanMemMapped; set => Settings.ScanMemMapped = value; }
    public static bool SkipPageNoCache { get => Settings.SkipPageNoCache; set => Settings.SkipPageNoCache = value; }
    public static bool SkipPageWriteCombine { get => Settings.SkipPageWriteCombine; set => Settings.SkipPageWriteCombine = value; }
    public static int HotkeySpeedhack { get => Settings.HotkeySpeedhack; set => Settings.HotkeySpeedhack = value; }
    public static int HotkeyFreezeAll { get => Settings.HotkeyFreezeAll; set => Settings.HotkeyFreezeAll = value; }
    public static int HotkeyUnfreezeAll { get => Settings.HotkeyUnfreezeAll; set => Settings.HotkeyUnfreezeAll = value; }
    public static int HotkeyNewScan { get => Settings.HotkeyNewScan; set => Settings.HotkeyNewScan = value; }
    public static int HotkeyNextScan { get => Settings.HotkeyNextScan; set => Settings.HotkeyNextScan = value; }
    public static int HotkeyUndoScan { get => Settings.HotkeyUndoScan; set => Settings.HotkeyUndoScan = value; }

    public static Dictionary<string, int> GetAllHotkeys()
    {
        lock (SyncRoot)
        {
            return new Dictionary<string, int>(Settings.Hotkeys, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SetAllHotkeys(Dictionary<string, int> hotkeys)
    {
        lock (SyncRoot)
        {
            Settings.Hotkeys = new Dictionary<string, int>(hotkeys, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static int GetHotkey(string functionId)
    {
        lock (SyncRoot)
        {
            return Settings.Hotkeys.TryGetValue(functionId, out int value) ? value : 0;
        }
    }

    public static void Save()
    {
        lock (SyncRoot)
        {
            Store.Save(Settings);
        }
    }

    private static CrxMemSettings? LoadLegacySettings()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            using RegistryKey? hotkeyKey = Registry.CurrentUser.OpenSubKey(RegistryPath + @"\Hotkeys");
            if (key == null && hotkeyKey == null)
            {
                return null;
            }

            var defaults = new CrxMemSettings();
            var migrated = new CrxMemSettings
            {
                UpdateInterval = ReadInt(key, nameof(UpdateInterval), defaults.UpdateInterval),
                FreezeInterval = ReadInt(key, nameof(FreezeInterval), defaults.FreezeInterval),
                FoundListUpdateInterval = ReadInt(key, nameof(FoundListUpdateInterval), defaults.FoundListUpdateInterval),
                ShowValuesAsSigned = ReadBool(key, nameof(ShowValuesAsSigned), defaults.ShowValuesAsSigned),
                SimpleCopyPaste = ReadBool(key, nameof(SimpleCopyPaste), defaults.SimpleCopyPaste),
                SaveWindowPositions = ReadBool(key, nameof(SaveWindowPositions), defaults.SaveWindowPositions),
                AutoAttachProcess = ReadString(key, nameof(AutoAttachProcess), defaults.AutoAttachProcess),
                AlwaysAutoAttach = ReadBool(key, nameof(AlwaysAutoAttach), defaults.AlwaysAutoAttach),
                FastAutoAttach = ReadBool(key, nameof(FastAutoAttach), defaults.FastAutoAttach),
                UseWmiProcessWatch = ReadBool(key, nameof(UseWmiProcessWatch), defaults.UseWmiProcessWatch),
                FastAutoAttachInterval = ReadInt(key, nameof(FastAutoAttachInterval), defaults.FastAutoAttachInterval),
                AutoLoadDriver = ReadBool(key, nameof(AutoLoadDriver), defaults.AutoLoadDriver),
                AutoInjectVeh = ReadBool(key, "AutoInjectVEH", defaults.AutoInjectVeh),
                FastSuspendOnAttach = ReadBool(key, nameof(FastSuspendOnAttach), defaults.FastSuspendOnAttach),
                SuspendInterval = ReadInt(key, nameof(SuspendInterval), defaults.SuspendInterval),
                KernelModeUpdateMultiplier = ReadInt(key, nameof(KernelModeUpdateMultiplier), defaults.KernelModeUpdateMultiplier),
                KernelCacheLifetimeMs = ReadInt(key, nameof(KernelCacheLifetimeMs), defaults.KernelCacheLifetimeMs),
                AskToClearListOnNewProcess = ReadBool(key, nameof(AskToClearListOnNewProcess), defaults.AskToClearListOnNewProcess),
                AlwaysLaunchAsAdmin = ReadBool(key, nameof(AlwaysLaunchAsAdmin), defaults.AlwaysLaunchAsAdmin),
                DarkMode = ReadBool(key, nameof(DarkMode), defaults.DarkMode),
                ScanThreadPriority = ReadInt(key, nameof(ScanThreadPriority), defaults.ScanThreadPriority),
                ScanBufferSize = ReadInt(key, nameof(ScanBufferSize), defaults.ScanBufferSize),
                FastScanByDefault = ReadBool(key, nameof(FastScanByDefault), defaults.FastScanByDefault),
                PauseWhileScanning = ReadBool(key, nameof(PauseWhileScanning), defaults.PauseWhileScanning),
                ScanMemPrivate = ReadBool(key, nameof(ScanMemPrivate), defaults.ScanMemPrivate),
                ScanMemImage = ReadBool(key, nameof(ScanMemImage), defaults.ScanMemImage),
                ScanMemMapped = ReadBool(key, nameof(ScanMemMapped), defaults.ScanMemMapped),
                SkipPageNoCache = ReadBool(key, nameof(SkipPageNoCache), defaults.SkipPageNoCache),
                SkipPageWriteCombine = ReadBool(key, nameof(SkipPageWriteCombine), defaults.SkipPageWriteCombine),
                HotkeySpeedhack = ReadInt(key, nameof(HotkeySpeedhack), 0),
                HotkeyFreezeAll = ReadInt(key, nameof(HotkeyFreezeAll), 0),
                HotkeyUnfreezeAll = ReadInt(key, nameof(HotkeyUnfreezeAll), 0),
                HotkeyNewScan = ReadInt(key, nameof(HotkeyNewScan), 0),
                HotkeyNextScan = ReadInt(key, nameof(HotkeyNextScan), 0),
                HotkeyUndoScan = ReadInt(key, nameof(HotkeyUndoScan), 0)
            };

            if (hotkeyKey != null)
            {
                foreach (string valueName in hotkeyKey.GetValueNames())
                {
                    if (hotkeyKey.GetValue(valueName) is int value)
                    {
                        migrated.Hotkeys[valueName] = value;
                    }
                }
            }

            return migrated;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadInt(RegistryKey? key, string name, int defaultValue) =>
        key?.GetValue(name) is int value ? value : defaultValue;

    private static bool ReadBool(RegistryKey? key, string name, bool defaultValue) =>
        key?.GetValue(name) is int value ? value != 0 : defaultValue;

    private static string ReadString(RegistryKey? key, string name, string defaultValue) =>
        key?.GetValue(name) as string ?? defaultValue;
}
