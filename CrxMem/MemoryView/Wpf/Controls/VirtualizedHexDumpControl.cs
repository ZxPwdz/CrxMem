using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using Brush = System.Windows.Media.Brush;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace CrxMem.MemoryView.Wpf.Controls;

public sealed class VirtualizedHexDumpControl : FrameworkElement, IScrollInfo
{
    private const double AddressWidth = 155;
    private const double ByteCellWidth = 24;
    private IReadOnlyList<HexPreviewRow> _items = Array.Empty<HexPreviewRow>();
    private INotifyCollectionChanged? _observable;
    private double _verticalOffset;
    private double _horizontalOffset;
    private Size _viewport;
    private Size _extent;
    private int _selectedRow = -1;
    private int _selectedByte = -1;
    private ulong? _selectionAnchorAddress;
    private ulong? _selectionEndAddress;
    private bool _isDraggingSelection;

    public VirtualizedHexDumpControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<HexPreviewRow>), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(21d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CodeFontSizeProperty = DependencyProperty.Register(
        nameof(CodeFontSize), typeof(double), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CodeFontFamilyProperty = DependencyProperty.Register(
        nameof(CodeFontFamily), typeof(FontFamily), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FocusAddressProperty = DependencyProperty.Register(
        nameof(FocusAddress), typeof(ulong), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(0UL, OnFocusAddressChanged));

    public IEnumerable<HexPreviewRow>? ItemsSource
    {
        get => (IEnumerable<HexPreviewRow>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double CodeFontSize { get => (double)GetValue(CodeFontSizeProperty); set => SetValue(CodeFontSizeProperty, value); }
    public FontFamily CodeFontFamily { get => (FontFamily)GetValue(CodeFontFamilyProperty); set => SetValue(CodeFontFamilyProperty, value); }
    public ulong FocusAddress { get => (ulong)GetValue(FocusAddressProperty); set => SetValue(FocusAddressProperty, value); }

    public ulong? SelectedByteAddress => _selectedRow >= 0 && _selectedRow < _items.Count && _selectedByte >= 0
        ? _items[_selectedRow].RuntimeAddress + (ulong)_selectedByte
        : null;

    public byte? SelectedByteValue => _selectedRow >= 0 && _selectedRow < _items.Count &&
                                      _items[_selectedRow].Data is { } data &&
                                      _selectedByte >= 0 && _selectedByte < data.Length
        ? data[_selectedByte]
        : null;

    public ulong? SelectedRangeAddress => _selectionAnchorAddress.HasValue && _selectionEndAddress.HasValue
        ? Math.Min(_selectionAnchorAddress.Value, _selectionEndAddress.Value)
        : SelectedByteAddress;

    public byte[] SelectedRangeBytes
    {
        get
        {
            if (!SelectedRangeAddress.HasValue || !_selectionAnchorAddress.HasValue || !_selectionEndAddress.HasValue)
                return Array.Empty<byte>();
            ulong first = Math.Min(_selectionAnchorAddress.Value, _selectionEndAddress.Value);
            ulong last = Math.Max(_selectionAnchorAddress.Value, _selectionEndAddress.Value);
            ulong length = last - first + 1;
            return GetContiguousBytes(SelectedRangeAddress.Value, (int)Math.Min(length, 4096));
        }
    }

    public byte[] GetAvailableBytesFromSelection(int maximum = 256) =>
        SelectedRangeAddress is ulong address ? GetContiguousBytes(address, Math.Clamp(maximum, 1, 4096)) : Array.Empty<byte>();

    public int VisibleRowCapacity => Math.Max(1, (int)Math.Ceiling(ActualHeight / RowHeight));
    public int LastRenderedRowCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 720 : Math.Max(0, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 300 : Math.Max(0, availableSize.Height));

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateScrollInfo(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnRender(DrawingContext context)
    {
        Brush background = ResourceBrush("MemoryView.PanelBackgroundBrush", Color.FromRgb(24, 30, 36));
        Brush text = ResourceBrush("MemoryView.TextSecondaryBrush", Color.FromRgb(155, 166, 176));
        Brush address = ResourceBrush("MemoryView.CyanBrush", Colors.Cyan);
        Brush changed = ResourceBrush("MemoryView.RedBrush", Colors.IndianRed);
        Brush selection = ResourceBrush("MemoryView.SelectionBackgroundBrush", Color.FromRgb(20, 83, 139));
        Brush border = ResourceBrush("MemoryView.BorderBrush", Color.FromRgb(52, 61, 69));
        var typeface = new Typeface(CodeFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        context.DrawRectangle(background, null, new Rect(RenderSize));

        int firstVisibleRow = Math.Max(0, (int)Math.Floor(_verticalOffset / RowHeight));
        int count = Math.Min(VisibleRowCapacity + 1, Math.Max(0, _items.Count - firstVisibleRow));
        LastRenderedRowCount = count;
        for (int visible = 0; visible < count; visible++)
        {
            int rowIndex = firstVisibleRow + visible;
            HexPreviewRow row = _items[rowIndex];
            double y = rowIndex * RowHeight - _verticalOffset;
            DrawText(context, row.Address, 8 - _horizontalOffset, y + 3, address, typeface);
            byte[] data = row.Data ?? ParseBytes(row.Bytes);
            for (int byteIndex = 0; byteIndex < data.Length; byteIndex++)
            {
                double x = AddressWidth + byteIndex * ByteCellWidth - _horizontalOffset;
                ulong byteAddress = row.RuntimeAddress + (ulong)byteIndex;
                if (IsAddressSelected(byteAddress))
                    context.DrawRectangle(selection, null, new Rect(x - 2, y, ByteCellWidth, RowHeight));
                Brush byteBrush = row.ChangedByteIndices?.Contains(byteIndex) == true ? changed : text;
                DrawText(context, data[byteIndex].ToString("X2"), x, y + 3, byteBrush, typeface);
            }
            DrawText(context, row.Ascii, AddressWidth + 16 * ByteCellWidth + 10 - _horizontalOffset, y + 3, text, typeface);
            context.DrawLine(new Pen(border, 1), new Point(0, y + RowHeight), new Point(ActualWidth, y + RowHeight));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetVerticalOffset(_verticalOffset - Math.Sign(e.Delta) * RowHeight * 3);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SelectByteAt(e.GetPosition(this), resetRange: true))
        {
            _isDraggingSelection = true;
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (TryGetByteAt(e.GetPosition(this), out int row, out int byteIndex, out ulong address) && !IsAddressSelected(address))
            SelectByte(row, byteIndex, address, resetRange: true);
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isDraggingSelection && e.LeftButton == MouseButtonState.Pressed &&
            TryGetByteAt(e.GetPosition(this), out int row, out int byteIndex, out ulong address))
            SelectByte(row, byteIndex, address, resetRange: false);
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _isDraggingSelection = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    private bool SelectByteAt(Point point, bool resetRange)
    {
        if (!TryGetByteAt(point, out int row, out int byteIndex, out ulong address)) return false;
        SelectByte(row, byteIndex, address, resetRange);
        return true;
    }

    private bool TryGetByteAt(Point point, out int row, out int byteIndex, out ulong address)
    {
        row = Math.Max(0, (int)Math.Floor(_verticalOffset / RowHeight)) + (int)(point.Y / RowHeight);
        byteIndex = (int)((point.X + _horizontalOffset - AddressWidth) / ByteCellWidth);
        address = 0;
        if (row >= 0 && row < _items.Count && byteIndex >= 0 &&
            byteIndex < (_items[row].Data?.Length ?? 16))
        {
            address = _items[row].RuntimeAddress + (ulong)byteIndex;
            return true;
        }
        return false;
    }

    private void SelectByte(int row, int byteIndex, ulong address, bool resetRange)
    {
        Focus();
        _selectedRow = row;
        _selectedByte = byteIndex;
        if (resetRange || !_selectionAnchorAddress.HasValue) _selectionAnchorAddress = address;
        _selectionEndAddress = address;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && SelectedRangeBytes.Length > 0)
        {
            Clipboard.SetText(string.Join(' ', SelectedRangeBytes.Select(value => value.ToString("X2"))));
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private static void OnItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (VirtualizedHexDumpControl)sender;
        if (control._observable != null) control._observable.CollectionChanged -= control.CollectionChanged;
        control._observable = args.NewValue as INotifyCollectionChanged;
        if (control._observable != null) control._observable.CollectionChanged += control.CollectionChanged;
        control.RefreshItems();
    }

    private static void OnFocusAddressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((VirtualizedHexDumpControl)sender).ScrollToAddress((ulong)args.NewValue);

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems();

    private void RefreshItems()
    {
        _items = ItemsSource as IReadOnlyList<HexPreviewRow> ?? ItemsSource?.ToArray() ?? Array.Empty<HexPreviewRow>();
        UpdateScrollInfo(RenderSize);
        if (FocusAddress != 0) ScrollToAddress(FocusAddress);
        InvalidateVisual();
    }

    public void ScrollToAddress(ulong address)
    {
        int index = ItemsSource is IHexMemorySource source ? source.IndexOfAddress(address) :
            Enumerable.Range(0, _items.Count).FirstOrDefault(i =>
                address >= _items[i].RuntimeAddress && address < _items[i].RuntimeAddress + (ulong)Math.Max(1, _items[i].Data?.Length ?? 16), -1);
        if (index < 0) return;
        SetVerticalOffset(index * RowHeight - Math.Max(0, (ActualHeight - RowHeight) / 2));
        HexPreviewRow row = _items[index];
        int byteIndex = checked((int)Math.Min(15UL, address - row.RuntimeAddress));
        SelectByte(index, byteIndex, address, resetRange: true);
    }

    private bool IsAddressSelected(ulong address)
    {
        if (!_selectionAnchorAddress.HasValue || !_selectionEndAddress.HasValue) return false;
        ulong first = Math.Min(_selectionAnchorAddress.Value, _selectionEndAddress.Value);
        ulong last = Math.Max(_selectionAnchorAddress.Value, _selectionEndAddress.Value);
        return address >= first && address <= last;
    }

    private byte[] GetContiguousBytes(ulong address, int maximum)
    {
        var bytes = new List<byte>(maximum);
        ulong expected = address;
        foreach (HexPreviewRow row in _items.OrderBy(item => item.RuntimeAddress))
        {
            byte[] data = row.Data ?? ParseBytes(row.Bytes);
            ulong rowEnd = row.RuntimeAddress + (ulong)data.Length;
            if (expected < row.RuntimeAddress || expected >= rowEnd)
            {
                if (bytes.Count > 0) break;
                continue;
            }
            int offset = checked((int)(expected - row.RuntimeAddress));
            for (int index = offset; index < data.Length && bytes.Count < maximum; index++)
            {
                bytes.Add(data[index]);
                expected++;
            }
            if (bytes.Count >= maximum) break;
        }
        return bytes.ToArray();
    }

    private static byte[] ParseBytes(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => byte.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out byte result) ? result : (byte)0)
        .ToArray();

    private Brush ResourceBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private void DrawText(DrawingContext context, string text, double x, double y, Brush brush, Typeface typeface) =>
        context.DrawText(new FormattedText(
            text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            CodeFontSize,
            brush,
            1), new Point(x, y));

    private void UpdateScrollInfo(Size viewport)
    {
        _viewport = viewport;
        _extent = new Size(Math.Max(viewport.Width, AddressWidth + 16 * ByteCellWidth + 180), _items.Count * RowHeight);
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; } = true;
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
    public void PageDown() => SetVerticalOffset(_verticalOffset + Math.Max(RowHeight, _viewport.Height));
    public void PageUp() => SetVerticalOffset(_verticalOffset - Math.Max(RowHeight, _viewport.Height));
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
        _verticalOffset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }
}
