using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CrxMem.Core;
using CrxMem.Models;
using CrxShield;

namespace CrxMem
{
    public partial class MainWindow : Window
    {
        // ═══════════════════════════════════════════════════════════════════
        // P/INVOKE & CONSTANTS
        // ═══════════════════════════════════════════════════════════════════
        
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        // ═══════════════════════════════════════════════════════════════════
        // FIELD DECLARATIONS
        // ═══════════════════════════════════════════════════════════════════
        
        private ProcessAccess? _process;
        private MemoryScanner? _scanner;
        private DispatcherTimer? _freezeTimer;
        private DispatcherTimer? _autoAttachTimer;
        private DispatcherTimer? _suspendTimer;
        private DispatcherTimer? _updateTimer;
        private FastProcessWatcher? _fastProcessWatcher;
        private readonly ITableSerializer _tableSerializer = new JsonTableSerializer();
        private bool _liveUpdatesEnabled = true;
        private HwndSource? _windowSource;
        private readonly HwndSourceHook _windowHook;

        // Singleton form instances
        private MemoryView.MemoryViewForm? _memoryViewForm;
        private AntiCheatBypassForm? _acBypassForm;

        // WPF ListView data collections
        private ObservableCollection<ScanResultItem> scanResults = new();
        private ObservableCollection<AddressEntryItem> addressEntries = new();

        // Hotkey tracking
        private Dictionary<int, string> _hotkeyIdToFunction = new();
        private int _nextHotkeyId = 1;

        // ═══════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ═══════════════════════════════════════════════════════════════════
        
        public MainWindow()
        {
            _windowHook = WndHook;
            InitializeComponent();
            
            // Set window title with version
            string version = System.Windows.Forms.Application.ProductVersion;
            if (version.EndsWith(".0.0")) version = version[..^4];
            else if (version.EndsWith(".0")) version = version[..^2];
            txtWindowTitle.Text = $"CrxMem v{version}";
            Title = $"CrxMem v{version}";

            // Bind ObservableCollections to WPF ListViews
            lvFoundAddressesWpf.ItemsSource = scanResults;
            lvAddressListWpf.ItemsSource = addressEntries;

            // Initialize timers
            InitializeTimers();

            // Apply settings
            ApplySettings();

            // Check for existing driver
            CheckExistingDriverConnection();

            // Load images/logos
            LoadResources();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Register for hotkey messages
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(_windowHook);

            // Register global hotkeys now that we have a window handle
            RegisterGlobalHotkeys();
        }

        protected override void OnClosed(EventArgs e)
        {
            _scanner?.CancelScan();

            _freezeTimer?.Stop();
            _autoAttachTimer?.Stop();
            _suspendTimer?.Stop();
            _updateTimer?.Stop();

            _fastProcessWatcher?.Dispose();
            _fastProcessWatcher = null;

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                UnregisterAllHotkeys(hwnd);

            _windowSource?.RemoveHook(_windowHook);
            _windowSource = null;

            _memoryViewForm?.Close();
            _acBypassForm?.Close();
            _luaEditorForm?.Close();
            _luaEditorWindow?.Close();

            _process?.Dispose();
            _process = null;

            base.OnClosed(e);
        }


        private void LoadResources()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("favicon.ico"))
                {
                    if (stream != null)
                    {
                        var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                        imgLogo.Source = decoder.Frames[0];
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════
        // WINDOW CHROME HANDLERS
        // ═══════════════════════════════════════════════════════════════════
        
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) Maximize_Click(sender, null!);
            else this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            btnMaximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            // Calculate new size
            double newWidth = this.Width + e.HorizontalChange;
            double newHeight = this.Height + e.VerticalChange;

            // Apply minimum constraints
            if (newWidth >= this.MinWidth)
                this.Width = newWidth;

            if (newHeight >= this.MinHeight)
                this.Height = newHeight;
        }

        // ═══════════════════════════════════════════════════════════════════
        // PROCESS HANDLING
        // ═══════════════════════════════════════════════════════════════════

