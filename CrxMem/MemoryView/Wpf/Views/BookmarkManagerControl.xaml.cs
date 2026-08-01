using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class BookmarkManagerControl : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<BookmarkManagerRow> _rows = new();
    private IEnumerableMemoryMetadataService? _service;
    private MemoryViewViewModel? _memoryView;
    private ListCollectionView? _view;

    public BookmarkManagerControl()
    {
        InitializeComponent();
        DataContext = _rows;
        Loaded += (_, _) => searchBox.Focus();
    }

    public event EventHandler<ResolvedMemoryAnnotation>? NavigateRequested;
    public event EventHandler? ReturnToMemoryRequested;

    public void Configure(IEnumerableMemoryMetadataService service, MemoryViewViewModel memoryView)
    {
        if (_service != null) _service.Changed -= Service_Changed;
        _service = service;
        _memoryView = memoryView;
        _service.Changed += Service_Changed;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Filter;
        RefreshRows();
    }

    public void FocusSearch() { searchBox.Focus(); searchBox.SelectAll(); }
    public void ApplySettings(MemoryViewLayoutSettings settings)
    {
        searchBox.Text = settings.BookmarkSearch ?? string.Empty;
        typeFilter.SelectedIndex = Math.Clamp(settings.BookmarkTypeFilter, 0, typeFilter.Items.Count - 1);
        stateFilter.SelectedIndex = Math.Clamp(settings.BookmarkStateFilter, 0, stateFilter.Items.Count - 1);
        pinnedFirstBox.IsChecked = settings.BookmarkPinnedFirst;
        groupFilter.Text = settings.BookmarkGroupFilter ?? string.Empty;
        tagFilter.Text = settings.BookmarkTagFilter ?? string.Empty;
        colorFilter.Text = settings.BookmarkColorFilter ?? string.Empty;
        for (int i = 0; i < Math.Min(grid.Columns.Count, settings.BookmarkColumnWidths.Count); i++)
            if (settings.BookmarkColumnWidths[i] is > 25 and < 2000) grid.Columns[i].Width = settings.BookmarkColumnWidths[i];
        Filter_Changed(this, new RoutedEventArgs());
    }

    public void CaptureSettings(MemoryViewLayoutSettings settings)
    {
        settings.BookmarkSearch = searchBox.Text;
        settings.BookmarkTypeFilter = typeFilter.SelectedIndex;
        settings.BookmarkStateFilter = stateFilter.SelectedIndex;
        settings.BookmarkPinnedFirst = pinnedFirstBox.IsChecked == true;
        settings.BookmarkGroupFilter = groupFilter.Text;
        settings.BookmarkTagFilter = tagFilter.Text;
        settings.BookmarkColorFilter = colorFilter.Text;
        settings.BookmarkColumnWidths = grid.Columns.Select(column => column.ActualWidth).ToList();
    }
    public void RefreshRows()
    {
        if (_service == null) return;
        MemoryAnnotationId[] selected = SelectedRows().Select(row => row.Annotation.Id).ToArray();
        _rows.Clear();
        foreach (ResolvedMemoryAnnotation annotation in _service.GetAllAnnotations()
                     .OrderByDescending(x => x.Record.IsPinned).ThenBy(x => x.Expression, StringComparer.OrdinalIgnoreCase))
            _rows.Add(new BookmarkManagerRow(annotation,
                _memoryView?.Instructions.FirstOrDefault(row => row.RuntimeAddress == annotation.RuntimeAddress)?.Instruction));
        _view?.Refresh();
        foreach (BookmarkManagerRow row in _rows.Where(row => selected.Contains(row.Annotation.Id))) grid.SelectedItems.Add(row);
        statusText.Text = $"{_rows.Count} annotation(s) · {_rows.Count(row => row.Annotation.State != MemoryAnnotationResolutionState.Resolved)} stale/unavailable";
    }

    private void Service_Changed(object? sender, MemoryAnnotationsChangedEventArgs e) => Dispatcher.InvokeAsync(RefreshRows);

    private bool Filter(object value)
    {
        if (value is not BookmarkManagerRow row) return false;
        string search = searchBox?.Text?.Trim() ?? string.Empty;
        if (search.Length > 0 && !row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (!ContainsFilter(row.Group, groupFilter?.Text)) return false;
        if (!ContainsFilter(row.Tags, tagFilter?.Text)) return false;
        if (!ContainsFilter(row.Color, colorFilter?.Text)) return false;
        string type = (typeFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All annotations";
        if (type == "Bookmarks" && !row.Annotation.Record.IsBookmarked) return false;
        if (type == "Comments only" && (row.Annotation.Record.IsBookmarked || string.IsNullOrWhiteSpace(row.Annotation.Record.Comment))) return false;
        if (type == "Module-relative" && row.Annotation.Record.LocationKind != MemoryAnnotationLocationKind.ModuleRelative) return false;
        if (type == "Absolute" && row.Annotation.Record.LocationKind != MemoryAnnotationLocationKind.Absolute) return false;
        string state = (stateFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All states";
        if (state == "Resolved" && row.Annotation.State != MemoryAnnotationResolutionState.Resolved) return false;
        if (state == "Stale" && row.Annotation.State == MemoryAnnotationResolutionState.Resolved) return false;
        return true;
    }

    private static bool ContainsFilter(string? value, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || (value?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        _view.CustomSort = pinnedFirstBox.IsChecked == true ? new PinnedComparer() : null;
        _view.Refresh();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _memoryView == null) return;
        string expression = _memoryView.SelectedInstructionExpression;
        if (string.IsNullOrWhiteSpace(expression)) expression = _memoryView.CurrentAddress;
        var dialog = new BookmarkEditDialog(_memoryView, expression) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        Report(_service.Create(_memoryView.CreateLocation(dialog.ResolvedAddress), dialog.Edit));
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _memoryView == null || grid.SelectedItem is not BookmarkManagerRow row) return;
        var dialog = new BookmarkEditDialog(_memoryView, row.Expression, row.Annotation.Record) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        AnnotationMutationResult result = dialog.Expression.Equals(row.Expression, StringComparison.OrdinalIgnoreCase)
            ? AnnotationMutationResult.Success(row.Annotation)
            : MoveWithConflict(row.Annotation.Id, _memoryView.CreateLocation(dialog.ResolvedAddress));
        if (result.Succeeded) result = _service.Update(row.Annotation.Id, dialog.Edit);
        Report(result);
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _memoryView == null || grid.SelectedItem is not BookmarkManagerRow row) return;
        var dialog = new BookmarkEditDialog(_memoryView, row.Expression, row.Annotation.Record) { Owner = Window.GetWindow(this), Title = "Move Bookmark Address" };
        if (dialog.ShowDialog() != true) return;
        AnnotationMutationResult result = MoveWithConflict(row.Annotation.Id, _memoryView.CreateLocation(dialog.ResolvedAddress));
        if (result.Succeeded) result = _service.Update(row.Annotation.Id, dialog.Edit);
        Report(result);
    }

    private AnnotationMutationResult MoveWithConflict(MemoryAnnotationId id, MemoryLocation location)
    {
        AnnotationMutationResult result = _service!.Move(id, location, AnnotationConflictResolution.Cancel);
        if (result.Error != AnnotationMutationError.Conflict) return result;
        MessageBoxResult choice = System.Windows.MessageBox.Show(Window.GetWindow(this),
            "An annotation already exists at that address.\n\nYes: merge records\nNo: replace destination\nCancel: make no changes",
            "Bookmark address conflict", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return choice switch
        {
            MessageBoxResult.Yes => _service.Move(id, location, AnnotationConflictResolution.Merge),
            MessageBoxResult.No => _service.Move(id, location, AnnotationConflictResolution.Replace),
            _ => result
        };
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        MemoryAnnotationId[] ids = SelectedRows().Select(row => row.Annotation.Id).ToArray();
        if (ids.Length == 0) return;
        if (System.Windows.MessageBox.Show(Window.GetWindow(this), $"Delete {ids.Length} selected annotation(s)?", "Delete bookmarks",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Report(_service.Delete(ids));
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not BookmarkManagerRow row) return;
        if (row.Annotation.State != MemoryAnnotationResolutionState.Resolved || !row.Annotation.RuntimeAddress.HasValue)
        {
            Report(AnnotationMutationResult.Failure(AnnotationMutationError.StaleIdentity, row.Annotation.Diagnostic), true); return;
        }
        NavigateRequested?.Invoke(this, row.Annotation);
        ReturnToMemoryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Rebind_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _memoryView == null || grid.SelectedItem is not BookmarkManagerRow row) return;
        if (row.Annotation.Record.LocationKind != MemoryAnnotationLocationKind.Absolute) { statusText.Text = "Only absolute bookmarks require executable rebinding."; return; }
        var dialog = new BookmarkEditDialog(_memoryView, row.Expression, row.Annotation.Record) { Owner = Window.GetWindow(this), Title = "Rebind Absolute Bookmark" };
        if (dialog.ShowDialog() != true) return;
        Report(_service.RebindAbsolute(row.Annotation.Id, dialog.ResolvedAddress));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRows();
    private void Pin_Click(object sender, RoutedEventArgs e) => Bulk(new MemoryAnnotationBulkEdit(IsPinned: true));
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        string? value = Prompt("Set group", "Group name (blank clears it):");
        if (value != null) Bulk(new MemoryAnnotationBulkEdit(Group: value));
    }
    private void Color_Click(object sender, RoutedEventArgs e)
    {
        string? value = Prompt("Set bookmark color", "Hex color, for example #39A9FF:");
        if (value != null) Bulk(new MemoryAnnotationBulkEdit(Color: value));
    }
    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        string? value = Prompt("Add tags", "Comma-separated tags to add:");
        if (value != null) Bulk(new MemoryAnnotationBulkEdit(AddTags: value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)));
    }
    private void Bulk(MemoryAnnotationBulkEdit edit)
    {
        if (_service == null) return;
        Report(_service.BulkUpdate(SelectedRows().Select(x => x.Annotation.Id).ToArray(), edit));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "CrxMem annotations (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        AnnotationMutationResult preview = _service.ImportJson(File.ReadAllText(dialog.FileName), AnnotationConflictResolution.Cancel);
        if (preview.Error == AnnotationMutationError.Conflict)
        {
            MessageBoxResult choice = System.Windows.MessageBox.Show(Window.GetWindow(this),
                "The import contains address conflicts. Merge imported fields into existing records? Choose No to replace existing records.",
                "Import conflicts", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            preview = _service.ImportJson(File.ReadAllText(dialog.FileName), choice == MessageBoxResult.Yes ? AnnotationConflictResolution.Merge : AnnotationConflictResolution.Replace);
        }
        Report(preview);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CrxMem annotations (*.json)|*.json|CSV spreadsheet (*.csv)|*.csv", FileName = "CrxMem-bookmarks.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        MemoryAnnotationId[] ids = SelectedRows().Select(row => row.Annotation.Id).ToArray();
        IReadOnlyCollection<MemoryAnnotationId>? selection = ids.Length == 0 ? null : ids;
        File.WriteAllText(dialog.FileName, Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? _service.ExportCsv(selection) : _service.ExportJson(selection));
        statusText.Text = $"Exported annotations to {dialog.FileName}";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is BookmarkManagerRow row) System.Windows.Clipboard.SetText(row.Expression);
    }
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Navigate_Click(sender, e);
    private void Control_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { FocusSearch(); e.Handled = true; }
        else if (e.Key == Key.F2) { Edit_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete) { Delete_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter && !searchBox.IsKeyboardFocusWithin) { Navigate_Click(sender, e); e.Handled = true; }
    }

    private IReadOnlyList<BookmarkManagerRow> SelectedRows() => grid.SelectedItems.Cast<BookmarkManagerRow>().ToArray();
    private void Report(AnnotationMutationResult result, bool dialog = false)
    {
        statusText.Text = result.Message;
        _memoryView?.ReportStatus(result.Message);
        if (!result.Succeeded && (dialog || result.Error is AnnotationMutationError.Persistence or AnnotationMutationError.StaleIdentity))
            System.Windows.MessageBox.Show(Window.GetWindow(this), result.Message, "Bookmark Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string? Prompt(string title, string prompt)
    {
        var window = new Window { Title = title, Width = 400, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new DockPanel { Margin = new Thickness(12) };
        var box = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 8, 0, 8) };
        var button = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        button.Click += (_, _) => window.DialogResult = true;
        panel.Children.Add(new TextBlock { Text = prompt }); panel.Children.Add(box); panel.Children.Add(button); window.Content = panel;
        return window.ShowDialog() == true ? box.Text : null;
    }

    private sealed class PinnedComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not BookmarkManagerRow a || y is not BookmarkManagerRow b) return 0;
            int pinned = b.Annotation.Record.IsPinned.CompareTo(a.Annotation.Record.IsPinned);
            return pinned != 0 ? pinned : string.Compare(a.Expression, b.Expression, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class BookmarkManagerRow
{
    public BookmarkManagerRow(ResolvedMemoryAnnotation annotation, string? instruction = null) { Annotation = annotation; Instruction = instruction; }
    public ResolvedMemoryAnnotation Annotation { get; }
    public string Pin => Annotation.Record.IsPinned ? "★" : string.Empty;
    public string? Color => Annotation.Record.Color;
    public string? Label => Annotation.Record.Label;
    public string Expression => Annotation.Expression;
    public string ResolvedAddress => Annotation.RuntimeAddress?.ToString(Annotation.Record.Is64Bit ? "X16" : "X8") ?? "—";
    public string Module => Annotation.Record.ModuleIdentity?.Name ?? Annotation.Record.ExecutableIdentity?.Name ?? "—";
    public string? Instruction { get; }
    public string? Comment => Annotation.Record.Comment;
    public string? Group => Annotation.Record.Group;
    public string Tags => string.Join(", ", Annotation.Record.Tags);
    public string State => Annotation.State.ToString();
    public string Created => Annotation.Record.CreatedAt.LocalDateTime.ToString("g");
    public string Updated => Annotation.Record.UpdatedAt.LocalDateTime.ToString("g");
    public string SearchText => string.Join(' ', Label, Expression, ResolvedAddress, Module, Instruction, Comment, Group, Tags, State, Color);
}
