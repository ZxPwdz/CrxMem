using System;
using System.Drawing;
using System.Windows.Forms;

namespace CrxMem
{
    public class SettingsForm : Form
    {
        public event EventHandler? SettingsApplied;

        private TabControl tabControl;
        private TabPage tabGeneral;
        private TabPage tabScan;
        private TabPage tabHotkeys;
        private Button btnOK;
        private Button btnCancel;
        private Button btnApply;

        // General Settings Controls
        private NumericUpDown nudUpdateInterval;
        private NumericUpDown nudFreezeInterval;
        private NumericUpDown nudFoundListInterval;
        private CheckBox chkShowSigned;
        private CheckBox chkSimpleCopyPaste;
        private CheckBox chkSaveWindowPos;
        private TextBox txtAutoAttachProcess;
        private CheckBox chkAlwaysAutoAttach;
        private CheckBox chkFastAutoAttach;
        private CheckBox chkUseWmiWatch;
        private NumericUpDown nudFastAttachInterval;
        private CheckBox chkAutoLoadDriver;
        private CheckBox chkAutoInjectVEH;
        private CheckBox chkFastSuspendOnAttach;
        private NumericUpDown nudSuspendInterval;
        private CheckBox chkAskClearList;
        private CheckBox chkLaunchAsAdmin;
        private CheckBox chkDarkMode;

        // Scan Settings Controls
        private ComboBox cmbThreadPriority;
        private NumericUpDown nudBufferSize;
        private CheckBox chkFastScanDefault;
        private CheckBox chkPauseScanning;
        private CheckBox chkMemPrivate;
        private CheckBox chkMemImage;
        private CheckBox chkMemMapped;
        private CheckBox chkSkipNoCache;
        private CheckBox chkSkipWriteCombine;
        private NumericUpDown nudKernelUpdateMultiplier;
        private NumericUpDown nudKernelCacheLifetime;

        // Hotkey Settings Controls (Cheat Engine style)
        private ListView lvHotkeys;
        private Button btnClearHotkey;
        private NumericUpDown nudKeypollInterval;
        private NumericUpDown nudDelayBetweenHotkeys;

        // Tooltips
        private ToolTip toolTip;

        public SettingsForm()
        {
            InitializeComponents();
            LoadSettings();
        }

        private void InitializeComponents()
        {
            this.Text = "Settings";
            this.Size = new Size(600, 550);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;

            // Tab Control
            tabControl = new TabControl
            {
                Location = new Point(10, 10),
                Size = new Size(560, 450)
            };

            // General Tab
            tabGeneral = new TabPage("General");
            tabGeneral.AutoScroll = true; // Enable scrolling for many controls
            CreateGeneralTab();
            tabControl.TabPages.Add(tabGeneral);

            // Scan Settings Tab
            tabScan = new TabPage("Scan Settings");
            CreateScanTab();
            tabControl.TabPages.Add(tabScan);

            // Hotkeys Tab
            tabHotkeys = new TabPage("Hotkeys");
            CreateHotkeysTab();
            tabControl.TabPages.Add(tabHotkeys);

            // Buttons
            btnOK = new Button
            {
                Text = "OK",
                Location = new Point(300, 470),
                Size = new Size(80, 30)
            };
            btnOK.Click += BtnOK_Click;

            btnCancel = new Button
            {
                Text = "Cancel",
                Location = new Point(390, 470),
                Size = new Size(80, 30),
                DialogResult = DialogResult.Cancel
            };

            btnApply = new Button
            {
                Text = "Apply",
                Location = new Point(480, 470),
                Size = new Size(80, 30)
            };
            btnApply.Click += BtnApply_Click;

            this.Controls.Add(tabControl);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnApply);
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;

            // Initialize tooltips
            SetupTooltips();
        }

