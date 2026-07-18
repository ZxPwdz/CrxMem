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
    private readonly AsyncRelayCommand _navigateCommand;
    private CancellationTokenSource? _navigationCancellation;
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

    public MemoryViewViewModel(IMemoryViewDataService dataService)
    {
        _dataService = dataService;
        _navigateCommand = new AsyncRelayCommand(NavigateFromExpressionAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(AddressExpression));
    }

    public ObservableCollection<DisassemblyPreviewRow> Instructions { get; } = new();
    public ObservableCollection<HexPreviewRow> HexRows { get; } = new();
    public ICommand NavigateCommand => _navigateCommand;
    public AsyncRelayCommand RefreshCommand { get; }

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
        await NavigateAsync(address);
    }

    private async Task NavigateFromExpressionAsync()
    {
        if (!_dataService.TryResolveAddress(AddressExpression, out ulong address, out string? error))
        {
            Status = error ?? "Invalid address.";
            return;
        }

        await NavigateAsync(address);
    }

    private async Task RefreshAsync()
    {
        if (_dataService.TryResolveAddress(AddressExpression, out ulong address, out string? error))
        {
            await NavigateAsync(address);
        }
        else
        {
            Status = error ?? "Invalid address.";
        }
    }

    private async Task NavigateAsync(ulong address)
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _navigationCancellation.Token;
        IsBusy = true;
        Status = $"Reading 0x{address:X}…";

        try
        {
            MemoryViewSnapshot snapshot = await _dataService.LoadSnapshotAsync(address, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Apply(snapshot);
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
            if (!cancellationToken.IsCancellationRequested) IsBusy = false;
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
