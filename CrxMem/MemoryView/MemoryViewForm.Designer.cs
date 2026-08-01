namespace CrxMem.MemoryView
{
    partial class MemoryViewForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            // Menu Strip
            menuStrip = new System.Windows.Forms.MenuStrip();

            // File Menu
            fileMenu = new System.Windows.Forms.ToolStripMenuItem();
            fileOpenMenu = new System.Windows.Forms.ToolStripMenuItem();
            fileSaveMenu = new System.Windows.Forms.ToolStripMenuItem();
            fileExportMenu = new System.Windows.Forms.ToolStripMenuItem();
            fileSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            fileExitMenu = new System.Windows.Forms.ToolStripMenuItem();

            // View Menu
            viewMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewCpuMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewRegistersMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewStackMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewHexDumpMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            viewModulesMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewMemoryMapMenu = new System.Windows.Forms.ToolStripMenuItem();
            viewThreadsMenu = new System.Windows.Forms.ToolStripMenuItem();

            // Debug Menu
            debugMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugRunMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugPauseMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugStopMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            debugStepIntoMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugStepOverMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugStepOutMenu = new System.Windows.Forms.ToolStripMenuItem();
            debugSeparator2 = new System.Windows.Forms.ToolStripSeparator();
            debugBreakpointMenu = new System.Windows.Forms.ToolStripMenuItem();

            // Search Menu
            searchMenu = new System.Windows.Forms.ToolStripMenuItem();
            searchGotoMenu = new System.Windows.Forms.ToolStripMenuItem();
            searchFindMenu = new System.Windows.Forms.ToolStripMenuItem();
            searchFindReferencesMenu = new System.Windows.Forms.ToolStripMenuItem();
            searchFindPatternMenu = new System.Windows.Forms.ToolStripMenuItem();

            // Options Menu
            optionsMenu = new System.Windows.Forms.ToolStripMenuItem();
            optionsPreferencesMenu = new System.Windows.Forms.ToolStripMenuItem();

            // Help Menu
            helpMenu = new System.Windows.Forms.ToolStripMenuItem();
            helpAboutMenu = new System.Windows.Forms.ToolStripMenuItem();

            // Main Toolbar
            mainToolStrip = new System.Windows.Forms.ToolStrip();
            btnOpen = new System.Windows.Forms.ToolStripButton();
            btnSave = new System.Windows.Forms.ToolStripButton();
            toolSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            btnRun = new System.Windows.Forms.ToolStripButton();
            btnPause = new System.Windows.Forms.ToolStripButton();
            btnStop = new System.Windows.Forms.ToolStripButton();
            toolSeparator2 = new System.Windows.Forms.ToolStripSeparator();
            btnStepInto = new System.Windows.Forms.ToolStripButton();
            btnStepOver = new System.Windows.Forms.ToolStripButton();
            btnStepOut = new System.Windows.Forms.ToolStripButton();
            toolSeparator3 = new System.Windows.Forms.ToolStripSeparator();
            btnBreakpoint = new System.Windows.Forms.ToolStripButton();
            toolSeparator4 = new System.Windows.Forms.ToolStripSeparator();
            txtGotoAddress = new System.Windows.Forms.ToolStripTextBox();
            btnGoto = new System.Windows.Forms.ToolStripButton();
            toolSeparator5 = new System.Windows.Forms.ToolStripSeparator();
            lblCurrentAddress = new System.Windows.Forms.ToolStripLabel();

            // Status Strip
            statusStrip1 = new System.Windows.Forms.StatusStrip();
            lblStatusProtection = new System.Windows.Forms.ToolStripStatusLabel();
            lblStatusAllocationBase = new System.Windows.Forms.ToolStripStatusLabel();
            lblStatusSize = new System.Windows.Forms.ToolStripStatusLabel();
            lblStatusModule = new System.Windows.Forms.ToolStripStatusLabel();
            lblStatusInfo = new System.Windows.Forms.ToolStripStatusLabel();
            lblStatusDriver = new System.Windows.Forms.ToolStripStatusLabel();

            // Main Split Container (horizontal: left=disasm+hex, right=registers)
            mainSplitContainer = new System.Windows.Forms.SplitContainer();

            // Left Split Container (vertical: top=disasm, bottom=hex)
            leftSplitContainer = new System.Windows.Forms.SplitContainer();

            // Panels
            panelDisassembler = new System.Windows.Forms.Panel();
            lblDisassemblerPlaceholder = new System.Windows.Forms.Label();
            panelHexView = new System.Windows.Forms.Panel();
            panelRegisters = new System.Windows.Forms.Panel();
            lblRegistersTitle = new System.Windows.Forms.Label();

            // Tab Control for bottom panels (Hex Dump, Stack, etc.)
            bottomTabControl = new System.Windows.Forms.TabControl();
            tabHexDump = new System.Windows.Forms.TabPage();
            tabStack = new System.Windows.Forms.TabPage();
            tabWatch = new System.Windows.Forms.TabPage();

            // Top TabControl (Memory View | Bookmarks) - x64dbg style
            topTabControl = new System.Windows.Forms.TabControl();
            tabMemoryView = new System.Windows.Forms.TabPage();
            tabBookmarks = new System.Windows.Forms.TabPage();
            lvBookmarks = new System.Windows.Forms.ListView();

            // Begin initialization
            menuStrip.SuspendLayout();
            mainToolStrip.SuspendLayout();
            statusStrip1.SuspendLayout();
            topTabControl.SuspendLayout();
            tabMemoryView.SuspendLayout();
            tabBookmarks.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)mainSplitContainer).BeginInit();
            mainSplitContainer.Panel1.SuspendLayout();
            mainSplitContainer.Panel2.SuspendLayout();
            mainSplitContainer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)leftSplitContainer).BeginInit();
            leftSplitContainer.Panel1.SuspendLayout();
            leftSplitContainer.Panel2.SuspendLayout();
            leftSplitContainer.SuspendLayout();
            panelDisassembler.SuspendLayout();
            panelRegisters.SuspendLayout();
            bottomTabControl.SuspendLayout();
            SuspendLayout();

            //
            // menuStrip
            //
            menuStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                fileMenu, viewMenu, debugMenu, searchMenu, optionsMenu, helpMenu
            });
            menuStrip.Location = new System.Drawing.Point(0, 0);
            menuStrip.Name = "menuStrip";
            menuStrip.Size = new System.Drawing.Size(1200, 24);
            menuStrip.TabIndex = 0;

            //
            // fileMenu
            //
            fileMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                fileOpenMenu, fileSaveMenu, fileExportMenu, fileSeparator1, fileExitMenu
            });
            fileMenu.Name = "fileMenu";
            fileMenu.Size = new System.Drawing.Size(37, 20);
            fileMenu.Text = "&File";

            fileOpenMenu.Name = "fileOpenMenu";
            fileOpenMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.O;
            fileOpenMenu.Text = "&Open...";
            fileOpenMenu.Click += FileOpen_Click;

            fileSaveMenu.Name = "fileSaveMenu";
            fileSaveMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.S;
            fileSaveMenu.Text = "&Save...";
            fileSaveMenu.Click += FileSave_Click;

            fileExportMenu.Name = "fileExportMenu";
            fileExportMenu.Text = "&Export Disassembly...";
            fileExportMenu.Click += FileExport_Click;

            fileSeparator1.Name = "fileSeparator1";

            fileExitMenu.Name = "fileExitMenu";
            fileExitMenu.ShortcutKeys = System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.F4;
            fileExitMenu.Text = "E&xit";
            fileExitMenu.Click += FileExit_Click;

            //
            // viewMenu
            //
            viewMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                viewCpuMenu, viewRegistersMenu, viewStackMenu, viewHexDumpMenu,
                viewSeparator1, viewModulesMenu, viewMemoryMapMenu, viewThreadsMenu
            });
            viewMenu.Name = "viewMenu";
            viewMenu.Size = new System.Drawing.Size(44, 20);
            viewMenu.Text = "&View";

            viewCpuMenu.Name = "viewCpuMenu";
            viewCpuMenu.Text = "&CPU";
            viewCpuMenu.Checked = true;
            viewCpuMenu.CheckOnClick = true;

            viewRegistersMenu.Name = "viewRegistersMenu";
            viewRegistersMenu.Text = "&Registers";
            viewRegistersMenu.Checked = true;
            viewRegistersMenu.CheckOnClick = true;
            viewRegistersMenu.Click += ViewRegisters_Click;

            viewStackMenu.Name = "viewStackMenu";
            viewStackMenu.Text = "&Stack";
            viewStackMenu.Checked = true;
            viewStackMenu.CheckOnClick = true;

            viewHexDumpMenu.Name = "viewHexDumpMenu";
            viewHexDumpMenu.Text = "&Hex Dump";
            viewHexDumpMenu.Checked = true;
            viewHexDumpMenu.CheckOnClick = true;

            viewSeparator1.Name = "viewSeparator1";

            viewModulesMenu.Name = "viewModulesMenu";
            viewModulesMenu.Text = "&Modules";
            viewModulesMenu.Click += ViewModules_Click;

            viewMemoryMapMenu.Name = "viewMemoryMapMenu";
            viewMemoryMapMenu.Text = "Memory &Map";
            viewMemoryMapMenu.Click += ViewMemoryMap_Click;

            viewThreadsMenu.Name = "viewThreadsMenu";
            viewThreadsMenu.Text = "&Threads";
            viewThreadsMenu.Click += ViewThreads_Click;

            //
            // debugMenu
            //
            debugMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                debugRunMenu, debugPauseMenu, debugStopMenu, debugSeparator1,
                debugStepIntoMenu, debugStepOverMenu, debugStepOutMenu, debugSeparator2,
                debugBreakpointMenu
            });
            debugMenu.Name = "debugMenu";
            debugMenu.Size = new System.Drawing.Size(54, 20);
            debugMenu.Text = "&Debug";

            debugRunMenu.Name = "debugRunMenu";
            debugRunMenu.ShortcutKeys = System.Windows.Forms.Keys.F9;
            debugRunMenu.Text = "&Run";
            debugRunMenu.Click += DebugRun_Click;

            debugPauseMenu.Name = "debugPauseMenu";
            debugPauseMenu.ShortcutKeys = System.Windows.Forms.Keys.F12;
            debugPauseMenu.Text = "&Pause";
            debugPauseMenu.Click += DebugPause_Click;

            debugStopMenu.Name = "debugStopMenu";
            debugStopMenu.Text = "&Stop";
            debugStopMenu.Click += DebugStop_Click;

            debugSeparator1.Name = "debugSeparator1";

            debugStepIntoMenu.Name = "debugStepIntoMenu";
            debugStepIntoMenu.ShortcutKeys = System.Windows.Forms.Keys.F7;
            debugStepIntoMenu.Text = "Step &Into";
            debugStepIntoMenu.Click += DebugStepInto_Click;

            debugStepOverMenu.Name = "debugStepOverMenu";
            debugStepOverMenu.ShortcutKeys = System.Windows.Forms.Keys.F8;
            debugStepOverMenu.Text = "Step &Over";
            debugStepOverMenu.Click += DebugStepOver_Click;

            debugStepOutMenu.Name = "debugStepOutMenu";
            debugStepOutMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F9;
            debugStepOutMenu.Text = "Step O&ut";
            debugStepOutMenu.Click += DebugStepOut_Click;

            debugSeparator2.Name = "debugSeparator2";

            debugBreakpointMenu.Name = "debugBreakpointMenu";
            debugBreakpointMenu.ShortcutKeys = System.Windows.Forms.Keys.F2;
            debugBreakpointMenu.Text = "Toggle &Breakpoint";
            debugBreakpointMenu.Click += DebugBreakpoint_Click;

            //
            // searchMenu
            //
            searchMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                searchGotoMenu, searchFindMenu, searchFindReferencesMenu, searchFindPatternMenu
            });
            searchMenu.Name = "searchMenu";
            searchMenu.Size = new System.Drawing.Size(54, 20);
            searchMenu.Text = "&Search";

            searchGotoMenu.Name = "searchGotoMenu";
            searchGotoMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.G;
            searchGotoMenu.Text = "&Go to Address...";
            searchGotoMenu.Click += SearchGoto_Click;

            searchFindMenu.Name = "searchFindMenu";
            searchFindMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.F;
            searchFindMenu.Text = "&Find...";
            searchFindMenu.Click += SearchFind_Click;

            searchFindReferencesMenu.Name = "searchFindReferencesMenu";
            searchFindReferencesMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.R;
            searchFindReferencesMenu.Text = "Find &References...";
            searchFindReferencesMenu.Click += SearchFindReferences_Click;

            searchFindPatternMenu.Name = "searchFindPatternMenu";
            searchFindPatternMenu.ShortcutKeys = System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.B;
            searchFindPatternMenu.Text = "Find &Pattern (AOB)...";
            searchFindPatternMenu.Click += SearchFindPattern_Click;

            //
            // optionsMenu
            //
            optionsMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                optionsPreferencesMenu
            });
            optionsMenu.Name = "optionsMenu";
            optionsMenu.Size = new System.Drawing.Size(61, 20);
            optionsMenu.Text = "&Options";

            optionsPreferencesMenu.Name = "optionsPreferencesMenu";
            optionsPreferencesMenu.Text = "&Preferences...";
            optionsPreferencesMenu.Click += OptionsPreferences_Click;

            //
            // helpMenu
            //
            helpMenu.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
                helpAboutMenu
            });
            helpMenu.Name = "helpMenu";
            helpMenu.Size = new System.Drawing.Size(44, 20);
            helpMenu.Text = "&Help";

            helpAboutMenu.Name = "helpAboutMenu";
            helpAboutMenu.Text = "&About";
            helpAboutMenu.Click += HelpAbout_Click;

            //
            // mainToolStrip
            //
            mainToolStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                btnOpen, btnSave, toolSeparator1,
                btnRun, btnPause, btnStop, toolSeparator2,
                btnStepInto, btnStepOver, btnStepOut, toolSeparator3,
                btnBreakpoint, toolSeparator4,
                txtGotoAddress, btnGoto, toolSeparator5, lblCurrentAddress
            });
            mainToolStrip.Location = new System.Drawing.Point(0, 24);
            mainToolStrip.Name = "mainToolStrip";
            mainToolStrip.Size = new System.Drawing.Size(1200, 25);
            mainToolStrip.TabIndex = 1;

            // Toolbar buttons
            btnOpen.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnOpen.Name = "btnOpen";
            btnOpen.Text = "Open";
            btnOpen.ToolTipText = "Open file (Ctrl+O)";
            btnOpen.Click += FileOpen_Click;

            btnSave.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnSave.Name = "btnSave";
            btnSave.Text = "Save";
            btnSave.ToolTipText = "Save (Ctrl+S)";
            btnSave.Click += FileSave_Click;

            toolSeparator1.Name = "toolSeparator1";

            btnRun.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnRun.Name = "btnRun";
            btnRun.Text = "▶ Run";
            btnRun.ToolTipText = "Run (F9)";
            btnRun.Click += DebugRun_Click;

            btnPause.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnPause.Name = "btnPause";
            btnPause.Text = "⏸ Pause";
            btnPause.ToolTipText = "Pause (F12)";
            btnPause.Click += DebugPause_Click;

            btnStop.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnStop.Name = "btnStop";
            btnStop.Text = "⏹ Stop";
            btnStop.ToolTipText = "Stop debugging";
            btnStop.Click += DebugStop_Click;

            toolSeparator2.Name = "toolSeparator2";

            btnStepInto.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnStepInto.Name = "btnStepInto";
            btnStepInto.Text = "↓ Into";
            btnStepInto.ToolTipText = "Step Into (F7)";
            btnStepInto.Click += DebugStepInto_Click;

            btnStepOver.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnStepOver.Name = "btnStepOver";
            btnStepOver.Text = "→ Over";
            btnStepOver.ToolTipText = "Step Over (F8)";
            btnStepOver.Click += DebugStepOver_Click;

            btnStepOut.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnStepOut.Name = "btnStepOut";
            btnStepOut.Text = "↑ Out";
            btnStepOut.ToolTipText = "Step Out (Ctrl+F9)";
            btnStepOut.Click += DebugStepOut_Click;

            toolSeparator3.Name = "toolSeparator3";

            btnBreakpoint.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnBreakpoint.Name = "btnBreakpoint";
            btnBreakpoint.Text = "● BP";
            btnBreakpoint.ForeColor = System.Drawing.Color.Red;
            btnBreakpoint.ToolTipText = "Toggle Breakpoint (F2)";
            btnBreakpoint.Click += DebugBreakpoint_Click;

            toolSeparator4.Name = "toolSeparator4";

            txtGotoAddress.Name = "txtGotoAddress";
            txtGotoAddress.Size = new System.Drawing.Size(150, 25);
            txtGotoAddress.ToolTipText = "Enter address (hex) - Ctrl+G";
            txtGotoAddress.KeyPress += TxtGotoAddress_KeyPress;

            btnGoto.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            btnGoto.Name = "btnGoto";
            btnGoto.Text = "Go";
            btnGoto.Click += BtnGoto_Click;

            toolSeparator5.Name = "toolSeparator5";

            lblCurrentAddress.Name = "lblCurrentAddress";
            lblCurrentAddress.Size = new System.Drawing.Size(120, 22);
            lblCurrentAddress.Text = "Address: 0000000000000000";

            //
            // statusStrip1
            //
            statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
                lblStatusProtection, lblStatusAllocationBase, lblStatusSize, lblStatusModule, lblStatusInfo, lblStatusDriver
            });
            statusStrip1.Location = new System.Drawing.Point(0, 678);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new System.Drawing.Size(1200, 22);
            statusStrip1.TabIndex = 2;

            lblStatusProtection.Name = "lblStatusProtection";
            lblStatusProtection.Size = new System.Drawing.Size(90, 17);
            lblStatusProtection.Text = "Protection: ---";
            lblStatusProtection.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right;

            lblStatusAllocationBase.Name = "lblStatusAllocationBase";
            lblStatusAllocationBase.Size = new System.Drawing.Size(140, 17);
            lblStatusAllocationBase.Text = "Base: ----------------";
            lblStatusAllocationBase.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right;

            lblStatusSize.Name = "lblStatusSize";
            lblStatusSize.Size = new System.Drawing.Size(100, 17);
            lblStatusSize.Text = "Size: --------";
            lblStatusSize.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right;

            lblStatusModule.Name = "lblStatusModule";
            lblStatusModule.Size = new System.Drawing.Size(150, 17);
            lblStatusModule.Text = "Module: ------------";
            lblStatusModule.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Right;

            lblStatusInfo.Name = "lblStatusInfo";
            lblStatusInfo.Size = new System.Drawing.Size(200, 17);
            lblStatusInfo.Text = "Ready";
            lblStatusInfo.Spring = true;
            lblStatusInfo.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            lblStatusDriver.Name = "lblStatusDriver";
            lblStatusDriver.Size = new System.Drawing.Size(120, 17);
            lblStatusDriver.Text = "● Driver: user-mode";
            lblStatusDriver.ForeColor = System.Drawing.Color.Gray;
            lblStatusDriver.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left;

            //
            // mainSplitContainer (horizontal split: left panels | right registers)
            //
            mainSplitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            mainSplitContainer.Location = new System.Drawing.Point(0, 0);
            mainSplitContainer.Name = "mainSplitContainer";
            mainSplitContainer.Orientation = System.Windows.Forms.Orientation.Vertical;
            //
            // mainSplitContainer.Panel1 (Left - disasm + hex)
            //
            mainSplitContainer.Panel1.Controls.Add(leftSplitContainer);
            //
            // mainSplitContainer.Panel2 (Right - registers)
            //
            mainSplitContainer.Panel2.Controls.Add(panelRegisters);
            mainSplitContainer.Size = new System.Drawing.Size(1200, 629);
            mainSplitContainer.SplitterDistance = 900;
            mainSplitContainer.TabIndex = 3;

            //
            // leftSplitContainer (vertical split: top disasm | bottom tabs)
            //
            leftSplitContainer.Dock = System.Windows.Forms.DockStyle.Fill;
            leftSplitContainer.Location = new System.Drawing.Point(0, 0);
            leftSplitContainer.Name = "leftSplitContainer";
            leftSplitContainer.Orientation = System.Windows.Forms.Orientation.Horizontal;
            //
            // leftSplitContainer.Panel1 (Disassembler)
            //
            leftSplitContainer.Panel1.Controls.Add(panelDisassembler);
            //
            // leftSplitContainer.Panel2 (Bottom tabs - Hex, Stack, Watch)
            //
            leftSplitContainer.Panel2.Controls.Add(bottomTabControl);
            leftSplitContainer.Size = new System.Drawing.Size(900, 629);
            leftSplitContainer.SplitterDistance = 380;
            leftSplitContainer.TabIndex = 0;

            //
            // panelDisassembler
            //
            panelDisassembler.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            panelDisassembler.Controls.Add(lblDisassemblerPlaceholder);
            panelDisassembler.Dock = System.Windows.Forms.DockStyle.Fill;
            panelDisassembler.Location = new System.Drawing.Point(0, 0);
            panelDisassembler.Name = "panelDisassembler";
            panelDisassembler.Size = new System.Drawing.Size(900, 380);
            panelDisassembler.TabIndex = 0;

            //
            // lblDisassemblerPlaceholder
            //
            lblDisassemblerPlaceholder.Dock = System.Windows.Forms.DockStyle.Fill;
            lblDisassemblerPlaceholder.Font = new System.Drawing.Font("Consolas", 10F);
            lblDisassemblerPlaceholder.ForeColor = System.Drawing.Color.Gray;
            lblDisassemblerPlaceholder.Location = new System.Drawing.Point(0, 0);
            lblDisassemblerPlaceholder.Name = "lblDisassemblerPlaceholder";
            lblDisassemblerPlaceholder.Size = new System.Drawing.Size(900, 380);
            lblDisassemblerPlaceholder.TabIndex = 0;
            lblDisassemblerPlaceholder.Text = "CPU - Disassembly View";
            lblDisassemblerPlaceholder.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;

            //
            // bottomTabControl
            //
            bottomTabControl.Controls.Add(tabHexDump);
            bottomTabControl.Controls.Add(tabStack);
            bottomTabControl.Controls.Add(tabWatch);
            bottomTabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            bottomTabControl.Location = new System.Drawing.Point(0, 0);
            bottomTabControl.Name = "bottomTabControl";
            bottomTabControl.SelectedIndex = 0;
            bottomTabControl.Size = new System.Drawing.Size(900, 245);
            bottomTabControl.TabIndex = 0;

            //
            // tabHexDump
            //
            tabHexDump.Location = new System.Drawing.Point(4, 24);
            tabHexDump.Name = "tabHexDump";
            tabHexDump.Padding = new System.Windows.Forms.Padding(0);
            tabHexDump.Size = new System.Drawing.Size(892, 217);
            tabHexDump.TabIndex = 0;
            tabHexDump.Text = "Hex Dump";
            tabHexDump.UseVisualStyleBackColor = true;

            //
            // tabStack
            //
            tabStack.Location = new System.Drawing.Point(4, 24);
            tabStack.Name = "tabStack";
            tabStack.Size = new System.Drawing.Size(892, 217);
            tabStack.TabIndex = 1;
            tabStack.Text = "Stack";
            tabStack.UseVisualStyleBackColor = true;

            //
            // tabWatch
            //
            tabWatch.Location = new System.Drawing.Point(4, 24);
            tabWatch.Name = "tabWatch";
            tabWatch.Size = new System.Drawing.Size(892, 217);
            tabWatch.TabIndex = 2;
            tabWatch.Text = "Watch";
            tabWatch.UseVisualStyleBackColor = true;

            //
            // topTabControl (x64dbg-style top tabs)
            //
            topTabControl.Controls.Add(tabMemoryView);
            topTabControl.Controls.Add(tabBookmarks);
            topTabControl.Dock = System.Windows.Forms.DockStyle.Fill;
            topTabControl.Location = new System.Drawing.Point(0, 49);
            topTabControl.Name = "topTabControl";
            topTabControl.SelectedIndex = 0;
            topTabControl.Size = new System.Drawing.Size(1200, 629);
            topTabControl.TabIndex = 10;

            //
            // tabMemoryView
            //
            tabMemoryView.Controls.Add(mainSplitContainer);
            tabMemoryView.Location = new System.Drawing.Point(4, 24);
            tabMemoryView.Name = "tabMemoryView";
            tabMemoryView.Padding = new System.Windows.Forms.Padding(0);
            tabMemoryView.Size = new System.Drawing.Size(1192, 601);
            tabMemoryView.TabIndex = 0;
            tabMemoryView.Text = "Memory View";
            tabMemoryView.UseVisualStyleBackColor = true;

            //
            // tabBookmarks
            //
            tabBookmarks.Controls.Add(lvBookmarks);
            tabBookmarks.Location = new System.Drawing.Point(4, 24);
            tabBookmarks.Name = "tabBookmarks";
            tabBookmarks.Padding = new System.Windows.Forms.Padding(3);
            tabBookmarks.Size = new System.Drawing.Size(1192, 601);
            tabBookmarks.TabIndex = 1;
            tabBookmarks.Text = "Bookmarks";
            tabBookmarks.UseVisualStyleBackColor = true;

            //
            // lvBookmarks
            //
            lvBookmarks.Dock = System.Windows.Forms.DockStyle.Fill;
            lvBookmarks.FullRowSelect = true;
            lvBookmarks.GridLines = false;
            lvBookmarks.Location = new System.Drawing.Point(3, 3);
            lvBookmarks.Name = "lvBookmarks";
            lvBookmarks.Size = new System.Drawing.Size(1186, 595);
            lvBookmarks.TabIndex = 0;
            lvBookmarks.UseCompatibleStateImageBehavior = false;
            lvBookmarks.View = System.Windows.Forms.View.Details;

            //
            // panelRegisters
            //
            panelRegisters.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            panelRegisters.Controls.Add(lblRegistersTitle);
            panelRegisters.Dock = System.Windows.Forms.DockStyle.Fill;
            panelRegisters.Location = new System.Drawing.Point(0, 0);
            panelRegisters.Name = "panelRegisters";
            panelRegisters.Size = new System.Drawing.Size(296, 629);
            panelRegisters.TabIndex = 0;

            //
            // lblRegistersTitle
            //
            lblRegistersTitle.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            lblRegistersTitle.Dock = System.Windows.Forms.DockStyle.Top;
            lblRegistersTitle.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            lblRegistersTitle.ForeColor = System.Drawing.Color.White;
            lblRegistersTitle.Location = new System.Drawing.Point(0, 0);
            lblRegistersTitle.Name = "lblRegistersTitle";
            lblRegistersTitle.Padding = new System.Windows.Forms.Padding(5, 2, 0, 2);
            lblRegistersTitle.Size = new System.Drawing.Size(296, 22);
            lblRegistersTitle.TabIndex = 0;
            lblRegistersTitle.Text = "Registers";

            //
            // panelHexInfo
            //
            panelHexInfo = new System.Windows.Forms.Panel();
            panelHexInfo.Dock = System.Windows.Forms.DockStyle.Top;
            panelHexInfo.Height = 25;
            panelHexInfo.BackColor = System.Drawing.Color.FromArgb(45, 45, 48);
            
            lblHexInfo = new System.Windows.Forms.Label();
            lblHexInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            lblHexInfo.ForeColor = System.Drawing.Color.White;
            lblHexInfo.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            lblHexInfo.Padding = new System.Windows.Forms.Padding(5, 0, 0, 0);
            lblHexInfo.Text = "Address: 00000000 | Protection: --- | Module: ---";
            
            panelHexInfo.Controls.Add(lblHexInfo);
            tabHexDump.Controls.Add(panelHexInfo);

            //
            // panelHexView (will be added to tabHexDump)
            //
            panelHexView.BackColor = System.Drawing.Color.FromArgb(30, 30, 30);
            panelHexView.Dock = System.Windows.Forms.DockStyle.Fill;
            panelHexView.Location = new System.Drawing.Point(0, 25);
            panelHexView.Name = "panelHexView";
            panelHexView.Size = new System.Drawing.Size(892, 192);
            panelHexView.TabIndex = 0;

            // Add panelHexView to tabHexDump
            tabHexDump.Controls.Add(panelHexView);

            //
            // MemoryViewForm
            //
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(1200, 700);
            Controls.Add(topTabControl);
            Controls.Add(mainToolStrip);
            Controls.Add(menuStrip);
            Controls.Add(statusStrip1);
            MainMenuStrip = menuStrip;
            Name = "MemoryViewForm";
            Text = "Memory View - CrxMem";
            Load += MemoryViewForm_Load;

            menuStrip.ResumeLayout(false);
            menuStrip.PerformLayout();
            mainToolStrip.ResumeLayout(false);
            mainToolStrip.PerformLayout();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            mainSplitContainer.Panel1.ResumeLayout(false);
            mainSplitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)mainSplitContainer).EndInit();
            mainSplitContainer.ResumeLayout(false);
            leftSplitContainer.Panel1.ResumeLayout(false);
            leftSplitContainer.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)leftSplitContainer).EndInit();
            leftSplitContainer.ResumeLayout(false);
            panelDisassembler.ResumeLayout(false);
            panelRegisters.ResumeLayout(false);
            bottomTabControl.ResumeLayout(false);
            tabMemoryView.ResumeLayout(false);
            tabBookmarks.ResumeLayout(false);
            topTabControl.ResumeLayout(false);
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        // Menu Strip
        private System.Windows.Forms.MenuStrip menuStrip;
        private System.Windows.Forms.ToolStripMenuItem fileMenu;
        private System.Windows.Forms.ToolStripMenuItem fileOpenMenu;
        private System.Windows.Forms.ToolStripMenuItem fileSaveMenu;
        private System.Windows.Forms.ToolStripMenuItem fileExportMenu;
        private System.Windows.Forms.ToolStripSeparator fileSeparator1;
        private System.Windows.Forms.ToolStripMenuItem fileExitMenu;

        private System.Windows.Forms.ToolStripMenuItem viewMenu;
        private System.Windows.Forms.ToolStripMenuItem viewCpuMenu;
        private System.Windows.Forms.ToolStripMenuItem viewRegistersMenu;
        private System.Windows.Forms.ToolStripMenuItem viewStackMenu;
        private System.Windows.Forms.ToolStripMenuItem viewHexDumpMenu;
        private System.Windows.Forms.ToolStripSeparator viewSeparator1;
        private System.Windows.Forms.ToolStripMenuItem viewModulesMenu;
        private System.Windows.Forms.ToolStripMenuItem viewMemoryMapMenu;
        private System.Windows.Forms.ToolStripMenuItem viewThreadsMenu;

        private System.Windows.Forms.ToolStripMenuItem debugMenu;
        private System.Windows.Forms.ToolStripMenuItem debugRunMenu;
        private System.Windows.Forms.ToolStripMenuItem debugPauseMenu;
        private System.Windows.Forms.ToolStripMenuItem debugStopMenu;
        private System.Windows.Forms.ToolStripSeparator debugSeparator1;
        private System.Windows.Forms.ToolStripMenuItem debugStepIntoMenu;
        private System.Windows.Forms.ToolStripMenuItem debugStepOverMenu;
        private System.Windows.Forms.ToolStripMenuItem debugStepOutMenu;
        private System.Windows.Forms.ToolStripSeparator debugSeparator2;
        private System.Windows.Forms.ToolStripMenuItem debugBreakpointMenu;

        private System.Windows.Forms.ToolStripMenuItem searchMenu;
        private System.Windows.Forms.ToolStripMenuItem searchGotoMenu;
        private System.Windows.Forms.ToolStripMenuItem searchFindMenu;
        private System.Windows.Forms.ToolStripMenuItem searchFindReferencesMenu;
        private System.Windows.Forms.ToolStripMenuItem searchFindPatternMenu;

        private System.Windows.Forms.ToolStripMenuItem optionsMenu;
        private System.Windows.Forms.ToolStripMenuItem optionsPreferencesMenu;

        private System.Windows.Forms.ToolStripMenuItem helpMenu;
        private System.Windows.Forms.ToolStripMenuItem helpAboutMenu;

        // Main Toolbar
        private System.Windows.Forms.ToolStrip mainToolStrip;
        private System.Windows.Forms.ToolStripButton btnOpen;
        private System.Windows.Forms.ToolStripButton btnSave;
        private System.Windows.Forms.ToolStripSeparator toolSeparator1;
        private System.Windows.Forms.ToolStripButton btnRun;
        private System.Windows.Forms.ToolStripButton btnPause;
        private System.Windows.Forms.ToolStripButton btnStop;
        private System.Windows.Forms.ToolStripSeparator toolSeparator2;
        private System.Windows.Forms.ToolStripButton btnStepInto;
        private System.Windows.Forms.ToolStripButton btnStepOver;
        private System.Windows.Forms.ToolStripButton btnStepOut;
        private System.Windows.Forms.ToolStripSeparator toolSeparator3;
        private System.Windows.Forms.ToolStripButton btnBreakpoint;
        private System.Windows.Forms.ToolStripSeparator toolSeparator4;
        private System.Windows.Forms.ToolStripTextBox txtGotoAddress;
        private System.Windows.Forms.ToolStripButton btnGoto;
        private System.Windows.Forms.ToolStripSeparator toolSeparator5;
        private System.Windows.Forms.ToolStripLabel lblCurrentAddress;

        // Status Strip
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusProtection;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusAllocationBase;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusSize;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusModule;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusInfo;
        private System.Windows.Forms.ToolStripStatusLabel lblStatusDriver;

        // Split Containers
        private System.Windows.Forms.SplitContainer mainSplitContainer;
        private System.Windows.Forms.SplitContainer leftSplitContainer;

        // Panels
        private System.Windows.Forms.Panel panelDisassembler;
        private System.Windows.Forms.Label lblDisassemblerPlaceholder;
        private System.Windows.Forms.Panel panelHexView;
        private System.Windows.Forms.Panel panelHexInfo;
        private System.Windows.Forms.Label lblHexInfo;
        private System.Windows.Forms.Panel panelRegisters;
        private System.Windows.Forms.Label lblRegistersTitle;

        // Tab Control
        private System.Windows.Forms.TabControl bottomTabControl;
        private System.Windows.Forms.TabPage tabHexDump;
        private System.Windows.Forms.TabPage tabStack;
        private System.Windows.Forms.TabPage tabWatch;

        // Top Tab Control (x64dbg-style)
        private System.Windows.Forms.TabControl topTabControl;
        private System.Windows.Forms.TabPage tabMemoryView;
        private System.Windows.Forms.TabPage tabBookmarks;
        private System.Windows.Forms.ListView lvBookmarks;
    }
}