        private void SetupTooltips()
        {
            toolTip = new ToolTip
            {
                AutoPopDelay = 10000,  // Show for 10 seconds
                InitialDelay = 500,    // Wait 0.5s before showing
                ReshowDelay = 200,
                ShowAlways = true
            };

            // General Tab Tooltips
            toolTip.SetToolTip(nudUpdateInterval, "How often to refresh displayed values in the address list (lower = more CPU usage)");
            toolTip.SetToolTip(nudFreezeInterval, "How often frozen values are written to memory (lower = more responsive but higher CPU)");
            toolTip.SetToolTip(nudFoundListInterval, "How often to update the scan results list during/after scanning");
            toolTip.SetToolTip(chkShowSigned, "Display integer values as signed (can show negative numbers)");
            toolTip.SetToolTip(chkSimpleCopyPaste, "Use simple text format when copying addresses to clipboard");
            toolTip.SetToolTip(chkSaveWindowPos, "Remember window position and size between sessions");
            toolTip.SetToolTip(txtAutoAttachProcess, "Process names to automatically attach to, separated by semicolons (e.g., game.exe;launcher.exe)");
            toolTip.SetToolTip(chkAlwaysAutoAttach, "Keep checking for target process even when already attached to another process");
            toolTip.SetToolTip(chkFastAutoAttach, "Use instant process detection (WMI events or 1ms polling) to attach before anti-cheat initializes");
            toolTip.SetToolTip(chkUseWmiWatch, "Use Windows WMI events for instant process detection. Falls back to polling if unavailable. Requires admin rights.");
            toolTip.SetToolTip(nudFastAttachInterval, "Polling interval in milliseconds when WMI is not available. 1ms = fastest possible.");
            toolTip.SetToolTip(chkAutoLoadDriver, "Automatically load CrxShield kernel driver when fast auto-attach starts");
            toolTip.SetToolTip(chkAutoInjectVEH, "Automatically inject VEH debugger DLL immediately after attaching to target process");
            toolTip.SetToolTip(chkFastSuspendOnAttach, "INSTANTLY suspend the process the moment it's detected (before anti-cheat can initialize). Resume manually or via hotkey.");
            toolTip.SetToolTip(nudSuspendInterval, "Interval in ms to repeatedly suspend the process (0 = only once on attach). Use this to keep process frozen while you work.");
            toolTip.SetToolTip(chkAskClearList, "Ask before clearing the address list when opening a new process");
            toolTip.SetToolTip(chkLaunchAsAdmin, "Always try to run with administrator privileges for better memory access");
            toolTip.SetToolTip(chkDarkMode, "Enable dark theme for Memory View and other windows (modern debugger style)");

            // Scan Tab Tooltips
            toolTip.SetToolTip(cmbThreadPriority, "Thread priority for scanning (higher = faster but may affect system responsiveness)");
            toolTip.SetToolTip(nudBufferSize, "Memory read buffer size in KB (larger = faster scans but uses more RAM)");
            toolTip.SetToolTip(chkFastScanDefault, "Enable fast scan by default (skips unaligned addresses, faster but may miss some values)");
            toolTip.SetToolTip(chkPauseScanning, "Pause the target process while scanning (more accurate but freezes the game)");
            toolTip.SetToolTip(chkMemPrivate, "Scan private memory (heap/stack) - most game data is stored here");
            toolTip.SetToolTip(chkMemImage, "Scan image memory (executable/DLL code and data sections)");
            toolTip.SetToolTip(chkMemMapped, "Scan memory-mapped files (usually not needed for game hacking)");
            toolTip.SetToolTip(chkSkipNoCache, "Skip memory regions marked as non-cacheable (usually device memory)");
            toolTip.SetToolTip(chkSkipWriteCombine, "Skip memory regions with write-combining (usually video memory)");
            toolTip.SetToolTip(nudKernelUpdateMultiplier, "Slow down UI updates when using kernel driver to reduce lag (2x = half as often)");
            toolTip.SetToolTip(nudKernelCacheLifetime, "How long to cache memory reads in kernel mode (higher = faster but may show stale values)");

            // Hotkey Tab Tooltips - set after controls are created
        }

