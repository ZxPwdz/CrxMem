using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CrxMem.Core;

namespace CrxMem
{
    /// <summary>
    /// Watchdog Hunter: enumerates target-process threads with their Win32 start addresses,
    /// flags suspicious ones (start address outside all modules, or inside user-defined ranges
    /// such as the Themida VM ranges), and lets the user suspend/resume threads one-by-one or
    /// in bisection rounds to identify a hidden integrity-check ("watchdog") thread.
    /// </summary>
    public partial class WatchdogHunterWindow : Window
    {
        // NtQueryInformationThread is not declared anywhere else in the codebase; declared here.
        // ThreadQuerySetWin32StartAddress = 9. OpenThread / SuspendThread / ResumeThread /
        // CloseHandle and the THREAD_SUSPEND_RESUME constant are reused from Core.ProcessAccess.
        private const uint THREAD_QUERY_INFORMATION = 0x0040;
        private const int ThreadQuerySetWin32StartAddress = 9;

        // ═══════════════════════════════════════════════════════════════
        // Debug log — every init step is written here so a future
        // "window closes immediately" failure is diagnosable after the fact.
        // ═══════════════════════════════════════════════════════════════

        internal static readonly string DebugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrxMem", "watchdog_hunter_debug.log");

        internal static void DebugLog(string message, Exception? ex = null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                if (ex != null)
                    line += $"{Environment.NewLine}{ex}";
                File.AppendAllText(DebugLogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationThread(
            IntPtr threadHandle, int threadInformationClass,
            ref IntPtr threadInformation, int threadInformationLength, out int returnLength);

        private readonly ProcessAccess _process;
        private readonly ObservableCollection<ThreadRow> _rows = new();
        private readonly HashSet<int> _suspendedByUs = new();
        private readonly DispatcherTimer _autoRefreshTimer;
        private readonly DispatcherTimer _countdownTimer;
        private TimeSpan _countdownRemaining;
        private bool _suspendWarningShown;
        private bool _targetExited;
        private int _roundNumber;
        private List<int>? _pendingRoundThreads;
        private List<(long Start, long End)> _suspectRanges = new();

        // #DB catcher state (kernel watchpoint hit recorder)
        private bool _dbCatching;
        private int _dbCatchPid;
        private int _dbHitCount;
        private DebugMonitor? _vehMonitor;
        private readonly Dictionary<uint, (ulong Address, uint Type)> _armedWatchpoints = new();

        public WatchdogHunterWindow(ProcessAccess process)
        {
            DebugLog("ctor: begin");

            // IMPORTANT: the suspect-range TextBox TextChanged handlers fire while
            // InitializeComponent applies the XAML default Text values, so _process
            // must be assigned BEFORE the XAML load and every handler must tolerate
            // sibling controls that do not exist yet.
            _process = process ?? throw new ArgumentNullException(nameof(process));

            try
            {
                InitializeComponent();
                DebugLog("ctor: InitializeComponent OK");
            }
            catch (Exception ex)
            {
                DebugLog("ctor: InitializeComponent FAILED", ex);
                throw;
            }

            try
            {
                lstThreads.ItemsSource = _rows;

                _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _autoRefreshTimer.Tick += (_, _) => RefreshThreads(silent: true);

                _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _countdownTimer.Tick += CountdownTimer_Tick;

                ParseSuspectRanges();
                UpdateCountdownDisplay();
                DebugLog("ctor: post-XAML init complete");
            }
            catch (Exception ex)
            {
                DebugLog("ctor: post-XAML init FAILED", ex);
                throw;
            }

            Loaded += WatchdogHunterWindow_Loaded;
        }

        private void WatchdogHunterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DebugLog("Loaded: begin initial census");
                if (_process.Target != null && _process.Target.Id == Process.GetCurrentProcess().Id)
                {
                    AppendLog("[guard] Target process is CrxMem itself — suspend/resume operations are disabled.");
                }
                UpdateKernelWatchpointState();
                RefreshThreads(silent: false);
                DebugLog("Loaded: initial census finished");
            }
            catch (Exception ex)
            {
                // Degrade gracefully: report the failure in the log panel instead of dying.
                DebugLog("Loaded: initial census FAILED", ex);
                AppendLog($"[error] Initial thread census failed: {ex.Message}");
                txtStatus.Text = $"census error: {ex.Message}";
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Thread census
        // ═══════════════════════════════════════════════════════════════

        private void RefreshThreads(bool silent)
        {
            if (_targetExited)
                return;

            if (CheckTargetExited())
                return;

            UpdateKernelWatchpointState();

            try
            {
                var target = _process.Target;
                if (target == null)
                {
                    txtStatus.Text = "no target";
                    return;
                }

                target.Refresh();

                // Fresh module map for start-address attribution.
                List<ModuleInfo> modules;
                try { modules = _process.GetModules(forceRefresh: true); }
                catch (Exception ex)
                {
                    modules = new List<ModuleInfo>();
                    if (!silent) AppendLog($"[warn] Module enumeration failed: {ex.Message}");
                }

                // Preserve checkbox state across refreshes.
                var checkedBefore = _rows.Where(r => r.IsChecked).Select(r => r.ThreadId).ToHashSet();

                // Kernel census (Phase 2): when connected to the CrxShield driver, use its
                // thread enumeration (TID + start address) as the primary source and fall
                // back to per-thread NtQueryInformationThread for anything missing.
                Dictionary<int, long>? kernelThreads = null;
                if (KernelModeAvailable)
                {
                    var driver = AntiCheatBypassForm.DriverController!;
                    try
                    {
                        if (driver.EnumThreads((uint)target.Id, out var driverThreads))
                        {
                            kernelThreads = new Dictionary<int, long>();
                            foreach (var t in driverThreads)
                                kernelThreads[(int)t.ThreadId] = (long)t.StartAddress;
                            if (!silent)
                                AppendLog($"[kernel] thread census via CrxShield driver: {driverThreads.Count} thread(s) reported"
                                    + (driver.LastError != null ? $" ({driver.LastError})" : "") + ".");
                        }
                        else if (!silent)
                        {
                            AppendLog($"[warn] Driver EnumThreads failed: {driver.LastError} — using usermode census.");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!silent) AppendLog($"[warn] Driver EnumThreads error: {ex.Message} — using usermode census.");
                    }
                }

                var newRows = new List<ThreadRow>();
                int index = 0;
                foreach (ProcessThread thread in target.Threads)
                {
                    index++;
                    ThreadRow row;
                    try
                    {
                        long? start = null;
                        if (kernelThreads != null && kernelThreads.TryGetValue(thread.Id, out long kernelStart) && kernelStart != 0)
                            start = kernelStart;
                        else
                            start = GetThreadStartAddress(thread.Id);
                        row = BuildRow(index, thread, start, modules);
                    }
                    catch (Exception ex)
                    {
                        row = new ThreadRow
                        {
                            Index = index,
                            ThreadId = thread.Id,
                            ThreadIdText = thread.Id.ToString(CultureInfo.InvariantCulture),
                            StartAddressText = "?",
                            ModuleText = $"error: {ex.Message}",
                            StateText = "?",
                        };
                    }

                    row.IsChecked = checkedBefore.Contains(row.ThreadId);
                    row.IsSuspendedByUs = _suspendedByUs.Contains(row.ThreadId);
                    newRows.Add(row);
                }

                // Merge in driver-reported threads that Process.Threads did not surface.
                if (kernelThreads != null)
                {
                    foreach (var pair in kernelThreads)
                    {
                        if (newRows.Any(r => r.ThreadId == pair.Key))
                            continue;
                        index++;
                        long? start = pair.Value != 0 ? pair.Value : GetThreadStartAddress(pair.Key);
                        var row = BuildKernelOnlyRow(index, pair.Key, start, modules);
                        row.IsChecked = checkedBefore.Contains(row.ThreadId);
                        row.IsSuspendedByUs = _suspendedByUs.Contains(row.ThreadId);
                        newRows.Add(row);
                    }
                }

                _rows.Clear();
                foreach (var row in newRows)
                    _rows.Add(row);

                int suspects = newRows.Count(r => r.IsSuspect);
                string censusSource = kernelThreads != null ? " · kernel census" : "";
                txtStatus.Text = $"{target.ProcessName} (pid {target.Id}) · {newRows.Count} threads · {suspects} suspect{censusSource} · {DateTime.Now:HH:mm:ss}";
                txtStatus.Foreground = (System.Windows.Media.Brush)FindResource(suspects > 0 ? "AccentOrangeBrush" : "TextSecondaryBrush");
            }
            catch (Exception ex)
            {
                if (CheckTargetExited())
                    return;
                txtStatus.Text = $"refresh failed: {ex.Message}";
                DebugLog("RefreshThreads failed", ex);
                if (!silent) AppendLog($"[error] Thread census failed: {ex.Message}");
            }
        }

        /// <summary>True when kernel mode is enabled and the CrxShield driver handle is live.</summary>
        private static bool KernelModeAvailable =>
            AntiCheatBypassForm.UseKernelMode &&
            AntiCheatBypassForm.DriverController != null &&
            AntiCheatBypassForm.DriverController.IsConnected();

        /// <summary>
        /// Build a row for a thread that only the kernel driver reported (no ProcessThread entry).
        /// State is unknown; start address comes from the driver census.
        /// </summary>
        private ThreadRow BuildKernelOnlyRow(int index, int threadId, long? startAddress, List<ModuleInfo> modules)
        {
            var row = new ThreadRow
            {
                Index = index,
                ThreadId = threadId,
                ThreadIdText = threadId.ToString(CultureInfo.InvariantCulture),
                StateText = "kernel-only",
            };

            if (startAddress == null || startAddress.Value == 0)
            {
                row.StartAddressText = "unavailable";
                row.ModuleText = "(kernel-only thread; start address unknown)";
                row.IsSuspect = true;
                return row;
            }

            long addr = startAddress.Value;
            row.StartAddress = addr;
            row.StartAddressText = $"0x{addr:X8}";

            ModuleInfo? owner = FindModule(modules, addr);
            if (owner != null)
            {
                string? section = TryGetSectionName(owner, addr);
                row.ModuleText = section != null ? $"{owner.ModuleName} · {section}" : owner.ModuleName;
                row.InsideModule = true;
            }
            else
            {
                row.ModuleText = "(no module — injected/orphan)";
            }

            bool inSuspectRange = _suspectRanges.Any(r => addr >= r.Start && addr <= r.End);
            row.IsSuspect = owner == null || inSuspectRange;
            return row;
        }

        private ThreadRow BuildRow(int index, ProcessThread thread, long? startAddress, List<ModuleInfo> modules)
        {
            string stateText;
            try { stateText = thread.ThreadState.ToString(); }
            catch { stateText = "?"; }

            var row = new ThreadRow
            {
                Index = index,
                ThreadId = thread.Id,
                ThreadIdText = thread.Id.ToString(CultureInfo.InvariantCulture),
                StateText = stateText,
            };

            if (startAddress == null || startAddress.Value == 0)
            {
                row.StartAddressText = "unavailable";
                row.ModuleText = "(start address query failed)";
                return row;
            }

            long addr = startAddress.Value;
            row.StartAddress = addr;
            row.StartAddressText = $"0x{addr:X8}";

            ModuleInfo? owner = FindModule(modules, addr);
            if (owner != null)
            {
                string? section = TryGetSectionName(owner, addr);
                row.ModuleText = section != null ? $"{owner.ModuleName} · {section}" : owner.ModuleName;
                row.InsideModule = true;
            }
            else
            {
                row.ModuleText = "(no module — injected/orphan)";
            }

            bool inSuspectRange = _suspectRanges.Any(r => addr >= r.Start && addr <= r.End);
            row.IsSuspect = owner == null || inSuspectRange;
            return row;
        }

        private static ModuleInfo? FindModule(List<ModuleInfo> modules, long addr)
        {
            foreach (var module in modules)
            {
                long baseAddr = module.BaseAddress.ToInt64();
                if (addr >= baseAddr && addr < baseAddr + module.Size)
                    return module;
            }
            return null;
        }

        /// <summary>
        /// Win32 start address via NtQueryInformationThread(ThreadQuerySetWin32StartAddress).
        /// More reliable than ProcessThread.StartAddress, which is often 0 without debug privileges.
        /// </summary>
        private static long? GetThreadStartAddress(int threadId)
        {
            IntPtr hThread = ProcessAccess.OpenThread(THREAD_QUERY_INFORMATION, false, (uint)threadId);
            if (hThread == IntPtr.Zero)
                return null;
            try
            {
                IntPtr startAddress = IntPtr.Zero;
                int status = NtQueryInformationThread(
                    hThread, ThreadQuerySetWin32StartAddress, ref startAddress, IntPtr.Size, out _);
                return status == 0 ? startAddress.ToInt64() : null;
            }
            finally
            {
                ProcessAccess.CloseHandle(hThread);
            }
        }

        /// <summary>
        /// Best-effort PE section resolution: reads the owning module's headers from the target
        /// and finds the section containing the given address. Returns null on any failure.
        /// </summary>
        private string? TryGetSectionName(ModuleInfo module, long addr)
        {
            try
            {
                long baseAddr = module.BaseAddress.ToInt64();
                byte[]? dos = _process.Read(module.BaseAddress, 0x40);
                if (dos == null || dos.Length < 0x40 || dos[0] != 'M' || dos[1] != 'Z')
                    return null;

                int lfanew = BitConverter.ToInt32(dos, 0x3C);
                if (lfanew <= 0 || lfanew > 0x1000)
                    return null;

                byte[]? nt = _process.Read(new IntPtr(baseAddr + lfanew), 4 + 20);
                if (nt == null || nt.Length < 24)
                    return null;

                ushort numberOfSections = BitConverter.ToUInt16(nt, 6);
                ushort sizeOfOptionalHeader = BitConverter.ToUInt16(nt, 20);
                if (numberOfSections == 0 || numberOfSections > 96)
                    return null;

                long sectionsOffset = baseAddr + lfanew + 24 + sizeOfOptionalHeader;
                byte[]? sections = _process.Read(new IntPtr(sectionsOffset), numberOfSections * 40);
                if (sections == null || sections.Length < numberOfSections * 40)
                    return null;

                long rva = addr - baseAddr;
                for (int i = 0; i < numberOfSections; i++)
                {
                    int off = i * 40;
                    uint virtualSize = BitConverter.ToUInt32(sections, off + 8);
                    uint virtualAddress = BitConverter.ToUInt32(sections, off + 12);
                    uint rawSize = BitConverter.ToUInt32(sections, off + 16);
                    uint span = Math.Max(virtualSize, rawSize);
                    if (span == 0)
                        continue;
                    if (rva >= virtualAddress && rva < virtualAddress + span)
                    {
                        var name = new StringBuilder(8);
                        for (int c = 0; c < 8; c++)
                        {
                            byte b = sections[off + c];
                            if (b == 0) break;
                            name.Append((char)b);
                        }
                        return name.Length > 0 ? name.ToString() : null;
                    }
                }
            }
            catch
            {
                // Best effort only — headers may be paged out or the read may be blocked.
            }
            return null;
        }

        // ═══════════════════════════════════════════════════════════════
        // Suspect ranges
        // ═══════════════════════════════════════════════════════════════

        private void ParseSuspectRanges()
        {
            // During InitializeComponent the TextChanged handlers fire while the XAML
            // default Text is applied; the other TextBox may not exist yet. Treat a
            // missing control as an empty list instead of crashing the constructor.
            var starts = SplitHexList(txtSuspectStart?.Text ?? string.Empty);
            var ends = SplitHexList(txtSuspectEnd?.Text ?? string.Empty);
            var ranges = new List<(long, long)>();
            int count = Math.Min(starts.Count, ends.Count);
            for (int i = 0; i < count; i++)
            {
                if (starts[i] is long start && ends[i] is long end && end >= start)
                    ranges.Add((start, end));
            }
            _suspectRanges = ranges;
        }

        private static List<long?> SplitHexList(string text)
        {
            var values = new List<long?>();
            foreach (var part in text.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0)
                    continue;
                string p = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
                if (long.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value))
                    values.Add(value);
                else
                    values.Add(null);
            }
            return values;
        }

