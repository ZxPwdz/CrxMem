using System.Collections.Specialized;
using System.Windows;
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

namespace CrxMem.MemoryView.Wpf.Controls;

public sealed class VirtualizedHexDumpControl : FrameworkElement
{
    private const double RowHeight = 21;
    private const double AddressWidth = 155;
    private const double ByteCellWidth = 24;
    private IReadOnlyList<HexPreviewRow> _items = Array.Empty<HexPreviewRow>();
    private INotifyCollectionChanged? _observable;
    private int _firstVisibleRow;
    private int _selectedRow = -1;
    private int _selectedByte = -1;

    public VirtualizedHexDumpControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<HexPreviewRow>), typeof(VirtualizedHexDumpControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public IEnumerable<HexPreviewRow>? ItemsSource
    {
        get => (IEnumerable<HexPreviewRow>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ulong? SelectedByteAddress => _selectedRow >= 0 && _selectedRow < _items.Count && _selectedByte >= 0
        ? _items[_selectedRow].RuntimeAddress + (ulong)_selectedByte
        : null;

    public byte? SelectedByteValue => _selectedRow >= 0 && _selectedRow < _items.Count &&
                                      _items[_selectedRow].Data is { } data &&
                                      _selectedByte >= 0 && _selectedByte < data.Length
        ? data[_selectedByte]
        : null;

    public int VisibleRowCapacity => Math.Max(1, (int)Math.Ceiling(ActualHeight / RowHeight));
    public int LastRenderedRowCount { get; private set; }

    protected override void OnRender(DrawingContext context)
    {
        Brush background = ResourceBrush("MemoryView.PanelBackgroundBrush", Color.FromRgb(24, 30, 36));
        Brush text = ResourceBrush("MemoryView.TextSecondaryBrush", Color.FromRgb(155, 166, 176));
        Brush address = ResourceBrush("MemoryView.CyanBrush", Colors.Cyan);
        Brush changed = ResourceBrush("MemoryView.RedBrush", Colors.IndianRed);
        Brush selection = ResourceBrush("MemoryView.SelectionBackgroundBrush", Color.FromRgb(20, 83, 139));
        Brush border = ResourceBrush("MemoryView.BorderBrush", Color.FromRgb(52, 61, 69));
        var typeface = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        context.DrawRectangle(background, null, new Rect(RenderSize));

        int count = Math.Min(VisibleRowCapacity + 1, Math.Max(0, _items.Count - _firstVisibleRow));
        LastRenderedRowCount = count;
        for (int visible = 0; visible < count; visible++)
        {
            int rowIndex = _firstVisibleRow + visible;
            HexPreviewRow row = _items[rowIndex];
            double y = visible * RowHeight;
            DrawText(context, row.Address, 8, y + 3, address, typeface);
            byte[] data = row.Data ?? ParseBytes(row.Bytes);
            for (int byteIndex = 0; byteIndex < data.Length; byteIndex++)
            {
                double x = AddressWidth + byteIndex * ByteCellWidth;
                if (rowIndex == _selectedRow && byteIndex == _selectedByte)
                    context.DrawRectangle(selection, null, new Rect(x - 2, y, ByteCellWidth, RowHeight));
                Brush byteBrush = row.ChangedByteIndices?.Contains(byteIndex) == true ? changed : text;
                DrawText(context, data[byteIndex].ToString("X2"), x, y + 3, byteBrush, typeface);
            }
            DrawText(context, row.Ascii, AddressWidth + 16 * ByteCellWidth + 10, y + 3, text, typeface);
            context.DrawLine(new Pen(border, 1), new Point(0, y + RowHeight), new Point(ActualWidth, y + RowHeight));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int maximum = Math.Max(0, _items.Count - VisibleRowCapacity);
        _firstVisibleRow = Math.Clamp(_firstVisibleRow - Math.Sign(e.Delta) * 3, 0, maximum);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Point point = e.GetPosition(this);
        int row = _firstVisibleRow + (int)(point.Y / RowHeight);
        int byteIndex = (int)((point.X - AddressWidth) / ByteCellWidth);
        if (row >= 0 && row < _items.Count && byteIndex >= 0 &&
            byteIndex < (_items[row].Data?.Length ?? 16))
        {
            _selectedRow = row;
            _selectedByte = byteIndex;
            InvalidateVisual();
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control &&
            SelectedByteAddress.HasValue && SelectedByteValue.HasValue)
        {
            Clipboard.SetText($"0x{SelectedByteAddress:X}: {SelectedByteValue:X2}");
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

    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems();

    private void RefreshItems()
    {
        _items = ItemsSource?.ToArray() ?? Array.Empty<HexPreviewRow>();
        _firstVisibleRow = Math.Clamp(_firstVisibleRow, 0, Math.Max(0, _items.Count - VisibleRowCapacity));
        InvalidateVisual();
    }

    private static byte[] ParseBytes(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => byte.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out byte result) ? result : (byte)0)
        .ToArray();

    private Brush ResourceBrush(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private static void DrawText(DrawingContext context, string text, double x, double y, Brush brush, Typeface typeface) =>
        context.DrawText(new FormattedText(
            text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            12,
            brush,
            1), new Point(x, y));
}