        private void CreateGeneralTab()
        {
            int yPos = 20;

            // Update Intervals Section
            var lblIntervals = new Label
            {
                Text = "Update Intervals (milliseconds)",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabGeneral.Controls.Add(lblIntervals);
            yPos += 30;

            AddNumericUpDownWithLabel(tabGeneral, "Update interval:", ref nudUpdateInterval, 20, ref yPos, 100, 5000, 500);
            AddNumericUpDownWithLabel(tabGeneral, "Freeze interval:", ref nudFreezeInterval, 20, ref yPos, 10, 1000, 100);
            AddNumericUpDownWithLabel(tabGeneral, "Found list update interval:", ref nudFoundListInterval, 20, ref yPos, 100, 5000, 1000);

            yPos += 10;

            // Display Options
            var lblDisplay = new Label
            {
                Text = "Display Options",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabGeneral.Controls.Add(lblDisplay);
            yPos += 30;

            chkShowSigned = AddCheckBox(tabGeneral, "Show values as signed", 20, ref yPos);
            chkSimpleCopyPaste = AddCheckBox(tabGeneral, "Simple copy/paste mode", 20, ref yPos);
            chkSaveWindowPos = AddCheckBox(tabGeneral, "Save window positions", 20, ref yPos);

            yPos += 10;

            // Theme Options
            var lblTheme = new Label
            {
                Text = "Theme",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabGeneral.Controls.Add(lblTheme);
            yPos += 30;

            chkDarkMode = AddCheckBox(tabGeneral, "Dark Mode (requires restart for full effect)", 20, ref yPos);

            yPos += 10;

            // Process Management
            var lblProcess = new Label
            {
                Text = "Process Management",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabGeneral.Controls.Add(lblProcess);
            yPos += 30;

            var lblAutoAttach = new Label
            {
                Text = "Auto-attach to process (semicolon-separated):",
                Location = new Point(20, yPos),
                Size = new Size(350, 20)
            };
            tabGeneral.Controls.Add(lblAutoAttach);
            yPos += 25;

            txtAutoAttachProcess = new TextBox
            {
                Location = new Point(20, yPos),
                Size = new Size(400, 23)
            };
            tabGeneral.Controls.Add(txtAutoAttachProcess);
            yPos += 35;

            chkAlwaysAutoAttach = AddCheckBox(tabGeneral, "Always auto-attach even when process selected", 20, ref yPos);

            // Fast Auto-Attach Section
            yPos += 10;
            var lblFastAttach = new Label
            {
                Text = "Fast Auto-Attach (for anti-cheat bypass)",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(300, 20)
            };
            tabGeneral.Controls.Add(lblFastAttach);
            yPos += 30;

            chkFastAutoAttach = AddCheckBox(tabGeneral, "Enable fast auto-attach (instant process detection)", 20, ref yPos);
            chkUseWmiWatch = AddCheckBox(tabGeneral, "Use WMI events (fastest, requires admin)", 20, ref yPos);

            var lblPollingInterval = new Label
            {
                Text = "Polling interval (ms) - fallback mode:",
                Location = new Point(20, yPos),
                Size = new Size(220, 20)
            };
            tabGeneral.Controls.Add(lblPollingInterval);

            nudFastAttachInterval = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 100,
                Value = 1,
                Location = new Point(250, yPos),
                Size = new Size(60, 23)
            };
            tabGeneral.Controls.Add(nudFastAttachInterval);
            yPos += 35;

            chkAutoLoadDriver = AddCheckBox(tabGeneral, "Auto-load CrxShield driver on startup", 20, ref yPos);
            chkAutoInjectVEH = AddCheckBox(tabGeneral, "Auto-inject VEH debugger after attach", 20, ref yPos);
            chkFastSuspendOnAttach = AddCheckBox(tabGeneral, "Suspend process immediately on attach (anti-cheat freeze)", 20, ref yPos);

            // Suspend Interval setting
            var lblSuspendInterval = new Label
            {
                Text = "Suspend interval (ms):",
                Location = new Point(40, yPos),
                Size = new Size(130, 20),
                Font = new Font("Segoe UI", 9F)
            };
            tabGeneral.Controls.Add(lblSuspendInterval);

            nudSuspendInterval = new NumericUpDown
            {
                Location = new Point(175, yPos - 2),
                Minimum = 0,
                Maximum = 10000,
                Increment = 100,
                Value = 0,
                Size = new Size(80, 23)
            };
            tabGeneral.Controls.Add(nudSuspendInterval);

            var lblSuspendInfo = new Label
            {
                Text = "(0 = once on attach only)",
                Location = new Point(260, yPos),
                Size = new Size(150, 20),
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.Gray
            };
            tabGeneral.Controls.Add(lblSuspendInfo);
            yPos += 30;

            yPos += 10;
            chkAskClearList = AddCheckBox(tabGeneral, "Ask to clear list on new process", 20, ref yPos);
            chkLaunchAsAdmin = AddCheckBox(tabGeneral, "Always attempt to launch as admin", 20, ref yPos);
        }

        private void CreateScanTab()
        {
            int yPos = 20;

            // Performance Section
            var lblPerformance = new Label
            {
                Text = "Scan Performance",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabScan.Controls.Add(lblPerformance);
            yPos += 30;

            var lblThreadPriority = new Label
            {
                Text = "Thread priority:",
                Location = new Point(20, yPos),
                Size = new Size(150, 20)
            };
            tabScan.Controls.Add(lblThreadPriority);

            cmbThreadPriority = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(180, yPos),
                Size = new Size(150, 23)
            };
            cmbThreadPriority.Items.AddRange(new object[]
            {
                "Idle",
                "Lowest",
                "Lower",
                "Normal",
                "Higher",
                "Highest"
            });
            tabScan.Controls.Add(cmbThreadPriority);
            yPos += 35;

            AddNumericUpDownWithLabel(tabScan, "Scan buffer size (KB):", ref nudBufferSize, 20, ref yPos, 1024, 65536, 4096);

            chkFastScanDefault = AddCheckBox(tabScan, "Fast scan on by default", 20, ref yPos);
            chkPauseScanning = AddCheckBox(tabScan, "Pause while scanning on by default", 20, ref yPos);

            yPos += 10;

            // Kernel Mode Performance Section
            var lblKernelPerf = new Label
            {
                Text = "Kernel Mode Performance",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabScan.Controls.Add(lblKernelPerf);
            yPos += 30;

            var lblUpdateMultiplier = new Label
            {
                Text = "UI update slowdown (x):",
                Location = new Point(20, yPos),
                Size = new Size(150, 20)
            };
            tabScan.Controls.Add(lblUpdateMultiplier);

            nudKernelUpdateMultiplier = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 10,
                Value = 2,
                Location = new Point(180, yPos),
                Size = new Size(60, 23)
            };
            tabScan.Controls.Add(nudKernelUpdateMultiplier);
            yPos += 30;

            var lblCacheLifetime = new Label
            {
                Text = "Read cache lifetime (ms):",
                Location = new Point(20, yPos),
                Size = new Size(150, 20)
            };
            tabScan.Controls.Add(lblCacheLifetime);

            nudKernelCacheLifetime = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 500,
                Value = 50,
                Location = new Point(180, yPos),
                Size = new Size(60, 23)
            };
            tabScan.Controls.Add(nudKernelCacheLifetime);
            yPos += 35;

            // Memory Region Types
            var lblMemoryTypes = new Label
            {
                Text = "Memory Region Types to Scan",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(20, yPos),
                Size = new Size(250, 20)
            };
            tabScan.Controls.Add(lblMemoryTypes);
            yPos += 30;

            chkMemPrivate = AddCheckBox(tabScan, "MEM_PRIVATE (private memory)", 20, ref yPos);
            chkMemImage = AddCheckBox(tabScan, "MEM_IMAGE (image section)", 20, ref yPos);
            chkMemMapped = AddCheckBox(tabScan, "MEM_MAPPED (mapped section)", 20, ref yPos);
            chkSkipNoCache = AddCheckBox(tabScan, "Skip PAGE_NOCACHE regions", 20, ref yPos);
            chkSkipWriteCombine = AddCheckBox(tabScan, "Skip PAGE_WRITECOMBINE regions", 20, ref yPos);
        }

        private void CreateHotkeysTab()
        {
            int yPos = 10;

            // Keypoll interval
            var lblKeypoll = new Label
            {
                Text = "Keypoll interval (milliseconds):",
                Location = new Point(20, yPos + 3),
                Size = new Size(180, 20)
            };
            tabHotkeys.Controls.Add(lblKeypoll);

            nudKeypollInterval = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 1000,
                Value = 100,
                Location = new Point(200, yPos),
                Size = new Size(80, 23)
            };
            tabHotkeys.Controls.Add(nudKeypollInterval);
            yPos += 30;

            // Delay between reactivating hotkeys
            var lblDelay = new Label
            {
                Text = "Delay between reactivating hotkeys:",
                Location = new Point(20, yPos + 3),
                Size = new Size(200, 20)
            };
            tabHotkeys.Controls.Add(lblDelay);

            nudDelayBetweenHotkeys = new NumericUpDown
            {
                Minimum = 50,
                Maximum = 2000,
                Value = 350,
                Location = new Point(220, yPos),
                Size = new Size(80, 23)
            };
            tabHotkeys.Controls.Add(nudDelayBetweenHotkeys);
            yPos += 35;

            // Functions label
            var lblFunctions = new Label
            {
                Text = "Functions",
                Location = new Point(20, yPos),
                Size = new Size(200, 20)
            };
            tabHotkeys.Controls.Add(lblFunctions);

            var lblHotkey = new Label
            {
                Text = "Hotkey",
                Location = new Point(380, yPos),
                Size = new Size(100, 20)
            };
            tabHotkeys.Controls.Add(lblHotkey);
            yPos += 20;

            // ListView for hotkey functions (like Cheat Engine)
            lvHotkeys = new ListView
            {
                Location = new Point(20, yPos),
                Size = new Size(500, 280),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HeaderStyle = ColumnHeaderStyle.None,
                MultiSelect = false
            };
            lvHotkeys.Columns.Add("Function", 340);
            lvHotkeys.Columns.Add("Hotkey", 140);

            // Add all the hotkey functions (matching Cheat Engine's list)
            AddHotkeyFunction("Attach to current foreground process", "AttachForeground");
            AddHotkeyFunction("Popup/Hide CrxMem", "PopupHide");
            AddHotkeyFunction("Pause the selected process", "PauseProcess");
            AddHotkeyFunction("Toggle the speedhack", "ToggleSpeedhack");
            AddHotkeyFunction("Speedhack speed 1", "SpeedhackSpeed1");
            AddHotkeyFunction("Speedhack speed 2", "SpeedhackSpeed2");
            AddHotkeyFunction("Speedhack speed 3", "SpeedhackSpeed3");
            AddHotkeyFunction("Speedhack speed 4", "SpeedhackSpeed4");
            AddHotkeyFunction("Speedhack speed 5", "SpeedhackSpeed5");
            AddHotkeyFunction("Speedhack speed +", "SpeedhackSpeedUp");
            AddHotkeyFunction("Speedhack speed -", "SpeedhackSpeedDown");
            AddHotkeyFunction("Change type to Binary", "ChangeTypeBinary");
            AddHotkeyFunction("Change type to Byte", "ChangeTypeByte");
            AddHotkeyFunction("Change type to 2 Bytes", "ChangeType2Bytes");
            AddHotkeyFunction("Change type to 4 Bytes", "ChangeType4Bytes");
            AddHotkeyFunction("Change type to 8 Bytes", "ChangeType8Bytes");
            AddHotkeyFunction("Change type to Float", "ChangeTypeFloat");
            AddHotkeyFunction("Change type to Double", "ChangeTypeDouble");
            AddHotkeyFunction("Change type to Text", "ChangeTypeText");
            AddHotkeyFunction("Change type to Array of byte", "ChangeTypeAOB");
            AddHotkeyFunction("New Scan", "NewScan");
            AddHotkeyFunction("New Scan-Exact Value", "NewScanExact");
            AddHotkeyFunction("New Scan-Unknown Initial Value", "NewScanUnknown");
            AddHotkeyFunction("Next Scan-Exact Value", "NextScanExact");
            AddHotkeyFunction("Next Scan-Increased Value", "NextScanIncreased", "(Alt+1)");
            AddHotkeyFunction("Next Scan-Decreased Value", "NextScanDecreased", "(Alt+2)");
            AddHotkeyFunction("Next Scan-Changed Value", "NextScanChanged");
            AddHotkeyFunction("Next Scan-Unchanged Value", "NextScanUnchanged", "(Alt+3)");
            AddHotkeyFunction("Toggle between first/last scan compare", "ToggleScanCompare");
            AddHotkeyFunction("Undo last scan", "UndoScan");
            AddHotkeyFunction("Cancel the current scan", "CancelScan");
            AddHotkeyFunction("Debug->Run", "DebugRun");

            // Handle hotkey assignment via key press
            lvHotkeys.KeyDown += LvHotkeys_KeyDown;

            tabHotkeys.Controls.Add(lvHotkeys);
            yPos += 290;

            // Clear button
            btnClearHotkey = new Button
            {
                Text = "Clear",
                Location = new Point(440, yPos),
                Size = new Size(80, 25)
            };
            btnClearHotkey.Click += BtnClearHotkey_Click;
            tabHotkeys.Controls.Add(btnClearHotkey);
        }

        private void AddHotkeyFunction(string functionName, string functionId, string defaultHotkey = "")
        {
            var item = new ListViewItem(functionName);
            item.SubItems.Add(defaultHotkey);
            item.Tag = functionId;
            lvHotkeys.Items.Add(item);
        }

        private void LvHotkeys_KeyDown(object? sender, KeyEventArgs e)
        {
            if (lvHotkeys.SelectedItems.Count == 0)
                return;

            var selectedItem = lvHotkeys.SelectedItems[0];
            e.Handled = true;
            e.SuppressKeyPress = true;

            // Clear on Delete/Backspace
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                selectedItem.SubItems[1].Text = "";
                selectedItem.SubItems[1].Tag = 0;
                return;
            }

            // Ignore modifier-only presses
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey ||
                e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                return;

            // Build hotkey string
            string hotkeyText = "";
            if (e.Control) hotkeyText += "Ctrl+";
            if (e.Shift) hotkeyText += "Shift+";
            if (e.Alt) hotkeyText += "Alt+";
            hotkeyText += GetKeyName(e.KeyCode);

            selectedItem.SubItems[1].Text = hotkeyText;
            selectedItem.SubItems[1].Tag = (int)e.KeyData;
        }

