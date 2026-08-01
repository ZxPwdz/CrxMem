using System.Windows;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class BookmarkEditDialog : Window
{
    private readonly MemoryViewViewModel _memoryView;

    public BookmarkEditDialog(MemoryViewViewModel memoryView, string expression, MemoryAnnotationRecord? record = null)
    {
        _memoryView = memoryView;
        InitializeComponent();
        addressBox.Text = expression;
        labelBox.Text = record?.Label ?? string.Empty;
        commentBox.Text = record?.Comment ?? string.Empty;
        groupBox.Text = record?.Group ?? string.Empty;
        tagsBox.Text = record == null ? string.Empty : string.Join(", ", record.Tags);
        colorBox.Text = record?.Color ?? "#39A9FF";
        bookmarkBox.IsChecked = record?.IsBookmarked ?? true;
        pinnedBox.IsChecked = record?.IsPinned ?? false;
    }

    public string Expression => addressBox.Text.Trim();
    public ulong ResolvedAddress { get; private set; }
    public MemoryAnnotationEdit Edit => new(labelBox.Text, commentBox.Text, groupBox.Text,
        tagsBox.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries), colorBox.Text,
        pinnedBox.IsChecked == true, bookmarkBox.IsChecked == true);

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        AddressExpressionResult result = await _memoryView.ResolveAddressExpressionAsync(Expression);
        if (!result.Success)
        {
            previewText.Text = result.Error ?? "Address could not be resolved.";
            addressBox.Focus(); addressBox.SelectAll();
            return;
        }
        ResolvedAddress = result.Address;
        DialogResult = true;
    }
}
