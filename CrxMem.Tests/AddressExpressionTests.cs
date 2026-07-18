using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class AddressExpressionTests
{
    private const ulong ModuleBase = 0x140000000;

    [Theory]
    [InlineData("0x140001000", 0x140001000UL)]
    [InlineData("00000001`40001000", 0x140001000UL)]
    [InlineData("Target.exe + 1000", 0x140001000UL)]
    [InlineData("target.EXE + (20 * 2)", 0x140000040UL)]
    [InlineData("RIP + 10", 0x7FF600001010UL)]
    [InlineData("Target.exe!Update + 20", 0x140002020UL)]
    public async Task EvaluateAsync_ResolvesArithmeticModulesRegistersAndSymbols(
        string expression,
        ulong expected)
    {
        AddressExpressionService service = CreateService();

        AddressExpressionResult result = await service.EvaluateAsync(expression, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.Address);
    }

    [Fact]
    public async Task EvaluateAsync_ReadsBoundedNestedPointers()
    {
        var memory = new ExpressionMemoryService(new Dictionary<ulong, ulong>
        {
            [ModuleBase + 0x100] = 0x200000000,
            [0x200000000] = 0x300000000
        });
        var service = new AddressExpressionService(memory, new ExpressionModuleService());

        AddressExpressionResult result = await service.EvaluateAsync(
            "[[Target.exe + 100]] + 20", CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0x300000020UL, result.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Missing.dll + 10")]
    [InlineData("10 - 20")]
    [InlineData("10 / 0")]
    [InlineData("(Target.exe + 10")]
    [InlineData("[[[[[Target.exe]]]]]")]
    public async Task EvaluateAsync_InvalidExpressionsReturnVisibleErrors(string expression)
    {
        AddressExpressionResult result = await CreateService().EvaluateAsync(expression, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task EvaluateAsync_UnreadablePointerReturnsVisibleError()
    {
        var service = new AddressExpressionService(
            new ExpressionMemoryService(new Dictionary<ulong, ulong>()),
            new ExpressionModuleService());

        AddressExpressionResult result = await service.EvaluateAsync("[Target.exe]", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("pointer", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static AddressExpressionService CreateService() => new(
        new ExpressionMemoryService(new Dictionary<ulong, ulong>()),
        new ExpressionModuleService(),
        new ExpressionRegisterSource(),
        new ExpressionSymbolSource());

    private sealed class ExpressionMemoryService(IReadOnlyDictionary<ulong, ulong> pointers)
        : IProcessMemoryService
    {
        public ProcessIdentity Identity { get; } = new("Target", 42, true, true);

        public Task<MemoryReadResult> ReadAsync(ulong address, int size, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pointers.TryGetValue(address, out ulong pointer))
                return Task.FromResult(new MemoryReadResult(
                    address, size, Array.Empty<byte>(), MemoryReadStatus.Unreadable,
                    $"Unable to read a pointer at 0x{address:X}."));

            byte[] data = BitConverter.GetBytes(pointer);
            return Task.FromResult(new MemoryReadResult(address, size, data, MemoryReadStatus.Complete));
        }

        public MemoryRegionDescriptor? QueryRegion(ulong address) => null;
    }

    private sealed class ExpressionModuleService : IModuleService
    {
        private static readonly ModuleDescriptor Module = new("Target.exe", ModuleBase, 0x100000);
        public IReadOnlyList<ModuleDescriptor> GetModules() => new[] { Module };
        public ModuleDescriptor? FindByAddress(ulong address) => Module.Contains(address) ? Module : null;
        public ModuleDescriptor? FindByName(string moduleName) =>
            Module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ? Module : null;
        public ulong GetDefaultAddress() => ModuleBase;
    }

    private sealed class ExpressionRegisterSource : IRegisterValueSource
    {
        public bool TryGetRegister(string name, out ulong value)
        {
            value = name.Equals("RIP", StringComparison.OrdinalIgnoreCase) ? 0x7FF600001000UL : 0UL;
            return value != 0;
        }
    }

    private sealed class ExpressionSymbolSource : ISymbolAddressSource
    {
        public bool TryGetSymbol(string name, out ulong address)
        {
            address = name.Equals("Target.exe!Update", StringComparison.OrdinalIgnoreCase)
                ? ModuleBase + 0x2000
                : 0;
            return address != 0;
        }
    }
}

public sealed class NavigationHistoryServiceTests
{
    [Fact]
    public void BackForwardAndNewNavigationMaintainBrowserSemantics()
    {
        var history = new NavigationHistoryService();
        history.Push(new NavigationEntry(0x1000, "1000"));
        history.Push(new NavigationEntry(0x2000, "2000"));
        history.Push(new NavigationEntry(0x3000, "3000"));

        Assert.Equal(0x2000UL, history.GoBack()!.Address);
        Assert.Equal(0x1000UL, history.GoBack()!.Address);
        Assert.False(history.CanGoBack);
        Assert.Equal(0x2000UL, history.GoForward()!.Address);

        history.Push(new NavigationEntry(0x4000, "4000"));

        Assert.False(history.CanGoForward);
        Assert.Equal(0x2000UL, history.GoBack()!.Address);
        Assert.Equal(0x4000UL, history.GoForward()!.Address);
    }

    [Fact]
    public void CapacityIsBoundedAndDuplicateAddressUpdatesCurrentEntry()
    {
        var history = new NavigationHistoryService(capacity: 3);
        history.Push(new NavigationEntry(1, "1"));
        history.Push(new NavigationEntry(2, "2"));
        history.Push(new NavigationEntry(3, "3"));
        history.Push(new NavigationEntry(4, "4"));
        history.Push(new NavigationEntry(4, "module+4", 4));

        Assert.Equal("module+4", history.Current!.Expression);
        Assert.Equal(3UL, history.GoBack()!.Address);
        Assert.Equal(2UL, history.GoBack()!.Address);
        Assert.False(history.CanGoBack);
    }
}

public sealed class MemoryViewLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CrxMemLayout-{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoad_RoundTripsLayoutAndNormalizesRecentExpressions()
    {
        string path = Path.Combine(_directory, "layout.json");
        var store = new MemoryViewLayoutStore(path);
        var settings = new MemoryViewLayoutSettings
        {
            WindowWidth = 1500,
              WindowHeight = 900,
              InspectorWidth = 420,
              SelectedToolTab = 4,
              SelectedBreakpointTab = 1,
              InspectorVisible = false,
              PatchEditorVisible = false,
              CodeFontSize = 15,
              RowHeight = 25,
              ColumnWidths = new Dictionary<string, List<double>> { ["Threads"] = new() { 80, 160 } },
              RecentExpressions = new List<string> { "Target.exe+1000", "", "target.exe+1000", "2000" }
        };

        store.Save(settings);
        MemoryViewLayoutSettings loaded = store.Load();

        Assert.Equal(1500, loaded.WindowWidth);
        Assert.Equal(900, loaded.WindowHeight);
          Assert.Equal(420, loaded.InspectorWidth);
          Assert.Equal(4, loaded.SelectedToolTab);
          Assert.Equal(1, loaded.SelectedBreakpointTab);
          Assert.False(loaded.InspectorVisible);
          Assert.False(loaded.PatchEditorVisible);
          Assert.Equal(15, loaded.CodeFontSize);
          Assert.Equal(25, loaded.RowHeight);
          Assert.Equal(new double[] { 80, 160 }, loaded.ColumnWidths["Threads"]);
        Assert.Equal(new[] { "Target.exe+1000", "2000" }, loaded.RecentExpressions);
    }

    [Fact]
    public void Load_CorruptOrUnknownSchema_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "layout.json");
        File.WriteAllText(path, "{not json");
        Assert.Equal(1360, new MemoryViewLayoutStore(path).Load().WindowWidth);

        File.WriteAllText(path, "{\"SchemaVersion\":99,\"WindowWidth\":42}");
        Assert.Equal(1360, new MemoryViewLayoutStore(path).Load().WindowWidth);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