        private void SuspectRanges_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                ParseSuspectRanges();
                // Re-classify existing rows against the new ranges without a full re-enumeration.
                List<ModuleInfo> modules;
                try { modules = _process.GetModules(); }
                catch { modules = new List<ModuleInfo>(); }
                foreach (var row in _rows)
                {
                    if (!row.StartAddress.HasValue)
                        continue;
                    bool inRange = _suspectRanges.Any(r => row.StartAddress.Value >= r.Start && row.StartAddress.Value <= r.End);
                    row.IsSuspect = !row.InsideModule || inRange;
                }
            }
            catch (Exception ex)
            {
                // This handler can fire during InitializeComponent; never let it kill the window.
                DebugLog("SuspectRanges_TextChanged failed", ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Suspend / Resume
        // ═══════════════════════════════════════════════════════════════

        private bool GuardSuspendAllowed()
        {
            if (_targetExited)
                return false;

            var target = _process.Target;
            if (target == null)
                return false;

            if (target.Id == Process.GetCurrentProcess().Id)
            {
                AppendLog("[guard] Refused: target process is CrxMem itself.");
                return false;
            }

            if (!_suspendWarningShown)
            {
                _suspendWarningShown = true;
                System.Windows.MessageBox.Show(this,
                    "Suspending game threads can freeze or crash the target. That's the point of this tool, but be ready to RESUME ALL.",
                    "Watchdog Hunter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return true;
        }

        private bool SuspendThreadById(int threadId)
        {
            IntPtr hThread = ProcessAccess.OpenThread(
                ProcessAccess.THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION, false, (uint)threadId);
            if (hThread == IntPtr.Zero)
                return false;
            try
            {
                uint result = ProcessAccess.SuspendThread(hThread);
                if (result == 0xFFFFFFFF)
                    return false;
                _suspendedByUs.Add(threadId);
                return true;
            }
            finally
            {
                ProcessAccess.CloseHandle(hThread);
            }
        }

        private bool ResumeThreadById(int threadId)
        {
            IntPtr hThread = ProcessAccess.OpenThread(
                ProcessAccess.THREAD_SUSPEND_RESUME, false, (uint)threadId);
            if (hThread == IntPtr.Zero)
                return false;
            try
            {
                uint result = ProcessAccess.ResumeThread(hThread);
                if (result == 0xFFFFFFFF)
                    return false;
                _suspendedByUs.Remove(threadId);
                return true;
            }
            finally
            {
                ProcessAccess.CloseHandle(hThread);
            }
        }

        private void SuspendRows(IEnumerable<ThreadRow> rows)
        {
            if (!GuardSuspendAllowed())
                return;
            foreach (var row in rows)
            {
                try
                {
                    if (SuspendThreadById(row.ThreadId))
                    {
                        row.IsSuspendedByUs = true;
                        AppendLog($"suspended thread {row.ThreadId} (start {row.StartAddressText})");
                    }
                    else
                    {
                        AppendLog($"[error] SuspendThread failed for thread {row.ThreadId} (error {Marshal.GetLastWin32Error()})");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[error] Suspend thread {row.ThreadId}: {ex.Message}");
                }
            }
        }

        private void ResumeRows(IEnumerable<ThreadRow> rows)
        {
            foreach (var row in rows)
            {
                try
                {
                    if (ResumeThreadById(row.ThreadId))
                    {
                        row.IsSuspendedByUs = false;
                        AppendLog($"resumed thread {row.ThreadId}");
                    }
                    else
                    {
                        AppendLog($"[error] ResumeThread failed for thread {row.ThreadId} (error {Marshal.GetLastWin32Error()})");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[error] Resume thread {row.ThreadId}: {ex.Message}");
                }
            }
        }

        private void RowSuspend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: ThreadRow row })
                SuspendRows(new[] { row });
        }

        private void RowResume_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { Tag: ThreadRow row })
                ResumeRows(new[] { row });
        }

        private void SuspendSelected_Click(object sender, RoutedEventArgs e) =>
            SuspendRows(lstThreads.SelectedItems.Cast<ThreadRow>().ToList());

        private void ResumeSelected_Click(object sender, RoutedEventArgs e) =>
            ResumeRows(lstThreads.SelectedItems.Cast<ThreadRow>().ToList());

        private void ResumeAll_Click(object sender, RoutedEventArgs e)
        {
            var target = _process.Target;
            if (target == null || _targetExited)
                return;
            if (target.Id == Process.GetCurrentProcess().Id)
            {
                AppendLog("[guard] Refused: target process is CrxMem itself.");
                return;
            }

            try
            {
                target.Refresh();
                foreach (ProcessThread thread in target.Threads)
                {
                    try
                    {
                        IntPtr hThread = ProcessAccess.OpenThread(
                            ProcessAccess.THREAD_SUSPEND_RESUME, false, (uint)thread.Id);
                        if (hThread == IntPtr.Zero)
                            continue;
                        try
                        {
                            // Resume repeatedly until the suspend count reaches zero.
                            for (int i = 0; i < 100; i++)
                            {
                                uint previousCount = ProcessAccess.ResumeThread(hThread);
                                if (previousCount == 0xFFFFFFFF || previousCount <= 1)
                                    break;
                            }
                        }
                        finally
                        {
                            ProcessAccess.CloseHandle(hThread);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[error] Panic-resume thread {thread.Id}: {ex.Message}");
                    }
                }
                _suspendedByUs.Clear();
                foreach (var row in _rows)
                    row.IsSuspendedByUs = false;
                AppendLog("RESUME ALL executed — every thread resumed to a zero suspend count.");
            }
            catch (Exception ex)
            {
                if (!CheckTargetExited())
                    AppendLog($"[error] RESUME ALL failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Kernel watchpoint (hardware breakpoints via CrxShield driver)
        // ═══════════════════════════════════════════════════════════════

        private void UpdateKernelWatchpointState()
        {
            grpKernelWatchpoint.IsEnabled = KernelModeAvailable && !_targetExited;
        }

        private static readonly string[] WatchpointTypeNames = { "execute", "write", "read-write" };

        /// <summary>
        /// Accepts "Gunz.exe+2BDC7D" (module+offset, resolved via the user-mode
        /// module list — reliable for WoW64 targets) or a raw hex VA like "0x42DC7D".
        /// </summary>
        private bool TryResolveWatchpointAddress(string text, out ulong address)
        {
            address = 0;
            text = (text ?? "").Trim();
            int plus = text.IndexOf('+');
            if (plus > 0)
            {
                string modName = text[..plus].Trim();
                string offHex = text[(plus + 1)..].Trim();
                if (offHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) offHex = offHex[2..];
                if (!ulong.TryParse(offHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong offset))
                    return false;
                try
                {
                    var mod = _process.GetModules().FirstOrDefault(m =>
                        string.Equals(m.ModuleName, modName, StringComparison.OrdinalIgnoreCase));
                    if (mod != null)
                    {
                        address = (ulong)mod.BaseAddress.ToInt64() + offset;
                        return true;
                    }
                }
                catch { /* fall through to driver */ }

                // Fallback: driver main-module base (known-broken on WoW64 — module list is primary)
                var target = _process.Target;
                var driver = AntiCheatBypassForm.DriverController;
                if (target == null || driver == null || !driver.IsConnected())
                    return false;
                if (!driver.GetProcessBaseAddress((uint)target.Id, out ulong modBase))
                    return false;
                address = modBase + offset;
                return true;
            }
            string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            return hex.Length > 0 && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        /// <summary>Resolve an EIP to "module!+0xoffset" via the user-mode module list.</summary>
        private string ResolveEip(ulong addr)
        {
            try
            {
                var mod = _process.GetModuleForAddress((IntPtr)addr);
                if (mod != null && addr >= (ulong)mod.BaseAddress.ToInt64())
                    return $" = {mod.ModuleName}!+0x{addr - (ulong)mod.BaseAddress.ToInt64():X}";
            }
            catch { /* best effort */ }
            return "";
        }

        private void SetWatchpoint_Click(object sender, RoutedEventArgs e)
        {
            if (!KernelModeAvailable)
            {
                AppendLog("[warn] Kernel watchpoint unavailable — connect the CrxShield driver and enable kernel mode first.");
                return;
            }

            var target = _process.Target;
            if (target == null || _targetExited)
            {
                AppendLog("[warn] No target attached — cannot set a watchpoint.");
                return;
            }
            if (target.Id == Process.GetCurrentProcess().Id)
            {
                AppendLog("[guard] Refused: target process is CrxMem itself.");
                return;
            }

            string addrText = txtWatchpointAddress.Text.Trim();
            if (!TryResolveWatchpointAddress(addrText, out ulong address))
            {
                AppendLog($"[error] Invalid watchpoint address '{addrText}' — use Gunz.exe+2BDC7D or a raw hex VA like 0x42DC7D (module+offset needs the driver connected).");
                return;
            }

            uint slot = (uint)Math.Max(0, cmbWatchpointSlot.SelectedIndex);
            uint type = (uint)Math.Max(0, cmbWatchpointType.SelectedIndex);
            // The driver requires a length: execution breakpoints must be 1 byte;
            // data watchpoints default to 4 bytes (a typical watched DWORD).
            uint length = type == 0 ? 1u : 4u;

            var driver = AntiCheatBypassForm.DriverController!;
            try
            {
                if (driver.SetHardwareBreakpoint((uint)target.Id, slot, address, type, length,
                        out uint updated, out uint failed,
                        out uint lookupFailed, out uint getCtxFailed, out uint setCtxFailed,
                        out int firstErr))
                {
                    AppendLog($"[kernel] watchpoint DR{slot} set at 0x{address:X} ({WatchpointTypeNames[type]}, len {length}) — {updated} thread(s) updated, {failed} failed.");
                    _armedWatchpoints[slot] = (address, type);
                    if (!_dbCatching)
                        AppendLog($"[warn] #DB catcher is NOT running — if the target touches 0x{address:X} now, the hit is unhandled and the target will crash. Click 'Catch #DB hits' first.");
                    if (failed > 0)
                    {
                        AppendLog($"[warn] Some threads failed; the watchpoint may be incomplete on newly spawned threads. " +
                                  $"Stages: Lookup={lookupFailed} GetCtx={getCtxFailed} SetCtx={setCtxFailed}, FirstErrorStatus=0x{firstErr:X8}");
                    }
                }
                else
                {
                    AppendLog($"[error] SetHardwareBreakpoint failed: {driver.LastError}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[error] SetHardwareBreakpoint: {ex.Message}");
            }
        }

        private void ClearWatchpoints_Click(object sender, RoutedEventArgs e)
        {
            if (!KernelModeAvailable)
            {
                AppendLog("[warn] Kernel watchpoint unavailable — connect the CrxShield driver and enable kernel mode first.");
                return;
            }

            var target = _process.Target;
            if (target == null || _targetExited)
            {
                AppendLog("[warn] No target attached — cannot clear watchpoints.");
                return;
            }
            if (target.Id == Process.GetCurrentProcess().Id)
            {
                AppendLog("[guard] Refused: target process is CrxMem itself.");
                return;
            }

            var driver = AntiCheatBypassForm.DriverController!;
            try
            {
                if (driver.ClearHardwareBreakpoint((uint)target.Id, 0xFFFFFFFF,
                        out uint updated, out uint failed,
                        out uint lookupFailed, out uint getCtxFailed, out uint setCtxFailed,
                        out int firstErr))
                {
                    AppendLog($"[kernel] all watchpoints cleared (DR0–DR3) — {updated} thread(s) updated, {failed} failed.");
                    _armedWatchpoints.Clear();
                    if (failed > 0)
                        AppendLog($"[warn] Some threads failed during clear. " +
                                  $"Stages: Lookup={lookupFailed} GetCtx={getCtxFailed} SetCtx={setCtxFailed}, FirstErrorStatus=0x{firstErr:X8}");
                }
                else
                    AppendLog($"[error] ClearHardwareBreakpoint failed: {driver.LastError}");
            }
            catch (Exception ex)
            {
                AppendLog($"[error] ClearHardwareBreakpoint: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // #DB catcher — records kernel-watchpoint hits via the Windows
        // debug port without stopping the target (DBG_CONTINUE).
        // ═══════════════════════════════════════════════════════════════

        private const uint DBG_CONTINUE = 0x00010002;
        private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
        private const uint EXCEPTION_DEBUG_EVENT = 1;
        private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
        private const uint EXCEPTION_SINGLE_STEP_CODE = 0x80000004;
        private const uint THREAD_GET_CONTEXT = 0x0008;
        private const uint THREAD_SET_CONTEXT = 0x0010;
        private const uint WOW64_CONTEXT_FULL_DBG = 0x00010017;   // WOW64 CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS
        private const uint WOW64_CONTEXT_DEBUG_REGISTERS = 0x00010010;
        private const int WOW64_CTX_DR6_OFFSET = 20;
        private const int WOW64_CTX_EIP_OFFSET = 184;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcess(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcessStop(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WaitForDebugEvent(ref DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ContinueDebugEvent(int dwProcessId, int dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, int dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64GetThreadContext(IntPtr hThread, byte[] lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Wow64SetThreadContext(IntPtr hThread, byte[] lpContext);

        [StructLayout(LayoutKind.Sequential)]
        private struct DEBUG_EVENT
        {
            public uint dwDebugEventCode;
            public uint dwProcessId;
            public uint dwThreadId;
            public uint _pad;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 176)]
            public byte[] Union;
        }

        private void CatchDb_Click(object sender, RoutedEventArgs e)
        {
            if (_dbCatching) { StopDbCatch(); return; }
            StartDbCatch();
        }

        private void StartDbCatch()
        {
            var target = _process.Target;
            if (target == null || _targetExited)
            {
                AppendLog("[warn] No target attached — cannot start the #DB catcher.");
                return;
            }
            if (target.Id == Process.GetCurrentProcess().Id)
            {
                AppendLog("[guard] Refused: target process is CrxMem itself.");
                return;
            }

            if (chkVehMode.IsChecked == true)
            {
                StartVehCatch(target.Id);
                return;
            }

            int pid = target.Id;
            if (!DebugActiveProcess(pid))
            {
                int err = Marshal.GetLastWin32Error();
                AppendLog($"[error] DebugActiveProcess failed (Win32 {err}). Run CrxMem as administrator, and make sure no other debugger (x64dbg, CE) is attached to the target.");
                return;
            }

            _dbCatching = true;
            _dbCatchPid = pid;
            _dbHitCount = 0;
            btnCatchDb.Content = "Stop catching";
            txtDbCatchStatus.Text = "catching (debug port) — 0 hits";
            AppendLog($"[#DB] catcher attached to PID {pid}. Watching for EXCEPTION_SINGLE_STEP; hits are logged and execution continues (target does not stop).");
            AppendLog("[#DB] note: for data watchpoints the reported EIP is the instruction AFTER the access (trap semantics).");
            if (_armedWatchpoints.Count == 0)
                AppendLog("[#DB] no kernel watchpoints armed yet — set one now (address, slot, type).");

            Task.Run(() => DbCatchLoop(pid));
        }

        // ── In-process VEH catch (no debug port → no BeingDebugged, no lag) ──

        private void StartVehCatch(int pid)
        {
            string addrText = txtWatchpointAddress.Text.Trim();
            if (!TryResolveWatchpointAddress(addrText, out ulong address))
            {
                AppendLog($"[error] Invalid watchpoint address '{addrText}' — use Gunz.exe+2BDC7D or a raw hex VA like 0x42DC7D (module+offset needs the driver connected).");
                return;
            }

            uint type = (uint)Math.Max(0, cmbWatchpointType.SelectedIndex);
            if (type == 0)
            {
                AppendLog("[warn] In-process VEH mode supports Write and Read-Write watchpoints only (not Execute).");
                return;
            }
            bool writeOnly = type == 1;

            _vehMonitor = new DebugMonitor(_process);
            _vehMonitor.BreakpointHit += VehMonitor_BreakpointHit;
            if (!_vehMonitor.StartMonitoringWithHardwareBreakpoint(address, writeOnly, 4))
            {
                AppendLog($"[error] VEH catch failed: {_vehMonitor.LastError}");
                _vehMonitor.BreakpointHit -= VehMonitor_BreakpointHit;
                _vehMonitor.Dispose();
                _vehMonitor = null;
                return;
            }

            _dbCatching = true;
            _dbCatchPid = pid;
            _dbHitCount = 0;
            btnCatchDb.Content = "Stop catching";
            txtDbCatchStatus.Text = "catching (VEH) — 0 hits";
            AppendLog($"[#DB] in-process VEH catcher armed at 0x{address:X} ({(writeOnly ? "write" : "read-write")}, len 4) on PID {pid}.");
            AppendLog("[#DB] no debug port — BeingDebugged stays clear and there is no event flood (no lag).");
            AppendLog("[#DB] note: VEH mode arms its own watchpoint from inside; do NOT also 'Set watchpoint' via the driver on the same slot.");
        }

        private void VehMonitor_BreakpointHit(object? sender, BreakpointHitEventArgs e)
        {
            _dbHitCount++;
            int hitNo = _dbHitCount;

            string resolved = ResolveEip(e.InstructionAddress);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"[#DB hit #{hitNo}] TID={e.ThreadId}  reader EIP=0x{e.InstructionAddress:X}{resolved}");
                txtDbCatchStatus.Text = $"catching (VEH) — {_dbHitCount} hit(s)";
            }));
        }

        private void StopDbCatch()
        {
            _dbCatching = false;
            if (_vehMonitor != null)
            {
                try
                {
                    _vehMonitor.BreakpointHit -= VehMonitor_BreakpointHit;
                    _vehMonitor.StopMonitoring();
                    _vehMonitor.Dispose();
                }
                catch { }
                _vehMonitor = null;
            }
            else
            {
                try { DebugActiveProcessStop(_dbCatchPid); } catch { }
            }
            btnCatchDb.Content = "Catch #DB hits";
            txtDbCatchStatus.Text = $"not catching — {_dbHitCount} hit(s) recorded";
            AppendLog($"[#DB] catcher stopped for PID {_dbCatchPid} — {_dbHitCount} hit(s) recorded.");
        }

        private void DbCatchLoop(int pid)
        {
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;
            var evt = new DEBUG_EVENT { Union = new byte[176] };
            var eventCounts = new Dictionary<uint, int>();
            int eventsSinceLog = 0;
            var lastRateLog = DateTime.UtcNow;
            string[] eventNames = { "", "EXCEPTION", "CREATE_THREAD", "CREATE_PROCESS", "EXIT_THREAD", "EXIT_PROCESS", "LOAD_DLL", "UNLOAD_DLL", "OUTPUT_DEBUG_STRING", "RIP" };

            while (_dbCatching)
            {
                if (!WaitForDebugEvent(ref evt, 500))
                    continue; // timeout — re-check _dbCatching

                // Flood diagnostics: count events by type, log the rate every 5 s
                // so a laggy target tells us exactly what is flooding the port.
                eventCounts.TryGetValue(evt.dwDebugEventCode, out int n);
                eventCounts[evt.dwDebugEventCode] = n + 1;
                eventsSinceLog++;
                if ((DateTime.UtcNow - lastRateLog).TotalSeconds >= 5)
                {
                    double rate = eventsSinceLog / (DateTime.UtcNow - lastRateLog).TotalSeconds;
                    var parts = string.Join(", ", eventCounts.Select(kv =>
                        $"{(kv.Key < eventNames.Length ? eventNames[kv.Key] : "type" + kv.Key)}={kv.Value}"));
                    int rateInt = (int)rate;
                    Dispatcher.BeginInvoke(new Action(() =>
                        AppendLog($"[#DB] debug-port load: {rateInt} events/s ({parts})" +
                                  (rateInt > 200 ? " — FLOOD: this is the lag source. Use In-process VEH mode instead." : ""))));
                    eventCounts.Clear();
                    eventsSinceLog = 0;
                    lastRateLog = DateTime.UtcNow;
                }

                uint continueStatus = DBG_EXCEPTION_NOT_HANDLED;
                switch (evt.dwDebugEventCode)
                {
                    case EXCEPTION_DEBUG_EVENT:
                        uint code = BitConverter.ToUInt32(evt.Union, 0);
                        if (code == EXCEPTION_SINGLE_STEP_CODE)
                        {
                            HandleDbHit(evt);
                            continueStatus = DBG_CONTINUE;
                        }
                        break;

                    case EXIT_PROCESS_DEBUG_EVENT:
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            AppendLog("[#DB] target exited — catcher stopping.");
                            _dbCatching = false;
                            btnCatchDb.Content = "Catch #DB hits";
                            txtDbCatchStatus.Text = $"target exited — {_dbHitCount} hit(s) recorded";
                        }));
                        _dbCatching = false;
                        continueStatus = DBG_CONTINUE;
                        break;
                }

                ContinueDebugEvent((int)evt.dwProcessId, (int)evt.dwThreadId, continueStatus);
            }
        }

        private void HandleDbHit(DEBUG_EVENT evt)
        {
            int tid = (int)evt.dwThreadId;
            ulong exceptionAddress = BitConverter.ToUInt64(evt.Union, 16);

            // Read the firing thread's debug registers to attribute the hit to a slot.
            uint dr6 = 0;
            uint eip32 = 0;
            IntPtr hThread = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT, false, tid);
            if (hThread != IntPtr.Zero)
            {
                try
                {
                    var ctx = new byte[1024];
                    BitConverter.GetBytes(WOW64_CONTEXT_FULL_DBG).CopyTo(ctx, 0);
                    if (Wow64GetThreadContext(hThread, ctx))
                    {
                        dr6 = BitConverter.ToUInt32(ctx, WOW64_CTX_DR6_OFFSET);
                        eip32 = BitConverter.ToUInt32(ctx, WOW64_CTX_EIP_OFFSET);

                        // Clear DR6 so the next hit reports fresh slot bits.
                        BitConverter.GetBytes(WOW64_CONTEXT_DEBUG_REGISTERS).CopyTo(ctx, 0);
                        BitConverter.GetBytes(0u).CopyTo(ctx, WOW64_CTX_DR6_OFFSET);
                        Wow64SetThreadContext(hThread, ctx);
                    }
                }
                finally { ProcessAccess.CloseHandle(hThread); }
            }

            // Attribute slot(s) from DR6 B0–B3.
            var fired = new List<string>();
            for (uint slot = 0; slot < 4; slot++)
            {
                if ((dr6 & (1u << (int)slot)) == 0) continue;
                fired.Add(_armedWatchpoints.TryGetValue(slot, out var wp)
                    ? $"DR{slot} ({WatchpointTypeNames[wp.Type]} @ 0x{wp.Address:X})"
                    : $"DR{slot}");
            }
            string slotInfo = fired.Count > 0 ? string.Join(", ", fired) : "unknown slot (DR6=0)";

            // Resolve EIP against the target's modules (user-mode module list; WoW64-safe).
            string resolved = ResolveEip(exceptionAddress);

            _dbHitCount++;
            int hitNo = _dbHitCount;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLog($"[#DB hit #{hitNo}] TID={tid}  {slotInfo}  reader EIP=0x{exceptionAddress:X}{resolved}" +
                          (eip32 != 0 && eip32 != (uint)exceptionAddress ? $"  (ctx EIP=0x{eip32:X})" : ""));
                txtDbCatchStatus.Text = $"catching — {_dbHitCount} hit(s)";
            }));
        }

        // ═══════════════════════════════════════════════════════════════
        // Bisection assistant
        // ═══════════════════════════════════════════════════════════════

        private void SelectSuspects_Click(object sender, RoutedEventArgs e)
        {
            int count = 0;
            foreach (var row in _rows)
            {
                if (row.IsSuspect)
                {
                    row.IsChecked = true;
                    count++;
                }
            }
            AppendLog($"selected {count} suspect thread(s).");
        }

        private void SuspendChecked_Click(object sender, RoutedEventArgs e)
        {
            var checkedRows = _rows.Where(r => r.IsChecked).ToList();
            if (checkedRows.Count == 0)
            {
                AppendLog("nothing checked — tick threads or use 'Select all suspects'.");
                return;
            }

            if (_pendingRoundThreads != null)
                AppendLog($"Round {_roundNumber}: suspended {{{string.Join(",", _pendingRoundThreads)}}} — result: (not marked)");

            if (!GuardSuspendAllowed())
                return;

            _roundNumber++;
            var suspended = new List<int>();
            foreach (var row in checkedRows)
            {
                try
                {
                    if (SuspendThreadById(row.ThreadId))
                    {
                        row.IsSuspendedByUs = true;
                        suspended.Add(row.ThreadId);
                    }
                    else
                    {
                        AppendLog($"[error] SuspendThread failed for thread {row.ThreadId} (error {Marshal.GetLastWin32Error()})");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[error] Suspend thread {row.ThreadId}: {ex.Message}");
                }
            }

            _pendingRoundThreads = suspended;
            AppendLog($"Round {_roundNumber} started: suspended {{{string.Join(",", suspended)}}} — apply your patch, start the timer, then mark the result.");
        }

        private void MarkCrashed_Click(object sender, RoutedEventArgs e) => MarkRound("CRASHED");
        private void MarkClean_Click(object sender, RoutedEventArgs e) => MarkRound("CLEAN");

        private void MarkRound(string result)
        {
            if (_pendingRoundThreads == null)
            {
                AppendLog($"no active round to mark — use 'Suspend checked' first.");
                return;
            }
            AppendLog($"Round {_roundNumber}: suspended {{{string.Join(",", _pendingRoundThreads)}}} — result: {result}");
            _pendingRoundThreads = null;
        }

        private void TimerStart_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtRoundSeconds.Text.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int seconds) || seconds < 5 || seconds > 3600)
            {
                seconds = 70;
                txtRoundSeconds.Text = "70";
            }
            _countdownRemaining = TimeSpan.FromSeconds(seconds);
            _countdownTimer.Start();
            UpdateCountdownDisplay();
            AppendLog($"round timer started ({seconds}s).");
        }

