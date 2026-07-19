using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class MemoryViewWindow : Window
{
    private readonly ulong _startAddress;
    private readonly MemoryViewViewModel _viewModel;
    private readonly IMemoryViewLayoutStore _layoutStore;
    private readonly IAutoAssemblerService? _autoAssemblerService;
    private double _visibleInspectorWidth = 350;
    private double _visiblePatchEditorWidth = 350;
    private double _codeFontSize = 12;
    private double _rowHeight = 21;
    private DataGrid? _dragSelectionGrid;
    private int _dragSelectionAnchor = -1;

    public MemoryViewWindow(ProcessAccess process, IntPtr startAddress = default, IMemoryViewHostBridge? hostBridge = null)
        : this(CreateComposition(process), startAddress, hostBridge)
    {
    }

    private MemoryViewWindow(MemoryViewComposition composition, IntPtr startAddress, IMemoryViewHostBridge? hostBridge)
        : this(
            composition.DataService,
            composition.LayoutStore,
            startAddress,
            composition.DebugSession,
            composition.RegisterService,
            composition.StackService,
            composition.WatchService,
            composition.PatchService,
            new IcedAssemblerService(),
            composition.BreakpointService,
            composition.BreakpointService,
            composition.ModuleService,
            composition.MetadataService,
            composition.ThreadService,
            composition.MemoryMapService,
            composition.HexMemorySourceFactory,
            hostBridge,
            composition.CallStackService,
            composition.SymbolService,
            composition.AutoAssemblerService)
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
        IAssemblerService? assemblerService = null,
        IBreakpointService? breakpointService = null,
        IDebugExecutionService? executionService = null,
        IModuleService? moduleService = null,
        IEditableMemoryMetadataService? editableMetadata = null,
        IThreadService? threadService = null,
        IMemoryMapService? memoryMapService = null,
        IHexMemorySourceFactory? hexMemorySourceFactory = null,
        IMemoryViewHostBridge? hostBridge = null,
        ICallStackService? callStackService = null,
        ISymbolService? symbolService = null,
        IAutoAssemblerService? autoAssemblerService = null)
    {
        InitializeComponent();
        _startAddress = unchecked((ulong)startAddress.ToInt64());
        _layoutStore = layoutStore ?? throw new ArgumentNullException(nameof(layoutStore));
        _autoAssemblerService = autoAssemblerService;
        _viewModel = new MemoryViewViewModel(
            dataService ?? throw new ArgumentNullException(nameof(dataService)),
            debugSession: debugSession,
            registerService: registerService,
            stackService: stackService,
            watchService: watchService,
            patchService: patchService,
            assemblerService: assemblerService,
            breakpointService: breakpointService,
            executionService: executionService,
            moduleService: moduleService,
            editableMetadata: editableMetadata,
            threadService: threadService,
            memoryMapService: memoryMapService,
            hexMemorySourceFactory: hexMemorySourceFactory,
            hostBridge: hostBridge,
            callStackService: callStackService,
            symbolService: symbolService);
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
        else if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && disassemblyView.IsKeyboardFocusWithin)
        {
            _ = ToggleSelectedBookmarkAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.OemSemicolon && Keyboard.Modifiers == ModifierKeys.None && disassemblyView.IsKeyboardFocusWithin)
        {
            _ = EditSelectedMetadataAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && hexDumpView.IsKeyboardFocusWithin)
        {
            _ = PasteSelectedHexBytesAsync();
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
        _visibleInspectorWidth = Math.Clamp(settings.InspectorWidth, 280, 900);
        _visiblePatchEditorWidth = Math.Clamp(settings.PatchEditorWidth, 280, 900);
        inspectorColumn.Width = new GridLength(_visibleInspectorWidth);
        bottomToolsRow.Height = new GridLength(Math.Clamp(settings.BottomToolsHeight, 160, 900));
        patchEditorColumn.Width = new GridLength(_visiblePatchEditorWidth);
        registerPanelRow.Height = new GridLength(Math.Clamp(settings.RegisterPanelHeight, 180, 1200));
        toolsTabControl.SelectedIndex = Math.Clamp(settings.SelectedToolTab, 0, Math.Max(0, toolsTabControl.Items.Count - 1));
        breakpointTabControl.SelectedIndex = Math.Clamp(settings.SelectedBreakpointTab, 0, Math.Max(0, breakpointTabControl.Items.Count - 1));
        _codeFontSize = Math.Clamp(settings.CodeFontSize, 10, 20);
        _rowHeight = Math.Clamp(settings.RowHeight, 18, 32);
        ApplyCodeMetrics();
        ConfigureColumnWidths();
        showInspectorMenu.IsChecked = settings.InspectorVisible;
        showPatchEditorMenu.IsChecked = settings.PatchEditorVisible;
        SetInspectorVisible(settings.InspectorVisible);
        SetPatchEditorVisible(settings.PatchEditorVisible);
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
                InspectorWidth = showInspectorMenu.IsChecked ? inspectorColumn.ActualWidth : _visibleInspectorWidth,
                BottomToolsHeight = bottomToolsRow.ActualHeight,
                PatchEditorWidth = showPatchEditorMenu.IsChecked ? patchEditorColumn.ActualWidth : _visiblePatchEditorWidth,
                RegisterPanelHeight = registerPanelRow.ActualHeight,
                SelectedToolTab = toolsTabControl.SelectedIndex,
                SelectedBreakpointTab = breakpointTabControl.SelectedIndex,
                InspectorVisible = showInspectorMenu.IsChecked,
                PatchEditorVisible = showPatchEditorMenu.IsChecked,
                CodeFontSize = _codeFontSize,
                RowHeight = _rowHeight,
                RecentExpressions = _viewModel.RecentExpressions.ToList()
            });
        }
        catch
        {
            // Layout persistence must never prevent the viewer from closing.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowInspector_Click(object sender, RoutedEventArgs e) =>
        SetInspectorVisible(showInspectorMenu.IsChecked);

    private void ShowPatchEditor_Click(object sender, RoutedEventArgs e) =>
        SetPatchEditorVisible(showPatchEditorMenu.IsChecked);

    private void SetInspectorVisible(bool visible)
    {
        if (!visible && inspectorColumn.ActualWidth > 0) _visibleInspectorWidth = inspectorColumn.ActualWidth;
        inspectorColumn.Width = visible ? new GridLength(_visibleInspectorWidth) : new GridLength(0);
        inspectorPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        inspectorSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPatchEditorVisible(bool visible)
    {
        if (!visible && patchEditorColumn.ActualWidth > 0) _visiblePatchEditorWidth = patchEditorColumn.ActualWidth;
        patchEditorColumn.Width = visible ? new GridLength(_visiblePatchEditorWidth) : new GridLength(0);
        patchEditorPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        patchSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CodeFontLarger_Click(object sender, RoutedEventArgs e)
    {
        _codeFontSize = Math.Min(20, _codeFontSize + 1);
        ApplyCodeMetrics();
    }

    private void CodeFontSmaller_Click(object sender, RoutedEventArgs e)
    {
        _codeFontSize = Math.Max(10, _codeFontSize - 1);
        ApplyCodeMetrics();
    }

    private void DenseRows_Click(object sender, RoutedEventArgs e)
    {
        _rowHeight = 19;
        ApplyCodeMetrics();
    }

    private void ComfortableRows_Click(object sender, RoutedEventArgs e)
    {
        _rowHeight = 25;
        ApplyCodeMetrics();
    }

    private void ApplyCodeMetrics()
    {
        disassemblyView.CodeFontSize = _codeFontSize;
        disassemblyView.RowHeight = _rowHeight;
        hexDumpView.CodeFontSize = _codeFontSize;
        hexDumpView.RowHeight = _rowHeight;
    }

    private IEnumerable<(string Name, System.Windows.Controls.DataGrid Grid)> TrackedGrids()
    {
        yield return ("Stack", stackGrid);
        yield return ("Watch", watchGrid);
        yield return ("SoftwareBreakpoints", softwareBreakpointsGrid);
        yield return ("HardwareBreakpoints", hardwareBreakpointsGrid);
        yield return ("CallStack", callStackGrid);
        yield return ("MemoryMap", memoryMapGrid);
        yield return ("PatchHistory", patchHistoryGrid);
        yield return ("Modules", modulesGrid);
        yield return ("Threads", threadsGrid);
    }

    private void ConfigureColumnWidths()
    {
        foreach ((string name, System.Windows.Controls.DataGrid grid) in TrackedGrids())
        {
            int lastIndex = grid.Columns.Count - 1;
            for (int index = 0; index < lastIndex; index++)
            {
                grid.Columns[index].MinWidth = 45;
                grid.Columns[index].MaxWidth = 320;
                grid.Columns[index].Width = DataGridLength.SizeToCells;
            }

            if (lastIndex >= 0)
            {
                grid.Columns[lastIndex].MinWidth = 80;
                grid.Columns[lastIndex].Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
        }
    }

    private async void PanelGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        ulong address = GetPanelAddress(grid.SelectedItem);
        if (address != 0) await _viewModel.NavigateToAddressAsync(address);
    }

    private static ulong GetPanelAddress(object? item) => item switch
    {
        BreakpointRecord row => row.Address,
        StackPreviewRow row => row.PointerValue,
        WatchPreviewRow row => row.RuntimeAddress ?? 0,
        MemoryMapRow row => row.RuntimeAddress,
        PatchHistoryRow row => row.RuntimeAddress,
        ModulePanelRow row => row.RuntimeAddress,
        CallStackPanelRow row => row.RuntimeAddress,
        ThreadDescriptor row => row.StartAddress,
        _ => 0
    };

    private static DataGrid? ContextGrid(object sender)
    {
        if (sender is not MenuItem item) return null;
        return ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu
            ? menu.PlacementTarget as DataGrid
            : null;
    }

    private void PanelGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        DependencyObject? current = source;
        while (current != null && current is not DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is DataGridRow row)
        {
            if (!grid.SelectedItems.Contains(row.Item))
            {
                grid.SelectedItems.Clear();
                grid.SelectedItem = row.Item;
            }
            row.Focus();
        }
    }

    private void PanelGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        DataGridRow? row = FindAncestor<DataGridRow>(source);
        if (row == null) return;
        _dragSelectionGrid = grid;
        _dragSelectionAnchor = grid.Items.IndexOf(row.Item);
    }

    private void PanelGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not DataGrid grid || grid != _dragSelectionGrid ||
            e.LeftButton != MouseButtonState.Pressed || _dragSelectionAnchor < 0 ||
            e.OriginalSource is not DependencyObject source) return;
        DataGridRow? row = FindAncestor<DataGridRow>(source);
        if (row == null) return;
        int end = grid.Items.IndexOf(row.Item);
        if (end < 0) return;
        grid.SelectedItems.Clear();
        for (int index = Math.Min(_dragSelectionAnchor, end); index <= Math.Max(_dragSelectionAnchor, end); index++)
            grid.SelectedItems.Add(grid.Items[index]);
        grid.SelectedItem = row.Item;
    }

    private void PanelGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragSelectionGrid = null;
        _dragSelectionAnchor = -1;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current != null && current is not T) current = VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private async void PanelGoToAddress_Click(object sender, RoutedEventArgs e)
    {
        DataGrid? grid = ContextGrid(sender);
        ulong address = GetPanelAddress(grid?.SelectedItem);
        if (address != 0) await _viewModel.NavigateToAddressAsync(address);
    }

    private void PanelCopyRow_Click(object sender, RoutedEventArgs e)
    {
        DataGrid? grid = ContextGrid(sender);
        if (grid?.SelectedItems.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine,
                grid.SelectedItems.Cast<object>().Select(row => row.ToString() ?? string.Empty)));
    }

    private void CopyDisassemblyRow_Click(object sender, RoutedEventArgs e)
    {
        if (disassemblyView.SelectedItems.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, disassemblyView.SelectedItems.Select(row =>
                $"{row.Address}  {row.Bytes}  {row.Instruction}")));
    }

    private void CopyDisassemblyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (disassemblyView.SelectedItems.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, disassemblyView.SelectedItems.Select(row => row.Address)));
    }

    private void CopyDisassemblyBytes_Click(object sender, RoutedEventArgs e)
    {
        if (disassemblyView.SelectedItems.Count > 0)
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, disassemblyView.SelectedItems.Select(row => row.Bytes)));
    }

    private void CopyModuleRva_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.SelectedInstructionExpression))
            System.Windows.Clipboard.SetText(_viewModel.SelectedInstructionExpression);
    }

    private async void FollowInHex_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedInstruction is not { } row) return;
        toolsTabControl.SelectedItem = hexDumpTab;
        await _viewModel.NavigateHexToAddressAsync(row.RuntimeAddress);
        hexDumpView.Focus();
    }

    private async void AddToWatch_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AddSelectedInstructionToWatchAsync();
        toolsTabControl.SelectedItem = watchTab;
    }

    private void AddToAddressList_Click(object sender, RoutedEventArgs e)
    {
        HostCommandResult result = _viewModel.AddSelectedInstructionToHost();
        if (!result.Succeeded)
            System.Windows.MessageBox.Show(this, result.Message, "Address-list transfer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetHardwareBreakpointContext_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SetHardwareBreakpointCommand.CanExecute(null))
            _viewModel.SetHardwareBreakpointCommand.Execute(null);
    }

    private void AssembleAtAddress_Click(object sender, RoutedEventArgs e)
    {
        SetPatchEditorVisible(true);
        assemblySourceBox.Focus();
        assemblySourceBox.SelectAll();
    }

    private void InspectMemoryRegion_Click(object sender, RoutedEventArgs e) =>
        toolsTabControl.SelectedItem = memoryMapTab;

    private void InspectModule_Click(object sender, RoutedEventArgs e) =>
        toolsTabControl.SelectedItem = modulesTab;

    private void MicrosoftSymbols_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = microsoftSymbolsMenu.IsChecked;
        if (enabled)
        {
            MessageBoxResult answer = System.Windows.MessageBox.Show(this,
                "Allow CrxMem to contact Microsoft's public symbol server and cache downloaded symbols locally?",
                "Enable online symbols", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                microsoftSymbolsMenu.IsChecked = false;
                enabled = false;
            }
        }
        _viewModel.SetMicrosoftSymbolServerEnabled(enabled);
    }

    private async void EditMetadata_Click(object sender, RoutedEventArgs e) =>
        await EditSelectedMetadataAsync();

    private async Task EditSelectedMetadataAsync()
    {
        EditableMemoryMetadata current = _viewModel.GetSelectedMetadata();
        var dialog = new MetadataEditDialog(current.Comment, current.IsBookmarked, current.Label) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _viewModel.UpdateSelectedMetadataAsync(new EditableMemoryMetadata(
            dialog.Comment, dialog.IsBookmarked, dialog.BookmarkLabel));
    }

    private async void ToggleBookmark_Click(object sender, RoutedEventArgs e) =>
        await ToggleSelectedBookmarkAsync();

    private async Task ToggleSelectedBookmarkAsync()
    {
        EditableMemoryMetadata current = _viewModel.GetSelectedMetadata();
        await _viewModel.UpdateSelectedMetadataAsync(current with { IsBookmarked = !current.IsBookmarked });
    }

    private void ToggleBreakpointContext_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ToggleBreakpointCommand.CanExecute(null)) _viewModel.ToggleBreakpointCommand.Execute(null);
    }

    private void NopInstructionContext_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.NopInstructionCommand.CanExecute(null)) _viewModel.NopInstructionCommand.Execute(null);
    }

    private void CopyHexByte_Click(object sender, RoutedEventArgs e)
    {
        byte[] bytes = hexDumpView.SelectedRangeBytes;
        if (bytes.Length > 0) System.Windows.Clipboard.SetText(string.Join(' ', bytes.Select(value => value.ToString("X2"))));
    }

    private void CopyHexAddress_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is ulong address) System.Windows.Clipboard.SetText($"{address:X16}");
    }

    private void CopyHexModuleRva_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address) return;
        System.Windows.Clipboard.SetText(_viewModel.GetAddressExpression(address));
    }

    private async void EditHexMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address) return;
        EditableMemoryMetadata current = _viewModel.GetMetadataAtAddress(address);
        var dialog = new MetadataEditDialog(current.Comment, current.IsBookmarked, current.Label) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _viewModel.UpdateMetadataAtAddressAsync(address, new EditableMemoryMetadata(
            dialog.Comment, dialog.IsBookmarked, dialog.BookmarkLabel));
    }

    private async void AddHexToWatch_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address) return;
        await _viewModel.AddAddressToWatchAsync(address);
        toolsTabControl.SelectedItem = watchTab;
    }

    private void AddHexToAddressList_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address) return;
        HostCommandResult result = _viewModel.AddAddressToHost(address);
        if (!result.Succeeded)
            System.Windows.MessageBox.Show(this, result.Message, "Address-list transfer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void EditHexBytes_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address) return;
        byte[] selected = hexDumpView.SelectedRangeBytes;
        if (selected.Length == 0) return;
        var dialog = new TextEntryDialog(
            "Edit memory bytes",
            "Enter exactly the replacement bytes in hexadecimal:",
            string.Join(' ', selected.Select(value => value.ToString("X2")))) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await _viewModel.ApplyHexPatchTextAsync(address, selected, dialog.Value);
    }

    private async void PasteHexBytes_Click(object sender, RoutedEventArgs e) =>
        await PasteSelectedHexBytesAsync();

    private async Task PasteSelectedHexBytesAsync()
    {
        if (hexDumpView.SelectedRangeAddress is not ulong address || !System.Windows.Clipboard.ContainsText()) return;
        byte[] available = hexDumpView.GetAvailableBytesFromSelection();
        if (available.Length == 0) return;
        await _viewModel.ApplyHexPatchTextAsync(address, available, System.Windows.Clipboard.GetText());
    }

    private async void RegisterRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: RegisterValue register }) return;
        e.Handled = true;
        await ShowRegisterEditorAsync(register);
    }

    private async void EditRegister_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RegisterValue register }) await ShowRegisterEditorAsync(register);
    }

    private void CopyRegisterValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RegisterValue register })
            System.Windows.Clipboard.SetText(register.Value.ToString("X"));
    }

    private async void ToggleFlag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !uint.TryParse(tag, out uint mask)) return;
        RegisterWriteResult? result = await _viewModel.ToggleFlagAsync(mask);
        if (result is { Success: false })
            System.Windows.MessageBox.Show(this, result.Diagnostic ?? "Flag write failed.",
                "Edit flags", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task ShowRegisterEditorAsync(RegisterValue register)
    {
        if (!_viewModel.CanEditRegister(register))
        {
            System.Windows.MessageBox.Show(this,
                register.Name.StartsWith("DR", StringComparison.OrdinalIgnoreCase)
                    ? "Debug registers are managed by the breakpoint service and cannot be edited here."
                    : "Register editing requires the matching selected thread in a verified paused debugger session.",
                "Edit register", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new TextEntryDialog($"Edit {register.Name}",
            "Enter hexadecimal, or prefix decimal input with dec:. The write will be recaptured and verified.",
            register.Value.ToString("X")) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        RegisterWriteResult? result = await _viewModel.WriteRegisterAsync(register, dialog.Value);
        if (result is { Success: false })
            System.Windows.MessageBox.Show(this, result.Diagnostic ?? "Register write failed.",
                "Edit register", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void GoToHexAddress_Click(object sender, RoutedEventArgs e)
    {
        if (hexDumpView.SelectedRangeAddress is ulong address) await _viewModel.NavigateToAddressAsync(address);
    }

    private static MemoryViewComposition CreateComposition(ProcessAccess process)
    {
        var memory = new ProcessMemoryService(process);
        var modules = new ModuleService(process);
        var moduleIdentity = new FileModuleIdentityService(modules);
        var metadata = new VersionedMemoryMetadataService(modules, moduleIdentity);
        var patchAdapter = new ProcessPatchMemoryAdapter(process);
        var patches = new PatchService(patchAdapter, new JsonPatchHistoryStore(), moduleIdentity);
        var addressExpressions = new AddressExpressionService(memory, modules);
        var aaSessions = new InMemoryAutoAssemblerSessionStore();
        var aaLua = new RestrictedAutoAssemblerLuaHost();
        var aaCompiler = new AutoAssemblerCompiler(addressExpressions, new IcedAssemblerService(),
            new ProcessNearAllocationPlanner(process), aobScanner: new ProcessAobScanService(process, memory, modules), memory: memory,
            luaHost: aaLua);
        var aaExecutor = new AutoAssemblerTransactionExecutor(process, patches, aaSessions);
        var autoAssembler = new AutoAssemblerService(process, aaCompiler, aaExecutor, aaSessions, aaLua);
        aaLua.AttachService(autoAssembler);
        var debugSession = new NativeDebugSessionService(process);
        var dbgHelp = new WindowsDbgHelpService(process, modules);
        var breakpoints = new BreakpointService(
            patchAdapter,
            patches,
            new WindowsThreadExecutionContextService(process),
            debugSession,
            new WindowsHardwareBreakpointAdapter(process));
        debugSession.EventCoordinator = breakpoints;
        return new MemoryViewComposition(
            new MemoryViewDataService(memory, modules, metadata),
            new MemoryViewLayoutStore(),
            debugSession,
            new WindowsRegisterService(process),
            new StackService(memory, modules),
            new WatchService(addressExpressions, memory),
            patches,
            breakpoints,
            modules,
            metadata,
            new ProcessThreadService(process),
            new ProcessMemoryMapService(process),
            new HexMemorySourceFactory(memory),
            dbgHelp,
            dbgHelp,
            autoAssembler);
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
        IEditableMemoryMetadataService MetadataService,
        IThreadService ThreadService,
        IMemoryMapService MemoryMapService,
        IHexMemorySourceFactory HexMemorySourceFactory,
        ICallStackService CallStackService,
        ISymbolService SymbolService,
        IAutoAssemblerService AutoAssemblerService);

    private void AutoAssembler_Click(object sender, RoutedEventArgs e)
    {
        if (_autoAssemblerService == null)
        {
            System.Windows.MessageBox.Show(this, "Auto Assembler is unavailable for this MemoryView host.",
                "Auto Assembler", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new AutoAssemblerWindow(_autoAssemblerService) { Owner = this }.Show();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this,
            "CrxMem Memory View\n\nInspection, native debugging, verified patches and assembly, versioned metadata, and synchronized tool panels are available.",
            "About Memory View", MessageBoxButton.OK, MessageBoxImage.Information);
}
