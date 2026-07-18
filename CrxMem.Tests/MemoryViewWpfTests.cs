using System.Runtime.ExceptionServices;
using System.Windows;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using CrxMem.MemoryView.Wpf.Views;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryViewWpfTests
{
    [Fact]
    public async Task InitializeAsync_PopulatesRealSnapshotFieldsAndSelection()
    {
        var service = new FakeMemoryViewDataService();
        using var viewModel = new MemoryViewViewModel(service);

        await viewModel.InitializeAsync(0);

        Assert.Equal(service.DefaultAddress, service.RequestedAddress);
        Assert.Equal("Target.exe (42)", viewModel.SessionTitle);
        Assert.Equal("Target.exe", viewModel.ModuleName);
        Assert.Equal("0000000140001000", viewModel.CurrentAddress);
        Assert.Single(viewModel.Instructions);
        Assert.Single(viewModel.HexRows);
        Assert.Equal("mov rax, rbx", viewModel.SelectedInstructionText);
        Assert.Equal("Inspection ready", viewModel.Status);
    }

    [Fact]
    public async Task InitializeAsync_WhenNoDefaultAddress_ReportsUnavailableState()
    {
        using var viewModel = new MemoryViewViewModel(new FakeMemoryViewDataService { DefaultAddress = 0 });

        await viewModel.InitializeAsync(0);

        Assert.Empty(viewModel.Instructions);
        Assert.Contains("No readable module", viewModel.Status);
    }

    [Fact]
    public void ThemeResources_LoadOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
                    Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"));

                _ = Application.Current ?? new Application();
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/CrxMem;component/MemoryView/Wpf/Themes/MemoryView.Controls.xaml",
                        UriKind.Absolute)
                };

                Assert.Equal(2, dictionary.MergedDictionaries.Count);
                Assert.Contains("MemoryView.PaneHeader", dictionary.Keys.Cast<object>());
                Assert.Contains("MemoryView.WindowBackgroundBrush",
                    dictionary.MergedDictionaries[0].Keys.Cast<object>());
                Assert.Contains("MemoryView.UiFont",
                    dictionary.MergedDictionaries[1].Keys.Cast<object>());

                var window = new MemoryViewWindow(
                    new FakeMemoryViewDataService(), new FakeMemoryViewLayoutStore());
                Assert.Equal("CrxMem Memory View (WPF Preview)", window.Title);
                Assert.IsType<MemoryViewViewModel>(window.DataContext);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeMemoryViewDataService : IMemoryViewDataService
    {
        public ulong DefaultAddress { get; init; } = 0x140001000;
        public ulong RequestedAddress { get; private set; }

        public ulong GetDefaultAddress() => DefaultAddress;

        public Task<AddressExpressionResult> ResolveAddressAsync(
            string expression,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                ulong.TryParse(expression, System.Globalization.NumberStyles.HexNumber, null, out ulong address)
                    ? AddressExpressionResult.Resolved(address)
                    : AddressExpressionResult.Failed("Invalid address."));
        }

        public Task<MemoryViewSnapshot> LoadSnapshotAsync(ulong address, CancellationToken cancellationToken)
        {
            RequestedAddress = address;
            cancellationToken.ThrowIfCancellationRequested();
            var instructions = new[]
            {
                new DisassemblyPreviewRow("", "0000000140001000", "48 89 D8", "mov rax, rbx", "entry", address)
            };
            var hexRows = new[]
            {
                new HexPreviewRow("0000000140001000", "48 89 D8", "H..")
            };
            return Task.FromResult(new MemoryViewSnapshot(
                address, "Target", 42, "Target.exe", 0x140000000, "x64", "1000",
                "Read / Execute", "0000000140000000 – 0000000140100000",
                instructions, hexRows, "Inspection ready"));
        }
    }

    private sealed class FakeMemoryViewLayoutStore : IMemoryViewLayoutStore
    {
        public MemoryViewLayoutSettings Load() => new();
        public void Save(MemoryViewLayoutSettings settings) { }
    }
}
