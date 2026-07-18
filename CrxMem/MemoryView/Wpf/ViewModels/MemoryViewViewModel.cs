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
    private readonly IDebugSessionService? _debugSession;
    private readonly IRegisterService? _registerService;
    private readonly IStackService? _stackService;
    private readonly IWatchService? _watchService;
    private readonly IPatchService? _patchService;
    private readonly SynchronizationContext? _uiContext;
    private readonly AsyncRelayCommand _navigateCommand;
    private readonly AsyncRelayCommand _backCommand;
    private readonly AsyncRelayCommand _forwardCommand;
    private readonly AsyncRelayCommand _attachCommand;
    private readonly AsyncRelayCommand _detachCommand;
    private readonly AsyncRelayCommand _pauseCommand;
    private readonly AsyncRelayCommand _resumeCommand;
    private readonly AsyncRelayCommand _addWatchCommand;
    private readonly AsyncRelayCommand _applyPatchCommand;
    private readonly AsyncRelayCommand _nopInstructionCommand;
    private readonly AsyncRelayCommand _undoPatchCommand;
    private readonly AsyncRelayCommand _redoPatchCommand;
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
    private string _debugStateText = "Inspection only";
    private string _debugStopReason = "Debugger detached";
    private bool _hasRegisterSnapshot;
    private string _flagsText = "—";
    private string _watchExpression = string.Empty;
    private RegisterSnapshot? _lastRegisterSnapshot;
    private string _patchBytes = string.Empty;
    private bool _padPatchWithNops = true;
    private ulong _moduleBaseAddress;

    public MemoryViewViewModel(
        IMemoryViewDataService dataService,
        INavigationHistoryService? history = null,
        IDebugSessionService? debugSession = null,
        IRegisterService? registerService = null,
        IStackService? stackService = null,
        IWatchService? watchService = null,
        IPatchService? patchService = null)
    {
        _dataService = dataService;
        _history = history ?? new NavigationHistoryService();
        _debugSession = debugSession;
        _registerService = registerService;
        _stackService = stackService;
        _watchService = watchService;
        _patchService = patchService;
        _uiContext = SynchronizationContext.Current;
        _navigateCommand = new AsyncRelayCommand(NavigateFromExpressionAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(AddressExpression));
        _backCommand = new AsyncRelayCommand(GoBackAsync, () => !IsBusy && _history.CanGoBack);
        _forwardCommand = new AsyncRelayCommand(GoForwardAsync, () => !IsBusy && _history.CanGoForward);
        _attachCommand = new AsyncRelayCommand(AttachDebuggerAsync,
            () => !IsBusy && _debugSession?.Snapshot.State == DebugSessionState.Detached);
        _detachCommand = new AsyncRelayCommand(DetachDebuggerAsync,
            () => !IsBusy && _debugSession?.Snapshot.State is not null and not DebugSessionState.Detached);
        _pauseCommand = new AsyncRelayCommand(PauseDebuggerAsync,
            () => !IsBusy && _debugSession?.Snapshot.State == DebugSessionState.Running);
        _resumeCommand = new AsyncRelayCommand(ResumeDebuggerAsync,
            () => !IsBusy && _debugSession?.Snapshot.State == DebugSessionState.Paused);
        _addWatchCommand = new AsyncRelayCommand(AddWatchAsync,
            () => !IsBusy && _watchService != null && !string.IsNullOrWhiteSpace(WatchExpression));
        _applyPatchCommand = new AsyncRelayCommand(ApplyPatchAsync, CanPatchSelection);
        _nopInstructionCommand = new AsyncRelayCommand(NopInstructionAsync, CanPatchSelection);
        _undoPatchCommand = new AsyncRelayCommand(UndoPatchAsync,
            () => !IsBusy && _patchService?.CanUndo == true);
        _redoPatchCommand = new AsyncRelayCommand(RedoPatchAsync,
            () => !IsBusy && _patchService?.CanRedo == true);
        if (_debugSession != null) _debugSession.StateChanged += DebugSession_StateChanged;
    }

    public ObservableCollection<DisassemblyPreviewRow> Instructions { get; } = new();
    public ObservableCollection<HexPreviewRow> HexRows { get; } = new();
    public ObservableCollection<string> RecentExpressions { get; } = new();
    public ObservableCollection<RegisterValue> Registers { get; } = new();
    public ObservableCollection<StackPreviewRow> StackRows { get; } = new();
    public ObservableCollection<WatchPreviewRow> Watches { get; } = new();
    public ICommand NavigateCommand => _navigateCommand;
    public AsyncRelayCommand RefreshCommand { get; }
    public ICommand BackCommand => _backCommand;
    public ICommand ForwardCommand => _forwardCommand;
    public ICommand AttachCommand => _attachCommand;
    public ICommand DetachCommand => _detachCommand;
    public ICommand PauseCommand => _pauseCommand;
    public ICommand ResumeCommand => _resumeCommand;
    public ICommand AddWatchCommand => _addWatchCommand;
    public ICommand ApplyPatchCommand => _applyPatchCommand;
    public ICommand NopInstructionCommand => _nopInstructionCommand;
    public ICommand UndoPatchCommand => _undoPatchCommand;
    public ICommand RedoPatchCommand => _redoPatchCommand;

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
    public string DebugStateText { get => _debugStateText; private set => SetField(ref _debugStateText, value); }
    public string DebugStopReason { get => _debugStopReason; private set => SetField(ref _debugStopReason, value); }
    public string FlagsText { get => _flagsText; private set => SetField(ref _flagsText, value); }
    public bool HasRegisterSnapshot { get => _hasRegisterSnapshot; private set => SetField(ref _hasRegisterSnapshot, value); }
    public string WatchExpression
    {
        get => _watchExpression;
        set { if (SetField(ref _watchExpression, value)) _addWatchCommand.RaiseCanExecuteChanged(); }
    }

    public string PatchBytes
    {
        get => _patchBytes;
        set { if (SetField(ref _patchBytes, value)) _applyPatchCommand.RaiseCanExecuteChanged(); }
    }

    public bool PadPatchWithNops
    {
        get => _padPatchWithNops;
        set => SetField(ref _padPatchWithNops, value);
    }

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
            RaiseDebugCanExecuteChanged();
            RaisePatchCanExecuteChanged();
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
            PatchBytes = value?.Bytes ?? string.Empty;
            RaisePatchCanExecuteChanged();
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
            await RefreshWatchesAsync();
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
        _moduleBaseAddress = snapshot.ModuleBase;
        Architecture = snapshot.Architecture;
        CurrentAddress = snapshot.Address.ToString("X16");
        Offset = snapshot.Offset;
        Protection = snapshot.Protection;
        Region = snapshot.Region;
        AddressExpression = snapshot.Address.ToString("X");

        Instructions.Clear();
        foreach (DisassemblyPreviewRow row in snapshot.Instructions) Instructions.Add(row);
        Dictionary<ulong, byte[]> previousHex = HexRows
            .Where(row => row.Data != null)
            .ToDictionary(row => row.RuntimeAddress, row => row.Data!);
        HexRows.Clear();
        foreach (HexPreviewRow row in snapshot.HexRows)
        {
            IReadOnlySet<int>? changed = null;
            if (row.Data != null && previousHex.TryGetValue(row.RuntimeAddress, out byte[]? oldData))
            {
                changed = Enumerable.Range(0, Math.Min(row.Data.Length, oldData.Length))
                    .Where(index => row.Data[index] != oldData[index])
                    .ToHashSet();
            }
            HexRows.Add(row with { ChangedByteIndices = changed });
        }
        SelectedInstruction = Instructions.FirstOrDefault();
        Status = snapshot.Status;
        OnPropertyChanged(nameof(SelectedInstructionAddress));
    }

    public void Dispose()
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        if (_debugSession != null)
        {
            _debugSession.StateChanged -= DebugSession_StateChanged;
            if (_debugSession is IDisposable disposable) disposable.Dispose();
        }
        if (_patchService is IDisposable patchDisposable) patchDisposable.Dispose();
    }

    private async Task AttachDebuggerAsync()
    {
        if (_debugSession == null) return;
        try
        {
            Status = "Attaching debugger…";
            await _debugSession.AttachAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Debugger attach failed: {ex.Message}";
        }
        RaiseDebugCanExecuteChanged();
    }

    private async Task DetachDebuggerAsync()
    {
        if (_debugSession == null) return;
        try
        {
            Status = "Detaching debugger…";
            await _debugSession.DetachAsync(CancellationToken.None);
            Registers.Clear();
            HasRegisterSnapshot = false;
            StackRows.Clear();
            _lastRegisterSnapshot = null;
        }
        catch (Exception ex)
        {
            Status = $"Debugger detach failed: {ex.Message}";
        }
        RaiseDebugCanExecuteChanged();
    }

    private async Task PauseDebuggerAsync()
    {
        if (_debugSession == null) return;
        try { await _debugSession.PauseAsync(CancellationToken.None); }
        catch (Exception ex) { Status = $"Pause failed: {ex.Message}"; }
        RaiseDebugCanExecuteChanged();
    }

    private async Task ResumeDebuggerAsync()
    {
        if (_debugSession == null) return;
        try { await _debugSession.ResumeAsync(CancellationToken.None); }
        catch (Exception ex) { Status = $"Resume failed: {ex.Message}"; }
        RaiseDebugCanExecuteChanged();
    }

    private void DebugSession_StateChanged(object? sender, DebugSessionSnapshot snapshot)
    {
        void Apply() => _ = ApplyDebugSnapshotAsync(snapshot);
        if (_uiContext != null) _uiContext.Post(_ => Apply(), null);
        else Apply();
    }

    private async Task ApplyDebugSnapshotAsync(DebugSessionSnapshot snapshot)
    {
        DebugStateText = snapshot.State.ToString();
        DebugStopReason = snapshot.StopReason ?? snapshot.Error ??
            (snapshot.State == DebugSessionState.Running ? "Target running" : "Debugger detached");
        Status = snapshot.Error ?? $"Debugger: {snapshot.State}";
        RaiseDebugCanExecuteChanged();

        if (!snapshot.CanReadThreadContext || _registerService == null) return;
        RegisterSnapshot? previous = HasRegisterSnapshot && Registers.Count > 0
            ? new RegisterSnapshot(
                snapshot.SelectedThreadId!.Value,
                Architecture == "x64",
                Registers.ToArray(),
                Registers.FirstOrDefault(value => value.Name is "RIP" or "EIP")?.Value ?? 0,
                Registers.FirstOrDefault(value => value.Name is "RSP" or "ESP")?.Value ?? 0,
                0,
                DateTimeOffset.UtcNow)
            : null;
        RegisterCaptureResult result = await _registerService.CaptureAsync(snapshot, previous, CancellationToken.None);
        if (_debugSession?.Snapshot.State != DebugSessionState.Paused ||
            _debugSession.Snapshot.SelectedThreadId != snapshot.SelectedThreadId) return;
        if (!result.IsAvailable || result.Snapshot == null)
        {
            Registers.Clear();
            HasRegisterSnapshot = false;
            Status = result.Error ?? "Registers unavailable.";
            return;
        }

        Registers.Clear();
        foreach (RegisterValue register in result.Snapshot.Registers) Registers.Add(register);
        _lastRegisterSnapshot = result.Snapshot;
        FlagsText = $"0x{result.Snapshot.Flags:X8}";
        HasRegisterSnapshot = true;
        SelectedInstruction = Instructions.FirstOrDefault(row =>
            row.RuntimeAddress == result.Snapshot.InstructionPointer) ?? SelectedInstruction;
        await RefreshStackAsync(result.Snapshot);
        await RefreshWatchesAsync();
    }

    private async Task RefreshStackAsync(RegisterSnapshot snapshot)
    {
        if (_stackService == null) return;
        IReadOnlyList<StackPreviewRow> rows = await _stackService.ReadAsync(snapshot, 128, CancellationToken.None);
        if (_lastRegisterSnapshot != snapshot) return;
        StackRows.Clear();
        foreach (StackPreviewRow row in rows) StackRows.Add(row);
    }

    private async Task AddWatchAsync()
    {
        if (_watchService == null || string.IsNullOrWhiteSpace(WatchExpression)) return;
        string expression = WatchExpression.Trim();
        WatchPreviewRow row = await _watchService.EvaluateAsync(expression, null, CancellationToken.None);
        Watches.Add(row);
        WatchExpression = string.Empty;
    }

    private async Task RefreshWatchesAsync()
    {
        if (_watchService == null || Watches.Count == 0) return;
        WatchPreviewRow[] existing = Watches.ToArray();
        for (int index = 0; index < existing.Length; index++)
        {
            WatchPreviewRow refreshed = await _watchService.EvaluateAsync(
                existing[index].Expression, existing[index], CancellationToken.None);
            if (index < Watches.Count && Watches[index].Expression == existing[index].Expression)
                Watches[index] = refreshed;
        }
    }

    private bool CanPatchSelection() =>
        !IsBusy && _patchService != null && SelectedInstruction != null &&
        TryParseBytes(SelectedInstruction.Bytes, out _) &&
        TryParseBytes(PatchBytes, out _);

    private async Task ApplyPatchAsync()
    {
        if (_patchService == null || SelectedInstruction == null ||
            !TryParseBytes(SelectedInstruction.Bytes, out byte[] original) ||
            !TryParseBytes(PatchBytes, out byte[] replacement)) return;

        IsBusy = true;
        try
        {
            PatchResult result = await _patchService.ApplyAsync(new PatchRequest(
                SelectedInstruction.RuntimeAddress,
                original,
                replacement,
                PadPatchWithNops,
                SelectedInstruction.Instruction,
                ModuleName == "—" ? null : ModuleName,
                _moduleBaseAddress != 0 && SelectedInstruction.RuntimeAddress >= _moduleBaseAddress
                    ? SelectedInstruction.RuntimeAddress - _moduleBaseAddress
                    : null));
            Status = result.Message;
            if (result.Succeeded) await RefreshAsync();
        }
        finally
        {
            IsBusy = false;
            RaisePatchCanExecuteChanged();
        }
    }

    private async Task NopInstructionAsync()
    {
        if (!TryParseBytes(SelectedInstruction?.Bytes, out byte[] original)) return;
        PatchBytes = string.Join(' ', Enumerable.Repeat("90", original.Length));
        await ApplyPatchAsync();
    }

    private async Task UndoPatchAsync()
    {
        if (_patchService == null) return;
        PatchResult result = await _patchService.UndoAsync();
        Status = result.Message;
        if (result.Succeeded) await RefreshAsync();
        RaisePatchCanExecuteChanged();
    }

    private async Task RedoPatchAsync()
    {
        if (_patchService == null) return;
        PatchResult result = await _patchService.RedoAsync();
        Status = result.Message;
        if (result.Succeeded) await RefreshAsync();
        RaisePatchCanExecuteChanged();
    }

    private static bool TryParseBytes(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] tokens = text.Split(new[] { ' ', '\t', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var parsed = new byte[tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? tokens[index][2..]
                : tokens[index];
            if (token.Length != 2 || !byte.TryParse(token, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed[index])) return false;
        }
        bytes = parsed;
        return bytes.Length > 0;
    }

    private void RaisePatchCanExecuteChanged()
    {
        _applyPatchCommand.RaiseCanExecuteChanged();
        _nopInstructionCommand.RaiseCanExecuteChanged();
        _undoPatchCommand.RaiseCanExecuteChanged();
        _redoPatchCommand.RaiseCanExecuteChanged();
    }

    private void RaiseDebugCanExecuteChanged()
    {
        _attachCommand.RaiseCanExecuteChanged();
        _detachCommand.RaiseCanExecuteChanged();
        _pauseCommand.RaiseCanExecuteChanged();
        _resumeCommand.RaiseCanExecuteChanged();
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
