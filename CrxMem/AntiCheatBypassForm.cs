using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;
using CrxMem.Core;
using CrxShield;

namespace CrxMem
{
    /// <summary>
    /// Anticheat Bypass Research Lab - Modern UI form for testing kernel-level anticheat
    /// </summary>
    public class AntiCheatBypassForm : Form
    {
        private readonly ProcessAccess? _process;
        private readonly ToolTip _toolTip;

        // Tab control
        private TabControl _tabControl;

        // Tab 1: Overview controls
        private Panel _headerPanel;
        private FlowLayoutPanel _statusCardsPanel;
        private StatusCard _processStatusCard;
        private StatusCard _adminRightsCard;
        private StatusCard _testSigningCard;
        private StatusCard _driverStatusCard;
        private Button _btnScanAntiCheat;
        private ProgressBar _scanProgress;
        private ListView _lvAntiCheats;

        // Tab 2: Driver Management controls
        private TextBox _txtDriverPath;
        private Button _btnBrowseDriver;
        private Button _btnLoadDriver;
        private Button _btnUnloadDriver;
        private Button _btnReloadDriver;
        private Label _lblDriverLoaded;
        private Label _lblDriverSigned;
        private TextBox _txtServiceName;
        private Label _lblTestSigningStatus;
        private Button _btnEnableTestSigning;
        private Button _btnDisableTestSigning;

        // Tab 3: Bypass Techniques controls
        private ListView _lvBypassTests;
        private Button _btnSelectAll;
        private Button _btnDeselectAll;
        private Button _btnRunSelected;
        private Button _btnRunAll;
        private ListView _lvTestResults;
        private Button _btnExportResults;

        // Tab 4: Detection Log controls
        private RichTextBox _rtbLog;
        private Button _btnClearLog;
        private Button _btnExportLog;
        private CheckBox _chkAutoScroll;
        private Label _lblLogCount;
        private int _logEntryCount = 0;

        // Tab 5: Patch Notes controls
        private TextBox _txtPatchNotes;
        private Button _btnSaveNotes;
        private Button _btnLoadNotes;
        private Button _btnExportMarkdown;
        private Button _btnClearNotes;
        private Label _lblLastSaved;

        // State
        private bool _isDriverLoaded = false;
        private string _notesFilePath;
        private List<TestRunHistory> _testHistory = new List<TestRunHistory>();
        private System.Windows.Forms.Timer? _acMonitorTimer;

        // Kernel mode toggle
        private CheckBox _chkUseKernelMode;
        private Label _lblKernelModeStatus;
        private CrxDriverController? _driverController;

        /// <summary>
        /// Static property to check if kernel mode is enabled globally
        /// </summary>
        public static bool UseKernelMode { get; private set; } = false;

        /// <summary>
        /// Static driver controller instance for use by other parts of the application
        /// </summary>
        public static CrxDriverController? DriverController { get; private set; }

        /// <summary>
        /// Set the static driver controller (used when reconnecting to existing driver on app startup)
        /// </summary>
        public static void SetDriverController(CrxDriverController? controller)
        {
            DriverController = controller;
        }

        /// <summary>
        /// Event fired when driver status changes (loaded/unloaded)
        /// </summary>
        public event EventHandler? DriverStatusChanged;

        public AntiCheatBypassForm(ProcessAccess? process)
        {
            _process = process;
            _toolTip = new ToolTip
            {
                AutoPopDelay = 10000,
                InitialDelay = 500,
                ReshowDelay = 200,
                ShowAlways = true
            };

            _notesFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CrxMem", "bypass_notes.json");

            InitializeComponent();
            ApplyTheme();
            UpdateStatusCards();
            LoadPatchNotes();

            // Check if driver is already loaded from previous session
            RestoreDriverState();
        }

        /// <summary>
        /// Restore driver state if static DriverController is still valid
        /// </summary>
        private void RestoreDriverState()
        {
            if (DriverController != null && DriverController.IsConnected())
            {
                // Driver was loaded in a previous form instance, restore the state
                _driverController = DriverController;
                _isDriverLoaded = true;
                _lblDriverLoaded.Text = "Yes";
                _lblDriverLoaded.ForeColor = ThemeManager.StatusGreen;
                _driverStatusCard?.SetValue("Loaded", StatusLevel.Good);
                _btnUnloadDriver.Enabled = true;
                _btnReloadDriver.Enabled = true;

                // Update kernel mode status
                if (_driverController.GetVersion(out uint major, out uint minor, out uint build))
                {
                    _lblKernelModeStatus.Text = $"Connected (v{major}.{minor}.{build})";
                    _lblKernelModeStatus.ForeColor = ThemeManager.StatusGreen;
                    _chkUseKernelMode.Enabled = true;
                    _chkUseKernelMode.Checked = UseKernelMode;
                }
                else
                {
                    _lblKernelModeStatus.Text = "Connected";
                    _lblKernelModeStatus.ForeColor = ThemeManager.StatusGreen;
                    _chkUseKernelMode.Enabled = true;
                    _chkUseKernelMode.Checked = UseKernelMode;
                }

                LogMessage("Restored driver state from previous session", LogLevel.Info);
            }
        }

        private void InitializeComponent()
        {
            this.Text = "Anticheat Bypass Research Lab";
            this.Size = new Size(1200, 800);
            this.MinimumSize = new Size(900, 600);
            this.StartPosition = FormStartPosition.CenterParent;
            this.Font = new Font("Segoe UI", 9F);
            this.FormClosing += AntiCheatBypassForm_FormClosing;

            // Main tab control
            _tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                Padding = new Point(12, 6)
            };

            // Create tabs
            var tabOverview = CreateOverviewTab();
            var tabDriver = CreateDriverManagementTab();
            var tabBypass = CreateBypassTechniquesTab();
            var tabLog = CreateDetectionLogTab();
            var tabNotes = CreatePatchNotesTab();

            _tabControl.TabPages.Add(tabOverview);
            _tabControl.TabPages.Add(tabDriver);
            _tabControl.TabPages.Add(tabBypass);
            _tabControl.TabPages.Add(tabLog);
            _tabControl.TabPages.Add(tabNotes);

            this.Controls.Add(_tabControl);
        }

        #region Tab 1: Overview / AC Detection

