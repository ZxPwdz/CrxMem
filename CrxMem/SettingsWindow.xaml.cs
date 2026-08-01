using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CrxMem.Core;
using CrxShield;
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
        InitializeCrxShieldTab();
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

    #region CrxShield driver tab

    private readonly KernelDriverManager _crxDriverManager = new();
    private bool _crxInitializing;
    private bool _crxSelfTestRunning;

    private void InitializeCrxShieldTab()
    {
        _crxInitializing = true;
        try
        {
            chkUseKernelMode.IsChecked = AntiCheatBypassForm.UseKernelMode;
            int? attachedPid = TryGetAttachedProcessId();
            if (attachedPid.HasValue)
            {
                txtCrxPid.Text = attachedPid.Value.ToString();
            }
        }
        finally
        {
            _crxInitializing = false;
        }

        RefreshCrxShieldStatus();
        CrxLog("CrxShield tab ready. Use Refresh to re-query service and connection state.");
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Resolves a CrxShield-relative file or folder: first next to the app exe,
    /// then walking up from the build output directory to the solution root.
    /// </summary>
    private static string? ResolveCrxShieldPath(string relativePath, bool isDirectory)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] prefixes =
        {
            baseDir,
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\.."))
        };

        foreach (string prefix in prefixes)
        {
            string candidate = Path.GetFullPath(Path.Combine(prefix, relativePath));
            bool exists = isDirectory ? Directory.Exists(candidate) : File.Exists(candidate);
            if (exists)
            {
                return candidate;
            }
        }

        return null;
    }

    private void RefreshCrxShieldStatus()
    {
        bool elevated = IsElevated();
        crxElevationWarning.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        DriverServiceState state = _crxDriverManager.GetServiceState();
        txtCrxServiceState.Text = state switch
        {
            DriverServiceState.Running => "Running",
            DriverServiceState.Stopped => "Stopped",
            DriverServiceState.NotInstalled => "Not installed",
            DriverServiceState.StartPending => "Starting…",
            DriverServiceState.StopPending => "Stopping…",
            _ => "Unknown"
        };
        txtCrxServiceState.Foreground = state == DriverServiceState.Running
            ? (System.Windows.Media.Brush)FindResource("StatusSuccessBrush")
            : (System.Windows.Media.Brush)FindResource("StatusErrorBrush");

        CrxDriverController? controller = AntiCheatBypassForm.DriverController;
        bool connected = controller?.IsConnected() == true;
        txtCrxConnectionState.Text = connected ? "Connected" : "Disconnected";
        txtCrxConnectionState.Foreground = connected
            ? (System.Windows.Media.Brush)FindResource("StatusSuccessBrush")
            : (System.Windows.Media.Brush)FindResource("StatusErrorBrush");

        if (connected && controller!.GetVersion(out uint major, out uint minor, out uint build))
        {
            txtCrxDriverVersion.Text = $"{major}.{minor}.{build}";
        }
        else
        {
            txtCrxDriverVersion.Text = connected ? "Unavailable" : "—";
        }

        string? sysPath = ResolveCrxShieldPath(Path.Combine("CrxShield", "bin", "Release", "CrxShield.sys"), isDirectory: false);
        txtCrxDriverPath.Text = sysPath ?? "CrxShield.sys not found";
        txtCrxDriverPath.ToolTip = sysPath;

        _crxInitializing = true;
        try
        {
            chkUseKernelMode.IsChecked = AntiCheatBypassForm.UseKernelMode;
        }
        finally
        {
            _crxInitializing = false;
        }
    }

    private void CrxLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => CrxLog(message));
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        txtCrxLog.AppendText(line + Environment.NewLine);
        txtCrxLog.ScrollToEnd();
    }

    private void CrxRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshCrxShieldStatus();
        CrxLog("Status refreshed.");
    }

    private void CrxLoadDriver_Click(object sender, RoutedEventArgs e)
    {
        if (!IsElevated())
        {
            CrxLog("ERROR: CrxMem is not elevated. Driver load requires administrator rights.");
            return;
        }

        string? sysPath = ResolveCrxShieldPath(Path.Combine("CrxShield", "bin", "Release", "CrxShield.sys"), isDirectory: false);
        if (sysPath == null)
        {
            CrxLog("ERROR: CrxShield.sys not found (looked next to the app and in the solution's CrxShield\\bin\\Release).");
            return;
        }

        try
        {
            _crxDriverManager.DriverPath = sysPath;
            if (_crxDriverManager.LoadDriver())
            {
                CrxLog($"Driver loaded: {sysPath}");
            }
            else
            {
                CrxLog($"ERROR loading driver: {_crxDriverManager.LastError}");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR loading driver: {ex.Message}");
        }

        RefreshCrxShieldStatus();
    }

    private void CrxUnloadDriver_Click(object sender, RoutedEventArgs e)
    {
        if (!IsElevated())
        {
            CrxLog("ERROR: CrxMem is not elevated. Driver unload requires administrator rights.");
            return;
        }

        try
        {
            CrxDriverController? controller = AntiCheatBypassForm.DriverController;
            if (controller?.IsConnected() == true)
            {
                controller.Disconnect();
                CrxLog("Closed the existing device connection before unloading.");
            }

            if (_crxDriverManager.UnloadDriver())
            {
                CrxLog("Driver unloaded and service removed.");
            }
            else
            {
                CrxLog($"ERROR unloading driver: {_crxDriverManager.LastError}");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR unloading driver: {ex.Message}");
        }

        RefreshCrxShieldStatus();
    }

    private bool _crxRestartRunning;

    private async void CrxRestartDriver_Click(object sender, RoutedEventArgs e)
    {
        if (!IsElevated())
        {
            CrxLog("ERROR: CrxMem is not elevated. Driver restart requires administrator rights.");
            return;
        }

        if (_crxRestartRunning)
        {
            return;
        }

        _crxRestartRunning = true;
        btnCrxRestartDriver.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    CrxDriverController? controller = AntiCheatBypassForm.DriverController;
                    if (controller?.IsConnected() == true)
                    {
                        controller.Disconnect();
                        CrxLog("Closed the existing device connection before restarting.");
                    }

                    DriverServiceState state = _crxDriverManager.GetServiceState();
                    if (state == DriverServiceState.NotInstalled)
                    {
                        CrxLog("ERROR: CrxShield service is not installed. Use 'Load driver' (or Tools\\Install-CrxShieldDriver.ps1) once first.");
                        return;
                    }

                    CrxLog($"Restarting driver (current state: {state}) — stopping old image…");
                    if (_crxDriverManager.RestartDriver(stopTimeoutMs: 10000, out int startError))
                    {
                        CrxLog("Driver restarted: CrxShield is running with the latest image.");
                    }
                    else if (startError == 577)
                    {
                        CrxLog("ERROR: driver start failed (577) — kernel Code Integrity rejected the signature. " +
                               "Testsigning must be enabled (Tools\\Start-CrxShield.cmd) and the machine rebooted before this driver can start.");
                    }
                    else
                    {
                        CrxLog($"ERROR restarting driver: {_crxDriverManager.LastError}");
                    }
                }
                catch (Exception ex)
                {
                    CrxLog($"ERROR restarting driver: {ex.Message}");
                }
            });
        }
        finally
        {
            _crxRestartRunning = false;
            btnCrxRestartDriver.IsEnabled = true;
            RefreshCrxShieldStatus();
        }
    }

    private void CrxOpenTools_Click(object sender, RoutedEventArgs e)
    {
        string? toolsDir = ResolveCrxShieldPath(Path.Combine("CrxShield", "Tools"), isDirectory: true);
        if (toolsDir == null)
        {
            CrxLog("ERROR: CrxShield\\Tools folder not found.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{toolsDir}\"") { UseShellExecute = true });
            CrxLog($"Opened tools folder: {toolsDir}");
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR opening tools folder: {ex.Message}");
        }
    }

    private void CrxConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CrxDriverController? controller = AntiCheatBypassForm.DriverController;
            if (controller?.IsConnected() == true)
            {
                CrxLog("Already connected to \\\\.\\CrxShield.");
                return;
            }

            controller ??= new CrxDriverController();
            if (controller.Connect())
            {
                AntiCheatBypassForm.SetDriverController(controller);
                CrxLog("Connected to \\\\.\\CrxShield.");
                if (controller.GetVersion(out uint major, out uint minor, out uint build))
                {
                    CrxLog($"Driver version: {major}.{minor}.{build}");
                }
            }
            else
            {
                CrxLog("ERROR: could not open \\\\.\\CrxShield. Is the driver loaded? The device is restricted to SYSTEM/Administrators — run CrxMem elevated.");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR connecting: {ex.Message}");
        }

        RefreshCrxShieldStatus();
    }

    private void CrxDisconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CrxDriverController? controller = AntiCheatBypassForm.DriverController;
            if (controller?.IsConnected() == true)
            {
                controller.Disconnect();
                CrxLog("Disconnected from \\\\.\\CrxShield.");
            }
            else
            {
                CrxLog("Not connected.");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR disconnecting: {ex.Message}");
        }

        RefreshCrxShieldStatus();
    }

    private void ChkUseKernelMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_crxInitializing)
        {
            return;
        }

        bool enabled = chkUseKernelMode.IsChecked == true;
        SetUseKernelMode(enabled);
        CrxLog(enabled
            ? "Kernel driver mode ENABLED — all memory operations now go through CrxShield."
            : "Kernel driver mode disabled — memory operations use the regular Win32 path.");
    }

    /// <summary>
    /// AntiCheatBypassForm.UseKernelMode currently exposes a private setter, so the live
    /// static is flipped through reflection to take effect immediately without a save.
    /// </summary>
    private static void SetUseKernelMode(bool value)
    {
        PropertyInfo? property = typeof(AntiCheatBypassForm).GetProperty(nameof(AntiCheatBypassForm.UseKernelMode));
        property?.SetValue(null, value);
    }

    private static int? TryGetAttachedProcessId()
    {
        try
        {
            // MainWindow keeps the attached process in its private _process field; read it
            // reflectively so no other file needs to change. Prefill stays empty on failure.
            Window? mainWindow = System.Windows.Application.Current?.MainWindow;
            if (mainWindow is not MainWindow)
            {
                return null;
            }

            FieldInfo? field = typeof(MainWindow).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(mainWindow) is ProcessAccess { IsOpen: true } process)
            {
                return process.Target?.Id;
            }
        }
        catch
        {
            // Prefill is best-effort only.
        }

        return null;
    }

    private static bool TryParseHexAddress(string text, out ulong value)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatHexAscii(byte[] data)
    {
        var hex = new StringBuilder(data.Length * 3);
        var ascii = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            hex.Append(b.ToString("X2")).Append(' ');
            ascii.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return $"{hex.ToString().TrimEnd()}  |{ascii}|";
    }

    private bool TryGetConnectedController(out CrxDriverController controller)
    {
        controller = AntiCheatBypassForm.DriverController!;
        if (controller?.IsConnected() == true)
        {
            return true;
        }

        CrxLog("ERROR: not connected to the driver. Use Connect first.");
        return false;
    }

    private bool TryReadManualInputs(bool requireWriteData, out uint pid, out ulong address, out int size, out byte[] writeData)
    {
        pid = 0;
        address = 0;
        size = 0;
        writeData = Array.Empty<byte>();

        if (!uint.TryParse(txtCrxPid.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid == 0)
        {
            CrxLog("ERROR: PID must be a positive decimal number.");
            return false;
        }

        if (!TryParseHexAddress(txtCrxAddress.Text, out address) || address == 0)
        {
            CrxLog("ERROR: Address must be a hex value (e.g. 7FF612340000 or 0x7FF612340000).");
            return false;
        }

        if (!int.TryParse(txtCrxSize.Text.Trim(), out size) || size < 1 || size > 4096)
        {
            CrxLog("ERROR: Size must be between 1 and 4096 bytes.");
            return false;
        }

        if (requireWriteData)
        {
            string[] tokens = txtCrxWriteData.Text.Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                CrxLog("ERROR: enter write data as hex bytes (e.g. DE AD BE EF).");
                return false;
            }

            var bytes = new List<byte>(tokens.Length);
            foreach (string token in tokens)
            {
                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    CrxLog($"ERROR: '{token}' is not a valid hex byte (00-FF).");
                    return false;
                }

                bytes.Add(b);
            }

            writeData = bytes.ToArray();
        }

        return true;
    }

    private void CrxRead_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConnectedController(out CrxDriverController controller) ||
            !TryReadManualInputs(requireWriteData: false, out uint pid, out ulong address, out int size, out _))
        {
            return;
        }

        try
        {
            var buffer = new byte[size];
            if (controller.ReadProcessMemory(pid, address, buffer))
            {
                CrxLog($"READ pid={pid} 0x{address:X} ({size} bytes):");
                CrxLog("  " + FormatHexAscii(buffer));
            }
            else
            {
                CrxLog($"ERROR: kernel read failed (pid={pid}, 0x{address:X}). Access denied or invalid address — is CrxMem elevated?");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR during read: {ex.Message}");
        }
    }

    private void CrxWrite_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConnectedController(out CrxDriverController controller) ||
            !TryReadManualInputs(requireWriteData: true, out uint pid, out ulong address, out _, out byte[] writeData))
        {
            return;
        }

        try
        {
            if (!controller.WriteProcessMemory(pid, address, writeData))
            {
                CrxLog($"ERROR: kernel write failed (pid={pid}, 0x{address:X}). Access denied or invalid address — is CrxMem elevated?");
                return;
            }

            CrxLog($"WROTE pid={pid} 0x{address:X} ({writeData.Length} bytes): {string.Join(" ", writeData.Select(b => b.ToString("X2")))}");

            var verify = new byte[writeData.Length];
            if (controller.ReadProcessMemory(pid, address, verify))
            {
                bool match = verify.SequenceEqual(writeData);
                CrxLog($"  Read-back: {string.Join(" ", verify.Select(b => b.ToString("X2")))} — {(match ? "VERIFIED (match)" : "MISMATCH")}");
            }
            else
            {
                CrxLog("  WARNING: read-back for verification failed.");
            }
        }
        catch (Exception ex)
        {
            CrxLog($"ERROR during write: {ex.Message}");
        }
    }

    private async void CrxRunSelfTest_Click(object sender, RoutedEventArgs e)
    {
        if (_crxSelfTestRunning)
        {
            return;
        }

        _crxSelfTestRunning = true;
        btnRunSelfTest.IsEnabled = false;
        try
        {
            await Task.Run(RunCrxSelfTest);
        }
        finally
        {
            _crxSelfTestRunning = false;
            btnRunSelfTest.IsEnabled = true;
            RefreshCrxShieldStatus();
        }
    }

    private void RunCrxSelfTest()
    {
        int passed = 0;
        int failed = 0;
        Process? target = null;

        void Step(bool ok, string name, string detail)
        {
            if (ok) passed++; else failed++;
            CrxLog($"{(ok ? "PASS" : "FAIL")}  {name} — {detail}");
        }

        CrxLog("── CrxShield self-test started ──");
        try
        {
            // Step 1: connect
            CrxDriverController? controller = AntiCheatBypassForm.DriverController;
            if (controller?.IsConnected() != true)
            {
                controller ??= new CrxDriverController();
                if (controller.Connect())
                {
                    AntiCheatBypassForm.SetDriverController(controller);
                }
            }

            bool connected = controller?.IsConnected() == true;
            Step(connected, "Connect to \\\\.\\CrxShield", connected ? "device opened" : "open failed — driver loaded? app elevated?");
            if (!connected || controller == null)
            {
                CrxLog($"── Self-test aborted: {passed} passed, {failed} failed ──");
                return;
            }

            // Step 2: driver version
            bool versionOk = controller.GetVersion(out uint major, out uint minor, out uint build);
            Step(versionOk && major == 1, "GET_VERSION", versionOk ? $"v{major}.{minor}.{build}" : controller.LastError ?? "no version returned");

            // Step 3: launch debug target and parse its READY line
            string? targetPath = ResolveCrxShieldPath(
                Path.Combine("CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"), isDirectory: false)
                ?? ResolveCrxShieldPath(
                Path.Combine("CrxMem.DebugTarget", "bin", "Debug", "net8.0-windows", "CrxMem.DebugTarget.exe"), isDirectory: false);

            uint targetPid = 0;
            ulong bufferAddress = 0;
            if (targetPath == null)
            {
                Step(false, "Launch debug target", "CrxMem.DebugTarget.exe not found (Release or Debug)");
            }
            else
            {
                try
                {
                    target = Process.Start(new ProcessStartInfo
                    {
                        FileName = targetPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    });

                    Task<string?> readLine = target!.StandardOutput.ReadLineAsync();
                    Task.WhenAny(readLine, Task.Delay(TimeSpan.FromSeconds(15))).Wait();
                    string? readyLine = readLine.IsCompletedSuccessfully ? readLine.Result : null;
                    Match match = Regex.Match(readyLine ?? "",
                        @"READY PID=(\d+) BUFFER=0x([0-9A-Fa-f]+) SIZE=(\d+) PROBE=0x([0-9A-Fa-f]+)");
                    if (match.Success)
                    {
                        targetPid = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        bufferAddress = ulong.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        Step(true, "Launch debug target", $"pid={targetPid}, buffer=0x{bufferAddress:X}");
                    }
                    else
                    {
                        Step(false, "Launch debug target", "READY line not received within 15 s or malformed");
                    }
                }
                catch (Exception ex)
                {
                    Step(false, "Launch debug target", ex.Message);
                }
            }

            if (targetPid != 0 && bufferAddress != 0)
            {
                // Step 4: kernel-read known pattern at BUFFER+16 (bytes 0x10..0x1F)
                var expected = new byte[16];
                for (int i = 0; i < 16; i++) expected[i] = (byte)(0x10 + i);
                var readBack = new byte[16];
                bool readOk = controller.ReadProcessMemory(targetPid, bufferAddress + 16, readBack);
                Step(readOk && readBack.SequenceEqual(expected), "Kernel read (pattern @ BUFFER+16)",
                    readOk
                        ? (readBack.SequenceEqual(expected) ? string.Join(" ", readBack.Select(b => b.ToString("X2"))) : $"unexpected data: {string.Join(" ", readBack.Select(b => b.ToString("X2")))}")
                        : "read failed");

                // Step 5: kernel-write at BUFFER+64, verify, restore
                var original = new byte[16];
                bool savedOriginal = controller.ReadProcessMemory(targetPid, bufferAddress + 64, original);
                var testPattern = new byte[16];
                for (int i = 0; i < 16; i++) testPattern[i] = (byte)(0xA0 + i);

                bool writeOk = savedOriginal && controller.WriteProcessMemory(targetPid, bufferAddress + 64, testPattern);
                var verifyBuffer = new byte[16];
                bool verifyOk = writeOk && controller.ReadProcessMemory(targetPid, bufferAddress + 64, verifyBuffer) && verifyBuffer.SequenceEqual(testPattern);

                string restoreNote = "";
                if (savedOriginal)
                {
                    try
                    {
                        restoreNote = controller.WriteProcessMemory(targetPid, bufferAddress + 64, original)
                            ? ", original bytes restored"
                            : ", WARNING: restore failed";
                    }
                    catch
                    {
                        restoreNote = ", WARNING: restore threw";
                    }
                }

                Step(verifyOk, "Kernel write + read-back (@ BUFFER+64)",
                    !savedOriginal ? "could not save original bytes"
                    : !writeOk ? "write failed"
                    : verifyOk ? $"write verified{restoreNote}"
                    : $"read-back mismatch{restoreNote}");

                // Step 6: process base sanity
                bool baseOk = controller.GetProcessBaseAddress(targetPid, out ulong baseAddress) && baseAddress != 0;
                Step(baseOk, "GET_PROCESS_BASE", baseOk ? $"base=0x{baseAddress:X}" : "no base address returned");
            }
        }
        catch (Exception ex)
        {
            failed++;
            CrxLog($"FAIL  unexpected exception — {ex.Message}");
        }
        finally
        {
            if (target != null)
            {
                try
                {
                    if (!target.HasExited)
                    {
                        target.Kill();
                    }

                    target.Dispose();
                    CrxLog("Debug target terminated.");
                }
                catch (Exception ex)
                {
                    CrxLog($"WARNING: could not terminate debug target: {ex.Message}");
                }
            }
        }

        CrxLog(failed == 0
            ? $"── SELF-TEST PASSED ({passed}/{passed + failed} steps) ──"
            : $"── SELF-TEST FAILED ({passed} passed, {failed} failed) ──");
    }

    #endregion
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
