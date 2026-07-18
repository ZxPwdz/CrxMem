using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FormsKeys = System.Windows.Forms.Keys;

namespace CrxMem;

public partial class SettingsWindow : Window
{
    public event EventHandler? SettingsApplied;
    public ObservableCollection<HotkeySetting> Hotkeys { get; } = new();

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadSettings();
    }

    private void LoadSettings()
    {
        txtUpdateInterval.Text = SettingsManager.UpdateInterval.ToString();
        txtFreezeInterval.Text = SettingsManager.FreezeInterval.ToString();
        txtFoundListInterval.Text = SettingsManager.FoundListUpdateInterval.ToString();
        chkDarkMode.IsChecked = SettingsManager.DarkMode;
        chkShowSigned.IsChecked = SettingsManager.ShowValuesAsSigned;
        chkSimpleCopyPaste.IsChecked = SettingsManager.SimpleCopyPaste;
        chkSaveWindowPositions.IsChecked = SettingsManager.SaveWindowPositions;
        chkAskClearList.IsChecked = SettingsManager.AskToClearListOnNewProcess;
        chkLaunchAsAdmin.IsChecked = SettingsManager.AlwaysLaunchAsAdmin;
        txtAutoAttachProcess.Text = SettingsManager.AutoAttachProcess;
        chkAlwaysAutoAttach.IsChecked = SettingsManager.AlwaysAutoAttach;
        chkFastAutoAttach.IsChecked = SettingsManager.FastAutoAttach;
        chkUseWmiWatch.IsChecked = SettingsManager.UseWmiProcessWatch;
        txtFastAttachInterval.Text = SettingsManager.FastAutoAttachInterval.ToString();
        chkAutoLoadDriver.IsChecked = SettingsManager.AutoLoadDriver;
        chkAutoInjectVeh.IsChecked = SettingsManager.AutoInjectVEH;
        chkFastSuspendOnAttach.IsChecked = SettingsManager.FastSuspendOnAttach;
        txtSuspendInterval.Text = SettingsManager.SuspendInterval.ToString();

        cmbThreadPriority.SelectedIndex = SettingsManager.ScanThreadPriority;
        txtScanBufferSize.Text = SettingsManager.ScanBufferSize.ToString();
        chkFastScanDefault.IsChecked = SettingsManager.FastScanByDefault;
        chkPauseWhileScanning.IsChecked = SettingsManager.PauseWhileScanning;
        chkMemPrivate.IsChecked = SettingsManager.ScanMemPrivate;
        chkMemImage.IsChecked = SettingsManager.ScanMemImage;
        chkMemMapped.IsChecked = SettingsManager.ScanMemMapped;
        chkSkipNoCache.IsChecked = SettingsManager.SkipPageNoCache;
        chkSkipWriteCombine.IsChecked = SettingsManager.SkipPageWriteCombine;
        txtKernelMultiplier.Text = SettingsManager.KernelModeUpdateMultiplier.ToString();
        txtKernelCacheLifetime.Text = SettingsManager.KernelCacheLifetimeMs.ToString();

        Dictionary<string, int> savedHotkeys = SettingsManager.GetAllHotkeys();
        AddHotkey("New Scan", "NewScan", savedHotkeys);
        AddHotkey("Next Scan", "NextScan", savedHotkeys);
        AddHotkey("Undo last scan", "UndoScan", savedHotkeys);
        txtSettingsPath.Text = SettingsManager.FilePath;
        txtSettingsPath.ToolTip = SettingsManager.FilePath;
    }

    private void AddHotkey(string displayName, string functionId, IReadOnlyDictionary<string, int> savedHotkeys)
    {
        int keyData = savedHotkeys.TryGetValue(functionId, out int value) ? value : 0;
        Hotkeys.Add(new HotkeySetting(displayName, functionId, keyData));
    }

    private bool TryApplySettings()
    {
        if (!TryReadInt(txtUpdateInterval, "Address list refresh", 50, 60_000, out int updateInterval) ||
            !TryReadInt(txtFreezeInterval, "Frozen value interval", 10, 60_000, out int freezeInterval) ||
            !TryReadInt(txtFoundListInterval, "Found list refresh", 50, 60_000, out int foundListInterval) ||
            !TryReadInt(txtFastAttachInterval, "Polling fallback interval", 1, 60_000, out int fastAttachInterval) ||
            !TryReadInt(txtSuspendInterval, "Repeated suspend interval", 0, 60_000, out int suspendInterval) ||
            !TryReadInt(txtScanBufferSize, "Read buffer size", 1, 1_048_576, out int scanBufferSize) ||
            !TryReadInt(txtKernelMultiplier, "UI update multiplier", 1, 100, out int kernelMultiplier) ||
            !TryReadInt(txtKernelCacheLifetime, "Read cache lifetime", 0, 60_000, out int cacheLifetime))
        {
            return false;
        }

        SettingsManager.UpdateInterval = updateInterval;
        SettingsManager.FreezeInterval = freezeInterval;
        SettingsManager.FoundListUpdateInterval = foundListInterval;
        SettingsManager.DarkMode = chkDarkMode.IsChecked == true;
        SettingsManager.ShowValuesAsSigned = chkShowSigned.IsChecked == true;
        SettingsManager.SimpleCopyPaste = chkSimpleCopyPaste.IsChecked == true;
        SettingsManager.SaveWindowPositions = chkSaveWindowPositions.IsChecked == true;
        SettingsManager.AskToClearListOnNewProcess = chkAskClearList.IsChecked == true;
        SettingsManager.AlwaysLaunchAsAdmin = chkLaunchAsAdmin.IsChecked == true;
        SettingsManager.AutoAttachProcess = txtAutoAttachProcess.Text.Trim();
        SettingsManager.AlwaysAutoAttach = chkAlwaysAutoAttach.IsChecked == true;
        SettingsManager.FastAutoAttach = chkFastAutoAttach.IsChecked == true;
        SettingsManager.UseWmiProcessWatch = chkUseWmiWatch.IsChecked == true;
        SettingsManager.FastAutoAttachInterval = fastAttachInterval;
        SettingsManager.AutoLoadDriver = chkAutoLoadDriver.IsChecked == true;
        SettingsManager.AutoInjectVEH = chkAutoInjectVeh.IsChecked == true;
        SettingsManager.FastSuspendOnAttach = chkFastSuspendOnAttach.IsChecked == true;
        SettingsManager.SuspendInterval = suspendInterval;
        SettingsManager.ScanThreadPriority = Math.Max(0, cmbThreadPriority.SelectedIndex);
        SettingsManager.ScanBufferSize = scanBufferSize;
        SettingsManager.FastScanByDefault = chkFastScanDefault.IsChecked == true;
        SettingsManager.PauseWhileScanning = chkPauseWhileScanning.IsChecked == true;
        SettingsManager.ScanMemPrivate = chkMemPrivate.IsChecked == true;
        SettingsManager.ScanMemImage = chkMemImage.IsChecked == true;
        SettingsManager.ScanMemMapped = chkMemMapped.IsChecked == true;
        SettingsManager.SkipPageNoCache = chkSkipNoCache.IsChecked == true;
        SettingsManager.SkipPageWriteCombine = chkSkipWriteCombine.IsChecked == true;
        SettingsManager.KernelModeUpdateMultiplier = kernelMultiplier;
        SettingsManager.KernelCacheLifetimeMs = cacheLifetime;

        Dictionary<string, int> allHotkeys = SettingsManager.GetAllHotkeys();
        foreach (HotkeySetting hotkey in Hotkeys)
        {
            allHotkeys[hotkey.FunctionId] = hotkey.KeyData;
        }
        SettingsManager.SetAllHotkeys(allHotkeys);

        try
        {
            SettingsManager.Save();
            SettingsApplied?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"CrxMem could not save settings:\n\n{ex.Message}", "Settings Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool TryReadInt(System.Windows.Controls.TextBox textBox, string label, int minimum, int maximum, out int value)
    {
        if (int.TryParse(textBox.Text, out value) && value >= minimum && value <= maximum)
        {
            return true;
        }

        System.Windows.MessageBox.Show(this, $"{label} must be a whole number from {minimum:N0} to {maximum:N0}.",
            "Invalid Setting", MessageBoxButton.OK, MessageBoxImage.Warning);
        textBox.Focus();
        textBox.SelectAll();
        return false;
    }

    private void HotkeyList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (hotkeyList.SelectedItem is not HotkeySetting selected)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            selected.KeyData = 0;
            return;
        }

        int keyData = KeyInterop.VirtualKeyFromKey(key);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) keyData |= (int)FormsKeys.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) keyData |= (int)FormsKeys.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) keyData |= (int)FormsKeys.Alt;
        selected.KeyData = keyData;
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (hotkeyList.SelectedItem is HotkeySetting selected)
        {
            selected.KeyData = 0;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => TryApplySettings();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryApplySettings())
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class HotkeySetting : INotifyPropertyChanged
{
    private int _keyData;

    public HotkeySetting(string displayName, string functionId, int keyData)
    {
        DisplayName = displayName;
        FunctionId = functionId;
        _keyData = keyData;
    }

    public string DisplayName { get; }
    public string FunctionId { get; }

    public int KeyData
    {
        get => _keyData;
        set
        {
            if (_keyData == value) return;
            _keyData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyText));
        }
    }

    public string HotkeyText => FormatKeyData(_keyData);
    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatKeyData(int keyData)
    {
        if (keyData == 0) return "Not assigned";

        var keys = (FormsKeys)keyData;
        var parts = new List<string>();
        if (keys.HasFlag(FormsKeys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(FormsKeys.Shift)) parts.Add("Shift");
        if (keys.HasFlag(FormsKeys.Alt)) parts.Add("Alt");

        FormsKeys keyCode = keys & FormsKeys.KeyCode;
        string keyName = keyCode >= FormsKeys.D0 && keyCode <= FormsKeys.D9
            ? ((int)keyCode - (int)FormsKeys.D0).ToString()
            : keyCode.ToString();
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
