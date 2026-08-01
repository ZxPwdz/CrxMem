using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using CrxMem.Core;

namespace CrxMem.MemoryView
{
    public partial class MemoryViewForm : Form
    {
        private ProcessAccess? _process;
        private HexViewControl? _hexView;
        private DisassemblerViewControl? _disassemblerView;
        private RegistersViewControl? _registersView;
        private StackViewControl? _stackView;
        private IntPtr _currentAddress;
        private bool _is64Bit;
        private System.Windows.Forms.Timer? _registerUpdateTimer;
        private System.Windows.Forms.Timer? _driverStatusTimer;
        private string? _driverVersionText;
        private uint _activeThreadId;

        public MemoryViewForm(ProcessAccess process, IntPtr startAddress = default)
        {
            InitializeComponent();
            ConfigureFeatureAvailability();
            _process = process;
            _is64Bit = process?.Is64Bit ?? false;

            // If no address provided, use the main module's base address (like Cheat Engine)
            if (startAddress == IntPtr.Zero && process != null && process.Target != null)
            {
                try
                {
                    // Try to get the main module directly - this is the EXE's entry point
                    var mainModule = process.Target.MainModule;
                    if (mainModule != null)
                    {
                        _currentAddress = mainModule.BaseAddress;
                    }
                    else
                    {
                        // Fallback to first module in list
                        var modules = process.GetModules();
                        if (modules.Count > 0)
                        {
                            _currentAddress = modules[0].BaseAddress;
                        }
                        else
                        {
                            _currentAddress = new IntPtr(0x00400000);
                        }
                    }
                }
                catch
                {
                    // If we can't access MainModule, try the modules list
                    var modules = process.GetModules();
                    if (modules.Count > 0)
                    {
                        _currentAddress = modules[0].BaseAddress;
                    }
                    else
                    {
                        _currentAddress = new IntPtr(0x00400000);
                    }
                }
            }
            else
            {
                _currentAddress = startAddress == IntPtr.Zero ? new IntPtr(0x00400000) : startAddress;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+G: Go to Address (works from anywhere in the form)
            if (keyData == (Keys.Control | Keys.G))
            {
                SearchGoto_Click(null, EventArgs.Empty);
                return true;
            }

            // Ctrl+F: Find an AOB pattern (works from anywhere in the form)
            if (keyData == (Keys.Control | Keys.F))
            {
                SearchFindPattern_Click(null, EventArgs.Empty);
                return true;
            }

            // Ctrl+Space: Sync views
            if (keyData == (Keys.Control | Keys.Space))
            {
                if (_hexView != null && _hexView.Focused && _hexView.CursorAddress != IntPtr.Zero)
                {
                    _disassemblerView?.SetAddress(_hexView.CursorAddress);
                }
                else if (_disassemblerView != null && _disassemblerView.Focused)
                {
                    var selectedAddr = _disassemblerView.SelectedAddress;
                    if (selectedAddr.HasValue && selectedAddr.Value != IntPtr.Zero)
                    {
                        _hexView?.SetAddress(selectedAddr.Value);
                    }
                }
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ConfigureFeatureAvailability()
        {
            const string plannedFeature = "Planned feature - not available in this release";

            fileOpenMenu.Enabled = false;
            fileOpenMenu.Text = "&Open Static File... (planned)";
            fileOpenMenu.ToolTipText = plannedFeature;
            fileSaveMenu.Enabled = false;
            fileSaveMenu.Text = "&Save Analysis Database... (planned)";
            fileSaveMenu.ToolTipText = plannedFeature;

            debugMenu.Enabled = false;
            debugMenu.Text = "&Debug (planned)";
            debugMenu.ToolTipText = "Debugger attachment and stepping are not implemented yet";

            // Keep the toolbar focused on actions that currently work.
            btnOpen.Visible = false;
            btnSave.Visible = false;
            toolSeparator1.Visible = false;
            btnRun.Visible = false;
            btnPause.Visible = false;
            btnStop.Visible = false;
            toolSeparator2.Visible = false;
            btnStepInto.Visible = false;
            btnStepOver.Visible = false;
            btnStepOut.Visible = false;
            toolSeparator3.Visible = false;
            btnBreakpoint.Visible = false;
            toolSeparator4.Visible = false;

            searchFindMenu.Text = "&Find Pattern (AOB)...";
            searchFindMenu.ToolTipText = "Scan readable process memory for a byte pattern";
            searchFindPatternMenu.Visible = false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_registerUpdateTimer != null)
            {
                _registerUpdateTimer.Stop();
                _registerUpdateTimer.Tick -= RegisterUpdateTimer_Tick;
                _registerUpdateTimer.Dispose();
                _registerUpdateTimer = null;
            }

            if (_driverStatusTimer != null)
            {
                _driverStatusTimer.Stop();
                _driverStatusTimer.Dispose();
                _driverStatusTimer = null;
            }

            base.OnFormClosed(e);
        }

        private void MemoryViewForm_Load(object? sender, EventArgs e)
        {
            // Apply theme to the form
            ApplyTheme();

            // Remove placeholder label
            if (panelDisassembler.Controls.Contains(lblDisassemblerPlaceholder))
            {
                panelDisassembler.Controls.Remove(lblDisassemblerPlaceholder);
            }

            // Create and add DisassemblerViewControl
            _disassemblerView = new DisassemblerViewControl(_process, _is64Bit);
            _disassemblerView.Dock = DockStyle.Fill;
            panelDisassembler.Controls.Add(_disassemblerView);

            // Create and add HexViewControl
            _hexView = new HexViewControl(_process);
            _hexView.Dock = DockStyle.Fill;
            _hexView.AddressSelected += (s, addr) => GotoAddress(addr);
            _hexView.GotoRequest += (s, e) => SearchGoto_Click(null, EventArgs.Empty);
            _hexView.SelectionChanged += (s, addr) => UpdateHexInfoBar(new IntPtr(addr));
            _hexView.FindAccessRequested += (s, addr) => {
                var form = new MemoryAccessForm(_process!, addr, useHardwareBreakpoint: false);
                form.Show();
            };
            _hexView.FindAccessHWBPRequested += (s, addr) => {
                var form = new MemoryAccessForm(_process!, addr, useHardwareBreakpoint: true);
                form.Show();
            };
            panelHexView.Controls.Add(_hexView);

            // Create and add RegistersViewControl
            _registersView = new RegistersViewControl();
            _registersView.Dock = DockStyle.Fill;
            _registersView.SetArchitecture(_is64Bit);
            if (_process != null) _registersView.SetProcess(_process);
            _registersView.AddressSelected += (s, addr) => GotoAddress(addr);
            panelRegisters.Controls.Add(_registersView);

            // Register update timer
            _registerUpdateTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _registerUpdateTimer.Tick += RegisterUpdateTimer_Tick;
            _registerUpdateTimer.Start();

            // Kernel-driver status indicator: poll live global state (no IOCTLs on the timer)
            UpdateDriverStatusIndicator();
            _driverStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _driverStatusTimer.Tick += (s, e) => UpdateDriverStatusIndicator();
            _driverStatusTimer.Start();
            Activated += (s, e) => UpdateDriverStatusIndicator();

            // Default to first thread
            if (_process != null)
            {
                var threads = _process.GetThreads();
                if (threads.Count > 0) _activeThreadId = (uint)threads[0].Id;
            }

            // Create and add StackViewControl to the Stack tab
            _stackView = new StackViewControl();
            _stackView.Dock = DockStyle.Fill;
            _stackView.SetProcess(_process, _is64Bit);
            tabStack.Controls.Add(_stackView);

            // Setup Bookmarks ListView
            SetupBookmarksListView();

            // Re-resolve bookmark addresses for the current process (handles ASLR)
            if (_process != null)
            {
                BookmarkManager.ResolveAddresses(_process);
            }

            // Navigate to initial address
            GotoAddress(_currentAddress);
        }

        /// <summary>
        /// Setup the bookmarks ListView with columns and events
        /// </summary>
        private void SetupBookmarksListView()
        {
            // Add columns
            lvBookmarks.Columns.Add("Address", 140);
            lvBookmarks.Columns.Add("Label", 120);
            lvBookmarks.Columns.Add("Module", 150);
            lvBookmarks.Columns.Add("Instruction", 250);
            lvBookmarks.Columns.Add("Comment", 200);

            // Apply theme
            lvBookmarks.BackColor = ThemeManager.Background;
            lvBookmarks.ForeColor = ThemeManager.Foreground;

            // Double-click to navigate
            lvBookmarks.MouseDoubleClick += LvBookmarks_MouseDoubleClick;

            // Right-click context menu
            var bookmarkContextMenu = new ContextMenuStrip();
            var menuGoto = new ToolStripMenuItem("Go to Address");
            menuGoto.Click += (s, e) => NavigateToSelectedBookmark();

            var menuEditLabel = new ToolStripMenuItem("Edit Label...");
            menuEditLabel.Click += (s, e) => EditSelectedBookmarkLabel();

            var menuEditComment = new ToolStripMenuItem("Edit Comment...");
            menuEditComment.Click += (s, e) => EditSelectedBookmarkComment();

            var menuDelete = new ToolStripMenuItem("Delete Bookmark");
            menuDelete.Click += (s, e) => DeleteSelectedBookmark();

            bookmarkContextMenu.Items.AddRange(new ToolStripItem[] {
                menuGoto,
                new ToolStripSeparator(),
                menuEditLabel,
                menuEditComment,
                new ToolStripSeparator(),
                menuDelete
            });

            lvBookmarks.ContextMenuStrip = bookmarkContextMenu;

            // Subscribe to BookmarkManager changes
            BookmarkManager.BookmarksChanged += RefreshBookmarksList;

            // Initial load
            RefreshBookmarksList();
        }

        /// <summary>
        /// Refresh the bookmarks ListView from BookmarkManager
        /// </summary>
        private void RefreshBookmarksList()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(RefreshBookmarksList));
                return;
            }

