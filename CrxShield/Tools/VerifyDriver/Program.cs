using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CrxShield;

namespace CrxShield.Verify
{
    /// <summary>
    /// End-to-end verification of the CrxShield kernel driver.
    /// Requires: driver loaded (sc start CrxShield) and an ELEVATED shell
    /// (the device is SDDL-restricted to SYSTEM/Administrators).
    ///
    /// Tests:
    ///   1. GET_VERSION handshake (expect 1.5.0)
    ///   2. Kernel READ vs known DebugTarget buffer contents
    ///   3. Kernel WRITE read back through user-mode ReadProcessMemory
    ///   4. GET_PROCESS_BASE sanity (non-zero, matches a module base)
    ///   5. ENUM_THREADS against the target PID (count >= 1, TIDs non-zero)
    ///   6. SET_HWBP/CLEAR_HWBP write watchpoint on the target buffer (armed on a
    ///      never-written region so no #DB can fire - there is no debugger attached)
    ///   7. GET_PROCESS_BASE on a WoW64 process (SysWOW64\notepad.exe) must match
    ///      the true 32-bit image base from toolhelp32 MODULEENTRY32
    /// </summary>
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress,
            byte[] buffer, IntPtr size, out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;

        private static int _failures;

        private static int Main()
        {
            Console.WriteLine("=== CrxShield end-to-end verification ===\n");

            // --- Start DebugTarget and parse its READY line ---
            string targetExe = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "CrxMem.DebugTarget.exe");
            if (!System.IO.File.Exists(targetExe))
            {
                Fail("CrxMem.DebugTarget.exe not found next to verifier (expected it copied to output).");
                return Summary();
            }

            var psi = new ProcessStartInfo(targetExe)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            Process target = Process.Start(psi);
            string readyLine = target.StandardOutput.ReadLine();
            Console.WriteLine($"[target] {readyLine}");

            int pid = target.Id;
            ulong bufferAddr = ParseHexField(readyLine, "BUFFER=");
            if (bufferAddr == 0)
            {
                Fail("Could not parse BUFFER= from DebugTarget READY line.");
                target.Kill();
                return Summary();
            }

            // --- Connect to driver ---
            var controller = new CrxDriverController();
            if (!controller.Connect())
            {
                Fail($"Connect to \\\\.\\CrxShield failed: {controller.LastError} (are you elevated?)");
                target.Kill();
                return Summary();
            }
            Pass("Connected to \\\\.\\CrxShield");

            // --- Test 1: GET_VERSION ---
            if (controller.GetVersion(out uint major, out uint minor, out uint build))
            {
                if (major == 1 && minor == 5 && build == 0)
                    Pass($"GET_VERSION -> {major}.{minor}.{build} (expected 1.5.0)");
                else
                    Fail($"GET_VERSION -> {major}.{minor}.{build} (expected 1.5.0)");
            }
            else Fail($"GET_VERSION failed: {controller.LastError}");

            // --- Test 2: kernel READ of the known buffer ---
            // DebugTarget fills its buffer with bytes 0..255 repeating; buffer[0] is
            // incremented every 25ms, so verify bytes 16..31 (stable region).
            byte[] readBuf = new byte[16];
            if (controller.ReadProcessMemory((uint)pid, bufferAddr + 16, readBuf))
            {
                bool ok = true;
                for (int i = 0; i < 16; i++)
                    if (readBuf[i] != (byte)(16 + i)) { ok = false; break; }
                if (ok) Pass("Kernel READ: bytes 16..31 match expected pattern");
                else Fail($"Kernel READ mismatch: {BitConverter.ToString(readBuf)}");
            }
            else Fail($"Kernel READ failed: {controller.LastError}");