        private void BtnClearHotkey_Click(object? sender, EventArgs e)
        {
            if (lvHotkeys.SelectedItems.Count == 0)
            {
                MessageBox.Show("Please select a function to clear its hotkey.", "No Selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedItem = lvHotkeys.SelectedItems[0];
            selectedItem.SubItems[1].Text = "";
            selectedItem.SubItems[1].Tag = 0;
        }

        private string KeyCodeToString(int keyData)
        {
            if (keyData == 0) return "";

            var keys = (Keys)keyData;
            string result = "";

            if ((keys & Keys.Control) == Keys.Control) result += "Ctrl+";
            if ((keys & Keys.Shift) == Keys.Shift) result += "Shift+";
            if ((keys & Keys.Alt) == Keys.Alt) result += "Alt+";

            // Get the key without modifiers
            var keyCode = keys & Keys.KeyCode;
            result += GetKeyName(keyCode);

            return result;
        }

        private string GetKeyName(Keys keyCode)
        {
            // Handle number keys (D0-D9) - these display as "D0", "D1", etc. by default
            if (keyCode >= Keys.D0 && keyCode <= Keys.D9)
                return ((int)keyCode - (int)Keys.D0).ToString();

            // Handle numpad keys (NumPad0-NumPad9)
            if (keyCode >= Keys.NumPad0 && keyCode <= Keys.NumPad9)
                return "Num" + ((int)keyCode - (int)Keys.NumPad0).ToString();

            // Handle function keys (F1-F24)
            if (keyCode >= Keys.F1 && keyCode <= Keys.F24)
                return "F" + ((int)keyCode - (int)Keys.F1 + 1).ToString();

            // Handle other special keys for cleaner display
            return keyCode switch
            {
                Keys.OemMinus => "-",
                Keys.Oemplus => "+",
                Keys.OemOpenBrackets => "[",
                Keys.OemCloseBrackets => "]",
                Keys.OemPipe => "\\",
                Keys.OemSemicolon => ";",
                Keys.OemQuotes => "'",
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.OemQuestion => "/",
                Keys.Oemtilde => "`",
                Keys.Space => "Space",
                Keys.Return => "Enter",
                Keys.Escape => "Esc",
                Keys.Back => "Backspace",
                Keys.Tab => "Tab",
                Keys.Insert => "Insert",
                Keys.Delete => "Delete",
                Keys.Home => "Home",
                Keys.End => "End",
                Keys.PageUp => "PageUp",
                Keys.PageDown => "PageDown",
                Keys.Up => "Up",
                Keys.Down => "Down",
                Keys.Left => "Left",
                Keys.Right => "Right",
                Keys.Multiply => "Num*",
                Keys.Add => "Num+",
                Keys.Subtract => "Num-",
                Keys.Divide => "Num/",
                Keys.Decimal => "Num.",
                _ => keyCode.ToString()
            };
        }

        private CheckBox AddCheckBox(Control parent, string text, int x, ref int y)
        {
            var checkbox = new CheckBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(400, 24),
                AutoSize = false
            };
            parent.Controls.Add(checkbox);
            y += 30;
            return checkbox;
        }

        private void AddNumericUpDownWithLabel(Control parent, string labelText, ref NumericUpDown control, int x, ref int y, int min, int max, int defaultValue)
        {
            var label = new Label
            {
                Text = labelText,
                Location = new Point(x, y),
                Size = new Size(200, 20)
            };
            parent.Controls.Add(label);

            control = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = defaultValue,
                Location = new Point(x + 210, y),
                Size = new Size(100, 23)
            };
            parent.Controls.Add(control);
            y += 35;
        }

