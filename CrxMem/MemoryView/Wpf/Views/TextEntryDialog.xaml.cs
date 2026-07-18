using System.Windows;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class TextEntryDialog : Window
{
    public TextEntryDialog(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        promptText.Text = prompt;
        valueTextBox.Text = value;
        Loaded += (_, _) => { valueTextBox.Focus(); valueTextBox.SelectAll(); };
    }

    public string Value => valueTextBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