        private void TimerReset_Click(object sender, RoutedEventArgs e)
        {
            _countdownTimer.Stop();
            _countdownRemaining = TimeSpan.Zero;
            UpdateCountdownDisplay();
        }

        private void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            if (_countdownRemaining > TimeSpan.Zero)
                _countdownRemaining -= TimeSpan.FromSeconds(1);
            UpdateCountdownDisplay();
            if (_countdownRemaining <= TimeSpan.Zero)
            {
                _countdownTimer.Stop();
                AppendLog("round timer elapsed — full watchdog cycle passed. Mark the round result.");
            }
        }

        private void UpdateCountdownDisplay()
        {
            txtCountdown.Text = _countdownRemaining <= TimeSpan.Zero
                ? (_countdownTimer.IsEnabled ? "00:00" : "--:--")
                : $"{(int)_countdownRemaining.TotalMinutes:00}:{_countdownRemaining.Seconds:00}";
        }

        // ═══════════════════════════════════════════════════════════════
        // Housekeeping
        // ═══════════════════════════════════════════════════════════════

        private bool CheckTargetExited()
        {
            if (_targetExited)
                return true;

            bool exited;
            try
            {
                var target = _process.Target;
                if (target == null)
                    return false;
                target.Refresh();
                exited = target.HasExited;
            }
            catch
            {
                exited = true;
            }

            if (!exited)
                return false;

            _targetExited = true;
            _autoRefreshTimer.Stop();
            _countdownTimer.Stop();
            chkAutoRefresh.IsChecked = false;

            txtStatus.Text = "TARGET EXITED — process is gone; controls disabled.";
            txtStatus.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            statusBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentRedBrush");

            btnRefresh.IsEnabled = false;
            btnSuspendSelected.IsEnabled = false;
            btnResumeSelected.IsEnabled = false;
            btnResumeAll.IsEnabled = false;
            btnSelectSuspects.IsEnabled = false;
            btnSuspendChecked.IsEnabled = false;
            btnTimerStart.IsEnabled = false;
            grpKernelWatchpoint.IsEnabled = false;

            AppendLog("target process exited — thread controls disabled.");
            return true;
        }

        private void AppendLog(string line)
        {
            if (txtLog == null)
            {
                DebugLog($"AppendLog before XAML ready: {line}");
                return;
            }
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            txtLog.ScrollToEnd();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshThreads(silent: false);

        private void ChkAutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (_autoRefreshTimer == null)
                return;
            if (chkAutoRefresh.IsChecked == true && !_targetExited)
                _autoRefreshTimer.Start();
            else
                _autoRefreshTimer.Stop();
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoRefreshTimer.Stop();
            _countdownTimer.Stop();
            base.OnClosed(e);
        }

        // ═══════════════════════════════════════════════════════════════
        // Row model
        // ═══════════════════════════════════════════════════════════════

        private sealed class ThreadRow : INotifyPropertyChanged
        {
            private bool _isChecked;
            private bool _isSuspect;
            private bool _isSuspendedByUs;

            public int Index { get; set; }
            public int ThreadId { get; set; }
            public string ThreadIdText { get; set; } = "";
            public long? StartAddress { get; set; }
            public string StartAddressText { get; set; } = "";
            public string ModuleText { get; set; } = "";
            public string StateText { get; set; } = "";
            public bool InsideModule { get; set; }

            public string SuspendedText => IsSuspendedByUs ? "YES" : "—";
            public string SuspectText => IsSuspect ? "SUSPECT" : "";

            public bool IsChecked
            {
                get => _isChecked;
                set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
            }

            public bool IsSuspect
            {
                get => _isSuspect;
                set
                {
                    if (_isSuspect != value)
                    {
                        _isSuspect = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(SuspectText));
                    }
                }
            }

            public bool IsSuspendedByUs
            {
                get => _isSuspendedByUs;
                set
                {
                    if (_isSuspendedByUs != value)
                    {
                        _isSuspendedByUs = value;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(SuspendedText));
                    }
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
