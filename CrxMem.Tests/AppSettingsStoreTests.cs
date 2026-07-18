using System.Text.Json;
using Xunit;

namespace CrxMem.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        "CrxMem.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RoundTripsSettingsAndHotkeys()
    {
        string path = Path.Combine(_testDirectory, "settings.json");
        var store = new AppSettingsStore(path);
        var settings = new CrxMemSettings
        {
            UpdateInterval = 750,
            DarkMode = false,
            AutoAttachProcess = "sample.exe",
            Hotkeys = new Dictionary<string, int> { ["freeze-all"] = 123 }
        };

        store.Save(settings);
        CrxMemSettings loaded = store.Load();

        Assert.Equal(750, loaded.UpdateInterval);
        Assert.False(loaded.DarkMode);
        Assert.Equal("sample.exe", loaded.AutoAttachProcess);
        Assert.Equal(123, loaded.Hotkeys["FREEZE-ALL"]);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void Load_UsesLegacyValuesOnceAndPersistsThem()
    {
        string path = Path.Combine(_testDirectory, "settings.json");
        int migrationCalls = 0;
        var store = new AppSettingsStore(path, () =>
        {
            migrationCalls++;
            return new CrxMemSettings { FreezeInterval = 225 };
        });

        Assert.Equal(225, store.Load().FreezeInterval);
        Assert.Equal(225, store.Load().FreezeInterval);
        Assert.Equal(1, migrationCalls);
    }

    [Fact]
    public void Load_PreservesInvalidJsonAndRecoversWithDefaults()
    {
        Directory.CreateDirectory(_testDirectory);
        string path = Path.Combine(_testDirectory, "settings.json");
        File.WriteAllText(path, "{ definitely not valid json");
        var store = new AppSettingsStore(path);

        CrxMemSettings loaded = store.Load();

        Assert.Equal(500, loaded.UpdateInterval);
        Assert.True(File.Exists(path));
        Assert.Single(Directory.GetFiles(_testDirectory, "settings.invalid-*.json"));
    }

    [Fact]
    public void Load_ClampsUnsafeTimingValues()
    {
        string path = Path.Combine(_testDirectory, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(new CrxMemSettings
        {
            UpdateInterval = 0,
            FreezeInterval = int.MaxValue,
            FastAutoAttachInterval = -10,
            ScanThreadPriority = 99
        });

        CrxMemSettings loaded = store.Load();

        Assert.Equal(50, loaded.UpdateInterval);
        Assert.Equal(60_000, loaded.FreezeInterval);
        Assert.Equal(1, loaded.FastAutoAttachInterval);
        Assert.Equal(4, loaded.ScanThreadPriority);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}
