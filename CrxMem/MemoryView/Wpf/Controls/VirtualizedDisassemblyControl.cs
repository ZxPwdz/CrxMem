using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CrxMem.MemoryView.Wpf.Models;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace CrxMem.MemoryView.Wpf.Controls;

public sealed class VirtualizedDisassemblyControl : FrameworkElement, IScrollInfo
{
    private const double HeaderHeight = 27;
    private const double GutterWidth = 38;
    private const double AddressWidth = 150;
    private const double BytesWidth = 210;
    private IReadOnlyList<DisassemblyPreviewRow> _items = Array.Empty<DisassemblyPreviewRow>();
    private INotifyCollectionChanged? _observableSource;
    private double _verticalOffset;
    private double _horizontalOffset;
    private Size _viewport;
    private Size _extent;

    public VirtualizedDisassemblyControl()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<DisassemblyPreviewRow>), typeof(VirtualizedDisassemblyControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(DisassemblyPreviewRow), typeof(VirtualizedDisassemblyControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            OnSelectedItemChanged));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(VirtualizedDisassemblyControl),
        new FrameworkPropertyMetadata(21d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CodeFontSizeProperty = DependencyProperty.Register(
        nameof(CodeFontSize), typeof(double), typeof(VirtualizedDisassemblyControl),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CodeFontFamilyProperty = DependencyProperty.Register(
        nameof(CodeFontFamily), typeof(FontFamily), typeof(VirtualizedDisassemblyControl),
        new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<DisassemblyPreviewRow>? ItemsSource
    {
        get => (IEnumerable<DisassemblyPreviewRow>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public DisassemblyPreviewRow? SelectedItem
    {
        get => (DisassemblyPreviewRow?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double CodeFontSize { get => (double)GetValue(CodeFontSizeProperty); set => SetValue(CodeFontSizeProperty, value); }
    public FontFamily CodeFontFamily { get => (FontFamily)GetValue(CodeFontFamilyProperty); set => SetValue(CodeFontFamilyProperty, value); }

    public int VisibleRowCapacity => Math.Max(0, (int)Math.Ceiling(Math.Max(0, ActualHeight - HeaderHeight) / RowHeight) + 1);
    public int LastRenderedRowCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateScrollInfo(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Brush background = ResourceBrush("MemoryView.PanelBackgroundBrush", Color.FromRgb(24, 30, 36));
        Brush header = ResourceBrush("MemoryView.PanelRaisedBrush", Color.FromRgb(31, 38, 45));
        Brush border = ResourceBrush("MemoryView.BorderBrush", Color.FromRgb(52, 61, 69));
        Brush primary = ResourceBrush("MemoryView.TextPrimaryBrush", Color.FromRgb(215, 221, 227));
        Brush secondary = ResourceBrush("MemoryView.TextSecondaryBrush", Color.FromRgb(155, 166, 176));
        Brush selection = ResourceBrush("MemoryView.SelectionBackgroundBrush", Color.FromRgb(20, 83, 139));
        var borderPen = new Pen(border, 1);
        drawingContext.DrawRectangle(background, null, new Rect(RenderSize));
        drawingContext.DrawRectangle(header, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        DrawHeader(drawingContext, primary, borderPen);

        double dataHeight = Math.Max(0, ActualHeight - HeaderHeight);
        int first = Math.Max(0, (int)Math.Floor(_verticalOffset / RowHeight));
        int count = Math.Min(_items.Count - first, Math.Max(0, (int)Math.Ceiling(dataHeight / RowHeight) + 1));
        LastRenderedRowCount = count;
        var typeface = new Typeface(CodeFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (int visible = 0; visible < count; visible++)
        {
            int index = first + visible;
            double y = HeaderHeight + index * RowHeight - _verticalOffset;
            DisassemblyPreviewRow row = _items[index];
            if (ReferenceEquals(row, SelectedItem))
                drawingContext.DrawRectangle(selection, null, new Rect(0, y, ActualWidth, RowHeight));
            drawingContext.DrawLine(borderPen, new Point(0, y + RowHeight), new Point(ActualWidth, y + RowHeight));

            DrawText(drawingContext, row.Marker, 12 - _horizontalOffset, y + 3,
                ResourceBrush("MemoryView.BookmarkBrush", Colors.DeepSkyBlue), typeface);
            DrawText(drawingContext, row.Address, GutterWidth - _horizontalOffset, y + 3,
                ResourceBrush("MemoryView.CyanBrush", Colors.Cyan), typeface);
            DrawText(drawingContext, row.Bytes, GutterWidth + AddressWidth - _horizontalOffset, y + 3, secondary, typeface);
            DrawInstruction(drawingContext, row, GutterWidth + AddressWidth + BytesWidth - _horizontalOffset, y + 3, typeface, primary);
            double commentX = Math.Max(690, ActualWidth * 0.68) - _horizontalOffset;
            DrawText(drawingContext, row.Comment, commentX, y + 3,
                ResourceBrush("MemoryView.GreenBrush", Colors.LightGreen), typeface);
        }

        DrawVisibleBranchFlows(drawingContext, first, count);
    }

    private void DrawHeader(DrawingContext context, Brush text, Pen border)
    {
        var typeface = new Typeface("Segoe UI");
        DrawText(context, "State", 5, 5, text, typeface);
        DrawText(context, "Address", GutterWidth, 5, text, typeface);
        DrawText(context, "Bytes", GutterWidth + AddressWidth, 5, text, typeface);
        DrawText(context, "Instruction", GutterWidth + AddressWidth + BytesWidth, 5, text, typeface);
        DrawText(context, "Comment / Symbol", Math.Max(690, ActualWidth * 0.68), 5, text, typeface);
        foreach (double x in new[] { GutterWidth, GutterWidth + AddressWidth, GutterWidth + AddressWidth + BytesWidth, Math.Max(690, ActualWidth * 0.68) })
            context.DrawLine(border, new Point(x - _horizontalOffset, 0), new Point(x - _horizontalOffset, ActualHeight));
        context.DrawLine(border, new Point(0, HeaderHeight), new Point(ActualWidth, HeaderHeight));
    }

    private void DrawInstruction(DrawingContext context, DisassemblyPreviewRow row, double x, double y, Typeface typeface, Brush fallback)
    {
        if (row.Tokens == null || row.Tokens.Count == 0)
        {
            DrawText(context, row.Instruction, x, y, fallback, typeface);
            return;
        }

        foreach (DisassemblyToken token in row.Tokens)
        {
            Brush brush = token.Kind switch
            {
                DisassemblyTokenKind.Mnemonic => ResourceBrush("MemoryView.BlueBrush", Colors.CornflowerBlue),
                DisassemblyTokenKind.Register => ResourceBrush("MemoryView.PurpleBrush", Colors.MediumPurple),
                DisassemblyTokenKind.Number or DisassemblyTokenKind.Address => ResourceBrush("MemoryView.YellowBrush", Colors.Gold),
                DisassemblyTokenKind.Keyword => ResourceBrush("MemoryView.CyanBrush", Colors.Cyan),
                _ => fallback
            };
            FormattedText formatted = CreateText(token.Text, brush, typeface);
            context.DrawText(formatted, new Point(x, y));
            x += formatted.WidthIncludingTrailingWhitespace;
        }
    }

    private void DrawVisibleBranchFlows(DrawingContext context, int first, int count)
    {
        if (count <= 0) return;
        var addressToIndex = new Dictionary<ulong, int>();
        for (int index = first; index < first + count; index++) addressToIndex[_items[index].RuntimeAddress] = index;
        foreach (int index in Enumerable.Range(first, count))
        {
            ulong? target = _items[index].FlowTarget;
            if (!target.HasValue || !addressToIndex.TryGetValue(target.Value, out int targetIndex)) continue;
            double fromY = HeaderHeight + index * RowHeight - _verticalOffset + RowHeight / 2;
            double toY = HeaderHeight + targetIndex * RowHeight - _verticalOffset + RowHeight / 2;
            Brush brush = targetIndex >= index
                ? ResourceBrush("MemoryView.GreenBrush", Colors.LightGreen)
                : ResourceBrush("MemoryView.RedBrush", Colors.IndianRed);
            var pen = new Pen(brush, 1);
            double x = 29;
            context.DrawLine(pen, new Point(x, fromY), new Point(x, toY));
            context.DrawLine(pen, new Point(x, toY), new Point(x + 5, toY));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        SelectRowAt(e.GetPosition(this));
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        SelectRowAt(e.GetPosition(this));
        base.OnMouseRightButtonDown(e);
    }

    private void SelectRowAt(Point point)
    {
        Focus();
        if (point.Y < HeaderHeight) return;
        int index = (int)Math.Floor((_verticalOffset + point.Y - HeaderHeight) / RowHeight);
        SelectIndex(index);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SetVerticalOffset(_verticalOffset - Math.Sign(e.Delta) * RowHeight * 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int index = SelectedItem == null ? 0 : IndexOf(SelectedItem);
        int page = Math.Max(1, VisibleRowCapacity - 1);
        int next = e.Key switch
        {
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.PageUp => index - page,
            Key.PageDown => index + page,
            Key.Home => 0,
            Key.End => _items.Count - 1,
            _ => index
        };
        if (next != index || e.Key is Key.Home or Key.End)
        {
            SelectIndex(Math.Clamp(next, 0, Math.Max(0, _items.Count - 1)));
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && SelectedItem != null)
        {
            Clipboard.SetText($"{SelectedItem.Address}  {SelectedItem.Bytes}  {SelectedItem.Instruction}");
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        SelectedItem = _items[index];
        double rowTop = index * RowHeight;
        double dataViewport = Math.Max(0, _viewport.Height - HeaderHeight);
        if (rowTop < _verticalOffset) SetVerticalOffset(rowTop);
        else if (rowTop + RowHeight > _verticalOffset + dataViewport)
            SetVerticalOffset(rowTop + RowHeight - dataViewport);
    }

    private int IndexOf(DisassemblyPreviewRow row)
    {
        for (int index = 0; index < _items.Count; index++) if (ReferenceEquals(_items[index], row)) return index;
        return -1;
    }

    private static void OnItemsSourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (VirtualizedDisassemblyControl)sender;
        if (control._observableSource != null) control._observableSource.CollectionChanged -= control.Source_CollectionChanged;
        control._observableSource = args.NewValue as INotifyCollectionChanged;
        if (control._observableSource != null) control._observableSource.CollectionChanged += control.Source_CollectionChanged;
        control.RefreshItems();
    }

    private void Source_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems();

    private void RefreshItems()
    {
        _items = ItemsSource?.ToArray() ?? Array.Empty<DisassemblyPreviewRow>();
        UpdateScrollInfo(RenderSize);
        if (SelectedItem != null && IndexOf(SelectedItem) < 0) SelectedItem = _items.FirstOrDefault();
        InvalidateVisual();
    }

    private static void OnSelectedItemChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((VirtualizedDisassemblyControl)sender).InvalidateVisual();

    private Brush ResourceBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private FormattedText CreateText(string text, Brush brush, Typeface typeface) => new(
        text ?? string.Empty, System.Globalization.CultureInfo.CurrentUICulture,
        System.Windows.FlowDirection.LeftToRight, typeface, CodeFontSize, brush, 1);
    private void DrawText(DrawingContext context, string text, double x, double y, Brush brush, Typeface typeface) =>
        context.DrawText(CreateText(text, brush, typeface), new Point(x, y));

    private void UpdateScrollInfo(Size viewport)
    {
        _viewport = viewport;
        _extent = new Size(Math.Max(viewport.Width, 980), HeaderHeight + _items.Count * RowHeight);
        _verticalOffset = ClampVertical(_verticalOffset);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    private double ClampVertical(double offset) => Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentHeight => _extent.Height;
    public double ExtentWidth => _extent.Width;
    public double HorizontalOffset => _horizontalOffset;
    public double VerticalOffset => _verticalOffset;
    public double ViewportHeight => _viewport.Height;
    public double ViewportWidth => _viewport.Width;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);
    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);
    public void LineLeft() => SetHorizontalOffset(_horizontalOffset - 24);
    public void LineRight() => SetHorizontalOffset(_horizontalOffset + 24);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + RowHeight * 3);
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - RowHeight * 3);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public void PageDown() => SetVerticalOffset(_verticalOffset + Math.Max(RowHeight, _viewport.Height - HeaderHeight));
    public void PageUp() => SetVerticalOffset(_verticalOffset - Math.Max(RowHeight, _viewport.Height - HeaderHeight));
    public void PageLeft() => SetHorizontalOffset(_horizontalOffset - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_horizontalOffset + _viewport.Width);
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
    public void SetHorizontalOffset(double offset)
    {
        _horizontalOffset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }
    public void SetVerticalOffset(double offset)
    {
        _verticalOffset = ClampVertical(offset);
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }
}