        private void LoadSettings()
        {
            // General Settings
            nudUpdateInterval.Value = SettingsManager.UpdateInterval;
            nudFreezeInterval.Value = SettingsManager.FreezeInterval;
            nudFoundListInterval.Value = SettingsManager.FoundListUpdateInterval;
            chkShowSigned.Checked = SettingsManager.ShowValuesAsSigned;
            chkSimpleCopyPaste.Checked = SettingsManager.SimpleCopyPaste;
            chkSaveWindowPos.Checked = SettingsManager.SaveWindowPositions;
            txtAutoAttachProcess.Text = SettingsManager.AutoAttachProcess;
            chkAlwaysAutoAttach.Checked = SettingsManager.AlwaysAutoAttach;
            chkFastAutoAttach.Checked = SettingsManager.FastAutoAttach;
            chkUseWmiWatch.Checked = SettingsManager.UseWmiProcessWatch;
            nudFastAttachInterval.Value = SettingsManager.FastAutoAttachInterval;
            chkAutoLoadDriver.Checked = SettingsManager.AutoLoadDriver;
            chkAutoInjectVEH.Checked = SettingsManager.AutoInjectVEH;
            chkFastSuspendOnAttach.Checked = SettingsManager.FastSuspendOnAttach;
            nudSuspendInterval.Value = SettingsManager.SuspendInterval;
            chkAskClearList.Checked = SettingsManager.AskToClearListOnNewProcess;
            chkLaunchAsAdmin.Checked = SettingsManager.AlwaysLaunchAsAdmin;
            chkDarkMode.Checked = SettingsManager.DarkMode;

            // Scan Settings
            cmbThreadPriority.SelectedIndex = SettingsManager.ScanThreadPriority;
            nudBufferSize.Value = SettingsManager.ScanBufferSize;
            chkFastScanDefault.Checked = SettingsManager.FastScanByDefault;
            chkPauseScanning.Checked = SettingsManager.PauseWhileScanning;
            chkMemPrivate.Checked = SettingsManager.ScanMemPrivate;
            chkMemImage.Checked = SettingsManager.ScanMemImage;
            chkMemMapped.Checked = SettingsManager.ScanMemMapped;
            chkSkipNoCache.Checked = SettingsManager.SkipPageNoCache;
            chkSkipWriteCombine.Checked = SettingsManager.SkipPageWriteCombine;
            nudKernelUpdateMultiplier.Value = SettingsManager.KernelModeUpdateMultiplier;
            nudKernelCacheLifetime.Value = SettingsManager.KernelCacheLifetimeMs;

            // Hotkey Settings - load from dictionary-based storage
            LoadHotkeysFromSettings();
        }

