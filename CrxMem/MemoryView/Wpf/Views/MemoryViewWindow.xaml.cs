using System.Windows;
using System.Windows.Input;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class MemoryViewWindow : Window
{
    private readonly ulong _startAddress;
    private readonly MemoryViewViewModel _viewModel;
    private readonly IMemoryViewLayoutStore _layoutStore;

    public MemoryViewWindow(ProcessAccess process, IntPtr startAddress = default)
        : this(CreateComposition(process), startAddress)
    {
    }

    private MemoryViewWindow(MemoryViewComposition composition, IntPtr startAddress)
        : this(
            composition.DataService,
            composition.LayoutStore,
            startAddress,
            composition.DebugSession,
            composition.RegisterService,
            composition.StackService,
            composition.WatchService,
            composition.PatchService,
            composition.BreakpointService,
            composition.BreakpointService,
            composition.ModuleService,
            composition.ThreadService,
            composition.MemoryMapService)
    {
    }

    public MemoryViewWindow(
        IMemoryViewDataService dataService,
        IMemoryViewLayoutStore layoutStore,
        IntPtr startAddress = default,
        IDebugSessionService? debugSession = null,
        IRegisterService? registerService = null,
        IStackService? stackService = null,
        IWatchService? watchService = null,
        IPatchService? patchService = null,
        IBreakpointService? breakpointService = null,
        IDebugExecutionService? executionService = null,
        IModuleService? moduleService = null,
        IThreadService? threadService = null,
        IMemoryMapService? memoryMapService = null)
    {
        InitializeComponent();
        _startAddress = unchecked((ulong)startAddress.ToInt64());
        _layoutStore = layoutStore ?? throw new ArgumentNullException(nameof(layoutStore));
        _viewModel = new MemoryViewViewModel(
            dataService ?? throw new ArgumentNullException(nameof(dataService)),
            debugSession: debugSession,
            registerService: registerService,
            stackService: stackService,
            watchService: watchService,
            patchService: patchService,
            breakpointService: breakpointService,
            executionService: executionService,
            moduleService: moduleService,
            threadService: threadService,
            memoryMapService: memoryMapService);
        DataContext = _viewModel;
        ApplyLayout(_layoutStore.Load());
        Loaded += MemoryViewWindow_Loaded;
        Closed += MemoryViewWindow_Closed;
        PreviewKeyDown += MemoryViewWindow_PreviewKeyDown;
    }

    private async void MemoryViewWindow_Loaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync(_startAddress);

    private void MemoryViewWindow_Closed(object? sender, EventArgs e)
    {
        SaveLayout();
        _viewModel.Dispose();
    }

    private void MemoryViewWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            addressBox.Focus();
            SelectAddressText();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FocusAddress_Click(object sender, RoutedEventArgs e)
    {
        addressBox.Focus();
        SelectAddressText();
    }

    private void SelectAddressText()
    {
        addressBox.ApplyTemplate();
        if (addressBox.Template.FindName("PART_EditableTextBox", addressBox) is System.Windows.Controls.TextBox editor)
            editor.SelectAll();
    }

    private void ApplyLayout(MemoryViewLayoutSettings settings)
    {
        Width = Math.Clamp(settings.WindowWidth, MinWidth, 3840);
        Height = Math.Clamp(settings.WindowHeight, MinHeight, 2160);
        inspectorColumn.Width = new GridLength(Math.Clamp(settings.InspectorWidth, 280, 900));
        bottomToolsRow.Height = new GridLength(Math.Clamp(settings.BottomToolsHeight, 160, 900));
        patchEditorColumn.Width = new GridLength(Math.Clamp(settings.PatchEditorWidth, 280, 900));
        registerPanelRow.Height = new GridLength(Math.Clamp(settings.RegisterPanelHeight, 180, 1200));
        _viewModel.LoadRecentExpressions(settings.RecentExpressions);

        if (settings.WindowLeft is double left && settings.WindowTop is double top &&
            double.IsFinite(left) && double.IsFinite(top) &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80 &&
            left + Width > SystemParameters.VirtualScreenLeft + 100 &&
            top + Height > SystemParameters.VirtualScreenTop + 80)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    private void SaveLayout()
    {
        try
        {
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _layoutStore.Save(new MemoryViewLayoutSettings
            {
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height,
                WindowLeft = bounds.Left,
                WindowTop = bounds.Top,
                InspectorWidth = inspectorColumn.ActualWidth,
                BottomToolsHeight = bottomToolsRow.ActualHeight,
                PatchEditorWidth = patchEditorColumn.ActualWidth,
                RegisterPanelHeight = registerPanelRow.ActualHeight,
                RecentExpressions = _viewModel.RecentExpressions.ToList()
            });
        }
        catch
        {
            // Layout persistence must never prevent the viewer from closing.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void PanelGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        ulong address = grid.SelectedItem switch
        {
            BreakpointRecord row => row.Address,
            MemoryMapRow row => row.RuntimeAddress,
            PatchHistoryRow row => row.RuntimeAddress,
            ModulePanelRow row => row.RuntimeAddress,
            CallStackPanelRow row => row.RuntimeAddress,
            _ => 0
        };
        if (address != 0) await _viewModel.NavigateToAddressAsync(address);
    }

    private static MemoryViewComposition CreateComposition(ProcessAccess process)
    {
        var memory = new ProcessMemoryService(process);
        var modules = new ModuleService(process);
        var patchAdapter = new ProcessPatchMemoryAdapter(process);
        var patches = new PatchService(patchAdapter);
        var debugSession = new NativeDebugSessionService(process);
        var breakpoints = new BreakpointService(
            patchAdapter,
            patches,
            new WindowsThreadExecutionContextService(process),
            debugSession,
            new WindowsHardwareBreakpointAdapter(process));
        debugSession.EventCoordinator = breakpoints;
        return new MemoryViewComposition(
            new MemoryViewDataService(process),
            new MemoryViewLayoutStore(),
            debugSession,
            new WindowsRegisterService(process),
            new StackService(memory, modules),
            new WatchService(new AddressExpressionService(memory, modules), memory),
            patches,
            breakpoints,
            modules,
            new ProcessThreadService(process),
            new ProcessMemoryMapService(process));
    }

    private sealed record MemoryViewComposition(
        IMemoryViewDataService DataService,
        IMemoryViewLayoutStore LayoutStore,
        NativeDebugSessionService DebugSession,
        IRegisterService RegisterService,
        IStackService StackService,
        IWatchService WatchService,
        IPatchService PatchService,
        BreakpointService BreakpointService,
        IModuleService ModuleService,
        IThreadService ThreadService,
        IMemoryMapService MemoryMapService);

    private void About_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this,
            "CrxMem Memory View WPF Preview\n\nInspection, native debugging, owned breakpoints, verified byte patches, and synchronized tool panels are available. Free-form assembly remains gated.",
            "About Memory View", MessageBoxButton.OK, MessageBoxImage.Information);
}
