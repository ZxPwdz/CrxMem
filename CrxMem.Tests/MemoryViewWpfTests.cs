using System.Runtime.ExceptionServices;
using System.Windows;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Controls;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using CrxMem.MemoryView.Wpf.Views;
using System.Windows.Media.Imaging;
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
    public async Task VerifiedPatchCommands_EnableOnlyForValidSelectedBytes()
    {
        var patchService = new FakePatchService();
        using var viewModel = new MemoryViewViewModel(
            new FakeMemoryViewDataService(), patchService: patchService);
        await viewModel.InitializeAsync(0);

        Assert.True(viewModel.ApplyPatchCommand.CanExecute(null));
        viewModel.PatchBytes = "not bytes";
        Assert.False(viewModel.ApplyPatchCommand.CanExecute(null));
        viewModel.PatchBytes = "90 90 90";
        Assert.True(viewModel.ApplyPatchCommand.CanExecute(null));

        viewModel.ApplyPatchCommand.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (patchService.LastRequest == null) await Task.Delay(10, timeout.Token);

        Assert.Equal(0x140001000UL, patchService.LastRequest.Address);
        Assert.Equal(new byte[] { 0x48, 0x89, 0xD8 }, patchService.LastRequest.ExpectedOriginalBytes);
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, patchService.LastRequest.ReplacementBytes);
    }

    [Fact]
    public async Task DebugExecutionCommands_EnableOnlyForPausedSelectedThread()
    {
        var debug = new PausedDebugSession();
        var breakpoints = new FakeBreakpointExecutionService();
        using var viewModel = new MemoryViewViewModel(
            new FakeMemoryViewDataService(),
            debugSession: debug,
            breakpointService: breakpoints,
            executionService: breakpoints);
        await viewModel.InitializeAsync(0);

        Assert.True(viewModel.StepIntoCommand.CanExecute(null));
        Assert.True(viewModel.ToggleBreakpointCommand.CanExecute(null));
        viewModel.ToggleBreakpointCommand.Execute(null);
        viewModel.StepIntoCommand.Execute(null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (breakpoints.ToggleCalls == 0 || breakpoints.StepIntoCalls == 0)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(0x140001000UL, breakpoints.LastAddress);
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
                Assert.Equal("CrxMem Memory View", window.Title);
                Assert.IsType<MemoryViewViewModel>(window.DataContext);
                window.Close();

                var renderer = new VirtualizedDisassemblyControl
                {
                    CodeFontSize = 15,
                    RowHeight = 25,
                    ItemsSource = Enumerable.Range(0, 1000)
                        .Select(index => new DisassemblyPreviewRow(
                            "", index.ToString("X16"), "90", "nop", "", (ulong)index))
                        .ToArray()
                };
                renderer.Measure(new System.Windows.Size(900, 300));
                renderer.Arrange(new Rect(0, 0, 900, 300));
                var bitmap = new RenderTargetBitmap(900, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(renderer);
                Assert.InRange(renderer.LastRenderedRowCount, 1, renderer.VisibleRowCapacity);
                Assert.True(renderer.ExtentHeight > renderer.ViewportHeight);
                Assert.Equal(15, renderer.CodeFontSize);
                Assert.Equal(25, renderer.RowHeight);

                var hexRenderer = new VirtualizedHexDumpControl
                {
                    CodeFontSize = 14,
                    RowHeight = 24,
                    ItemsSource = Enumerable.Range(0, 1000)
                        .Select(index => new HexPreviewRow(
                            index.ToString("X16"), "00", ".", (ulong)index, new byte[] { 0 }))
                        .ToArray()
                };
                hexRenderer.Measure(new System.Windows.Size(800, 250));
                hexRenderer.Arrange(new Rect(0, 0, 800, 250));
                var hexBitmap = new RenderTargetBitmap(800, 250, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                hexBitmap.Render(hexRenderer);
                Assert.InRange(hexRenderer.LastRenderedRowCount, 1, hexRenderer.VisibleRowCapacity + 1);
                Assert.Equal(14, hexRenderer.CodeFontSize);
                Assert.Equal(24, hexRenderer.RowHeight);

                foreach (double dpi in new[] { 96d, 120d, 144d, 192d })
                {
                    var dpiBitmap = new RenderTargetBitmap(900, 300, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
                    dpiBitmap.Render(renderer);
                    Assert.Equal(900, dpiBitmap.PixelWidth);
                    Assert.Equal(300, dpiBitmap.PixelHeight);
                }
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

    private sealed class FakePatchService : IPatchService
    {
        public PatchRequest? LastRequest { get; private set; }
        public IReadOnlyList<PatchRecord> History => Array.Empty<PatchRecord>();
        public bool CanUndo => false;
        public bool CanRedo => false;

        public Task<PatchResult> ApplyAsync(PatchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var record = new PatchRecord(Guid.NewGuid(), request.Address,
                request.ExpectedOriginalBytes, request.ReplacementBytes, request.Description,
                request.ModuleName, request.ModuleRelativeAddress, DateTimeOffset.UtcNow, true, false);
            return Task.FromResult(new PatchResult(PatchStatus.Applied, PatchFailureStage.None, "Applied", record));
        }

        public Task<PatchResult> RestoreAsync(Guid patchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PatchResult> ReapplyAsync(Guid patchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PatchResult> UndoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PatchResult(PatchStatus.Rejected, PatchFailureStage.Validation, "None"));
        public Task<PatchResult> RedoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PatchResult(PatchStatus.Rejected, PatchFailureStage.Validation, "None"));
    }

    private sealed class PausedDebugSession : IDebugSessionService
    {
        public DebugSessionSnapshot Snapshot { get; } = new(DebugSessionState.Paused, 42, 7, "Breakpoint");
        public event EventHandler<DebugSessionSnapshot>? StateChanged;
        public Task AttachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DetachAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeBreakpointExecutionService : IBreakpointService, IDebugExecutionService
    {
        public int ToggleCalls { get; private set; }
        public int StepIntoCalls { get; private set; }
        public ulong LastAddress { get; private set; }
        public IReadOnlyList<BreakpointRecord> Breakpoints => Array.Empty<BreakpointRecord>();
        public IReadOnlyList<HardwareBreakpointRecord> HardwareBreakpoints => Array.Empty<HardwareBreakpointRecord>();
        public bool HasEnabledBreakpoint(ulong address) => false;
        public Task<BreakpointRecord?> ToggleAsync(ulong address, string? moduleName = null, ulong? moduleRelativeAddress = null, CancellationToken cancellationToken = default)
        { ToggleCalls++; LastAddress = address; return Task.FromResult<BreakpointRecord?>(null); }
        public Task<BreakpointRecord> AddTemporaryAsync(ulong address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<HardwareBreakpointRecord> SetHardwareAsync(ulong address, HardwareBreakpointAccess access, HardwareBreakpointSize size, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ClearHardwareAsync(int slot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepIntoAsync(CancellationToken cancellationToken = default)
        { StepIntoCalls++; return Task.CompletedTask; }
        public Task StepOverAsync(ulong address, int instructionLength, bool isCall, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StepOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RunToAsync(ulong address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RunUntilReturnAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