            lvBookmarks.Items.Clear();

            foreach (var bookmark in BookmarkManager.GetAll())
            {
                string addrStr = _is64Bit ? $"{bookmark.Address:X16}" : $"{bookmark.Address:X8}";
                var item = new ListViewItem(new[] {
                    addrStr,
                    bookmark.Label,
                    bookmark.Module,
                    bookmark.Instruction,
                    bookmark.Comment
                });
                item.Tag = bookmark.Address;
                lvBookmarks.Items.Add(item);
            }
        }

        private void LvBookmarks_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            NavigateToSelectedBookmark();
        }

        private void NavigateToSelectedBookmark()
        {
            if (lvBookmarks.SelectedItems.Count > 0 && lvBookmarks.SelectedItems[0].Tag is long addr)
            {
                // Switch to Memory View tab
                topTabControl.SelectedTab = tabMemoryView;
                // Navigate to address
                GotoAddress(new IntPtr(addr));
            }
        }

        private void EditSelectedBookmarkLabel()
        {
            if (lvBookmarks.SelectedItems.Count == 0) return;
            if (lvBookmarks.SelectedItems[0].Tag is not long addr) return;

            var bookmark = BookmarkManager.Get(addr);
            if (bookmark == null) return;

            using var dialog = new Form
            {
                Text = "Edit Label",
                Size = new Size(400, 130),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblLabel = new Label { Text = "Label:", Location = new Point(10, 15), AutoSize = true };
            var txtLabel = new TextBox { Location = new Point(10, 35), Size = new Size(360, 23), Text = bookmark.Label };
            var btnOk = new Button { Text = "OK", Location = new Point(210, 65), Size = new Size(80, 25), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(295, 65), Size = new Size(80, 25), DialogResult = DialogResult.Cancel };

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.AddRange(new Control[] { lblLabel, txtLabel, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                BookmarkManager.UpdateLabel(addr, txtLabel.Text);
            }
        }

        private void EditSelectedBookmarkComment()
        {
            if (lvBookmarks.SelectedItems.Count == 0) return;
            if (lvBookmarks.SelectedItems[0].Tag is not long addr) return;

            var bookmark = BookmarkManager.Get(addr);
            if (bookmark == null) return;

            using var dialog = new Form
            {
                Text = "Edit Comment",
                Size = new Size(400, 130),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblComment = new Label { Text = "Comment:", Location = new Point(10, 15), AutoSize = true };
            var txtComment = new TextBox { Location = new Point(10, 35), Size = new Size(360, 23), Text = bookmark.Comment };
            var btnOk = new Button { Text = "OK", Location = new Point(210, 65), Size = new Size(80, 25), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(295, 65), Size = new Size(80, 25), DialogResult = DialogResult.Cancel };

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.AddRange(new Control[] { lblComment, txtComment, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                BookmarkManager.UpdateComment(addr, txtComment.Text);
            }
        }

        private void DeleteSelectedBookmark()
        {
            if (lvBookmarks.SelectedItems.Count == 0) return;
            if (lvBookmarks.SelectedItems[0].Tag is not long addr) return;

            BookmarkManager.Remove(addr);
        }

        /// <summary>
        /// Apply the current theme to the entire form
        /// </summary>
        private void ApplyTheme()
        {
            // Apply theme to form and all controls
            ThemeManager.ApplyTheme(this);

            if (panelHexInfo != null)
            {
                panelHexInfo.BackColor = ThemeManager.BackgroundAlt;
                lblHexInfo.ForeColor = ThemeManager.Foreground;
            }

            // Update title with version
            string version = System.Windows.Forms.Application.ProductVersion;
            if (version.EndsWith(".0.0")) version = version[..^4];
            else if (version.EndsWith(".0")) version = version[..^2];
            this.Text = $"Memory View - CrxMem v{version}";
        }

        /// <summary>
        /// Refresh the kernel-driver status label from live global state.
        /// Polls AntiCheatBypassForm.UseKernelMode + DriverController.IsConnected()
        /// (cheap handle check). The driver version is queried via IOCTL only once,
        /// when the indicator first observes a connected state.
        /// </summary>
        private void UpdateDriverStatusIndicator()
        {
            var controller = AntiCheatBypassForm.DriverController;
            bool connected = AntiCheatBypassForm.UseKernelMode
                && controller != null
                && controller.IsConnected();

            if (connected)
            {
                if (_driverVersionText == null)
                {
                    _driverVersionText = string.Empty;
                    try
                    {
                        if (controller!.GetVersion(out uint major, out uint minor, out uint build))
                            _driverVersionText = $" v{major}.{minor}.{build}";
                    }
                    catch
                    {
                        // Version is cosmetic; never let an IOCTL hiccup break the status strip.
                    }
                }

                lblStatusDriver.Text = $"● DRIVER: KERNEL MODE{_driverVersionText}";
                lblStatusDriver.ForeColor = ThemeManager.StatusGreen;
                lblStatusDriver.ToolTipText = "Memory read/write operations route through the CrxShield kernel driver";
            }
            else
            {
                _driverVersionText = null; // re-query version on the next connect
                lblStatusDriver.Text = "● Driver: user-mode";
                lblStatusDriver.ForeColor = ThemeManager.ForegroundDim;
                lblStatusDriver.ToolTipText = "Memory operations use standard Win32 APIs";
            }
        }

        private void BtnGoto_Click(object? sender, EventArgs e)
        {
            GotoAddressFromTextBox();
        }

        private void TxtGotoAddress_KeyPress(object? sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                e.Handled = true;
                GotoAddressFromTextBox();
            }
        }

        private void GotoAddressFromTextBox()
        {
            string addrText = txtGotoAddress.Text.Trim().Replace("0x", "").Replace("0X", "");

            // Check for Module+Offset format (e.g., "Gunz.exe+29DF28")
            if (addrText.Contains('+'))
            {
                var parts = addrText.Split('+');
                if (parts.Length == 2 && _process != null)
                {
                    string moduleName = parts[0].Trim();
                    string offsetStr = parts[1].Trim();

                    // Find the module
                    var modules = _process.GetModules();
                    var module = modules.FirstOrDefault(m =>
                        m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

                    if (module != null && long.TryParse(offsetStr, NumberStyles.HexNumber, null, out long offset))
                    {
                        long absoluteAddress = module.BaseAddress.ToInt64() + offset;
                        GotoAddress(new IntPtr(absoluteAddress));
                        return;
                    }
                    else if (module == null)
                    {
                        MessageBox.Show($"Module '{moduleName}' not found in process.",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }

            // Standard hex address format
            if (long.TryParse(addrText, NumberStyles.HexNumber, null, out long addr))
            {
                GotoAddress(new IntPtr(addr));
            }
            else
            {
                MessageBox.Show("Invalid address format.\n\nSupported formats:\n• Hex address: 00400000\n• Module+Offset: Gunz.exe+29DF28",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void GotoAddress(IntPtr address)
        {
            _currentAddress = address;
            lblCurrentAddress.Text = $"Address: {address.ToInt64():X8}";
            txtGotoAddress.Text = $"{address.ToInt64():X8}";

            // Update status bar with memory region information
            UpdateMemoryRegionStatus(address);

            // Sync both views
            _disassemblerView?.SetAddress(address);
            _hexView?.SetAddress(address);
        }

        private void UpdateMemoryRegionStatus(IntPtr address)
        {
            if (_process == null)
            {
                lblStatusProtection.Text = "Protection: ---";
                lblStatusAllocationBase.Text = "Allocation Base: -----";
                lblStatusSize.Text = "Size: ----------";
                lblStatusModule.Text = "Module: ----------";
                return;
            }

            try
            {
                // Get memory region information using VirtualQueryEx
                var regionInfo = MemoryRegion.Query(_process.Handle, address);

                if (regionInfo != null)
                {
                    // Format protection string (e.g., "RWX", "R--", "RW-")
                    string protection = FormatProtection(regionInfo.Protection);
                    lblStatusProtection.Text = $"Protection: {protection}";

                    // Show allocation base
                    lblStatusAllocationBase.Text = $"Allocation Base: {regionInfo.AllocationBase.ToInt64():X}";

                    // Show region size
                    string sizeStr = FormatSize(regionInfo.RegionSize);
                    lblStatusSize.Text = $"Size: {sizeStr}";

                    // Show module name if address is in a module
                    var module = _process.GetModuleForAddress(address);
                    if (module != null)
                    {
                        lblStatusModule.Text = $"Module: {module.ModuleName}";
                    }
                    else
                    {
                        lblStatusModule.Text = "Module: (heap/stack)";
                    }
                }
                else
                {
                    lblStatusProtection.Text = "Protection: ---";
                    lblStatusAllocationBase.Text = "Allocation Base: -----";
                    lblStatusSize.Text = "Size: ----------";
                    lblStatusModule.Text = "Module: ----------";
                }
            }
            catch
            {
                lblStatusProtection.Text = "Protection: ERROR";
                lblStatusAllocationBase.Text = "Allocation Base: -----";
                lblStatusSize.Text = "Size: ----------";
                lblStatusModule.Text = "Module: ----------";
            }
        }

        private string FormatProtection(uint protection)
        {
            // PAGE_EXECUTE_READWRITE = 0x40
            // PAGE_EXECUTE_READ = 0x20
            // PAGE_READONLY = 0x02
            // PAGE_READWRITE = 0x04
            // PAGE_EXECUTE = 0x10

            string result = "";

            if ((protection & 0x04) != 0 || (protection & 0x02) != 0 || (protection & 0x08) != 0 ||
                (protection & 0x20) != 0 || (protection & 0x40) != 0)
            {
                result += "R";
            }
            else
            {
                result += "-";
            }

            if ((protection & 0x04) != 0 || (protection & 0x08) != 0 || (protection & 0x40) != 0)
            {
                result += "W";
            }
            else
            {
                result += "-";
            }

            if ((protection & 0x10) != 0 || (protection & 0x20) != 0 || (protection & 0x40) != 0)
            {
                result += "X";
            }
            else
            {
                result += "-";
            }

            return result;
        }

        private string FormatSize(long size)
        {
            if (size < 1024)
                return $"{size} B";
            else if (size < 1024 * 1024)
                return $"{size / 1024} KB";
            else if (size < 1024 * 1024 * 1024)
                return $"{size / (1024 * 1024)} MB";
            else
                return $"{size / (1024 * 1024 * 1024)} GB";
        }

        public void NavigateToAddress(IntPtr address)
        {
            GotoAddress(address);
        }

        #region File Menu Event Handlers

        private void FileOpen_Click(object? sender, EventArgs e)
        {
            using var openDialog = new OpenFileDialog
            {
                Filter = "Executable files (*.exe)|*.exe|DLL files (*.dll)|*.dll|All files (*.*)|*.*",
                Title = "Open File for Analysis"
            };

            if (openDialog.ShowDialog() == DialogResult.OK)
            {
                // TODO: Implement file loading for static analysis
                MessageBox.Show($"File loading for static analysis not yet implemented.\nSelected: {openDialog.FileName}",
                    "Open File", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void FileSave_Click(object? sender, EventArgs e)
        {
            using var saveDialog = new SaveFileDialog
            {
                Filter = "CrxMem Database (*.cdb)|*.cdb|All files (*.*)|*.*",
                Title = "Save Analysis Database"
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                // TODO: Implement database saving
                MessageBox.Show("Database saving not yet implemented.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void FileExport_Click(object? sender, EventArgs e)
        {
            using var saveDialog = new SaveFileDialog
            {
                Filter = "Text files (*.txt)|*.txt|Assembly files (*.asm)|*.asm|All files (*.*)|*.*",
                Title = "Export Disassembly"
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    // Export visible disassembly
                    string content = _disassemblerView?.ExportDisassembly() ?? "No disassembly to export";
                    System.IO.File.WriteAllText(saveDialog.FileName, content);
                    MessageBox.Show($"Disassembly exported to:\n{saveDialog.FileName}", "Export Complete",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting: {ex.Message}", "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void FileExit_Click(object? sender, EventArgs e)
        {
            Close();
        }

        #endregion

        #region View Menu Event Handlers

        private void ViewRegisters_Click(object? sender, EventArgs e)
        {
            // Toggle registers panel visibility
            panelRegisters.Visible = viewRegistersMenu.Checked;
            if (!viewRegistersMenu.Checked)
            {
                // Collapse the right panel
                mainSplitContainer.Panel2Collapsed = true;
            }
            else
            {
                mainSplitContainer.Panel2Collapsed = false;
            }
        }

        private void ViewModules_Click(object? sender, EventArgs e)
        {
            if (_process == null)
            {
                MessageBox.Show("No process attached.", "Modules", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show modules in a simple dialog for now
            var modules = _process.GetModules();
            var moduleList = new System.Text.StringBuilder();
            moduleList.AppendLine("Loaded Modules:\n");
            moduleList.AppendLine("Base Address         Size         Name");
            moduleList.AppendLine("".PadRight(60, '-'));

            foreach (var module in modules)
            {
                moduleList.AppendLine($"{module.BaseAddress.ToInt64():X16}  {module.ModuleMemorySize,10}  {module.ModuleName}");
            }

            using var dialog = new Form
            {
                Text = "Modules",
                Size = new Size(600, 400),
                StartPosition = FormStartPosition.CenterParent
            };

            var textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9F),
                Text = moduleList.ToString()
            };

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.Add(textBox);
            dialog.ShowDialog(this);
        }

        private void ViewMemoryMap_Click(object? sender, EventArgs e)
        {
            if (_process == null)
            {
                MessageBox.Show("No process attached.", "Memory Map", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show memory regions
            var regions = Core.MemoryRegion.EnumerateRegions(_process.Handle);
            var regionList = new System.Text.StringBuilder();
            regionList.AppendLine("Memory Regions:\n");
            regionList.AppendLine("Base Address         Size           State      Protect    Type");
            regionList.AppendLine("".PadRight(80, '-'));

            foreach (var region in regions)
            {
                string state = region.State switch
                {
                    0x1000 => "Commit",
                    0x2000 => "Reserve",
                    0x10000 => "Free",
                    _ => $"0x{region.State:X}"
                };

                string protection = FormatProtection(region.Protection);

                regionList.AppendLine($"{region.BaseAddress.ToInt64():X16}  {region.RegionSize,12}  {state,-10} {protection,-10}");
            }

            using var dialog = new Form
            {
                Text = "Memory Map",
                Size = new Size(700, 500),
                StartPosition = FormStartPosition.CenterParent
            };

            var textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9F),
                Text = regionList.ToString()
            };

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.Add(textBox);
            dialog.ShowDialog(this);
        }

        private void ViewThreads_Click(object? sender, EventArgs e)
        {
            if (_process == null)
            {
                MessageBox.Show("No process attached.", "Threads", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show threads
            var threads = _process.GetThreads();
            var threadList = new System.Text.StringBuilder();
            threadList.AppendLine("Threads:\n");
            threadList.AppendLine("Thread ID      Start Address      Priority");
            threadList.AppendLine("".PadRight(50, '-'));

            foreach (var thread in threads)
            {
                threadList.AppendLine($"{thread.Id,-14} {thread.StartAddress.ToInt64():X16}  {thread.Priority}");
            }

            using var dialog = new Form
            {
                Text = "Threads",
                Size = new Size(500, 400),
                StartPosition = FormStartPosition.CenterParent
            };

            var textBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9F),
                Text = threadList.ToString()
            };

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.Add(textBox);
            dialog.ShowDialog(this);
        }

        #endregion

        #region Debug Menu Event Handlers

        private void DebugRun_Click(object? sender, EventArgs e)
        {
            // Debug functionality - requires actual debugger implementation
            lblStatusInfo.Text = "Run - Not implemented (requires debugger attachment)";
        }

        private void DebugPause_Click(object? sender, EventArgs e)
        {
            lblStatusInfo.Text = "Pause - Not implemented (requires debugger attachment)";
        }

        private void DebugStop_Click(object? sender, EventArgs e)
        {
            lblStatusInfo.Text = "Stop - Not implemented (requires debugger attachment)";
        }

        private void DebugStepInto_Click(object? sender, EventArgs e)
        {
            lblStatusInfo.Text = "Step Into - Not implemented (requires debugger attachment)";
        }

        private void DebugStepOver_Click(object? sender, EventArgs e)
        {
            lblStatusInfo.Text = "Step Over - Not implemented (requires debugger attachment)";
        }

        private void DebugStepOut_Click(object? sender, EventArgs e)
        {
            lblStatusInfo.Text = "Step Out - Not implemented (requires debugger attachment)";
        }

        private void DebugBreakpoint_Click(object? sender, EventArgs e)
        {
            // Toggle breakpoint at current address
            lblStatusInfo.Text = $"Breakpoint toggled at {_currentAddress.ToInt64():X}";
            // TODO: Implement actual breakpoint management
        }

        #endregion

        #region Search Menu Event Handlers

        private void SearchGoto_Click(object? sender, EventArgs e)
        {
            // Open a Go to Address dialog (like Cheat Engine)
            using var gotoDialog = new Form
            {
                Text = "Go to Address",
                Size = new Size(350, 130),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblAddress = new Label
            {
                Text = "Enter address (hex or module+offset):",
                Location = new Point(10, 15),
                AutoSize = true
            };

            var txtAddress = new TextBox
            {
                Location = new Point(10, 38),
                Size = new Size(310, 23),
                Font = new Font("Consolas", 9F),
                Text = txtGotoAddress.Text // Pre-fill with current address
            };
            txtAddress.SelectAll();

            var btnOK = new Button
            {
                Text = "OK",
                Location = new Point(160, 70),
                Size = new Size(75, 25),
                DialogResult = DialogResult.OK
            };

            var btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(245, 70),
                Size = new Size(75, 25),
                DialogResult = DialogResult.Cancel
            };

            gotoDialog.Controls.AddRange(new Control[] { lblAddress, txtAddress, btnOK, btnCancel });
            gotoDialog.AcceptButton = btnOK;
            gotoDialog.CancelButton = btnCancel;

            // Apply theme
            ThemeManager.ApplyTheme(gotoDialog);

            if (gotoDialog.ShowDialog(this) == DialogResult.OK)
            {
                string addrText = txtAddress.Text.Trim();
                if (string.IsNullOrEmpty(addrText))
                    return;

                // Update the top textbox as well
                txtGotoAddress.Text = addrText;

                // Use existing navigation logic
                GotoAddressFromTextBox();
            }
        }

        private void SearchFind_Click(object? sender, EventArgs e)
        {
            SearchFindPattern_Click(sender, e);
        }

        private void SearchFindReferences_Click(object? sender, EventArgs e)
        {
            if (_process == null)
            {
                MessageBox.Show("No process attached.", "Find References", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new Form
            {
                Text = "Find References",
                Size = new Size(400, 150),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblAddr = new Label { Text = "Find references to address:", Location = new Point(10, 15), AutoSize = true };
            var txtAddr = new TextBox { Location = new Point(10, 35), Size = new Size(360, 23), Text = $"{_currentAddress.ToInt64():X}" };
            var btnFind = new Button { Text = "Find", Location = new Point(210, 70), Size = new Size(80, 25) };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(295, 70), Size = new Size(75, 25) };

            btnFind.Click += (s, ev) =>
            {
                if (long.TryParse(txtAddr.Text.Replace("0x", "").Replace("0X", ""), NumberStyles.HexNumber, null, out long addr))
                {
                    lblStatusInfo.Text = $"Finding references to {addr:X}...";
                    var targetAddr = new IntPtr(addr);
                    var references = FindReferencesToAddress(targetAddr);

                    if (references.Count == 0)
                    {
                        MessageBox.Show($"No references found to address {addr:X}.", "Find References",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        ShowReferencesDialog(references, targetAddr);
                    }

                    lblStatusInfo.Text = $"Found {references.Count} references to {addr:X}";
                    dialog.DialogResult = DialogResult.OK;
                }
                else
                {
                    MessageBox.Show("Invalid address format.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            btnCancel.Click += (s, ev) => dialog.DialogResult = DialogResult.Cancel;

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.AddRange(new Control[] { lblAddr, txtAddr, btnFind, btnCancel });
            dialog.AcceptButton = btnFind;
            dialog.CancelButton = btnCancel;
            dialog.ShowDialog(this);
        }

        private void SearchFindPattern_Click(object? sender, EventArgs e)
        {
            if (_process == null || !_process.IsOpen)
            {
                MessageBox.Show("No process attached.", "Find Pattern", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new Form
            {
                Text = "Find Pattern (AOB Scan)",
                Size = new Size(500, 250),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblPattern = new Label { Text = "Pattern (hex bytes, use ?? or ? for wildcards):", Location = new Point(10, 15), AutoSize = true };
            var txtPattern = new TextBox { Location = new Point(10, 35), Size = new Size(460, 23), Text = "75 0B 8B 8D ?? ?? ?? ?? E8 ?? ?? ?? ??" };
            var lblExample = new Label { Text = "Examples:\n• 48 8B ?? ?? ?? ?? ?? 48 85 C0\n• 75 0B 8B 8D 28 FE FF FF E8 ?? ?? ?? ??", Location = new Point(10, 65), Size = new Size(460, 50), ForeColor = Color.Gray };

            var chkModuleOnly = new CheckBox { Text = "Scan current module only", Location = new Point(10, 120), AutoSize = true, Checked = false };
            var chkExecutableOnly = new CheckBox { Text = "Executable memory only", Location = new Point(200, 120), AutoSize = true, Checked = true };

            var btnFind = new Button { Text = "Scan", Location = new Point(310, 160), Size = new Size(80, 28) };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(395, 160), Size = new Size(75, 28) };

            btnFind.Click += async (s, ev) =>
            {
                string pattern = txtPattern.Text.Trim();
                if (string.IsNullOrEmpty(pattern))
                {
                    MessageBox.Show("Please enter a pattern.", "Find Pattern", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                btnFind.Enabled = false;
                btnFind.Text = "Scanning...";
                lblStatusInfo.Text = $"Scanning for pattern: {pattern}...";
                dialog.Cursor = Cursors.WaitCursor;

                try
                {
                    // Run AOB scan on background thread
                    var scanner = new MemoryScanner(_process);
                    List<IntPtr>? results = null;

                    IntPtr startAddr = IntPtr.Zero;
                    IntPtr endAddr = IntPtr.Zero;

                    // If module-only, restrict to current module
                    if (chkModuleOnly.Checked && _currentAddress != IntPtr.Zero)
                    {
                        var module = _process.GetModuleForAddress(_currentAddress);
                        if (module != null)
                        {
                            startAddr = module.BaseAddress;
                            endAddr = new IntPtr(module.BaseAddress.ToInt64() + module.Size);
                        }
                    }

                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        results = ScanAOBPattern(pattern, startAddr, endAddr, chkExecutableOnly.Checked);
                    });

                    if (results == null || results.Count == 0)
                    {
                        MessageBox.Show("No results found for the pattern.", "Find Pattern",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        lblStatusInfo.Text = "No pattern matches found.";
                    }
                    else
                    {
                        lblStatusInfo.Text = $"Found {results.Count} pattern match(es).";
                        dialog.DialogResult = DialogResult.OK;
                        dialog.Close();

                        // Show results dialog
                        ShowAOBResultsDialog(results, pattern);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"AOB Scan error: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    lblStatusInfo.Text = "AOB scan failed.";
                }
                finally
                {
                    btnFind.Enabled = true;
                    btnFind.Text = "Scan";
                    dialog.Cursor = Cursors.Default;
                }
            };

            btnCancel.Click += (s, ev) => dialog.DialogResult = DialogResult.Cancel;

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.AddRange(new Control[] { lblPattern, txtPattern, lblExample, chkModuleOnly, chkExecutableOnly, btnFind, btnCancel });
            dialog.AcceptButton = btnFind;
            dialog.CancelButton = btnCancel;
            dialog.ShowDialog(this);
        }

        /// <summary>
        /// Scans for AOB pattern with wildcard support
        /// </summary>
        private System.Collections.Generic.List<IntPtr> ScanAOBPattern(string pattern, IntPtr startAddr, IntPtr endAddr, bool executableOnly)
        {
            var results = new System.Collections.Generic.List<IntPtr>();
            if (_process == null || !_process.IsOpen) return results;

            // Parse pattern into bytes and mask
            var patternBytes = ParseAOBPattern(pattern, out var mask);
            if (patternBytes.Length == 0) return results;

            // Get memory regions
            IntPtr scanStart = startAddr != IntPtr.Zero ? startAddr : new IntPtr(0x10000);
            IntPtr scanEnd = endAddr != IntPtr.Zero ? endAddr :
                (_process.Is64Bit ? new IntPtr(0x7FFFFFFFFFFF) : new IntPtr(0x7FFFFFFF));

            var regions = EnumerateMemoryRegionsForAOB(scanStart, scanEnd, executableOnly);

            foreach (var region in regions)
            {
                try
                {
                    byte[]? buffer = _process.Read(region.BaseAddress, (int)Math.Min(region.Size, 50_000_000));
                    if (buffer == null || buffer.Length < patternBytes.Length) continue;

                    for (int i = 0; i <= buffer.Length - patternBytes.Length; i++)
                    {
                        if (MatchAOBPattern(buffer, i, patternBytes, mask))
                        {
                            results.Add(new IntPtr(region.BaseAddress.ToInt64() + i));
                            if (results.Count >= 10000) return results; // Limit results
                        }
                    }
                }
                catch { }
            }

            return results;
        }

        /// <summary>
        /// Parse AOB pattern string into bytes and mask
        /// Supports: "48 8B ?? ?? ?? ??" or "48 8B ? ? ? ?" or "488B????????"
        /// </summary>
        private byte[] ParseAOBPattern(string pattern, out bool[] mask)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            var maskList = new System.Collections.Generic.List<bool>();

            // Normalize pattern - handle both spaced and non-spaced patterns
            pattern = pattern.Replace("-", " ").Trim();

            // Check if it's a spaced pattern or continuous hex string
            if (pattern.Contains(" "))
            {
                // Spaced pattern: "48 8B ?? C0"
                var parts = pattern.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part == "?" || part == "??")
                    {
                        bytes.Add(0);
                        maskList.Add(true); // Wildcard
                    }
                    else
                    {
                        try
                        {
                            bytes.Add(Convert.ToByte(part, 16));
                            maskList.Add(false); // Must match
                        }
                        catch
                        {
                            // Invalid byte, treat as wildcard
                            bytes.Add(0);
                            maskList.Add(true);
                        }
                    }
                }
            }
            else
            {
                // Continuous pattern: "488B??C0" or "488B**C0"
                for (int i = 0; i < pattern.Length; i += 2)
                {
                    if (i + 1 >= pattern.Length) break;

                    string byteStr = pattern.Substring(i, 2);
                    if (byteStr == "??" || byteStr == "**")
                    {
                        bytes.Add(0);
                        maskList.Add(true);
                    }
                    else
                    {
                        try
                        {
                            bytes.Add(Convert.ToByte(byteStr, 16));
                            maskList.Add(false);
                        }
                        catch
                        {
                            bytes.Add(0);
                            maskList.Add(true);
                        }
                    }
                }
            }

            mask = maskList.ToArray();
            return bytes.ToArray();
        }

        /// <summary>
        /// Match pattern at buffer offset
        /// </summary>
        private bool MatchAOBPattern(byte[] buffer, int offset, byte[] pattern, bool[] mask)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (!mask[i] && buffer[offset + i] != pattern[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Enumerate memory regions for AOB scanning using MemoryRegion class
        /// </summary>
        private System.Collections.Generic.List<AOBRegionInfo> EnumerateMemoryRegionsForAOB(IntPtr start, IntPtr end, bool executableOnly)
        {
            var regions = new System.Collections.Generic.List<AOBRegionInfo>();
            if (_process == null || !_process.IsOpen) return regions;

            IntPtr address = start;

            while (address.ToInt64() < end.ToInt64())
            {
                var region = MemoryRegion.Query(_process.Handle, address);
                if (region == null)
                    break;

                // Skip if no size
                if (region.Size == 0)
                {
                    address = new IntPtr(address.ToInt64() + 0x1000);
                    continue;
                }

                // Check if committed and readable, not guarded
                bool isGuarded = (region.Protection & MemoryRegion.PAGE_GUARD) != 0;
                bool isExecutable = (region.Protection & (MemoryRegion.PAGE_EXECUTE | MemoryRegion.PAGE_EXECUTE_READ |
                    MemoryRegion.PAGE_EXECUTE_READWRITE | MemoryRegion.PAGE_EXECUTE_WRITECOPY)) != 0;

                if (region.IsCommitted && region.IsReadable && !isGuarded)
                {
                    if (!executableOnly || isExecutable)
                    {
                        regions.Add(new AOBRegionInfo
                        {
                            BaseAddress = region.BaseAddress,
                            Size = region.Size
                        });
                    }
                }

                address = new IntPtr(region.BaseAddress.ToInt64() + region.Size);

                // Safety check for overflow
                if (address.ToInt64() <= region.BaseAddress.ToInt64())
                    break;
            }

            return regions;
        }

        /// <summary>
        /// Simple struct to hold AOB region info
        /// </summary>
        private struct AOBRegionInfo
        {
            public IntPtr BaseAddress;
            public long Size;
        }

        /// <summary>
        /// Show dialog with AOB scan results
        /// </summary>
        private void ShowAOBResultsDialog(System.Collections.Generic.List<IntPtr> results, string pattern)
        {
            using var dialog = new Form
            {
                Text = $"AOB Results: {pattern}",
                Size = new Size(600, 450),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false
            };

            var lblInfo = new Label
            {
                Text = $"Found {results.Count} match(es). Double-click to navigate.",
                Dock = DockStyle.Top,
                Height = 25,
                Padding = new Padding(5, 5, 0, 0)
            };

            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            listView.Columns.Add("Address", 130);
            listView.Columns.Add("Module+Offset", 200);
            listView.Columns.Add("Bytes Preview", 200);

            foreach (var addr in results)
            {
                string addressStr = _is64Bit ? $"{addr.ToInt64():X16}" : $"{addr.ToInt64():X8}";
                string moduleOffset = addressStr;

                // Try to resolve to module+offset
                if (_process != null)
                {
                    var module = _process.GetModuleForAddress(addr);
                    if (module != null)
                    {
                        long offset = addr.ToInt64() - module.BaseAddress.ToInt64();
                        moduleOffset = $"{module.ModuleName}+{offset:X}";
                    }
                }

                // Read bytes preview
                string bytesPreview = "";
                if (_process != null)
                {
                    try
                    {
                        byte[]? preview = _process.Read(addr, 16);
                        if (preview != null)
                        {
                            bytesPreview = BitConverter.ToString(preview).Replace("-", " ");
                        }
                    }
                    catch { }
                }

                var item = new ListViewItem(new[] { addressStr, moduleOffset, bytesPreview });
                item.Tag = addr;
                listView.Items.Add(item);
            }

            // Double-click to navigate
            listView.MouseDoubleClick += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is IntPtr addr)
                {
                    NavigateToAddress(addr);
                }
            };

            var btnGoto = new Button { Text = "Go to Address", Size = new Size(110, 28) };
            btnGoto.Click += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is IntPtr addr)
                {
                    NavigateToAddress(addr);
                }
            };

            var btnCopyAddress = new Button { Text = "Copy Address", Size = new Size(110, 28) };
            btnCopyAddress.Click += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0)
                {
                    Clipboard.SetText(listView.SelectedItems[0].Text);
                }
            };

            var btnCopyModuleOffset = new Button { Text = "Copy Module+Offset", Size = new Size(130, 28) };
            btnCopyModuleOffset.Click += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0)
                {
                    Clipboard.SetText(listView.SelectedItems[0].SubItems[1].Text);
                }
            };

            var btnClose = new Button { Text = "Close", Size = new Size(80, 28) };
            btnClose.Click += (s, ev) => dialog.Close();

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5)
            };
            buttonPanel.Controls.Add(btnClose);
            buttonPanel.Controls.Add(btnCopyModuleOffset);
            buttonPanel.Controls.Add(btnCopyAddress);
            buttonPanel.Controls.Add(btnGoto);

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.Add(listView);
            dialog.Controls.Add(lblInfo);
            dialog.Controls.Add(buttonPanel);
            dialog.ShowDialog(this);
        }

        /// <summary>
        /// Find all instructions that reference the specified address
        /// </summary>
        private List<IntPtr> FindReferencesToAddress(IntPtr targetAddr)
        {
            var results = new List<IntPtr>();

            if (_disassemblerView == null) return results;

            ulong targetAddress = (ulong)targetAddr.ToInt64();

            foreach (var instr in _disassemblerView.Instructions)
            {
                if (instr.IsDataByte) continue;

                var instruction = instr.Instruction;

                // Check if instruction has a near branch target (jmp, call, conditional branches)
                if (instruction.NearBranchTarget == targetAddress)
                {
                    results.Add(instr.Address);
                    continue;
                }

                // Check if instruction has an IP-relative memory operand
                if (instruction.IsIPRelativeMemoryOperand &&
                    instruction.IPRelativeMemoryAddress == targetAddress)
                {
                    results.Add(instr.Address);
                    continue;
                }

                // Check for immediate operands containing the address
                if (instruction.Immediate64 == targetAddress ||
                    instruction.Immediate32 == (uint)targetAddress)
                {
                    results.Add(instr.Address);
                    continue;
                }

                // Check memory displacement (mov reg, [base+offset])
                if (instruction.MemoryDisplacement64 == targetAddress)
                {
                    results.Add(instr.Address);
                    continue;
                }
            }

            return results;
        }

        /// <summary>
        /// Show a dialog listing all found cross-references
        /// </summary>
        private void ShowReferencesDialog(List<IntPtr> references, IntPtr targetAddr)
        {
            using var dialog = new Form
            {
                Text = $"References to {targetAddr.ToInt64():X}",
                Size = new Size(500, 400),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = true,
                MinimizeBox = false
            };

            var listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            listView.Columns.Add("Address", 150);
            listView.Columns.Add("Module+Offset", 200);
            listView.Columns.Add("Type", 100);

            foreach (var refAddr in references)
            {
                string addressStr = $"{refAddr.ToInt64():X}";
                string moduleOffset = addressStr;

                // Try to resolve to module+offset
                if (_process != null)
                {
                    var module = _process.GetModuleForAddress(refAddr);
                    if (module != null)
                    {
                        long offset = refAddr.ToInt64() - module.BaseAddress.ToInt64();
                        moduleOffset = $"{module.ModuleName}+{offset:X}";
                    }
                }

                var item = new ListViewItem(new[] { addressStr, moduleOffset, "xref" });
                item.Tag = refAddr;
                listView.Items.Add(item);
            }

            // Double-click to navigate
            listView.MouseDoubleClick += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is IntPtr addr)
                {
                    NavigateToAddress(addr);
                    dialog.Close();
                }
            };

            var btnGoto = new Button { Text = "Go to Address", Size = new Size(100, 25), Dock = DockStyle.Bottom };
            btnGoto.Click += (s, ev) =>
            {
                if (listView.SelectedItems.Count > 0 && listView.SelectedItems[0].Tag is IntPtr addr)
                {
                    NavigateToAddress(addr);
                    dialog.Close();
                }
            };

            var btnClose = new Button { Text = "Close", Size = new Size(80, 25), Dock = DockStyle.Bottom };
            btnClose.Click += (s, ev) => dialog.Close();

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 35,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5)
            };
            buttonPanel.Controls.Add(btnClose);
            buttonPanel.Controls.Add(btnGoto);

            ThemeManager.ApplyTheme(dialog);
            dialog.Controls.Add(listView);
            dialog.Controls.Add(buttonPanel);
            dialog.ShowDialog(this);
        }

        #endregion

        #region Options Menu Event Handlers

        private void OptionsPreferences_Click(object? sender, EventArgs e)
        {
            using var settingsForm = new SettingsForm();
            if (settingsForm.ShowDialog(this) == DialogResult.OK)
            {
                // Reapply theme after settings change
                ApplyTheme();
                _disassemblerView?.ApplyTheme();
                _hexView?.ApplyTheme();
                _registersView?.ApplyTheme();
                _stackView?.ApplyTheme();
            }
        }

        #endregion

        #region Help Menu Event Handlers

        private void HelpAbout_Click(object? sender, EventArgs e)
        {
            string version = Application.ProductVersion;
            if (version.EndsWith(".0.0")) version = version[..^4];
            else if (version.EndsWith(".0")) version = version[..^2];

            string aboutText = $"CrxMem v{version}\n\n" +
                              "A modern memory analysis tool\n" +
                              "inspired by Cheat Engine and x64dbg.\n\n" +
                              $"Runtime: .NET {Environment.Version}\n" +
                              $"Platform: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}\n\n" +
                              "Built with ❤ using C# and WinForms";

            MessageBox.Show(aboutText, "About CrxMem", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion
        private void UpdateHexInfoBar(IntPtr address)
        {
            if (_process == null || address == IntPtr.Zero) return;

            string moduleName = "---";
            var modules = _process.GetModules();
            foreach (var mod in modules)
            {
                if (address.ToInt64() >= mod.BaseAddress.ToInt64() && 
                    address.ToInt64() < mod.BaseAddress.ToInt64() + mod.Size)
                {
                    moduleName = mod.ModuleName;
                    break;
                }
            }

            string protection = "---";
            var regions = _process.GetMemoryRegions();
            foreach (var region in regions)
            {
                if (address.ToInt64() >= region.BaseAddress.ToInt64() && 
                    address.ToInt64() < region.BaseAddress.ToInt64() + region.RegionSize)
                {
                    protection = FormatProtectionName(region.Protection);
                    break;
                }
            }

            lblHexInfo.Text = $"Address: {address.ToInt64():X} | Protection: {protection} | Module: {moduleName}";
        }

        private string FormatProtectionName(uint protect)
        {
            return protect switch
            {
                0x01 => "PAGE_NOACCESS",
                0x02 => "PAGE_READONLY",
                0x04 => "PAGE_READWRITE",
                0x08 => "PAGE_WRITECOPY",
                0x10 => "PAGE_EXECUTE",
                0x20 => "PAGE_EXECUTE_READ",
                0x40 => "PAGE_EXECUTE_READWRITE",
                0x80 => "PAGE_EXECUTE_WRITECOPY",
                0x100 => "PAGE_GUARD",
                0x200 => "PAGE_NOCACHE",
                0x400 => "PAGE_WRITECOMBINE",
                _ => $"0x{protect:X}"
            };
        }

        private void RegisterUpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_registersView != null && _activeThreadId != 0)
            {
                _registersView.RefreshFromThread(_activeThreadId);
            }
        }
    }
}
