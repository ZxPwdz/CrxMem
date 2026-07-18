using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;

namespace CrxMem.MemoryView.Wpf.ViewModels;

public sealed class MemoryViewViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IMemoryViewDataService _dataService;
    private readonly INavigationHistoryService _history;
    private readonly AsyncRelayCommand _navigateCommand;
    private readonly AsyncRelayCommand _backCommand;
    private readonly AsyncRelayCommand _forwardCommand;
    private CancellationTokenSource? _navigationCancellation;
    private long _navigationGeneration;
    private string _addressExpression = string.Empty;
    private string _status = "Ready";
    private bool _isBusy;
    private string _sessionTitle = "No process";
    private string _moduleName = "—";
    private string _moduleBase = "—";
    private string _architecture = "—";
    private string _currentAddress = "—";
    private string _offset = "—";
    private string _protection = "—";
    private string _region = "—";
    private DisassemblyPreviewRow? _selectedInstruction;

    public MemoryViewViewModel(
        IMemoryViewDataService dataService,
        INavigationHistoryService? history = null)
    {
        _dataService = dataService;
        _history = history ?? new NavigationHistoryService();
        _navigateCommand = new AsyncRelayCommand(NavigateFromExpressionAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(AddressExpression));
        _backCommand = new AsyncRelayCommand(GoBackAsync, () => !IsBusy && _history.CanGoBack);
        _forwardCommand = new AsyncRelayCommand(GoForwardAsync, () => !IsBusy && _history.CanGoForward);
    }

    public ObservableCollection<DisassemblyPreviewRow> Instructions { get; } = new();
    public ObservableCollection<HexPreviewRow> HexRows { get; } = new();
    public ObservableCollection<string> RecentExpressions { get; } = new();
    public ICommand NavigateCommand => _navigateCommand;
    public AsyncRelayCommand RefreshCommand { get; }
    public ICommand BackCommand => _backCommand;
    public ICommand ForwardCommand => _forwardCommand;

    public string AddressExpression
    {
        get => _addressExpression;
        set { if (SetField(ref _addressExpression, value)) RefreshCommand.RaiseCanExecuteChanged(); }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }
    public string SessionTitle { get => _sessionTitle; private set => SetField(ref _sessionTitle, value); }
    public string ModuleName { get => _moduleName; private set => SetField(ref _moduleName, value); }
    public string ModuleBase { get => _moduleBase; private set => SetField(ref _moduleBase, value); }
    public string Architecture { get => _architecture; private set => SetField(ref _architecture, value); }
    public string CurrentAddress { get => _currentAddress; private set => SetField(ref _currentAddress, value); }
    public string Offset { get => _offset; private set => SetField(ref _offset, value); }
    public string Protection { get => _protection; private set => SetField(ref _protection, value); }
    public string Region { get => _region; private set => SetField(ref _region, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            _navigateCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            _backCommand.RaiseCanExecuteChanged();
            _forwardCommand.RaiseCanExecuteChanged();
        }
    }

    public DisassemblyPreviewRow? SelectedInstruction
    {
        get => _selectedInstruction;
        set
        {
            if (!SetField(ref _selectedInstruction, value)) return;
            OnPropertyChanged(nameof(SelectedInstructionAddress));
            OnPropertyChanged(nameof(SelectedInstructionBytes));
            OnPropertyChanged(nameof(SelectedInstructionText));
            OnPropertyChanged(nameof(SelectedInstructionComment));
        }
    }

    public string SelectedInstructionAddress => SelectedInstruction?.Address ?? CurrentAddress;
    public string SelectedInstructionBytes => SelectedInstruction?.Bytes ?? "—";
    public string SelectedInstructionText => SelectedInstruction?.Instruction ?? "Select an instruction";
    public string SelectedInstructionComment => SelectedInstruction?.Comment ?? "—";

    public async Task InitializeAsync(ulong requestedAddress)
    {
        ulong address = requestedAddress != 0 ? requestedAddress : _dataService.GetDefaultAddress();
        if (address == 0)
        {
            Status = "No readable module or entry point is available.";
            return;
        }

        AddressExpression = $"{address:X}";
        await NavigateAsync(address, AddressExpression, addToHistory: true);
    }

    private async Task NavigateFromExpressionAsync()
    {
        AddressExpressionResult result = await _dataService.ResolveAddressAsync(
            AddressExpression, CancellationToken.None);
        if (!result.Success)
        {
            Status = result.Error ?? "Invalid address.";
            return;
        }

        await NavigateAsync(result.Address, AddressExpression, addToHistory: true);
    }

    private async Task RefreshAsync()
    {
        AddressExpressionResult result = await _dataService.ResolveAddressAsync(
            AddressExpression, CancellationToken.None);
        if (result.Success)
        {
            await NavigateAsync(result.Address, AddressExpression, addToHistory: false);
        }
        else
        {
            Status = result.Error ?? "Invalid address.";
        }
    }

    private async Task GoBackAsync()
    {
        NavigationEntry? entry = _history.GoBack();
        if (entry != null) await NavigateAsync(entry.Address, entry.Expression, addToHistory: false, entry.SelectedAddress);
        RaiseHistoryCanExecuteChanged();
    }

    private async Task GoForwardAsync()
    {
        NavigationEntry? entry = _history.GoForward();
        if (entry != null) await NavigateAsync(entry.Address, entry.Expression, addToHistory: false, entry.SelectedAddress);
        RaiseHistoryCanExecuteChanged();
    }

    private async Task NavigateAsync(
        ulong address,
        string expression,
        bool addToHistory,
        ulong? selectedAddress = null)
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _navigationCancellation.Token;
        long generation = Interlocked.Increment(ref _navigationGeneration);
        IsBusy = true;
        Status = $"Reading 0x{address:X}…";

        try
        {
            MemoryViewSnapshot snapshot = await _dataService.LoadSnapshotAsync(address, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _navigationGeneration)) return;
            Apply(snapshot);
            AddressExpression = expression;
            if (selectedAddress.HasValue)
                SelectedInstruction = Instructions.FirstOrDefault(row => row.RuntimeAddress == selectedAddress.Value)
                    ?? Instructions.FirstOrDefault();
            if (addToHistory)
            {
                _history.Push(new NavigationEntry(address, expression, SelectedInstruction?.RuntimeAddress));
                RememberExpression(expression);
            }
            RaiseHistoryCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // A newer navigation request owns the visible status.
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Instructions.Clear();
            HexRows.Clear();
        }
        finally
        {
            if (generation == Interlocked.Read(ref _navigationGeneration)) IsBusy = false;
        }
    }

    private void Apply(MemoryViewSnapshot snapshot)
    {
        SessionTitle = $"{snapshot.ProcessName}.exe ({snapshot.ProcessId})";
        ModuleName = snapshot.ModuleName;
        ModuleBase = snapshot.ModuleBase == 0 ? "—" : snapshot.ModuleBase.ToString("X16");
        Architecture = snapshot.Architecture;
        CurrentAddress = snapshot.Address.ToString("X16");
        Offset = snapshot.Offset;
        Protection = snapshot.Protection;
        Region = snapshot.Region;
        AddressExpression = snapshot.Address.ToString("X");

        Instructions.Clear();
        foreach (DisassemblyPreviewRow row in snapshot.Instructions) Instructions.Add(row);
        HexRows.Clear();
        foreach (HexPreviewRow row in snapshot.HexRows) HexRows.Add(row);
        SelectedInstruction = Instructions.FirstOrDefault();
        Status = snapshot.Status;
        OnPropertyChanged(nameof(SelectedInstructionAddress));
    }

    public void Dispose()
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
    }

    public void LoadRecentExpressions(IEnumerable<string> expressions)
    {
        RecentExpressions.Clear();
        foreach (string expression in expressions
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(20))
            RecentExpressions.Add(expression);
    }

    private void RememberExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return;
        string? existing = RecentExpressions.FirstOrDefault(value =>
            value.Equals(expression, StringComparison.OrdinalIgnoreCase));
        if (existing != null) RecentExpressions.Remove(existing);
        RecentExpressions.Insert(0, expression);
        while (RecentExpressions.Count > 20) RecentExpressions.RemoveAt(RecentExpressions.Count - 1);
    }

    private void RaiseHistoryCanExecuteChanged()
    {
        _backCommand.RaiseCanExecuteChanged();
        _forwardCommand.RaiseCanExecuteChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try { await _execute(); }
        finally { _isExecuting = false; RaiseCanExecuteChanged(); }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