        private void LoadHotkeysFromSettings()
        {
            // Load hotkeys from settings manager into ListView
            var hotkeys = SettingsManager.GetAllHotkeys();
            foreach (ListViewItem item in lvHotkeys.Items)
            {
                string functionId = item.Tag?.ToString() ?? "";
                if (hotkeys.TryGetValue(functionId, out int keyData) && keyData != 0)
                {
                    item.SubItems[1].Text = KeyCodeToString(keyData);
                    item.SubItems[1].Tag = keyData;
                }
            }
        }

        private void SaveSettings()
        {
            // General Settings
            SettingsManager.UpdateInterval = (int)nudUpdateInterval.Value;
            SettingsManager.FreezeInterval = (int)nudFreezeInterval.Value;
            SettingsManager.FoundListUpdateInterval = (int)nudFoundListInterval.Value;
            SettingsManager.ShowValuesAsSigned = chkShowSigned.Checked;
            SettingsManager.SimpleCopyPaste = chkSimpleCopyPaste.Checked;
            SettingsManager.SaveWindowPositions = chkSaveWindowPos.Checked;
            SettingsManager.AutoAttachProcess = txtAutoAttachProcess.Text;
            SettingsManager.AlwaysAutoAttach = chkAlwaysAutoAttach.Checked;
            SettingsManager.FastAutoAttach = chkFastAutoAttach.Checked;
            SettingsManager.UseWmiProcessWatch = chkUseWmiWatch.Checked;
            SettingsManager.FastAutoAttachInterval = (int)nudFastAttachInterval.Value;
            SettingsManager.AutoLoadDriver = chkAutoLoadDriver.Checked;
            SettingsManager.AutoInjectVEH = chkAutoInjectVEH.Checked;
            SettingsManager.FastSuspendOnAttach = chkFastSuspendOnAttach.Checked;
            SettingsManager.SuspendInterval = (int)nudSuspendInterval.Value;
            SettingsManager.AskToClearListOnNewProcess = chkAskClearList.Checked;
            SettingsManager.AlwaysLaunchAsAdmin = chkLaunchAsAdmin.Checked;
            SettingsManager.DarkMode = chkDarkMode.Checked;

            // Scan Settings
            SettingsManager.ScanThreadPriority = cmbThreadPriority.SelectedIndex;
            SettingsManager.ScanBufferSize = (int)nudBufferSize.Value;
            SettingsManager.FastScanByDefault = chkFastScanDefault.Checked;
            SettingsManager.PauseWhileScanning = chkPauseScanning.Checked;
            SettingsManager.ScanMemPrivate = chkMemPrivate.Checked;
            SettingsManager.ScanMemImage = chkMemImage.Checked;
            SettingsManager.ScanMemMapped = chkMemMapped.Checked;
            SettingsManager.SkipPageNoCache = chkSkipNoCache.Checked;
            SettingsManager.SkipPageWriteCombine = chkSkipWriteCombine.Checked;
            SettingsManager.KernelModeUpdateMultiplier = (int)nudKernelUpdateMultiplier.Value;
            SettingsManager.KernelCacheLifetimeMs = (int)nudKernelCacheLifetime.Value;

            // Hotkey Settings - save all hotkeys from ListView
            SaveHotkeysToSettings();
        }

        private void SaveHotkeysToSettings()
        {
            var hotkeys = new System.Collections.Generic.Dictionary<string, int>();
            foreach (ListViewItem item in lvHotkeys.Items)
            {
                string functionId = item.Tag?.ToString() ?? "";
                int keyData = 0;
                if (item.SubItems[1].Tag is int kd)
                    keyData = kd;
                hotkeys[functionId] = keyData;
            }
            SettingsManager.SetAllHotkeys(hotkeys);
        }

        private void BtnOK_Click(object? sender, EventArgs e)
        {
            SaveSettings();
            SettingsApplied?.Invoke(this, EventArgs.Empty);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            SaveSettings();
            SettingsApplied?.Invoke(this, EventArgs.Empty);
            MessageBox.Show("Settings applied successfully!", "Success",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