        private TabPage CreateOverviewTab()
        {
            var tab = new TabPage("Overview");
            tab.Padding = new Padding(12);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));  // Header
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); // Status cards
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Content

            // Row 1: Header
            _headerPanel = new Panel { Dock = DockStyle.Fill };

            var lblTitle = new Label
            {
                Text = "Anticheat Bypass Research Lab",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(0, 5)
            };

            var lblSubtitle = new Label
            {
                Text = "Test and harden your kernel-level anticheat system",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.Gray,
                AutoSize = true,
                Location = new Point(3, 38)
            };

            _headerPanel.Controls.AddRange(new Control[] { lblTitle, lblSubtitle });
            mainLayout.Controls.Add(_headerPanel, 0, 0);

            // Row 2: Status Cards
            _statusCardsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0, 5, 0, 5)
            };

            _processStatusCard = new StatusCard("Process", "No Process", StatusLevel.Neutral);
            _toolTip.SetToolTip(_processStatusCard, "Shows the currently attached process");

            _adminRightsCard = new StatusCard("Admin Rights", "Checking...", StatusLevel.Neutral);
            _toolTip.SetToolTip(_adminRightsCard, "Administrator privileges are required for kernel operations");

            _testSigningCard = new StatusCard("Test Signing", "Checking...", StatusLevel.Neutral);
            _toolTip.SetToolTip(_testSigningCard, "Test Signing Mode allows loading unsigned kernel drivers");

            _driverStatusCard = new StatusCard("Test Driver", "Not Loaded", StatusLevel.Warning);
            _toolTip.SetToolTip(_driverStatusCard, "Status of the loaded test kernel driver");

            _statusCardsPanel.Controls.AddRange(new Control[] {
                _processStatusCard, _adminRightsCard, _testSigningCard, _driverStatusCard
            });
            mainLayout.Controls.Add(_statusCardsPanel, 0, 1);

            // Row 3: Anti-cheat scanner
            var contentPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            contentPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // Left: Scan controls
            var scanPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 10, 0) };

            _btnScanAntiCheat = CreateStyledButton("Scan for Anti-Cheats", ButtonStyle.Primary);
            _btnScanAntiCheat.Size = new Size(200, 45);
            _btnScanAntiCheat.Location = new Point(0, 0);
            _btnScanAntiCheat.Click += BtnScanAntiCheat_Click;
            _toolTip.SetToolTip(_btnScanAntiCheat, "Scans for known anti-cheat systems running on your machine");

            _scanProgress = new ProgressBar
            {
                Size = new Size(200, 20),
                Location = new Point(0, 55),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
                Visible = false
            };

            var btnMonitorAC = CreateStyledButton("Monitor (Real-time)", ButtonStyle.Success);
            btnMonitorAC.Size = new Size(200, 35);
            btnMonitorAC.Location = new Point(0, 85);
            btnMonitorAC.Click += BtnMonitorAC_Click;
            _toolTip.SetToolTip(btnMonitorAC, "Start real-time monitoring for AC processes");

            var lblScanInfo = new Label
            {
                Text = "Detects:\n- EasyAntiCheat\n- BattlEye\n- Vanguard\n- GameGuard\n- PunkBuster\n- XIGNCODE\n- And more...",
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.Gray,
                Location = new Point(0, 130),
                Size = new Size(200, 150)
            };

            scanPanel.Controls.AddRange(new Control[] { _btnScanAntiCheat, _scanProgress, btnMonitorAC, lblScanInfo });
            contentPanel.Controls.Add(scanPanel, 0, 0);

            // Right: Results list
            var resultsGroup = new GroupBox
            {
                Text = "Detected Anti-Cheat Systems",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            _lvAntiCheats = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false, // Disable harsh grid lines for dark theme
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            _lvAntiCheats.Columns.Add("Name", 200);
            _lvAntiCheats.Columns.Add("Type", 100);
            _lvAntiCheats.Columns.Add("Threat Level", 100);
            _lvAntiCheats.Columns.Add("Status", 150);
            _lvAntiCheats.DoubleClick += LvAntiCheats_DoubleClick;
            _toolTip.SetToolTip(_lvAntiCheats, "Double-click for detailed information");

            resultsGroup.Controls.Add(_lvAntiCheats);
            contentPanel.Controls.Add(resultsGroup, 1, 0);

            mainLayout.Controls.Add(contentPanel, 0, 2);
            tab.Controls.Add(mainLayout);
            return tab;
        }

        #endregion

        #region Tab 2: Driver Management

        private TabPage CreateDriverManagementTab()
        {
            var tab = new TabPage("Driver Management");
            tab.Padding = new Padding(12);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));  // Driver path
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));  // Action buttons
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130)); // Status panel
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Test signing

            // Row 1: Driver Path
            var pathGroup = new GroupBox
            {
                Text = "Test Driver Path",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var pathPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(5)
            };
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

            _txtDriverPath = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                PlaceholderText = "Select your compiled .sys driver file..."
            };
            _toolTip.SetToolTip(_txtDriverPath, "Full path to your test kernel driver (.sys file)");

            _btnBrowseDriver = CreateStyledButton("Browse...", ButtonStyle.Secondary);
            _btnBrowseDriver.Dock = DockStyle.Fill;
            _btnBrowseDriver.Margin = new Padding(5, 0, 0, 0);
            _btnBrowseDriver.Click += BtnBrowseDriver_Click;

            pathPanel.Controls.Add(_txtDriverPath, 0, 0);
            pathPanel.Controls.Add(_btnBrowseDriver, 1, 0);
            pathGroup.Controls.Add(pathPanel);
            mainLayout.Controls.Add(pathGroup, 0, 0);

            // Row 2: Action Buttons
            var actionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 10, 0, 10),
                WrapContents = false
            };

            _btnLoadDriver = CreateStyledButton("Load Driver", ButtonStyle.Success);
            _btnLoadDriver.Size = new Size(140, 40);
            _btnLoadDriver.Click += BtnLoadDriver_Click;
            _toolTip.SetToolTip(_btnLoadDriver, "Load the test driver into the kernel (requires admin)");

            _btnUnloadDriver = CreateStyledButton("Unload Driver", ButtonStyle.Danger);
            _btnUnloadDriver.Size = new Size(140, 40);
            _btnUnloadDriver.Margin = new Padding(10, 0, 0, 0);
            _btnUnloadDriver.Enabled = false;
            _btnUnloadDriver.Click += BtnUnloadDriver_Click;
            _toolTip.SetToolTip(_btnUnloadDriver, "Unload the test driver from the kernel");

            _btnReloadDriver = CreateStyledButton("Reload Driver", ButtonStyle.Primary);
            _btnReloadDriver.Size = new Size(140, 40);
            _btnReloadDriver.Margin = new Padding(10, 0, 0, 0);
            _btnReloadDriver.Enabled = false;
            _btnReloadDriver.Click += BtnReloadDriver_Click;
            _toolTip.SetToolTip(_btnReloadDriver, "Unload and reload the test driver");

            actionsPanel.Controls.AddRange(new Control[] { _btnLoadDriver, _btnUnloadDriver, _btnReloadDriver });
            mainLayout.Controls.Add(actionsPanel, 0, 1);

            // Row 3: Status Panel
            var statusGroup = new GroupBox
            {
                Text = "Driver Status",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var statusLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10)
            };
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            statusLayout.Controls.Add(new Label { Text = "Driver Loaded:", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left }, 0, 0);
            _lblDriverLoaded = new Label { Text = "No", Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = Color.Red, Anchor = AnchorStyles.Left };
            statusLayout.Controls.Add(_lblDriverLoaded, 1, 0);

            statusLayout.Controls.Add(new Label { Text = "Driver Signed:", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left }, 0, 1);
            _lblDriverSigned = new Label { Text = "N/A", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left };
            statusLayout.Controls.Add(_lblDriverSigned, 1, 1);

            statusLayout.Controls.Add(new Label { Text = "Service Name:", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left }, 0, 2);
            _txtServiceName = new TextBox { Text = "CrxShield", Font = new Font("Segoe UI", 9F), Width = 200 };
            _toolTip.SetToolTip(_txtServiceName, "Windows service name for the driver");
            statusLayout.Controls.Add(_txtServiceName, 1, 2);

            // Kernel mode toggle
            statusLayout.Controls.Add(new Label { Text = "Kernel Mode:", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left }, 0, 3);
            var kernelModePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0) };
            _chkUseKernelMode = new CheckBox
            {
                Text = "Use kernel driver for memory operations",
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Enabled = false // Disabled until driver is loaded
            };
            _chkUseKernelMode.CheckedChanged += ChkUseKernelMode_CheckedChanged;
            _toolTip.SetToolTip(_chkUseKernelMode, "When enabled, memory read/write operations will use the kernel driver instead of usermode APIs");
            kernelModePanel.Controls.Add(_chkUseKernelMode);
            statusLayout.Controls.Add(kernelModePanel, 1, 3);

            statusLayout.Controls.Add(new Label { Text = "Driver Comms:", Font = new Font("Segoe UI", 9F), Anchor = AnchorStyles.Left }, 0, 4);
            _lblKernelModeStatus = new Label { Text = "Not Connected", Font = new Font("Segoe UI", 9F, FontStyle.Bold), ForeColor = Color.Gray, Anchor = AnchorStyles.Left };
            statusLayout.Controls.Add(_lblKernelModeStatus, 1, 4);

            statusGroup.Controls.Add(statusLayout);
            mainLayout.Controls.Add(statusGroup, 0, 2);

            // Row 4: KDMapper Section (No test mode required!)
            var kdmapperGroup = new GroupBox
            {
                Text = "KDMapper - Load Without Test Mode",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ThemeManager.StatusGreen
            };

            var kdmapperLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            kdmapperLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            kdmapperLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            kdmapperLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var kdmapperInfo = new Label
            {
                Text = "Bypass driver signature checks without test mode using Intel CPU exploit",
                Font = new Font("Segoe UI", 9F),
                ForeColor = ThemeManager.ForegroundDim,
                AutoSize = false,
                Dock = DockStyle.Fill
            };
            kdmapperLayout.Controls.Add(kdmapperInfo, 0, 0);

            var kdmapperButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight
            };

            var btnKDMapperLoad = CreateStyledButton("Load with KDMapper", ButtonStyle.Success);
            btnKDMapperLoad.Size = new Size(160, 35);
            btnKDMapperLoad.Click += BtnKDMapperLoad_Click;
            _toolTip.SetToolTip(btnKDMapperLoad, "Load driver using KDMapper (no test mode, no reboot!)");

            var btnKDMapperInfo = CreateStyledButton("What is KDMapper?", ButtonStyle.Secondary);
            btnKDMapperInfo.Size = new Size(160, 35);
            btnKDMapperInfo.Margin = new Padding(10, 0, 0, 0);
            btnKDMapperInfo.Click += (s, e) => ShowKDMapperInfo();
            _toolTip.SetToolTip(btnKDMapperInfo, "Learn about KDMapper and how to download it");

            kdmapperButtons.Controls.AddRange(new Control[] { btnKDMapperLoad, btnKDMapperInfo });
            kdmapperLayout.Controls.Add(kdmapperButtons, 0, 1);

            kdmapperGroup.Controls.Add(kdmapperLayout);
            mainLayout.Controls.Add(kdmapperGroup, 0, 3);

            // Row 5: Test Signing Section
            var testSignGroup = new GroupBox
            {
                Text = "Test Signing Mode",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var testSignLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            testSignLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            testSignLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            testSignLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var warningPanel = new Panel { Dock = DockStyle.Fill };
            var lblWarning = new Label
            {
                Text = "Test Signing Mode allows loading unsigned drivers. Enabling/disabling requires a system reboot.",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.Orange,
                AutoSize = false,
                Dock = DockStyle.Fill
            };
            warningPanel.Controls.Add(lblWarning);
            testSignLayout.Controls.Add(warningPanel, 0, 0);

            var testSignButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight
            };

            _lblTestSigningStatus = new Label
            {
                Text = "Status: Checking...",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true,
                Padding = new Padding(0, 8, 20, 0)
            };

            _btnEnableTestSigning = CreateStyledButton("Enable Test Signing", ButtonStyle.Warning);
            _btnEnableTestSigning.Size = new Size(160, 35);
            _btnEnableTestSigning.Click += BtnEnableTestSigning_Click;
            _toolTip.SetToolTip(_btnEnableTestSigning, "Runs: bcdedit /set testsigning on (requires reboot)");

            _btnDisableTestSigning = CreateStyledButton("Disable Test Signing", ButtonStyle.Secondary);
            _btnDisableTestSigning.Size = new Size(160, 35);
            _btnDisableTestSigning.Margin = new Padding(10, 0, 0, 0);
            _btnDisableTestSigning.Click += BtnDisableTestSigning_Click;
            _toolTip.SetToolTip(_btnDisableTestSigning, "Runs: bcdedit /set testsigning off (requires reboot)");

            testSignButtons.Controls.AddRange(new Control[] { _lblTestSigningStatus, _btnEnableTestSigning, _btnDisableTestSigning });
            testSignLayout.Controls.Add(testSignButtons, 0, 1);

            testSignGroup.Controls.Add(testSignLayout);
            mainLayout.Controls.Add(testSignGroup, 0, 4);

            tab.Controls.Add(mainLayout);
            return tab;
        }

        #endregion

        #region Tab 3: Bypass Techniques

        private TabPage CreateBypassTechniquesTab()
        {
            var tab = new TabPage("Bypass Techniques");
            tab.Padding = new Padding(12);

            var splitContainer = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 500,
                SplitterWidth = 8
            };

            // Left Panel: Test Selection
            var leftGroup = new GroupBox
            {
                Text = "Available Tests",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var leftLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            _lvBypassTests = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                GridLines = false, // Disable harsh grid lines for dark theme
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            _lvBypassTests.Columns.Add("Test Name", 220);
            _lvBypassTests.Columns.Add("Description", 200);
            _lvBypassTests.Columns.Add("Risk", 80);

            // Add test items
            AddBypassTest("NtReadVirtualMemory", "Direct NT API memory read", "Low");
            AddBypassTest("NtWriteVirtualMemory", "Direct NT API memory write", "Low");
            AddBypassTest("Unsigned Driver Load", "Attempt to load test driver", "Medium");
            AddBypassTest("MmCopyVirtualMemory", "Kernel-mode memory access", "High");
            AddBypassTest("Hypervisor Detection", "Check for virtualization", "Low");
            AddBypassTest("Hardware Breakpoints", "DR register manipulation", "Medium");
            AddBypassTest("Manual PE Mapping", "Stealth DLL injection", "Medium");
            AddBypassTest("PEB Unlinking", "Hide from process list", "High");

            // Set tooltips for each item
            foreach (ListViewItem item in _lvBypassTests.Items)
            {
                item.ToolTipText = GetTestTooltip(item.Text);
            }
            _lvBypassTests.ShowItemToolTips = true;

            leftLayout.Controls.Add(_lvBypassTests, 0, 0);

            // Button bar
            var buttonBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 5, 0, 0)
            };

            _btnSelectAll = CreateStyledButton("Select All", ButtonStyle.Secondary);
            _btnSelectAll.Size = new Size(90, 32);
            _btnSelectAll.Click += (s, e) => { foreach (ListViewItem item in _lvBypassTests.Items) item.Checked = true; };

            _btnDeselectAll = CreateStyledButton("Deselect All", ButtonStyle.Secondary);
            _btnDeselectAll.Size = new Size(90, 32);
            _btnDeselectAll.Margin = new Padding(5, 0, 0, 0);
            _btnDeselectAll.Click += (s, e) => { foreach (ListViewItem item in _lvBypassTests.Items) item.Checked = false; };

            // Risk level filter buttons
            var btnLowRisk = CreateStyledButton("Low", ButtonStyle.Secondary);
            btnLowRisk.Size = new Size(50, 32);
            btnLowRisk.Margin = new Padding(15, 0, 0, 0);
            btnLowRisk.Click += (s, e) => FilterTestsByRisk("Low");
            _toolTip.SetToolTip(btnLowRisk, "Select only Low risk tests");

            var btnMedRisk = CreateStyledButton("Med", ButtonStyle.Secondary);
            btnMedRisk.Size = new Size(50, 32);
            btnMedRisk.Margin = new Padding(2, 0, 0, 0);
            btnMedRisk.Click += (s, e) => FilterTestsByRisk("Medium");
            _toolTip.SetToolTip(btnMedRisk, "Select only Medium risk tests");

            var btnHighRisk = CreateStyledButton("High", ButtonStyle.Secondary);
            btnHighRisk.Size = new Size(50, 32);
            btnHighRisk.Margin = new Padding(2, 0, 0, 0);
            btnHighRisk.Click += (s, e) => FilterTestsByRisk("High");
            _toolTip.SetToolTip(btnHighRisk, "Select only High risk tests");

            _btnRunSelected = CreateStyledButton("Run Selected", ButtonStyle.Success);
            _btnRunSelected.Size = new Size(110, 32);
            _btnRunSelected.Margin = new Padding(20, 0, 0, 0);
            _btnRunSelected.Click += BtnRunSelected_Click;
            _toolTip.SetToolTip(_btnRunSelected, "Run only the checked bypass tests");

            _btnRunAll = CreateStyledButton("Run All Tests", ButtonStyle.Primary);
            _btnRunAll.Size = new Size(110, 32);
            _btnRunAll.Margin = new Padding(5, 0, 0, 0);
            _btnRunAll.Click += BtnRunAll_Click;
            _toolTip.SetToolTip(_btnRunAll, "Run all bypass tests sequentially");

            buttonBar.Controls.AddRange(new Control[] { _btnSelectAll, _btnDeselectAll, btnLowRisk, btnMedRisk, btnHighRisk, _btnRunSelected, _btnRunAll });
            leftLayout.Controls.Add(buttonBar, 0, 1);

            leftGroup.Controls.Add(leftLayout);
            splitContainer.Panel1.Controls.Add(leftGroup);

            // Right Panel: Results
            var rightGroup = new GroupBox
            {
                Text = "Test Results",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

            _lvTestResults = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false, // Disable harsh grid lines for dark theme
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            _lvTestResults.Columns.Add("Status", 70);
            _lvTestResults.Columns.Add("Test Name", 160);
            _lvTestResults.Columns.Add("Result", 100);
            _lvTestResults.Columns.Add("Duration", 80);
            _lvTestResults.Columns.Add("Details", 200);

            rightLayout.Controls.Add(_lvTestResults, 0, 0);

            var exportPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 5, 0, 0)
            };

            var btnCopyResults = CreateStyledButton("Copy", ButtonStyle.Secondary);
            btnCopyResults.Size = new Size(80, 32);
            btnCopyResults.Click += (s, e) => CopyResultsToClipboard();
            _toolTip.SetToolTip(btnCopyResults, "Copy test results to clipboard");

            _btnExportResults = CreateStyledButton("Export", ButtonStyle.Secondary);
            _btnExportResults.Size = new Size(80, 32);
            _btnExportResults.Margin = new Padding(5, 0, 0, 0);
            _btnExportResults.Click += BtnExportResults_Click;
            _toolTip.SetToolTip(_btnExportResults, "Export test results to a markdown file");

            var btnViewHistory = CreateStyledButton("History", ButtonStyle.Secondary);
            btnViewHistory.Size = new Size(80, 32);
            btnViewHistory.Margin = new Padding(5, 0, 0, 0);
            btnViewHistory.Click += (s, e) => ShowTestHistory();
            _toolTip.SetToolTip(btnViewHistory, "View test result history");

            exportPanel.Controls.AddRange(new Control[] { btnCopyResults, _btnExportResults, btnViewHistory });
            rightLayout.Controls.Add(exportPanel, 0, 1);

            rightGroup.Controls.Add(rightLayout);
            splitContainer.Panel2.Controls.Add(rightGroup);

            tab.Controls.Add(splitContainer);
            return tab;
        }

        private void AddBypassTest(string name, string description, string risk)
        {
            var item = new ListViewItem(name);
            item.SubItems.Add(description);
            item.SubItems.Add(risk);

            // Color code risk level
            item.UseItemStyleForSubItems = false;
            var riskItem = item.SubItems[2];
            switch (risk)
            {
                case "Low":
                    riskItem.ForeColor = ThemeManager.StatusGreen;
                    break;
                case "Medium":
                    riskItem.ForeColor = ThemeManager.StatusYellow;
                    break;
                case "High":
                    riskItem.ForeColor = ThemeManager.StatusRed;
                    break;
            }

            _lvBypassTests.Items.Add(item);
        }

        private string GetTestTooltip(string testName)
        {
            return testName switch
            {
                "NtReadVirtualMemory" => "Tests if direct NT API calls for memory reading are detected",
                "NtWriteVirtualMemory" => "Tests if direct NT API calls for memory writing are detected",
                "Unsigned Driver Load" => "Attempts to load the test driver to check if unsigned drivers are blocked",
                "MmCopyVirtualMemory" => "Tests kernel-mode memory access via MmCopyVirtualMemory",
                "Hypervisor Detection" => "Checks for hypervisor/virtualization presence using CPUID",
                "Hardware Breakpoints" => "Tests manipulation of debug registers (DR0-DR7)",
                "Manual PE Mapping" => "Tests stealth DLL injection without LoadLibrary",
                "PEB Unlinking" => "Tests hiding a process from the process list",
                _ => "No description available"
            };
        }

        #endregion

        #region Tab 4: Detection Log

        private TabPage CreateDetectionLogTab()
        {
            var tab = new TabPage("Detection Log");
            tab.Padding = new Padding(12);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9.5F),
                BackColor = ThemeManager.IsDarkMode ? ThemeManager.DarkTheme.Background : Color.White,
                ForeColor = ThemeManager.Foreground,
                BorderStyle = BorderStyle.FixedSingle,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };
            mainLayout.Controls.Add(_rtbLog, 0, 0);

            // Bottom panel
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 5, 0, 0)
            };

            _btnClearLog = CreateStyledButton("Clear Log", ButtonStyle.Secondary);
            _btnClearLog.Size = new Size(100, 32);
            _btnClearLog.Click += (s, e) => { _rtbLog.Clear(); _logEntryCount = 0; UpdateLogCount(); };
            _toolTip.SetToolTip(_btnClearLog, "Clear all log entries");

            _btnExportLog = CreateStyledButton("Export Log", ButtonStyle.Secondary);
            _btnExportLog.Size = new Size(100, 32);
            _btnExportLog.Margin = new Padding(10, 0, 0, 0);
            _btnExportLog.Click += BtnExportLog_Click;
            _toolTip.SetToolTip(_btnExportLog, "Save log to a text file");

            _chkAutoScroll = new CheckBox
            {
                Text = "Auto-scroll",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(20, 8, 0, 0)
            };
            _toolTip.SetToolTip(_chkAutoScroll, "Automatically scroll to the latest log entry");

            _lblLogCount = new Label
            {
                Text = "0 entries",
                AutoSize = true,
                Margin = new Padding(20, 8, 0, 0),
                ForeColor = Color.Gray
            };

            bottomPanel.Controls.AddRange(new Control[] { _btnClearLog, _btnExportLog, _chkAutoScroll, _lblLogCount });
            mainLayout.Controls.Add(bottomPanel, 0, 1);

            tab.Controls.Add(mainLayout);

            // Add initial log entry
            LogMessage("Anticheat Bypass Research Lab initialized", LogLevel.Info);

            return tab;
        }

        #endregion

        #region Tab 5: Patch Notes

        private TabPage CreatePatchNotesTab()
        {
            var tab = new TabPage("Patch Notes");
            tab.Padding = new Padding(12);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            var notesGroup = new GroupBox
            {
                Text = "Vulnerability Documentation",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            _txtPatchNotes = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 10F),
                AcceptsTab = true,
                AcceptsReturn = true,
                WordWrap = true
            };
            _txtPatchNotes.Text = "# Vulnerability Documentation\n\nDocument discovered vulnerabilities and applied patches here...\n\n## Findings\n\n- \n\n## Patches Applied\n\n- \n";
            _toolTip.SetToolTip(_txtPatchNotes, "Document your security findings and patches");

            notesGroup.Controls.Add(_txtPatchNotes);
            mainLayout.Controls.Add(notesGroup, 0, 0);

            // Bottom panel
            var bottomPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 5, 0, 0)
            };

            _btnSaveNotes = CreateStyledButton("Save Notes", ButtonStyle.Primary);
            _btnSaveNotes.Size = new Size(110, 32);
            _btnSaveNotes.Click += BtnSaveNotes_Click;
            _toolTip.SetToolTip(_btnSaveNotes, "Save notes to local storage");

            _btnLoadNotes = CreateStyledButton("Load Notes", ButtonStyle.Secondary);
            _btnLoadNotes.Size = new Size(110, 32);
            _btnLoadNotes.Margin = new Padding(10, 0, 0, 0);
            _btnLoadNotes.Click += BtnLoadNotes_Click;
            _toolTip.SetToolTip(_btnLoadNotes, "Load previously saved notes");

            _btnExportMarkdown = CreateStyledButton("Export to MD", ButtonStyle.Secondary);
            _btnExportMarkdown.Size = new Size(110, 32);
            _btnExportMarkdown.Margin = new Padding(10, 0, 0, 0);
            _btnExportMarkdown.Click += BtnExportMarkdown_Click;
            _toolTip.SetToolTip(_btnExportMarkdown, "Export notes as a markdown file");

            _btnClearNotes = CreateStyledButton("Clear Notes", ButtonStyle.Danger);
            _btnClearNotes.Size = new Size(110, 32);
            _btnClearNotes.Margin = new Padding(10, 0, 0, 0);
            _btnClearNotes.Click += BtnClearNotes_Click;
            _toolTip.SetToolTip(_btnClearNotes, "Clear all notes (cannot be undone)");

            _lblLastSaved = new Label
            {
                Text = "Not saved",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(20, 8, 0, 0)
            };

            bottomPanel.Controls.AddRange(new Control[] { _btnSaveNotes, _btnLoadNotes, _btnExportMarkdown, _btnClearNotes, _lblLastSaved });
            mainLayout.Controls.Add(bottomPanel, 0, 1);

            tab.Controls.Add(mainLayout);
            return tab;
        }

        #endregion

        #region Event Handlers

        private async void BtnScanAntiCheat_Click(object? sender, EventArgs e)
        {
            _btnScanAntiCheat.Enabled = false;
            _scanProgress.Visible = true;
            _lvAntiCheats.Items.Clear();

            LogMessage("Starting anti-cheat scan...", LogLevel.Info);

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var detectionResult = AntiCheatDetector.DetectSystemWide();

                    this.Invoke(() =>
                    {
                        foreach (var ac in detectionResult.DetectedAntiCheats)
                        {
                            var item = new ListViewItem(ac.Name);
                            item.SubItems.Add(ac.Type);
                            item.SubItems.Add(ac.Severity);
                            item.SubItems.Add(string.Join(", ", ac.DetectedComponents.Take(3)));

                            // Color code by severity
                            item.UseItemStyleForSubItems = false;
                            var severityItem = item.SubItems[2];
                            severityItem.ForeColor = ac.Severity switch
                            {
                                "Low" => ThemeManager.StatusGreen,
                                "Medium" => ThemeManager.StatusYellow,
                                "High" or "Extreme" => ThemeManager.StatusRed,
                                _ => ThemeManager.Foreground
                            };

                            _lvAntiCheats.Items.Add(item);
                            LogMessage($"Detected: {ac.Name} ({ac.Type}) - Severity: {ac.Severity}",
                                ac.Severity == "High" || ac.Severity == "Extreme" ? LogLevel.Warning : LogLevel.Info);
                        }

                        if (detectionResult.DetectedAntiCheats.Count == 0)
                        {
                            LogMessage("No known anti-cheat systems detected", LogLevel.Success);
                        }
                        else
                        {
                            LogMessage($"Scan complete: {detectionResult.DetectedAntiCheats.Count} anti-cheat system(s) detected", LogLevel.Info);
                        }
                    });
                }
                catch (Exception ex)
                {
                    this.Invoke(() =>
                    {
                        LogMessage($"Scan error: {ex.Message}", LogLevel.Error);
                        MessageBox.Show($"Scan failed:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });

            _scanProgress.Visible = false;
            _btnScanAntiCheat.Enabled = true;
        }

        private void BtnBrowseDriver_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Driver files (*.sys)|*.sys|All files (*.*)|*.*",
                Title = "Select Test Kernel Driver"
            };

            if (ofd.ShowDialog() == DialogResult.OK)
            {
                _txtDriverPath.Text = ofd.FileName;
                CheckDriverSigned(ofd.FileName);
            }
        }

        private void CheckDriverSigned(string path)
        {
            // Stub - will be implemented in KernelDriverManager
            _lblDriverSigned.Text = "Unknown";
            _lblDriverSigned.ForeColor = ThemeManager.StatusYellow;
        }

        private KernelDriverManager? _driverManager;

        private void BtnLoadDriver_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtDriverPath.Text))
            {
                MessageBox.Show("Please select a driver file first.", "No Driver Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(_txtDriverPath.Text))
            {
                MessageBox.Show("Driver file not found.", "File Not Found",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                LogMessage($"Loading driver: {_txtDriverPath.Text}", LogLevel.Info);

                _driverManager = new KernelDriverManager
                {
                    DriverPath = _txtDriverPath.Text,
                    ServiceName = _txtServiceName.Text
                };

                if (_driverManager.LoadDriver())
                {
                    _isDriverLoaded = true;
                    _lblDriverLoaded.Text = "Yes";
                    _lblDriverLoaded.ForeColor = ThemeManager.StatusGreen;
                    _driverStatusCard?.SetValue("Loaded", StatusLevel.Good);
                    _btnUnloadDriver.Enabled = true;
                    _btnReloadDriver.Enabled = true;
                    LogMessage("Driver loaded successfully!", LogLevel.Success);

                    // Try to connect to driver for kernel mode operations
                    ConnectToDriverController();
                }
                else
                {
                    LogMessage($"Failed to load driver: {_driverManager.LastError}", LogLevel.Error);
                    MessageBox.Show($"Failed to load driver:\n{_driverManager.LastError}",
                        "Driver Load Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Driver load error: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Error loading driver:\n{ex.Message}",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnUnloadDriver_Click(object? sender, EventArgs e)
        {
            if (_driverManager == null || !_isDriverLoaded)
            {
                LogMessage("No driver is currently loaded", LogLevel.Warning);
                return;
            }

            try
            {
                // Disconnect from driver controller first
                DisconnectFromDriverController();

                LogMessage("Unloading driver...", LogLevel.Info);

                if (_driverManager.UnloadDriver())
                {
                    _isDriverLoaded = false;
                    _lblDriverLoaded.Text = "No";
                    _lblDriverLoaded.ForeColor = ThemeManager.StatusRed;
                    _driverStatusCard?.SetValue("Not Loaded", StatusLevel.Warning);
                    _btnUnloadDriver.Enabled = false;
                    _btnReloadDriver.Enabled = false;
                    LogMessage("Driver unloaded successfully!", LogLevel.Success);
                }
                else
                {
                    LogMessage($"Failed to unload driver: {_driverManager.LastError}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Driver unload error: {ex.Message}", LogLevel.Error);
            }
        }

        private void BtnReloadDriver_Click(object? sender, EventArgs e)
        {
            LogMessage("Reloading driver...", LogLevel.Info);
            BtnUnloadDriver_Click(sender, e);
            System.Threading.Thread.Sleep(500);
            BtnLoadDriver_Click(sender, e);
        }

        private void BtnEnableTestSigning_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Enabling Test Signing Mode will allow unsigned drivers to load.\n\n" +
                "This requires administrator privileges and a system REBOOT.\n\n" +
                "Do you want to continue?",
                "Enable Test Signing",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                LogMessage("Enabling Test Signing Mode...", LogLevel.Warning);

                if (KernelDriverManager.EnableTestSigning(out string message))
                {
                    LogMessage(message, LogLevel.Success);
                    MessageBox.Show(message + "\n\nPlease reboot your system for changes to take effect.",
                        "Test Signing Enabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusCards();
                }
                else
                {
                    LogMessage($"Failed to enable test signing: {message}", LogLevel.Error);
                    MessageBox.Show($"Failed to enable test signing:\n{message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnDisableTestSigning_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "Disabling Test Signing Mode will prevent unsigned drivers from loading.\n\n" +
                "This requires administrator privileges and a system REBOOT.\n\n" +
                "Do you want to continue?",
                "Disable Test Signing",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                LogMessage("Disabling Test Signing Mode...", LogLevel.Info);

                if (KernelDriverManager.DisableTestSigning(out string message))
                {
                    LogMessage(message, LogLevel.Success);
                    MessageBox.Show(message + "\n\nPlease reboot your system for changes to take effect.",
                        "Test Signing Disabled", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    UpdateStatusCards();
                }
                else
                {
                    LogMessage($"Failed to disable test signing: {message}", LogLevel.Error);
                    MessageBox.Show($"Failed to disable test signing:\n{message}",
                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnKDMapperLoad_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_txtDriverPath.Text) || !File.Exists(_txtDriverPath.Text))
            {
                MessageBox.Show("Please select a driver file first.", "No Driver Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var kdmapper = new KDMapperLoader();

            // Try to auto-detect KDMapper
            if (!kdmapper.AutoDetectKDMapper())
            {
                // Ask user to locate it
                var result = MessageBox.Show(
                    "KDMapper.exe not found.\n\n" +
                    "Would you like to download it now?\n\n" +
                    "GitHub: https://github.com/TheCruZ/kdmapper",
                    "KDMapper Not Found",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://github.com/TheCruZ/kdmapper/releases",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }

                // Let user browse for kdmapper.exe
                using var ofd = new OpenFileDialog
                {
                    Filter = "KDMapper Executable (kdmapper.exe)|kdmapper.exe|All files (*.*)|*.*",
                    Title = "Locate KDMapper.exe"
                };

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    kdmapper.KDMapperExePath = ofd.FileName;
                }
                else
                {
                    return; // User cancelled
                }
            }

            // Confirm with user
            var confirmResult = MessageBox.Show(
                $"Load driver using KDMapper?\n\n" +
                $"Driver: {Path.GetFileName(_txtDriverPath.Text)}\n" +
                $"KDMapper: {kdmapper.KDMapperExePath}\n\n" +
                $"This will:\n" +
                $"1. Load vulnerable Intel driver temporarily\n" +
                $"2. Exploit it to bypass driver signature checks\n" +
                $"3. Map your driver into kernel memory\n" +
                $"4. Clean up vulnerable driver\n\n" +
                $"No test mode or reboot required!",
                "Confirm KDMapper Load",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (confirmResult != DialogResult.Yes)
                return;

            LogMessage("Loading driver with KDMapper...", LogLevel.Info);

            try
            {
                if (kdmapper.LoadDriverWithKDMapper(_txtDriverPath.Text, out string output))
                {
                    _isDriverLoaded = true;
                    _lblDriverLoaded.Text = "Yes (KDMapper)";
                    _lblDriverLoaded.ForeColor = ThemeManager.StatusGreen;
                    _driverStatusCard?.SetValue("Loaded (Mapped)", StatusLevel.Good);

                    LogMessage("Driver loaded successfully with KDMapper!", LogLevel.Success);
                    LogMessage($"KDMapper output:\n{output}", LogLevel.Info);

                    MessageBox.Show(
                        "Driver loaded successfully using KDMapper!\n\n" +
                        "The driver is now running in kernel mode without requiring test mode.\n\n" +
                        "Note: Driver will unload on reboot.",
                        "Success",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);

                    // Try to connect to driver
                    ConnectToDriverController();
                }
                else
                {
                    LogMessage($"KDMapper failed: {kdmapper.LastError}", LogLevel.Error);
                    MessageBox.Show(
                        $"Failed to load driver with KDMapper:\n\n{kdmapper.LastError}",
                        "KDMapper Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"KDMapper exception: {ex.Message}", LogLevel.Error);
                MessageBox.Show(
                    $"Error using KDMapper:\n{ex.Message}",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ShowKDMapperInfo()
        {
            var infoForm = new Form
            {
                Text = "About KDMapper",
                Size = new Size(700, 600),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Segoe UI", 9F),
                BackColor = ThemeManager.Background,
                ForeColor = ThemeManager.Foreground
            };
            ThemeManager.ApplyTheme(infoForm);

            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9.5F),
                BackColor = ThemeManager.Background,
                ForeColor = ThemeManager.Foreground,
                BorderStyle = BorderStyle.None,
                Text = KDMapperLoader.GetKDMapperInfo()
            };

            var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = ThemeManager.Background };

            var btnDownload = CreateStyledButton("Download KDMapper", ButtonStyle.Success);
            btnDownload.Size = new Size(150, 35);
            btnDownload.Location = new Point(10, 8);
            btnDownload.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/TheCruZ/kdmapper/releases",
                        UseShellExecute = true
                    });
                }
                catch { }
            };

            var btnClose = CreateStyledButton("Close", ButtonStyle.Secondary);
            btnClose.Size = new Size(100, 35);
            btnClose.Location = new Point(170, 8);
            btnClose.Click += (s, e) => infoForm.Close();

            buttonPanel.Controls.AddRange(new Control[] { btnDownload, btnClose });

            infoForm.Controls.AddRange(new Control[] { rtb, buttonPanel });
            infoForm.ShowDialog(this);
        }

        private void BtnRunSelected_Click(object? sender, EventArgs e)
        {
            var selectedTests = _lvBypassTests.CheckedItems.Cast<ListViewItem>().Select(i => i.Text).ToList();

            if (selectedTests.Count == 0)
            {
                MessageBox.Show("Please select at least one test to run.", "No Tests Selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RunTests(selectedTests);
        }

        private void BtnRunAll_Click(object? sender, EventArgs e)
        {
            var allTests = _lvBypassTests.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();
            RunTests(allTests);
        }

        private async void RunTests(List<string> testNames)
        {
            _lvTestResults.Items.Clear();
            LogMessage($"Running {testNames.Count} bypass test(s)...", LogLevel.Info);

            // Disable buttons during test
            _btnRunSelected.Enabled = false;
            _btnRunAll.Enabled = false;

            try
            {
                var runner = new BypassTestRunner();
                runner.TargetProcess = _process;
                runner.DriverManager = _driverManager;

                // Wire up logging
                runner.OnLogMessage += (msg, color) =>
                {
                    this.Invoke(() =>
                    {
                        var level = color == Color.Red ? LogLevel.Error :
                                    color == Color.Yellow ? LogLevel.Warning :
                                    color == Color.LightGreen ? LogLevel.Success : LogLevel.Info;
                        LogMessage(msg, level);
                    });
                };

                // Map test names to IDs
                var testIds = new List<string>();
                foreach (var testName in testNames)
                {
                    var technique = runner.AvailableTechniques.FirstOrDefault(t => t.Name == testName);
                    if (technique != null)
                        testIds.Add(technique.Id);
                }

                // Run tests
                var results = await runner.RunTestsAsync(testIds);

                // Display results
                foreach (var result in results)
                {
                    var item = new ListViewItem(result.StatusIcon);
                    item.SubItems.Add(result.TestName);
                    item.SubItems.Add(result.StatusDisplay);
                    item.SubItems.Add(result.DurationDisplay);
                    item.SubItems.Add(result.Details);

                    // Color code based on status
                    item.ForeColor = result.Status switch
                    {
                        DetectionStatus.Undetected => ThemeManager.StatusGreen,
                        DetectionStatus.PartialDetection => ThemeManager.StatusYellow,
                        DetectionStatus.Blocked => ThemeManager.StatusRed,
                        DetectionStatus.Error => Color.OrangeRed,
                        _ => ThemeManager.Foreground
                    };

                    _lvTestResults.Items.Add(item);
                }

                // Summary
                int passed = results.Count(r => r.Status == DetectionStatus.Undetected);
                int blocked = results.Count(r => r.Status == DetectionStatus.Blocked);
                int errors = results.Count(r => r.Status == DetectionStatus.Error);

                LogMessage($"Test complete: {passed} undetected, {blocked} blocked, {errors} errors",
                    blocked > 0 ? LogLevel.Success : LogLevel.Warning);

                // Save to history
                var historyEntry = new TestRunHistory
                {
                    Timestamp = DateTime.Now,
                    TestNames = testNames,
                    TotalTests = results.Count,
                    PassedTests = passed,
                    BlockedTests = blocked,
                    ErrorTests = errors
                };
                _testHistory.Add(historyEntry);

                // Keep only last 100 entries
                if (_testHistory.Count > 100)
                    _testHistory.RemoveAt(0);
            }
            catch (Exception ex)
            {
                LogMessage($"Test execution error: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                // Re-enable buttons
                _btnRunSelected.Enabled = true;
                _btnRunAll.Enabled = true;
            }
        }

        private void BtnExportResults_Click(object? sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md|Text files (*.txt)|*.txt",
                FileName = $"bypass_results_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var lines = new List<string>
                    {
                        "# Bypass Test Results",
                        $"Generated: {DateTime.Now}",
                        "",
                        "| Status | Test | Result | Duration | Details |",
                        "|--------|------|--------|----------|---------|"
                    };

                    foreach (ListViewItem item in _lvTestResults.Items)
                    {
                        lines.Add($"| {item.Text} | {item.SubItems[1].Text} | {item.SubItems[2].Text} | {item.SubItems[3].Text} | {item.SubItems[4].Text} |");
                    }

                    File.WriteAllLines(sfd.FileName, lines);
                    LogMessage($"Results exported to {sfd.FileName}", LogLevel.Success);
                    MessageBox.Show("Results exported successfully!", "Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    LogMessage($"Export failed: {ex.Message}", LogLevel.Error);
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void FilterTestsByRisk(string riskLevel)
        {
            // Deselect all first
            foreach (ListViewItem item in _lvBypassTests.Items)
                item.Checked = false;

            // Select only tests matching risk level
            foreach (ListViewItem item in _lvBypassTests.Items)
            {
                if (item.SubItems[2].Text == riskLevel)
                    item.Checked = true;
            }

            LogMessage($"Selected all {riskLevel} risk tests", LogLevel.Info);
        }

        private void CopyResultsToClipboard()
        {
            if (_lvTestResults.Items.Count == 0)
            {
                MessageBox.Show("No test results to copy.", "No Results",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Bypass Test Results ===");
                sb.AppendLine($"Generated: {DateTime.Now}");
                sb.AppendLine();

                foreach (ListViewItem item in _lvTestResults.Items)
                {
                    sb.AppendLine($"{item.Text} {item.SubItems[1].Text}");
                    sb.AppendLine($"  Result: {item.SubItems[2].Text}");
                    sb.AppendLine($"  Duration: {item.SubItems[3].Text}");
                    sb.AppendLine($"  Details: {item.SubItems[4].Text}");
                    sb.AppendLine();
                }

                Clipboard.SetText(sb.ToString());
                LogMessage("Results copied to clipboard", LogLevel.Success);
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to copy: {ex.Message}", LogLevel.Error);
            }
        }

        private void ShowTestHistory()
        {
            if (_testHistory.Count == 0)
            {
                MessageBox.Show("No test history available yet.\n\nRun some tests first!",
                    "No History", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var historyForm = new Form
            {
                Text = "Test Result History",
                Size = new Size(800, 600),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Segoe UI", 9F),
                BackColor = ThemeManager.Background,
                ForeColor = ThemeManager.Foreground
            };
            ThemeManager.ApplyTheme(historyForm);

            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                Font = new Font("Segoe UI", 9F)
            };
            lv.Columns.Add("Timestamp", 150);
            lv.Columns.Add("Tests Run", 80);
            lv.Columns.Add("Passed", 70);
            lv.Columns.Add("Blocked", 70);
            lv.Columns.Add("Errors", 70);
            lv.Columns.Add("Success Rate", 100);
            lv.Columns.Add("Details", 200);

            foreach (var history in _testHistory.OrderByDescending(h => h.Timestamp))
            {
                var item = new ListViewItem(history.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(history.TotalTests.ToString());
                item.SubItems.Add(history.PassedTests.ToString());
                item.SubItems.Add(history.BlockedTests.ToString());
                item.SubItems.Add(history.ErrorTests.ToString());
                item.SubItems.Add($"{history.SuccessRate:F1}%");
                item.SubItems.Add(string.Join(", ", history.TestNames.Take(3)) + (history.TestNames.Count > 3 ? "..." : ""));
                item.Tag = history;
                lv.Items.Add(item);
            }

            lv.BackColor = ThemeManager.Background;
            lv.ForeColor = ThemeManager.Foreground;

            var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 50 };
            var btnClose = CreateStyledButton("Close", ButtonStyle.Secondary);
            btnClose.Size = new Size(100, 35);
            btnClose.Location = new Point(10, 8);
            btnClose.Click += (s, e) => historyForm.Close();

            var btnClear = CreateStyledButton("Clear History", ButtonStyle.Danger);
            btnClear.Size = new Size(120, 35);
            btnClear.Location = new Point(120, 8);
            btnClear.Click += (s, e) =>
            {
                _testHistory.Clear();
                historyForm.Close();
                LogMessage("Test history cleared", LogLevel.Info);
            };

            buttonPanel.BackColor = ThemeManager.Background;
            buttonPanel.Controls.AddRange(new Control[] { btnClose, btnClear });

            historyForm.Controls.AddRange(new Control[] { lv, buttonPanel });
            historyForm.ShowDialog(this);
        }

        private void LvAntiCheats_DoubleClick(object? sender, EventArgs e)
        {
            if (_lvAntiCheats.SelectedItems.Count == 0)
                return;

            var selectedItem = _lvAntiCheats.SelectedItems[0];
            var acName = selectedItem.Text;
            var acType = selectedItem.SubItems[1].Text;
            var severity = selectedItem.SubItems[2].Text;

            // Get detailed info from AntiCheatDetector
            var detectionResult = AntiCheatDetector.DetectSystemWide();
            var acInfo = detectionResult.DetectedAntiCheats.FirstOrDefault(ac => ac.Name == acName);

            if (acInfo == null)
                return;

            var infoForm = new Form
            {
                Text = $"Anti-Cheat Information: {acName}",
                Size = new Size(600, 500),
                StartPosition = FormStartPosition.CenterParent,
                Font = new Font("Segoe UI", 9F),
                BackColor = ThemeManager.Background,
                ForeColor = ThemeManager.Foreground
            };
            ThemeManager.ApplyTheme(infoForm);

            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 9.5F),
                BackColor = ThemeManager.Background,
                ForeColor = ThemeManager.Foreground,
                BorderStyle = BorderStyle.None
            };

            rtb.Text = $@"═══════════════════════════════════════════════════
ANTI-CHEAT INFORMATION
═══════════════════════════════════════════════════

Name: {acInfo.Name}
Type: {acInfo.Type}
Severity: {acInfo.Severity}
Description: {acInfo.Description}
Version: {acInfo.Version}

DETECTED COMPONENTS:
{string.Join("\n", acInfo.DetectedComponents.Select(c => $"  • {c}"))}

═══════════════════════════════════════════════════
THREAT ANALYSIS
═══════════════════════════════════════════════════

{GetThreatAnalysis(acInfo)}

═══════════════════════════════════════════════════
RECOMMENDED ACTIONS
═══════════════════════════════════════════════════

{GetRecommendedActions(acInfo)}
";

            var btnClose = CreateStyledButton("Close", ButtonStyle.Secondary);
            btnClose.Dock = DockStyle.Bottom;
            btnClose.Height = 40;
            btnClose.Click += (s, ev) => infoForm.Close();

            infoForm.Controls.AddRange(new Control[] { rtb, btnClose });
            infoForm.ShowDialog(this);
        }

        private string GetThreatAnalysis(AntiCheatDetector.AntiCheatInfo ac)
        {
            return ac.Severity switch
            {
                "Extreme" => $@"⚠️ CRITICAL THREAT LEVEL

{ac.Name} uses advanced kernel-level protection:
• Kernel driver with PatchGuard integration
• Hypervisor-based memory protection
• Hardware-assisted virtualization detection
• Real-time process monitoring
• Code integrity enforcement

Testing bypass techniques against this AC is HIGH RISK.
Detection may result in permanent bans.",

                "High" => $@"⚠️ HIGH THREAT LEVEL

{ac.Name} employs strong anti-tamper protection:
• Kernel-mode driver components
• Process handle monitoring
• Memory integrity checks
• Driver signature validation

Proceed with caution when testing bypasses.",

                "Medium" => $@"⚙️ MODERATE THREAT LEVEL

{ac.Name} uses standard protection mechanisms:
• User-mode monitoring
• Basic signature detection
• Process enumeration

Testing bypasses should be relatively safe.",

                _ => $@"ℹ️ LOW THREAT LEVEL

{ac.Name} has basic protection:
• Minimal kernel integration
• Standard detection methods

Safe for bypass research."
            };
        }

        private string GetRecommendedActions(AntiCheatDetector.AntiCheatInfo ac)
        {
            return ac.Severity switch
            {
                "Extreme" => @"1. DO NOT test on production/main accounts
2. Use isolated testing environment (VM recommended)
3. Test driver loading only if necessary
4. Monitor system logs for AC responses
5. Have rollback plan ready",

                "High" => @"1. Test on secondary accounts only
2. Start with low-risk tests (NtRead, Hypervisor Detection)
3. Monitor for AC service responses
4. Avoid driver-based tests initially",

                "Medium" => @"1. Start with low-risk bypass tests
2. Monitor process activity
3. Test incrementally
4. Document AC behavior",

                _ => @"1. All tests should be safe
2. Monitor for unexpected behavior
3. Document findings"
            };
        }

        private void BtnMonitorAC_Click(object? sender, EventArgs e)
        {
            if (_acMonitorTimer != null && _acMonitorTimer.Enabled)
            {
                // Stop monitoring
                _acMonitorTimer.Stop();
                _acMonitorTimer.Dispose();
                _acMonitorTimer = null;
                ((Button)sender!).Text = "Monitor (Real-time)";
                LogMessage("AC monitoring stopped", LogLevel.Info);
                return;
            }

            // Start monitoring
            _acMonitorTimer = new System.Windows.Forms.Timer { Interval = 2000 }; // Every 2 seconds
            _acMonitorTimer.Tick += (s, ev) =>
            {
                try
                {
                    var result = AntiCheatDetector.DetectSystemWide();

                    // Update UI with current AC status
                    this.Invoke(() =>
                    {
                        // Check for new ACs
                        foreach (var ac in result.DetectedAntiCheats)
                        {
                            bool found = false;
                            foreach (ListViewItem existingItem in _lvAntiCheats.Items)
                            {
                                if (existingItem.Text == ac.Name)
                                {
                                    found = true;
                                    // Update status
                                    existingItem.SubItems[3].Text = "Active";
                                    existingItem.SubItems[3].ForeColor = ThemeManager.StatusGreen;
                                    break;
                                }
                            }

                            if (!found)
                            {
                                // New AC detected!
                                var item = new ListViewItem(ac.Name);
                                item.SubItems.Add(ac.Type);
                                item.SubItems.Add(ac.Severity);
                                item.SubItems.Add("Active (NEW!)");
                                item.ForeColor = ThemeManager.StatusYellow;
                                _lvAntiCheats.Items.Add(item);
                                LogMessage($"NEW AC DETECTED: {ac.Name}", LogLevel.Warning);
                            }
                        }

                        // Mark inactive ACs
                        foreach (ListViewItem item in _lvAntiCheats.Items)
                        {
                            bool stillActive = result.DetectedAntiCheats.Any(ac => ac.Name == item.Text);
                            if (!stillActive && item.SubItems[3].Text.Contains("Active"))
                            {
                                item.SubItems[3].Text = "Inactive";
                                item.SubItems[3].ForeColor = Color.Gray;
                                LogMessage($"AC stopped: {item.Text}", LogLevel.Info);
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    LogMessage($"Monitor error: {ex.Message}", LogLevel.Error);
                }
            };
            _acMonitorTimer.Start();
            ((Button)sender!).Text = "Stop Monitoring";
            LogMessage("Real-time AC monitoring started (2s interval)", LogLevel.Success);
        }

        private void BtnExportLog_Click(object? sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"detection_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    File.WriteAllText(sfd.FileName, _rtbLog.Text);
                    LogMessage($"Log exported to {sfd.FileName}", LogLevel.Success);
                    MessageBox.Show("Log exported successfully!", "Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnSaveNotes_Click(object? sender, EventArgs e)
        {
            try
            {
                var dir = Path.GetDirectoryName(_notesFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var data = new { Notes = _txtPatchNotes.Text, SavedAt = DateTime.Now };
                File.WriteAllText(_notesFilePath, JsonSerializer.Serialize(data));

                _lblLastSaved.Text = $"Saved: {DateTime.Now:HH:mm:ss}";
                _lblLastSaved.ForeColor = ThemeManager.StatusGreen;
                LogMessage("Patch notes saved", LogLevel.Success);
            }
            catch (Exception ex)
            {
                LogMessage($"Failed to save notes: {ex.Message}", LogLevel.Error);
                MessageBox.Show($"Failed to save notes:\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnLoadNotes_Click(object? sender, EventArgs e)
        {
            LoadPatchNotes();
        }

        private void LoadPatchNotes()
        {
            try
            {
                if (File.Exists(_notesFilePath))
                {
                    var json = File.ReadAllText(_notesFilePath);
                    var data = JsonSerializer.Deserialize<JsonElement>(json);

                    if (data.TryGetProperty("Notes", out var notes))
                    {
                        _txtPatchNotes.Text = notes.GetString() ?? "";
                    }
                    if (data.TryGetProperty("SavedAt", out var savedAt))
                    {
                        _lblLastSaved.Text = $"Last saved: {savedAt.GetDateTime():g}";
                    }
                }
            }
            catch
            {
                // Ignore load errors for notes
            }
        }

        private void BtnExportMarkdown_Click(object? sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog
            {
                Filter = "Markdown files (*.md)|*.md",
                FileName = $"patch_notes_{DateTime.Now:yyyyMMdd}.md"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    File.WriteAllText(sfd.FileName, _txtPatchNotes.Text);
                    LogMessage($"Notes exported to {sfd.FileName}", LogLevel.Success);
                    MessageBox.Show("Notes exported successfully!", "Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BtnClearNotes_Click(object? sender, EventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to clear all notes?\n\nThis cannot be undone.",
                "Clear Notes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                _txtPatchNotes.Text = "# Vulnerability Documentation\n\n";
                LogMessage("Patch notes cleared", LogLevel.Info);
            }
        }

        private void AntiCheatBypassForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Auto-save notes on close
            try
            {
                var dir = Path.GetDirectoryName(_notesFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var data = new { Notes = _txtPatchNotes.Text, SavedAt = DateTime.Now };
                File.WriteAllText(_notesFilePath, JsonSerializer.Serialize(data));
            }
            catch
            {
                // Ignore save errors on close
            }
        }

        #endregion

        #region Helper Methods

        private void UpdateStatusCards()
        {
            // Process status
            if (_process != null && _process.Target != null)
            {
                try
                {
                    // Check if process has exited before accessing properties
                    if (!_process.Target.HasExited)
                    {
                        _processStatusCard.SetValue($"{_process.Target.ProcessName} ({_process.Target.Id})", StatusLevel.Good);
                    }
                    else
                    {
                        _processStatusCard.SetValue("Process Exited", StatusLevel.Warning);
                    }
                }
                catch
                {
                    _processStatusCard.SetValue("Process Unavailable", StatusLevel.Warning);
                }
            }
            else
            {
                _processStatusCard.SetValue("No Process", StatusLevel.Neutral);
            }

            // Admin rights
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            _adminRightsCard.SetValue(isAdmin ? "Yes" : "No", isAdmin ? StatusLevel.Good : StatusLevel.Bad);

            // Test signing - stub for now
            _testSigningCard.SetValue("Unknown", StatusLevel.Neutral);
            _lblTestSigningStatus.Text = "Status: Unknown";

            // Driver status
            _driverStatusCard.SetValue(_isDriverLoaded ? "Loaded" : "Not Loaded",
                _isDriverLoaded ? StatusLevel.Good : StatusLevel.Warning);
        }

        private enum LogLevel { Info, Success, Warning, Error, Blocked }

        private void LogMessage(string message, LogLevel level)
        {
            if (_rtbLog.InvokeRequired)
            {
                _rtbLog.Invoke(() => LogMessage(message, level));
                return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var prefix = level switch
            {
                LogLevel.Info => "[INFO]",
                LogLevel.Success => "[SUCCESS]",
                LogLevel.Warning => "[WARNING]",
                LogLevel.Error => "[ERROR]",
                LogLevel.Blocked => "[BLOCKED]",
                _ => "[INFO]"
            };

            var color = level switch
            {
                LogLevel.Info => ThemeManager.ForegroundDim,
                LogLevel.Success => Color.FromArgb(0, 200, 0),
                LogLevel.Warning => Color.FromArgb(255, 200, 0),
                LogLevel.Error => Color.FromArgb(255, 80, 80),
                LogLevel.Blocked => Color.FromArgb(255, 50, 50),
                _ => ThemeManager.Foreground
            };

            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionLength = 0;

            // Timestamp in dim color
            _rtbLog.SelectionColor = ThemeManager.ForegroundDim;
            _rtbLog.AppendText($"[{timestamp}] ");

            // Prefix in level color
            _rtbLog.SelectionColor = color;
            _rtbLog.AppendText($"{prefix} ");

            // Message in foreground color
            _rtbLog.SelectionColor = ThemeManager.Foreground;
            _rtbLog.AppendText($"{message}\n");

            _logEntryCount++;
            UpdateLogCount();

            if (_chkAutoScroll?.Checked == true)
            {
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();
            }
        }

        private void UpdateLogCount()
        {
            if (_lblLogCount != null)
                _lblLogCount.Text = $"{_logEntryCount} entries";
        }

        #region Kernel Mode Driver Controller

        private void ConnectToDriverController()
        {
            try
            {
                _driverController = new CrxDriverController();
                if (_driverController.Connect())
                {
                    // Get version to verify communication
                    if (_driverController.GetVersion(out uint major, out uint minor, out uint build))
                    {
                        _lblKernelModeStatus.Text = $"Connected (v{major}.{minor}.{build})";
                        _lblKernelModeStatus.ForeColor = ThemeManager.StatusGreen;
                        _chkUseKernelMode.Enabled = true;
                        DriverController = _driverController;
                        LogMessage($"Connected to driver controller (v{major}.{minor}.{build})", LogLevel.Success);

                        // Notify listeners that driver is now loaded
                        DriverStatusChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        _lblKernelModeStatus.Text = "Connected (version unknown)";
                        _lblKernelModeStatus.ForeColor = ThemeManager.StatusYellow;
                        _chkUseKernelMode.Enabled = true;
                        DriverController = _driverController;
                        LogMessage("Connected to driver controller (could not get version)", LogLevel.Warning);

                        // Notify listeners that driver is now loaded
                        DriverStatusChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                else
                {
                    _lblKernelModeStatus.Text = "Connection Failed";
                    _lblKernelModeStatus.ForeColor = ThemeManager.StatusRed;
                    _chkUseKernelMode.Enabled = false;
                    _driverController.Dispose();
                    _driverController = null;
                    LogMessage("Failed to connect to driver controller - device may not be ready", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                _lblKernelModeStatus.Text = "Error";
                _lblKernelModeStatus.ForeColor = ThemeManager.StatusRed;
                _chkUseKernelMode.Enabled = false;
                LogMessage($"Driver controller connection error: {ex.Message}", LogLevel.Error);
            }
        }

        private void DisconnectFromDriverController()
        {
            // Disable kernel mode first
            if (_chkUseKernelMode.Checked)
            {
                _chkUseKernelMode.Checked = false;
            }

            _chkUseKernelMode.Enabled = false;
            UseKernelMode = false;
            DriverController = null;

            if (_driverController != null)
            {
                _driverController.Disconnect();
                _driverController.Dispose();
                _driverController = null;
                LogMessage("Disconnected from driver controller", LogLevel.Info);
            }

            _lblKernelModeStatus.Text = "Not Connected";
            _lblKernelModeStatus.ForeColor = Color.Gray;

            // Notify listeners that driver is now unloaded
            DriverStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ChkUseKernelMode_CheckedChanged(object? sender, EventArgs e)
        {
            UseKernelMode = _chkUseKernelMode.Checked;

            if (_chkUseKernelMode.Checked)
            {
                LogMessage("Kernel mode ENABLED - memory operations will use driver", LogLevel.Warning);
                _chkUseKernelMode.ForeColor = ThemeManager.StatusGreen;
            }
            else
            {
                LogMessage("Kernel mode DISABLED - using standard usermode APIs", LogLevel.Info);
                _chkUseKernelMode.ForeColor = ThemeManager.Foreground;
            }
        }

        #endregion

        private void ApplyTheme()
        {
            ThemeManager.ApplyTheme(this);

            // Additional theme fixes for custom controls
            _rtbLog.BackColor = ThemeManager.IsDarkMode ? ThemeManager.DarkTheme.Background : Color.White;
            _rtbLog.ForeColor = ThemeManager.Foreground;

            // Apply theme to ListViews (disable harsh grid lines, apply colors)
            foreach (var lv in new[] { _lvAntiCheats, _lvBypassTests, _lvTestResults })
            {
                if (lv != null)
                {
                    lv.BackColor = ThemeManager.BackgroundAlt;
                    lv.ForeColor = ThemeManager.Foreground;
                }
            }
        }

        private enum ButtonStyle { Primary, Secondary, Success, Danger, Warning }

        private Button CreateStyledButton(string text, ButtonStyle style)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F),
                Cursor = Cursors.Hand
            };

            Color backColor, foreColor, borderColor;

            switch (style)
            {
                case ButtonStyle.Primary:
                    backColor = Color.FromArgb(0, 122, 204);
                    foreColor = Color.White;
                    borderColor = Color.FromArgb(0, 100, 180);
                    break;
                case ButtonStyle.Success:
                    backColor = Color.FromArgb(40, 167, 69);
                    foreColor = Color.White;
                    borderColor = Color.FromArgb(30, 140, 55);
                    break;
                case ButtonStyle.Danger:
                    backColor = Color.FromArgb(220, 53, 69);
                    foreColor = Color.White;
                    borderColor = Color.FromArgb(180, 40, 55);
                    break;
                case ButtonStyle.Warning:
                    backColor = Color.FromArgb(255, 193, 7);
                    foreColor = Color.Black;
                    borderColor = Color.FromArgb(220, 165, 5);
                    break;
                default: // Secondary
                    backColor = ThemeManager.IsDarkMode ? ThemeManager.DarkTheme.BackgroundAlt : SystemColors.Control;
                    foreColor = ThemeManager.Foreground;
                    borderColor = ThemeManager.Border;
                    break;
            }

            btn.BackColor = backColor;
            btn.ForeColor = foreColor;
            btn.FlatAppearance.BorderColor = borderColor;
            btn.FlatAppearance.BorderSize = 1;

            // Hover effects
            btn.MouseEnter += (s, e) => btn.BackColor = ControlPaint.Light(backColor, 0.1f);
            btn.MouseLeave += (s, e) => btn.BackColor = backColor;

            return btn;
        }

        #endregion
    }

    #region Status Card Control

    public enum StatusLevel { Good, Warning, Bad, Neutral }

    /// <summary>
    /// Modern status card control with rounded corners
    /// </summary>
    public class StatusCard : Panel
    {
        private readonly Label _lblTitle;
        private readonly Label _lblValue;
        private readonly Label _lblIcon;
        private StatusLevel _status = StatusLevel.Neutral;

        public StatusCard(string title, string value, StatusLevel status)
        {
            this.Size = new Size(180, 70);
            this.Margin = new Padding(0, 0, 15, 0);
            this.BackColor = ThemeManager.BackgroundPanel;
            this.Padding = new Padding(10);

            _lblIcon = new Label
            {
                Text = GetStatusIcon(status),
                Font = new Font("Segoe UI", 14F),
                Location = new Point(10, 10),
                AutoSize = true
            };

            _lblTitle = new Label
            {
                Text = title,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = ThemeManager.ForegroundDim,
                Location = new Point(40, 8),
                AutoSize = true
            };

            _lblValue = new Label
            {
                Text = value,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                Location = new Point(40, 28),
                AutoSize = true
            };

            this.Controls.AddRange(new Control[] { _lblIcon, _lblTitle, _lblValue });
            SetStatus(status);

            // Rounded corners
            this.Paint += StatusCard_Paint;
        }

        public void SetValue(string value, StatusLevel status)
        {
            _lblValue.Text = value;
            SetStatus(status);
        }

        private void SetStatus(StatusLevel status)
        {
            _status = status;
            _lblIcon.Text = GetStatusIcon(status);
            _lblValue.ForeColor = GetStatusColor(status);
        }

        private string GetStatusIcon(StatusLevel status) => status switch
        {
            StatusLevel.Good => "\u2713",    // Checkmark
            StatusLevel.Warning => "\u26A0", // Warning
            StatusLevel.Bad => "\u2717",     // X
            _ => "\u2022"                    // Bullet
        };

        private Color GetStatusColor(StatusLevel status) => status switch
        {
            StatusLevel.Good => ThemeManager.StatusGreen,
            StatusLevel.Warning => ThemeManager.StatusYellow,
            StatusLevel.Bad => ThemeManager.StatusRed,
            _ => ThemeManager.Foreground
        };

        private void StatusCard_Paint(object? sender, PaintEventArgs e)
        {
            // Draw rounded rectangle border
            using var pen = new Pen(ThemeManager.Border, 1);
            using var path = GetRoundedRectPath(new Rectangle(0, 0, Width - 1, Height - 1), 8);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawPath(pen, path);
        }

        private GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    #endregion

    /// <summary>
    /// Represents a test run history entry
    /// </summary>
    public class TestRunHistory
    {
        public DateTime Timestamp { get; set; }
        public List<string> TestNames { get; set; } = new List<string>();
        public int TotalTests { get; set; }
        public int PassedTests { get; set; }
        public int BlockedTests { get; set; }
        public int ErrorTests { get; set; }
        public double SuccessRate => TotalTests > 0 ? (PassedTests * 100.0 / TotalTests) : 0;
    }
}
