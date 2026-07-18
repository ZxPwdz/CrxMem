using System.Windows;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class MetadataEditDialog : Window
{
    public MetadataEditDialog(string? comment, bool isBookmarked, string? label)
    {
        InitializeComponent();
        commentBox.Text = comment ?? string.Empty;
        bookmarkBox.IsChecked = isBookmarked;
        labelBox.Text = label ?? string.Empty;
        Loaded += (_, _) => { commentBox.Focus(); commentBox.SelectAll(); };
    }

    public string Comment => commentBox.Text.Trim();
    public bool IsBookmarked => bookmarkBox.IsChecked == true;
    public string BookmarkLabel => labelBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
