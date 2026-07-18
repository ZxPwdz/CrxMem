using System.Windows;
using System.Windows.Input;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;

namespace CrxMem.MemoryView.Wpf.Views;

public partial class MemoryViewWindow : Window
{
    private readonly ulong _startAddress;
    private readonly MemoryViewViewModel _viewModel;

    public MemoryViewWindow(ProcessAccess process, IntPtr startAddress = default)
    {
        InitializeComponent();
        _startAddress = unchecked((ulong)startAddress.ToInt64());
        _viewModel = new MemoryViewViewModel(new MemoryViewDataService(process));
        DataContext = _viewModel;
        Loaded += MemoryViewWindow_Loaded;
        Closed += MemoryViewWindow_Closed;
        PreviewKeyDown += MemoryViewWindow_PreviewKeyDown;
    }

    private async void MemoryViewWindow_Loaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync(_startAddress);

    private void MemoryViewWindow_Closed(object? sender, EventArgs e) => _viewModel.Dispose();

    private void MemoryViewWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
        {
            addressBox.Focus();
            addressBox.SelectAll();
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
        addressBox.SelectAll();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e) =>
        System.Windows.MessageBox.Show(this,
            "CrxMem Memory View WPF Preview\n\nRead-only process inspection shell. Debugging and patching commands remain disabled until their services are verified.",
            "About Memory View", MessageBoxButton.OK, MessageBoxImage.Information);
}