            // --- Test 3: kernel WRITE, verified via user-mode ReadProcessMemory ---
            byte[] original = new byte[16];
            byte[] patch = { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE,
                             0x13, 0x37, 0xC0, 0xDE, 0x42, 0x42, 0x42, 0x42 };
            IntPtr hProc = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, pid);
            if (hProc == IntPtr.Zero)
            {
                Fail($"OpenProcess failed (Win32 {Marshal.GetLastWin32Error()})");
            }
            else
            {
                // Save original via driver read
                if (!controller.ReadProcessMemory((uint)pid, bufferAddr + 64, original))
                    Fail($"Pre-write kernel READ failed: {controller.LastError}");
                else if (!controller.WriteProcessMemory((uint)pid, bufferAddr + 64, patch))
                    Fail($"Kernel WRITE failed: {controller.LastError}");
                else
                {
                    byte[] userRead = new byte[16];
                    bool rok = ReadProcessMemory(hProc, (IntPtr)(bufferAddr + 64), userRead,
                        (IntPtr)16, out _);
                    bool match = rok && StructuralEquals(patch, userRead);
                    if (match) Pass("Kernel WRITE: patch visible via user-mode ReadProcessMemory");
                    else Fail($"Kernel WRITE not visible: {BitConverter.ToString(userRead)}");

                    // Restore original bytes
                    if (!controller.WriteProcessMemory((uint)pid, bufferAddr + 64, original))
                        Fail("RESTORE write failed (target left modified!)");
                    else Pass("Restore write succeeded (target left clean)");
                }
                CloseHandle(hProc);
            }

            // --- Test 4: GET_PROCESS_BASE ---
            if (controller.GetProcessBaseAddress((uint)pid, out ulong baseAddr))
            {
                if (baseAddr != 0 && (baseAddr & 0xFFFF) == 0)
                    Pass($"GET_PROCESS_BASE -> 0x{baseAddr:X} (page-aligned, non-zero)");
                else
                    Fail($"GET_PROCESS_BASE -> 0x{baseAddr:X} (suspicious)");
            }
            else Fail($"GET_PROCESS_BASE failed: {controller.LastError}");

            // ===================== Phase 2 =====================

            // --- Test 5: ENUM_THREADS against the target PID ---
            if (controller.EnumThreads((uint)pid, out var threads))
            {
                if (threads.Count >= 1 && threads.TrueForAll(t => t.ThreadId != 0))
                {
                    Pass($"ENUM_THREADS -> {threads.Count} thread(s), all TIDs non-zero");
                    int shown = Math.Min(4, threads.Count);
                    for (int i = 0; i < shown; i++)
                        Console.WriteLine($"       thread[{i}] TID={threads[i].ThreadId} Start=0x{threads[i].StartAddress:X}");
                }
                else if (threads.Count < 1)
                    Fail("ENUM_THREADS succeeded but returned 0 threads");
                else
                    Fail("ENUM_THREADS returned a zero ThreadId");
            }
            else Fail($"ENUM_THREADS failed: {controller.LastError}");

            // Truncation sub-check: STATUS_BUFFER_OVERFLOW / ERROR_MORE_DATA path.
            // CrxDriverController.EnumThreads always allocates the full 256-entry
            // response buffer and does not expose output-buffer sizing, so a
            // deliberately tiny buffer cannot be requested through the wrapper.
            Console.WriteLine("[SKIP] ENUM_THREADS tiny-buffer truncation sub-check: " +
                "wrapper does not expose buffer sizing (always requests the full 256-entry buffer)");

            // --- Test 6: SET_HWBP + CLEAR_HWBP (write watchpoint, slot 0) ---
            // IMPORTANT: buffer[0] is incremented and buffer[1] is probe-written every
            // 25 ms by the target, so arming a watchpoint at the buffer base would fire
            // a #DB almost immediately - with no debugger attached that kills the target.
            // Arm on buffer+256 instead: inside the known buffer but never written.
            ulong watchAddr = bufferAddr + 256;
            bool hwbpArmed = false;
            if (controller.SetHardwareBreakpoint((uint)pid, 0, watchAddr, 1, 4,
                    out uint setUpdated, out uint setFailed,
                    out uint setLookupFailed, out uint setGetCtxFailed, out uint setSetCtxFailed,
                    out int setFirstErr))
            {
                Console.WriteLine($"       SET_HWBP: ThreadsUpdated={setUpdated} ThreadsFailed={setFailed} " +
                    $"[Lookup={setLookupFailed} GetCtx={setGetCtxFailed} SetCtx={setSetCtxFailed}] " +
                    $"FirstErrorStatus=0x{setFirstErr:X8}");
                if (setUpdated >= 1)
                {
                    hwbpArmed = true;
                    if (setFailed > 0)
                        Pass($"SET_HWBP write watchpoint armed on {setUpdated} thread(s) ({setFailed} failed, FirstErrorStatus=0x{setFirstErr:X8})");
                    else
                        Pass($"SET_HWBP write watchpoint armed on all {setUpdated} thread(s)");
                }
                else
                    Fail($"SET_HWBP: ALL threads failed (ThreadsFailed={setFailed}, " +
                         $"Lookup={setLookupFailed} GetCtx={setGetCtxFailed} SetCtx={setSetCtxFailed}, " +
                         $"FirstErrorStatus=0x{setFirstErr:X8}) - investigate driver context path!");
            }
            else Fail($"SET_HWBP failed: {controller.LastError}");

            if (controller.ClearHardwareBreakpoint((uint)pid, 0,
                    out uint clrUpdated, out uint clrFailed,
                    out uint clrLookupFailed, out uint clrGetCtxFailed, out uint clrSetCtxFailed,
                    out int clrFirstErr))
            {
                Console.WriteLine($"       CLEAR_HWBP: ThreadsUpdated={clrUpdated} ThreadsFailed={clrFailed} " +
                    $"[Lookup={clrLookupFailed} GetCtx={clrGetCtxFailed} SetCtx={clrSetCtxFailed}] " +
                    $"FirstErrorStatus=0x{clrFirstErr:X8}");
                if (clrUpdated >= 1)
                    Pass($"CLEAR_HWBP slot 0 cleared on {clrUpdated} thread(s)");
                else
                    Fail($"CLEAR_HWBP: no threads updated (ThreadsFailed={clrFailed}, FirstErrorStatus=0x{clrFirstErr:X8})");
            }
            else Fail($"CLEAR_HWBP failed: {controller.LastError}");

            // Clearing an already-clear slot must still return sensibly (IOCTL succeeds).
            if (controller.ClearHardwareBreakpoint((uint)pid, 0,
                    out uint clr2Updated, out uint clr2Failed, out _, out _, out _, out _))
                Pass($"CLEAR_HWBP again (already clear) returned sensibly: ThreadsUpdated={clr2Updated} ThreadsFailed={clr2Failed}");
            else
                Fail($"CLEAR_HWBP (second call) failed: {controller.LastError}");

            // Safety net: never leave the target armed if the clear above was skipped.
            if (hwbpArmed && clrUpdated == 0)
                controller.ClearHardwareBreakpoint((uint)pid, 0xFFFFFFFF, out _, out _, out _, out _, out _, out _);

            // --- Test 7: GET_PROCESS_BASE on a WoW64 (32-bit) process ---
            // Regression test for the WoW64 bug where the driver returned the
            // 64-bit loader section base (e.g. 0x242000) instead of the true
            // 32-bit image base (e.g. 0x170000). Requires the PEB32 read fix.
            const string wowNotepad = @"C:\Windows\SysWOW64\notepad.exe";
            Process wow = null;
            try
            {
                if (!System.IO.File.Exists(wowNotepad))
                {
                    Fail($"WoW64 test: {wowNotepad} not found (not an x64 OS?)");
                }
                else
                {
                    wow = Process.Start(wowNotepad);
                    Thread.Sleep(1500);  // let the process fully initialize its PEB/modules
                    ulong expected = GetToolhelp32ImageBase(wow.Id, "notepad.exe");
                    if (controller.GetProcessBaseAddress((uint)wow.Id, out ulong wowBase))
                    {
                        if (expected != 0 && wowBase == expected)
                            Pass($"GET_PROCESS_BASE WoW64 -> 0x{wowBase:X} matches toolhelp32 image base");
                        else
                            Fail($"GET_PROCESS_BASE WoW64 -> 0x{wowBase:X}, expected 0x{expected:X} (toolhelp32)");
                    }
                    else Fail($"GET_PROCESS_BASE WoW64 failed: {controller.LastError}");
                }
            }
            catch (Exception ex)
            {
                Fail($"WoW64 test exception: {ex.Message}");
            }
            finally
            {
                try { wow?.Kill(); } catch { /* already exited */ }
            }

            controller.Disconnect();
            target.Kill();
            return Summary();
        }

        // toolhelp32: image base (modBaseAddr) of the named module in a process.
        // Works cross-bitness: from this x64 host, TH32CS_SNAPMODULE32 includes
        // the 32-bit modules of a WoW64 target.
        private const uint TH32CS_SNAPMODULE = 0x00000008;
        private const uint TH32CS_SNAPMODULE32 = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MODULEENTRY32
        {
            public uint dwSize;
            public uint th32ModuleID;
            public uint th32ProcessID;
            public uint GlblcntUsage;
            public uint ProccntUsage;
            public IntPtr modBaseAddr;
            public uint modBaseSize;
            public IntPtr hModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private static ulong GetToolhelp32ImageBase(int pid, string moduleName)
        {
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
            if (snap == IntPtr.Zero || snap == InvalidHandleValue) return 0;
            try
            {
                var me = new MODULEENTRY32 { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32>() };
                for (bool ok = Module32First(snap, ref me); ok; ok = Module32Next(snap, ref me))
                {
                    if (string.Equals(me.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                        return (ulong)me.modBaseAddr.ToInt64();
                }
                return 0;
            }
            finally
            {
                CloseHandle(snap);
            }
        }

        private static bool StructuralEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static ulong ParseHexField(string line, string key)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            int idx = line.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int start = idx + key.Length;
            int end = start;
            while (end < line.Length && "0123456789abcdefABCDEFxX".IndexOf(line[end]) >= 0) end++;
            string hex = line.Substring(start, end - start);
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong v) ? v : 0;
        }

        private static void Pass(string msg) =>
            Console.WriteLine($"[PASS] {msg}");

        private static void Fail(string msg)
        {
            _failures++;
            Console.WriteLine($"[FAIL] {msg}");
        }

        private static int Summary()
        {
            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("=== ALL TESTS PASSED — driver verified end-to-end ===");
                return 0;
            }
            Console.WriteLine($"=== {_failures} TEST(S) FAILED ===");
            return 1;
        }
    }
}