        private void OpenProcess_Click(object sender, RoutedEventArgs e)
        {
            ProcessSelectDialog dialog = new ProcessSelectDialog();
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && dialog.SelectedProcess != null)
            {
                AttachToProcess(dialog.SelectedProcess);
            }
        }

        private void AttachToProcess(Process process)
        {
            try
            {
                // Stop suspend timer when switching processes
                StopSuspendTimer();

                _process?.Dispose();
                _process = new ProcessAccess();

                if (_process.Open(process.Id))
                {
                    txtProcessName.Text = $"{process.ProcessName} - PID: {process.Id} ({(_process.Is64Bit ? "64" : "32")}-bit)";
                    txtProcessName.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;

                    string ver = System.Windows.Forms.Application.ProductVersion;
                    if (ver.EndsWith(".0.0")) ver = ver[..^4];
                    else if (ver.EndsWith(".0")) ver = ver[..^2];
                    txtWindowTitle.Text = $"CrxMem v{ver} - {process.ProcessName} (PID: {process.Id})";

                    _scanner = new MemoryScanner(_process);
                    _scanner.StatusChanged += (status) => Dispatcher.Invoke(() => txtFoundCount.Text = status);
                    _scanner.ProgressChanged += (current, total) => Dispatcher.Invoke(() => {
                        progressBar.Maximum = total;
                        progressBar.Value = Math.Min(current, total);
                    });

                    PopulateModuleDropdown();
                    btnFirstScan.IsEnabled = true;

                    // Start suspend timer if configured
                    StartSuspendTimerIfNeeded();
                }
                else
                {
                    System.Windows.MessageBox.Show("Failed to open process. Try running as Administrator.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error opening process: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PopulateModuleDropdown()
        {
            cmbModule.Items.Clear();
            cmbModule.Items.Add("All Modules");

            if (_process == null) return;
            try
            {
                var modules = _process.GetModules();
                System.Diagnostics.Debug.WriteLine($"[PopulateModuleDropdown] Got {modules.Count} modules");
                foreach (var module in modules)
                {
                    cmbModule.Items.Add(module); // Add ModuleInfo object, not just string
                    System.Diagnostics.Debug.WriteLine($"  - {module.ModuleName} at {module.BaseAddress:X}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PopulateModuleDropdown] Error: {ex.Message}");
            }
            cmbModule.SelectedIndex = 0;
        }

        // ═══════════════════════════════════════════════════════════════════
        // SCANNING LOGIC
        // ═══════════════════════════════════════════════════════════════════

        private async void FirstScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner == null) return;

            if (cmbValueType.SelectedIndex == 7) // Array of Bytes (AOB)
            {
                await PerformAOBScan(txtValue.Text);
                return;
            }

            var scanType = GetScanType();
            var valueType = GetValueType();

            // Validate that value input is provided when needed
            bool requiresValue = scanType == ScanType.Exact ||
                                scanType == ScanType.IncreasedBy ||
                                scanType == ScanType.DecreasedBy ||
                                scanType == ScanType.Between ||
                                scanType == ScanType.BiggerThan ||
                                scanType == ScanType.SmallerThan;

            if (requiresValue && string.IsNullOrWhiteSpace(txtValue.Text))
            {
                System.Windows.MessageBox.Show("Please enter a value to scan for!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Prepare value string
            string value = txtValue.Text.Trim();

            // For "Between" scan, combine min and max with hyphen
            if (scanType == ScanType.Between)
            {
                string value2 = txtValue2.Text.Trim();
                if (string.IsNullOrEmpty(value2))
                {
                    System.Windows.MessageBox.Show("Please enter both min and max values for Between scan.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                value = $"{value}-{value2}"; // Format: "min-max"
            }

            // Capture all UI values BEFORE entering background thread
            bool fastScan = chkFastScan.IsChecked == true;
            bool? writableOnly = chkWritable.IsChecked;
            bool? copyOnWrite = chkCopyOnWrite.IsChecked;
            bool? executableOnly = chkExecutable.IsChecked;
            bool? activeMemoryOnly = chkActiveMemoryOnly.IsChecked;

            btnFirstScan.IsEnabled = false;
            btnNextScan.IsEnabled = false;
            btnCancelScan.IsEnabled = true;
            scanResults.Clear();
            txtFoundCount.Text = "Preparing scan...";
            progressBar.Visibility = Visibility.Visible;
            progressBar.Value = 0;

            try
            {
                var startTime = DateTime.Now;

                await System.Threading.Tasks.Task.Run(() =>
                {
                    // Don't pass address range parameters - let MemoryScanner use its safe built-in defaults
                    // The XAML defaults (0x7FFFFFFFFFFFFFFF) are incompatible and cause 0 results
                    _scanner.FirstScan(scanType, valueType, value ?? "",
                        fastScan, writableOnly, copyOnWrite, executableOnly, activeMemoryOnly);
                });

                var elapsed = DateTime.Now - startTime;
                progressBar.Visibility = Visibility.Collapsed;

                // DEBUG: Log actual scanner result count
                System.Diagnostics.Debug.WriteLine($"Scanner ResultCount: {_scanner.ResultCount}");
                System.Diagnostics.Debug.WriteLine($"scanResults ObservableCollection count before update: {scanResults.Count}");

                UpdateResultsList();

                System.Diagnostics.Debug.WriteLine($"scanResults ObservableCollection count after update: {scanResults.Count}");
                txtFoundCount.Text = $"Found: {_scanner.ResultCount:N0} ({elapsed.TotalSeconds:F1}s)";

                btnNextScan.IsEnabled = _scanner.ResultCount > 0;
                btnUndoScan.IsEnabled = _scanner.CanUndo;

                // After first scan completes, enable next scan types
                UpdateScanTypeAvailability(false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Scan error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                txtFoundCount.Text = "Scan failed";
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                btnFirstScan.IsEnabled = true;
                btnCancelScan.IsEnabled = false;
            }
        }

        private async void NextScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner == null || _scanner.ResultCount == 0) return;

            var scanType = GetScanType();
            var valueType = GetValueType();

            // Prepare value string
            string value = txtValue.Text.Trim();

            // For "Between" scan, combine min and max with hyphen
            if (scanType == ScanType.Between)
            {
                string value2 = txtValue2.Text.Trim();
                if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(value2))
                {
                    System.Windows.MessageBox.Show("Please enter both min and max values for Between scan.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                value = $"{value}-{value2}"; // Format: "min-max"
            }

            btnNextScan.IsEnabled = false;
            btnFirstScan.IsEnabled = false;
            btnCancelScan.IsEnabled = true;
            txtFoundCount.Text = "Scanning...";
            progressBar.Visibility = Visibility.Visible;
            progressBar.Value = 0;

            try
            {
                var startTime = DateTime.Now;
                await System.Threading.Tasks.Task.Run(() => _scanner.NextScan(scanType, valueType, value ?? ""));
                var elapsed = DateTime.Now - startTime;
                
                UpdateResultsList();
                txtFoundCount.Text = $"Found: {_scanner.ResultCount:N0} ({elapsed.TotalSeconds:F1}s)";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Scan error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                btnNextScan.IsEnabled = _scanner.ResultCount > 0;
                btnUndoScan.IsEnabled = _scanner.CanUndo;
                btnFirstScan.IsEnabled = true;
                btnCancelScan.IsEnabled = false;
            }
        }

        private void NewScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner == null) return;
            _scanner.ClearResults();
            scanResults.Clear();
            btnNextScan.IsEnabled = false;
            btnUndoScan.IsEnabled = false;
            txtFoundCount.Text = "Found: 0";
            progressBar.Visibility = Visibility.Collapsed;

            // Reset to first scan mode - disable scan types that need previous results
            UpdateScanTypeAvailability(true);
        }

        private void CancelScan_Click(object sender, RoutedEventArgs e)
        {
            _scanner?.CancelScan();
            btnCancelScan.IsEnabled = false;
            txtFoundCount.Text = "Cancelling...";
        }

        private void UndoScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanner != null && _scanner.CanUndo)
            {
                _scanner.UndoScan();
                UpdateResultsList();
                txtFoundCount.Text = $"Found: {_scanner.ResultCount:N0} (Undo)";
                btnNextScan.IsEnabled = _scanner.ResultCount > 0;
                btnUndoScan.IsEnabled = _scanner.CanUndo;
            }
        }

        private async System.Threading.Tasks.Task PerformAOBScan(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;

            btnFirstScan.IsEnabled = false;
            btnNextScan.IsEnabled = false;
            scanResults.Clear();
            txtFoundCount.Text = "Scanning for pattern...";
            progressBar.Visibility = Visibility.Visible;

            try
            {
                var startTime = DateTime.Now;
                List<IntPtr>? aobResults = null;
                await System.Threading.Tasks.Task.Run(() => aobResults = _scanner!.ScanArrayOfBytes(pattern));
                var elapsed = DateTime.Now - startTime;

                if (aobResults != null && aobResults.Count > 0)
                {
                    int displayCount = Math.Min(aobResults.Count, 5000);
                    for (int i = 0; i < displayCount; i++)
                    {
                        var addr = aobResults[i];
                        var (addressDisplay, addressColor) = FormatAddressDisplay(addr);
                        Dispatcher.Invoke(() => scanResults.Add(new ScanResultItem
                        {
                            Address = addressDisplay,
                            Value = pattern,
                            Previous = "",
                            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                                addressColor.R,
                                addressColor.G,
                                addressColor.B)),
                            AddressPtr = addr,
                            ValueBytes = System.Text.Encoding.ASCII.GetBytes(pattern)
                        }));
                    }
                    txtFoundCount.Text = $"Found: {aobResults.Count:N0} ({elapsed.TotalSeconds:F1}s)";
                }
                else txtFoundCount.Text = $"Found: 0 ({elapsed.TotalSeconds:F1}s)";
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                btnFirstScan.IsEnabled = true;
            }
        }

        private void UpdateResultsList()
        {
            Dispatcher.Invoke(() =>
            {
                scanResults.Clear();
                if (_scanner == null) return;

                int displayCount = Math.Min(_scanner.ResultCount, 5000);
                for (int i = 0; i < displayCount; i++)
                {
                    var result = _scanner.Results[i];
                    string value = ReadValueAsString(result.Address);
                    var (addressDisplay, addressColor) = FormatAddressDisplay(result.Address);

                    scanResults.Add(new ScanResultItem
                    {
                        Address = addressDisplay,
                        Value = value,
                        Previous = "",
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                            addressColor.R,
                            addressColor.G,
                            addressColor.B)),
                        AddressPtr = result.Address,
                        ValueBytes = result.Value
                    });
                }
            });
        }

        private ScanType GetScanType()
        {
            // ComboBox items: Exact, Increased value, Increased value by..., Decreased value,
            // Decreased value by..., Value between, Changed value, Unchanged value, Unknown initial value
            return cmbScanType.SelectedIndex switch
            {
                0 => ScanType.Exact,
                1 => ScanType.Increased,
                2 => ScanType.IncreasedBy,
                3 => ScanType.Decreased,
                4 => ScanType.DecreasedBy,
                5 => ScanType.Between,
                6 => ScanType.Changed,
                7 => ScanType.Unchanged,
                8 => ScanType.Unknown,
                _ => ScanType.Exact
            };
        }

        private ScanValueType GetValueType()
        {
            // ComboBox items: Byte, 2 Bytes, 4 Bytes, 8 Bytes, Float, Double, String, AOB
            return cmbValueType.SelectedIndex switch
            {
                0 => ScanValueType.Byte,
                1 => ScanValueType.Int16,
                2 => ScanValueType.Int32,
                3 => ScanValueType.Int64,
                4 => ScanValueType.Float,
                5 => ScanValueType.Double,
                6 => ScanValueType.String,
                7 => ScanValueType.Byte, // AOB handled separately in FirstScan_Click
                _ => ScanValueType.Int32
            };
        }

        private string ReadValueAsString(IntPtr address)
        {
            if (_process == null) return "?";
            try
            {
                var valueType = GetValueType();
                return valueType switch
                {
                    ScanValueType.Byte => _process.Read<byte>(address).ToString(),
                    ScanValueType.Int16 => _process.Read<short>(address).ToString(),
                    ScanValueType.Int32 => _process.Read<int>(address).ToString(),
                    ScanValueType.Int64 => _process.Read<long>(address).ToString(),
                    ScanValueType.Float => _process.Read<float>(address).ToString("F2"),
                    ScanValueType.Double => _process.Read<double>(address).ToString("F4"),
                    _ => "?"
                };
            }
            catch { return "?"; }
        }

        // ═══════════════════════════════════════════════════════════════════
        // ADDRESS LIST LOGIC
        // ═══════════════════════════════════════════════════════════════════

        private void AddToList_Click(object sender, RoutedEventArgs e) => AddSelectedFoundAddressesToList();

        private void AddSelectedFoundAddressesToList()
        {
            if (_process == null)
            {
                System.Windows.MessageBox.Show("Please open a process first.", "No Process", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (lvFoundAddressesWpf.SelectedItems.Count == 0)
            {
                System.Windows.MessageBox.Show("Please select one or more addresses from the scan results first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string typeStr = GetValueType() switch
            {
                ScanValueType.Byte => "Byte",
                ScanValueType.Int16 => "2 Bytes",
                ScanValueType.Int32 => "4 Bytes",
                ScanValueType.Int64 => "8 Bytes",
                ScanValueType.Float => "Float",
                ScanValueType.Double => "Double",
                _ => "4 Bytes"
            };

            foreach (ScanResultItem item in lvFoundAddressesWpf.SelectedItems)
            {
                AddAddressToList(item.AddressPtr, "", ReadValueAsString(item.AddressPtr), typeStr);
            }
        }

        private AddressEntryItem AddAddressToList(IntPtr address, string description, string value, string type, string originalAddressString = "")
        {
            var (addressDisplay, addressColor) = FormatAddressDisplay(address);
            var entry = new AddressEntryItem
            {
                Frozen = false,
                Description = description,
                Address = addressDisplay,
                Type = type,
                Value = value,
                AddressForeground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                    addressColor.R,
                    addressColor.G,
                    addressColor.B)),
                AddressPtr = address,
                Active = true,
                FrozenValue = "",
                ShowAsHex = false,
                OriginalAddressString = string.IsNullOrEmpty(originalAddressString) ? addressDisplay : originalAddressString
            };

            Dispatcher.Invoke(() => addressEntries.Add(entry));
            return entry;
        }

        private void AddAddressManually_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_process == null)
                {
                    System.Windows.MessageBox.Show("Please open a process first.", "No Process", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dialog = new AddAddressDialog(_process);
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // AddAddressDialog already resolves the address internally
                    string valueType = dialog.ValueType;
                    string value = ReadValueAsString(dialog.Address);

                    AddAddressToList(
                        dialog.Address,
                        dialog.Description,
                        value,
                        valueType,
                        dialog.OriginalAddressString
                    );

                    // Update the list item to show hex/signed if those were checked
                    if (addressEntries.Count > 0)
                    {
                        var lastEntry = addressEntries[addressEntries.Count - 1];
                        lastEntry.ShowAsHex = dialog.IsHexadecimal;
                        // Re-format value if hex was selected
                        if (lastEntry.ShowAsHex)
                        {
                            lastEntry.Value = FormatValue(value, valueType, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error adding address: {ex.Message}\n\n{ex.StackTrace}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LvFoundAddresses_DoubleClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AddSelectedFoundAddressesToList();
        }

        // WPF ListView event handlers
        private void AddressEntry_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.DataContext is AddressEntryItem entry)
            {
                entry.Frozen = true;
                entry.FrozenValue = ReadValueAsString(entry.AddressPtr);
            }
        }

        private void AddressEntry_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.DataContext is AddressEntryItem entry)
            {
                entry.Frozen = false;
            }
        }

        private void LvAddressList_DoubleClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                var (addressDisplay, addressColor) = FormatAddressDisplay(entry.AddressPtr);
                var dialog = new EditAddressDialog(addressDisplay, entry.Value, entry.Description, addressColor);
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (_process != null && !string.IsNullOrEmpty(dialog.NewValue))
                    {
                        if (WriteValue(entry.AddressPtr, entry.Type, dialog.NewValue))
                        {
                            entry.FrozenValue = dialog.NewValue;
                            entry.Value = dialog.NewValue;
                        }
                    }
                    entry.Description = dialog.Description;
                }
            }
        }

        // Context menu handlers for scan results
        private void CopyFoundAddress_Click(object sender, RoutedEventArgs e)
        {
            if (lvFoundAddressesWpf.SelectedItem is ScanResultItem item)
                System.Windows.Clipboard.SetText(item.Address);
        }

        private void BrowseMemoryRegion_Click(object sender, RoutedEventArgs e)
        {
            if (lvFoundAddressesWpf.SelectedItem is ScanResultItem item)
            {
                string addrStr = item.Address;
                if (addrStr.Contains('+'))
                {
                    var resolved = ResolveModuleOffsetAddress(addrStr);
                    if (resolved != IntPtr.Zero) OpenOrFocusMemoryView(resolved);
                }
                else if (long.TryParse(addrStr, System.Globalization.NumberStyles.HexNumber, null, out long addr))
                {
                    OpenOrFocusMemoryView(new IntPtr(addr));
                }
            }
        }

        // Context menu handlers for address list
        private void ToggleFreeze_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                entry.Frozen = !entry.Frozen;
                if (entry.Frozen)
                    entry.FrozenValue = ReadValueAsString(entry.AddressPtr);
            }
        }

        private void ShowAsHex_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                entry.ShowAsHex = !entry.ShowAsHex;
                string rawValue = ReadValueAsString(entry.AddressPtr);
                entry.Value = FormatValue(rawValue, entry.Type, entry.ShowAsHex);
            }
        }

        private void IncreaseValue_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                ModifyValue(entry, 1);
                entry.Value = ReadValueAsString(entry.AddressPtr);
            }
        }

        private void DecreaseValue_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                ModifyValue(entry, -1);
                entry.Value = ReadValueAsString(entry.AddressPtr);
            }
        }

        private void ChangeValue_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                string currentValue = ReadValueAsString(entry.AddressPtr);
                var dialog = new ChangeValueDialog(currentValue, entry.Type);
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(dialog.NewValue))
                {
                    WriteValueFromString(entry.AddressPtr, entry.Type, dialog.NewValue);
                    entry.FrozenValue = dialog.NewValue;
                    entry.Value = dialog.NewValue;
                }
            }
        }

        private void FindWrites_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                var dialog = new FindAccessDialog(_process!, (ulong)entry.AddressPtr.ToInt64(), writeOnly: true);
                dialog.NavigateToAddress += (s, addr) => OpenOrFocusMemoryView(new IntPtr((long)addr));
                dialog.Show();
            }
        }

        private void FindAccesses_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                var dialog = new FindAccessDialog(_process!, (ulong)entry.AddressPtr.ToInt64(), writeOnly: false);
                dialog.NavigateToAddress += (s, addr) => OpenOrFocusMemoryView(new IntPtr((long)addr));
                dialog.Show();
            }
        }

        private void CopyAddress_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
                System.Windows.Clipboard.SetText($"0x{entry.AddressPtr.ToInt64():X}");
        }

        private void CopyValue_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
            {
                string value = ReadValueAsString(entry.AddressPtr);
                System.Windows.Clipboard.SetText(value);
            }
        }

        private void CopyDescription_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry && !string.IsNullOrEmpty(entry.Description))
                System.Windows.Clipboard.SetText(entry.Description);
        }

        private void DeleteAddress_Click(object sender, RoutedEventArgs e)
        {
            if (lvAddressListWpf.SelectedItem is AddressEntryItem entry)
                addressEntries.Remove(entry);
        }

        private void ModifyValue(AddressEntryItem entry, int delta)
        {
            if (_process == null) return;
            switch (entry.Type)
            {
                case "Byte":
                    byte b = _process.Read<byte>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, (byte)(b + delta));
                    break;
                case "2 Bytes":
                    short s = _process.Read<short>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, (short)(s + delta));
                    break;
                case "4 Bytes":
                    int i = _process.Read<int>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, i + delta);
                    break;
                case "8 Bytes":
                    long l = _process.Read<long>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, l + delta);
                    break;
                case "Float":
                    float f = _process.Read<float>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, f + delta);
                    break;
                case "Double":
                    double d = _process.Read<double>(entry.AddressPtr);
                    _process.WriteWithProtection(entry.AddressPtr, d + delta);
                    break;
            }
        }

        private bool WriteValue(IntPtr address, string type, string value)
        {
            if (_process == null) return false;
            bool success = false;
            switch (type)
            {
                case "Byte": if (byte.TryParse(value, out byte b)) success = _process.WriteWithProtection(address, b); break;
                case "2 Bytes": if (short.TryParse(value, out short s)) success = _process.WriteWithProtection(address, s); break;
                case "4 Bytes": if (int.TryParse(value, out int i)) success = _process.WriteWithProtection(address, i); break;
                case "8 Bytes": if (long.TryParse(value, out long l)) success = _process.WriteWithProtection(address, l); break;
                case "Float": if (float.TryParse(value, out float f)) success = _process.WriteWithProtection(address, f); break;
                case "Double": if (double.TryParse(value, out double d)) success = _process.WriteWithProtection(address, d); break;
            }
            return success;
        }

        // ═══════════════════════════════════════════════════════════════════
        // TIMERS & LIVE UPDATES
        // ═══════════════════════════════════════════════════════════════════

        private void InitializeTimers()
        {
            _freezeTimer = new DispatcherTimer(DispatcherPriority.Background);
            _freezeTimer.Tick += FreezeTimer_Tick;
            _freezeTimer.Start();

            _autoAttachTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _autoAttachTimer.Tick += AutoAttachTimer_Tick;
            _autoAttachTimer.Start();

            // Suspend timer - for repeatedly suspending attached process
            _suspendTimer = new DispatcherTimer(DispatcherPriority.Send);
            _suspendTimer.Tick += SuspendTimer_Tick;
            // Don't start yet - will be started when process attaches and interval > 0

            _updateTimer = new DispatcherTimer(DispatcherPriority.Background);
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();
        }

        private void SuspendTimer_Tick(object? sender, EventArgs e)
        {
            if (_process == null || !_process.IsOpen) return;

            try
            {
                // Suspend the process
                _process.Suspend();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SuspendTimer] Failed to suspend: {ex.Message}");
            }
        }

        private void StartSuspendTimerIfNeeded()
        {
            int interval = SettingsManager.SuspendInterval;
            if (interval > 0 && SettingsManager.FastSuspendOnAttach && _process != null)
            {
                _suspendTimer!.Interval = TimeSpan.FromMilliseconds(interval);
                _suspendTimer.Start();
                System.Diagnostics.Debug.WriteLine($"[SuspendTimer] Started with interval {interval}ms");
            }
        }

        private void StopSuspendTimer()
        {
            _suspendTimer?.Stop();
            System.Diagnostics.Debug.WriteLine("[SuspendTimer] Stopped");
        }

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_process == null || !_liveUpdatesEnabled) return;

            Dispatcher.Invoke(() =>
            {
                foreach (var item in scanResults)
                {
                    if (item.Address.Contains("+")) continue;
                    try
                    {
                        IntPtr address;
                        if (item.Address.Replace("0x", "").All(c => "0123456789ABCDEFabcdef".Contains(c)))
                        {
                            address = new IntPtr(Convert.ToInt64(item.Address, 16));
                        }
                        else address = ResolveModuleOffsetAddress(item.Address);

                        if (address != IntPtr.Zero)
                        {
                            string val = ReadValueAsString(address);
                            if (item.Value != val)
                            {
                                item.Value = val;
                                // WPF doesn't easily support row background colors in GridView
                                // Consider adding a Background property to ScanResultItem if needed
                            }
                        }
                    }
                    catch { }
                }

                foreach (var entry in addressEntries)
                {
                    try
                    {
                        string raw = entry.Type switch
                        {
                            "Byte" => _process.Read<byte>(entry.AddressPtr).ToString(),
                            "2 Bytes" => _process.Read<short>(entry.AddressPtr).ToString(),
                            "4 Bytes" => _process.Read<int>(entry.AddressPtr).ToString(),
                            "8 Bytes" => _process.Read<long>(entry.AddressPtr).ToString(),
                            "Float" => _process.Read<float>(entry.AddressPtr).ToString("F2"),
                            "Double" => _process.Read<double>(entry.AddressPtr).ToString("F4"),
                            _ => "?"
                        };
                        string display = FormatValue(raw, entry.Type, entry.ShowAsHex);
                        if (entry.Value != display)
                        {
                            entry.Value = display;
                        }
                    }
                    catch { entry.Value = "???"; }
                }
            });
        }

        private void FreezeTimer_Tick(object? sender, EventArgs e)
        {
            if (_process == null) return;
            foreach (var entry in addressEntries)
            {
                if (entry.Frozen)
                {
                    try { WriteValue(entry.AddressPtr, entry.Type, entry.FrozenValue); } catch { }
                }
            }
        }

        private void AutoAttachTimer_Tick(object? sender, EventArgs e)
        {
            if (SettingsManager.AlwaysAutoAttach && (_process == null || !_process.IsOpen))
                TryAutoAttach();
        }

        private void TryAutoAttach()
        {
            string autoAttachList = SettingsManager.AutoAttachProcess;
            if (string.IsNullOrWhiteSpace(autoAttachList)) return;
            var processNames = autoAttachList.Split(';', StringSplitOptions.RemoveEmptyEntries);
            
            var runningProcesses = Process.GetProcesses();
            foreach (var targetName in processNames)
            {
                string trimmed = targetName.Trim();
                var match = runningProcesses.FirstOrDefault(p => p.ProcessName.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
                if (match != null && match.Id != Environment.ProcessId)
                {
                    if (_process == null || !_process.IsOpen || _process.Target?.Id != match.Id)
                    {
                        AttachToProcess(match);
                        break;
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════

        private void ApplySettings()
        {
            chkFastScan.IsChecked = SettingsManager.FastScanByDefault;
            _liveUpdatesEnabled = menuLiveUpdates.IsChecked;

            if (_freezeTimer != null)
                _freezeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, SettingsManager.FreezeInterval));

            if (_updateTimer != null)
                _updateTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, SettingsManager.UpdateInterval));

            StopSuspendTimer();
            StartSuspendTimerIfNeeded();
            RestartAutoAttach();
        }

        private void RestartAutoAttach()
        {
            _fastProcessWatcher?.Dispose();
            _fastProcessWatcher = null;

            if (SettingsManager.AlwaysAutoAttach && SettingsManager.FastAutoAttach)
            {
                _fastProcessWatcher = new FastProcessWatcher
                {
                    PollingIntervalMs = SettingsManager.FastAutoAttachInterval,
                    ImmediatelySuspend = SettingsManager.FastSuspendOnAttach
                };
                _fastProcessWatcher.ProcessDetected += (p) => Dispatcher.Invoke(() => HandleFastProcessDetected(p));
                var processNames = SettingsManager.AutoAttachProcess.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());
                _fastProcessWatcher.Start(processNames, SettingsManager.UseWmiProcessWatch);
                _autoAttachTimer?.Stop();
            }
            else _autoAttachTimer?.Start();
        }

        private void HandleFastProcessDetected(Process p)
        {
            if (p.Id == Environment.ProcessId) return;
            if (_process != null && _process.IsOpen && _process.Target?.Id == p.Id) return;
            AttachToProcess(p);
        }

        private (string display, System.Drawing.Color color) FormatAddressDisplay(IntPtr address)
        {
            if (_process != null)
            {
                var module = _process.GetModuleForAddress(address);
                if (module != null)
                {
                    long offset = address.ToInt64() - module.BaseAddress.ToInt64();
                    return ($"{module.ModuleName}+{offset:X}", System.Drawing.Color.MediumSeaGreen);
                }
            }
            return ($"{address.ToInt64():X8}", System.Drawing.Color.FromArgb(230, 237, 243));
        }

        private IntPtr ResolveModuleOffsetAddress(string addressString)
        {
            if (_process == null || string.IsNullOrEmpty(addressString)) return IntPtr.Zero;
            int plusIndex = addressString.IndexOf('+');
            if (plusIndex <= 0) return IntPtr.Zero;
            string modName = addressString.Substring(0, plusIndex).Trim();
            string offsetStr = addressString.Substring(plusIndex + 1).Trim().Replace("0x", "");
            if (!long.TryParse(offsetStr, System.Globalization.NumberStyles.HexNumber, null, out long offset)) return IntPtr.Zero;
            var mod = _process.GetModules().FirstOrDefault(m => m.ModuleName.Equals(modName, StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals(modName + ".exe", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals(modName + ".dll", StringComparison.OrdinalIgnoreCase));
            return mod != null ? new IntPtr(mod.BaseAddress.ToInt64() + offset) : IntPtr.Zero;
        }

        private string FormatValue(string value, string type, bool showHex)
        {
            if (!showHex) return value;
            try {
                switch (type) {
                    case "Byte": if (byte.TryParse(value, out byte b)) return $"0x{b:X2}"; break;
                    case "2 Bytes": if (short.TryParse(value, out short s)) return $"0x{s:X4}"; break;
                    case "4 Bytes": if (int.TryParse(value, out int i)) return $"0x{i:X8}"; break;
                    case "8 Bytes": if (long.TryParse(value, out long l)) return $"0x{l:X16}"; break;
                }
            } catch { }
            return value;
        }

        private void ModifyValue(AddressEntry entry, int delta)
        {
            if (_process == null) return;

            try
            {
                string currentValue = ReadValueAsString(entry.Address);
                string newValueStr = "";

                // Parse and modify based on type
                switch (entry.Type)
                {
                    case "Byte":
                        if (byte.TryParse(currentValue, out byte b))
                        {
                            b = (byte)Math.Max(0, Math.Min(255, b + delta));
                            newValueStr = b.ToString();
                        }
                        break;
                    case "2 Bytes":
                        if (short.TryParse(currentValue, out short s))
                        {
                            s = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s + delta));
                            newValueStr = s.ToString();
                        }
                        break;
                    case "4 Bytes":
                        if (int.TryParse(currentValue, out int i))
                        {
                            i = Math.Max(int.MinValue, Math.Min(int.MaxValue, i + delta));
                            newValueStr = i.ToString();
                        }
                        break;
                    case "8 Bytes":
                        if (long.TryParse(currentValue, out long l))
                        {
                            l = l + delta;
                            newValueStr = l.ToString();
                        }
                        break;
                    case "Float":
                        if (float.TryParse(currentValue, out float f))
                        {
                            f = f + delta;
                            newValueStr = f.ToString();
                        }
                        break;
                    case "Double":
                        if (double.TryParse(currentValue, out double d))
                        {
                            d = d + delta;
                            newValueStr = d.ToString();
                        }
                        break;
                }

                if (!string.IsNullOrEmpty(newValueStr))
                {
                    WriteValueFromString(entry.Address, entry.Type, newValueStr);
                    entry.FrozenValue = newValueStr;
                }
            }
            catch { }
        }

        private void WriteValueFromString(IntPtr address, string type, string valueStr)
        {
            WriteValue(address, type, valueStr);
        }

        // ═══════════════════════════════════════════════════════════════════
        // SINGLETON FORM HANDLERS
        // ═══════════════════════════════════════════════════════════════════

        private void MemoryView_Click(object sender, RoutedEventArgs e) => OpenOrFocusMemoryView(IntPtr.Zero);

        private void OpenOrFocusMemoryView(IntPtr startAddress)
        {
            if (_process == null) return;
            if (_memoryViewForm != null && !_memoryViewForm.IsDisposed)
            {
                if (startAddress != IntPtr.Zero) _memoryViewForm.NavigateToAddress(startAddress);
                _memoryViewForm.Activate();
            }
            else
            {
                _memoryViewForm = new MemoryView.MemoryViewForm(_process, startAddress);
                _memoryViewForm.FormClosed += (s, ev) => _memoryViewForm = null;
                _memoryViewForm.Show();
            }
        }

        private void ACBypassLab_Click(object sender, RoutedEventArgs e) => OpenOrFocusACBypassForm();

        private void OpenOrFocusACBypassForm()
        {
            if (_acBypassForm != null && !_acBypassForm.IsDisposed) _acBypassForm.Activate();
            else
            {
                _acBypassForm = new AntiCheatBypassForm(_process);
                _acBypassForm.FormClosed += (s, ev) => { _acBypassForm = null; CheckExistingDriverConnection(); };
                _acBypassForm.DriverStatusChanged += (s, ev) => CheckExistingDriverConnection();
                _acBypassForm.Show();
            }
        }


        // ═══════════════════════════════════════════════════════════════════
        // DRIVER CONNECTION
        // ═══════════════════════════════════════════════════════════════════

        private void CheckExistingDriverConnection()
        {
            bool isLoaded = AntiCheatBypassForm.DriverController?.IsConnected() ?? false;
            driverStatusPanel.Visibility = isLoaded ? Visibility.Visible : Visibility.Collapsed;
            txtDriverStatus.Text = isLoaded ? "Driver: CrxShield Loaded" : "";
        }

        // ═══════════════════════════════════════════════════════════════════
        // HOTKEYS
        // ═══════════════════════════════════════════════════════════════════

        private void RegisterGlobalHotkeys()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UnregisterAllHotkeys(hwnd);
            
            var hotkeys = SettingsManager.GetAllHotkeys();
            foreach (var kvp in hotkeys)
            {
                if (kvp.Value == 0) continue;
                var keys = (System.Windows.Forms.Keys)kvp.Value;
                uint modifiers = 0;
                uint vk = (uint)(keys & System.Windows.Forms.Keys.KeyCode);
                if ((keys & System.Windows.Forms.Keys.Control) == System.Windows.Forms.Keys.Control) modifiers |= MOD_CONTROL;
                if ((keys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift) modifiers |= MOD_SHIFT;
                if ((keys & System.Windows.Forms.Keys.Alt) == System.Windows.Forms.Keys.Alt) modifiers |= MOD_ALT;
                if (RegisterHotKey(hwnd, _nextHotkeyId, modifiers, vk))
                {
                    _hotkeyIdToFunction[_nextHotkeyId] = kvp.Key;
                    _nextHotkeyId++;
                }
            }
        }

        private void UnregisterAllHotkeys(IntPtr hwnd)
        {
            foreach (var id in _hotkeyIdToFunction.Keys) UnregisterHotKey(hwnd, id);
            _hotkeyIdToFunction.Clear();
            _nextHotkeyId = 1;
        }

        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY) ExecuteHotkeyFunction(wParam.ToInt32());
            return IntPtr.Zero;
        }

        private void ExecuteHotkeyFunction(int id)
        {
            if (_hotkeyIdToFunction.TryGetValue(id, out string? func))
            {
                // Execute function based on string name
                switch (func)
                {
                    case "NewScan": NewScan_Click(null!, null!); break;
                    case "NextScan": NextScan_Click(null!, null!); break;
                    case "UndoScan": UndoScan_Click(null!, null!); break;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // SAVE / LOAD TABLE
        // ═══════════════════════════════════════════════════════════════════

        private void SaveTable_Click(object sender, RoutedEventArgs e)
        {
            if (_process == null) return;
            var sfd = new SaveFileDialog
            {
                Filter = "CrxMem Table (*.crxct)|*.crxct",
                DefaultExt = "crxct",
                AddExtension = true
            };
            if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try
                {
                    var table = new CheatTable { ProcessName = _process.Target.ProcessName, Entries = new List<CheatEntry>() };
                    foreach (var entry in addressEntries)
                    {
                        table.Entries.Add(new CheatEntry
                        {
                            Address = !string.IsNullOrEmpty(entry.OriginalAddressString) ? entry.OriginalAddressString : FormatAddressDisplay(entry.AddressPtr).display,
                            Description = entry.Description,
                            Type = entry.Type,
                            Frozen = entry.Frozen,
                            FrozenValue = entry.FrozenValue,
                            Active = entry.Active,
                            ShowAsHex = entry.ShowAsHex
                        });
                    }

                    _tableSerializer.Save(sfd.FileName, table);
                    txtStatus.Text = $"Saved {table.Entries.Count} addresses";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error saving table: {ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadTable_Click(object sender, RoutedEventArgs e)
        {
            if (_process == null)
            {
                System.Windows.MessageBox.Show("Please open a process first before loading a cheat table.", "No Process", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ofd = new OpenFileDialog
            {
                Filter = "CrxMem Tables (*.crxct;*.ct)|*.crxct;*.ct|CrxMem Table (*.crxct)|*.crxct|Legacy CrxMem JSON (*.ct)|*.ct"
            };
            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try {
                    CheatTable table = _tableSerializer.Load(ofd.FileName);

                    addressEntries.Clear();
                    int loadedCount = 0;
                    foreach (var entry in table.Entries)
                    {
                        IntPtr addr = entry.Address.Contains('+') ? ResolveModuleOffsetAddress(entry.Address) : new IntPtr(Convert.ToInt64(entry.Address, 16));
                        if (addr != IntPtr.Zero)
                        {
                            AddressEntryItem addedEntry = AddAddressToList(addr, entry.Description, ReadValueAsString(addr), entry.Type, entry.Address);
                            addedEntry.Active = entry.Active;
                            addedEntry.Frozen = entry.Frozen;
                            addedEntry.FrozenValue = entry.FrozenValue;
                            addedEntry.ShowAsHex = entry.ShowAsHex;
                            if (addedEntry.ShowAsHex)
                                addedEntry.Value = FormatValue(addedEntry.Value, addedEntry.Type, showHex: true);
                            loadedCount++;
                        }
                    }

                    System.Windows.MessageBox.Show($"Loaded {loadedCount} of {table.Entries.Count} addresses from cheat table.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error loading cheat table: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // OTHER MENU STUBS
        // ═══════════════════════════════════════════════════════════════════
        
        private void LiveUpdates_Click(object sender, RoutedEventArgs e) => _liveUpdatesEnabled = menuLiveUpdates.IsChecked;
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var form = new SettingsForm();
            form.SettingsApplied += SettingsForm_SettingsApplied;
            form.ShowDialog();
        }

        private void SettingsForm_SettingsApplied(object? sender, EventArgs e)
        {
            ApplySettings();
            RegisterGlobalHotkeys();
        }

        private LuaScriptEditorForm? _luaEditorForm;
        private LuaEditorWindow? _luaEditorWindow;

        private void LuaEngine_Click(object sender, RoutedEventArgs e)
        {
            if (_luaEditorForm != null && !_luaEditorForm.IsDisposed)
            {
                _luaEditorForm.Activate();
                _luaEditorForm.UpdateProcessAccess(_process);
            }
            else
            {
                _luaEditorForm = new LuaScriptEditorForm(_process, this);
                _luaEditorForm.FormClosed += (s, ev) => _luaEditorForm = null;
                _luaEditorForm.Show();
            }
        }

        private void LuaEngineWpf_Click(object sender, RoutedEventArgs e)
        {
            if (_luaEditorWindow != null && _luaEditorWindow.IsLoaded)
            {
                _luaEditorWindow.Activate();
                _luaEditorWindow.UpdateProcessAccess(_process);
            }
            else
            {
                _luaEditorWindow = new LuaEditorWindow(_process, this);
                _luaEditorWindow.Closed += (s, ev) => _luaEditorWindow = null;
                _luaEditorWindow.Show();
            }
        }

        private void LuaMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (_process == null)
            {
                System.Windows.MessageBox.Show("Please open a process first.", "No Process", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var form = new LuaMonitorForm(_process);
            form.Show();
        }

        private void PEAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_process == null)
            {
                System.Windows.MessageBox.Show("Please open a process first.", "No Process", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var form = new PEAnalysisForm(_process);
            form.Show();
        }
        private void DllInjectionTest_Click(object sender, RoutedEventArgs e)
        {
            var form = new DllInjectionTestForm(_process);
            form.Show();
        }
        private void About_Click(object sender, RoutedEventArgs e) { System.Windows.MessageBox.Show("CrxMem v1.1\nModern Memory Scanner & Debugger", "About"); }
        private void CmbScanType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbScanType == null || txtValue == null || txtValue2 == null)
                return;

            int selectedIndex = cmbScanType.SelectedIndex;
            bool isFirstScan = scanResults.Count == 0;

            // Determine which scan types require user input
            bool requiresValue = selectedIndex switch
            {
                0 => true,  // Exact value
                2 => true,  // Increased value by
                4 => true,  // Decreased value by
                5 => true,  // Value between (shows 2 inputs)
                _ => false  // Increased, Decreased, Changed, Unchanged, Unknown
            };

            // Show/hide value input based on scan type
            if (requiresValue)
            {
                txtValue.Visibility = Visibility.Visible;

                // Show second input only for "Between"
                if (selectedIndex == 5)
                {
                    txtValue2.Visibility = Visibility.Visible;
                }
                else
                {
                    txtValue2.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // Hide both inputs for scan types that don't need values
                txtValue.Visibility = Visibility.Collapsed;
                txtValue2.Visibility = Visibility.Collapsed;
            }

            // Update scan type availability based on first/next scan
            UpdateScanTypeAvailability(isFirstScan);
        }

        private void UpdateScanTypeAvailability(bool isFirstScan)
        {
            if (cmbScanType.Items.Count == 0) return;

            if (isFirstScan)
            {
                // First scan: Only show scan types that don't require previous values
                // Enable: Exact (0), Between (5), Unknown (8)
                // Disable: Increased (1), IncreasedBy (2), Decreased (3), DecreasedBy (4), Changed (6), Unchanged (7)
                for (int i = 0; i < cmbScanType.Items.Count; i++)
                {
                    var item = cmbScanType.Items[i] as ComboBoxItem;
                    if (item != null)
                    {
                        // Indices 1-4, 6-7 require previous scan results
                        item.IsEnabled = !(i >= 1 && i <= 4) && i != 6 && i != 7;
                    }
                }
            }
            else
            {
                // Next scan: All scan types available
                for (int i = 0; i < cmbScanType.Items.Count; i++)
                {
                    var item = cmbScanType.Items[i] as ComboBoxItem;
                    if (item != null)
                    {
                        item.IsEnabled = true;
                    }
                }
            }
        }

        private void CmbModule_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbModule.SelectedIndex <= 0 || cmbModule.SelectedItem == null)
            {
                // "All Modules" selected - reset to full range
                txtStartAddress.Text = "0000000000000000";
                txtStopAddress.Text = "7FFFFFFFFFFFFFFF";
                return;
            }

            // Get selected module and set address range
            if (cmbModule.SelectedItem is Core.ModuleInfo module)
            {
                long baseAddr = module.BaseAddress.ToInt64();
                long endAddr = baseAddr + module.Size;

                txtStartAddress.Text = baseAddr.ToString("X16");
                txtStopAddress.Text = endAddr.ToString("X16");
            }
        }

        private void TxtValue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Enter) FirstScan_Click(sender, null!); }

        // ═══════════════════════════════════════════════════════════════════
        // NESTED CLASSES
        // ═══════════════════════════════════════════════════════════════════

        private class AddressEntry
        {
            public IntPtr Address { get; set; }
            public string Description { get; set; } = "";
            public string Type { get; set; } = "";
            public bool Active { get; set; } = true;
            public bool Frozen { get; set; } = false;
            public string FrozenValue { get; set; } = "";
            public bool ShowAsHex { get; set; } = false;
            public string OriginalAddressString { get; set; } = "";
        }
    }
}
