using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CrxMem.Core
{
    /// <summary>
    /// Manages hardware breakpoints using x86/x64 debug registers (DR0-DR7)
    /// </summary>
    public class HardwareBreakpointManager
    {
        private readonly ProcessAccess _process;
        private readonly bool[] _usedSlots = new bool[4]; // DR0-DR3

        public enum BreakpointType
        {
            Execute = 0,    // DR7 condition = 00
            Write = 1,      // DR7 condition = 01
            ReadWrite = 3   // DR7 condition = 11 (Access)
        }

        public enum BreakpointSize
        {
            Byte1 = 0,  // DR7 size = 00
            Byte2 = 1,  // DR7 size = 01
            Byte4 = 3,  // DR7 size = 11
            Byte8 = 2   // DR7 size = 10 (x64 only)
        }

        public HardwareBreakpointManager(ProcessAccess process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        /// <summary>
        /// Gets a free breakpoint slot (0-3)
        /// </summary>
        public int GetFreeSlot()
        {
            for (int i = 0; i < 4; i++)
            {
                if (!_usedSlots[i])
                    return i;
            }
            return -1; // No free slots
        }

        /// <summary>
        /// Sets a hardware breakpoint on all threads of the target process
        /// </summary>
        public bool SetBreakpoint(int slot, ulong address, BreakpointType type, BreakpointSize size)
        {
            if (slot < 0 || slot > 3)
                throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 0-3");

            if (_process?.Target == null)
                return false;

            var threads = GetProcessThreads();
            if (threads.Count == 0)
                return false;

            bool success = true;
            foreach (uint threadId in threads)
            {
                if (!SetBreakpointOnThread(threadId, slot, address, type, size))
                {
                    success = false;
                }
            }

            if (success)
            {
                _usedSlots[slot] = true;
            }

            return success;
        }

        /// <summary>
        /// Applies an already-owned hardware breakpoint to one thread. Debug-event callers
        /// pass false because a CREATE_THREAD_DEBUG_EVENT already holds the new thread.
        /// </summary>
        public bool ApplyBreakpointToThread(
            uint threadId,
            int slot,
            ulong address,
            BreakpointType type,
            BreakpointSize size,
            bool suspendThread = true) =>
            SetBreakpointOnThread(threadId, slot, address, type, size, suspendThread);

        public bool ClearBreakpointFromThread(uint threadId, int slot, bool suspendThread = true) =>
            ClearBreakpointOnThread(threadId, slot, suspendThread);

        /// <summary>
        /// Clears a hardware breakpoint from all threads
        /// </summary>
        public bool ClearBreakpoint(int slot)
        {
            if (slot < 0 || slot > 3)
                throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 0-3");

            if (_process?.Target == null)
                return false;

            var threads = GetProcessThreads();
            bool success = true;

            foreach (uint threadId in threads)
            {
                if (!ClearBreakpointOnThread(threadId, slot))
                {
                    success = false;
                }
            }

            _usedSlots[slot] = false;
            return success;
        }

        /// <summary>
        /// Clears a slot while a native debug event already holds every target thread.
        /// This avoids changing suspend counts owned by the debugger.
        /// </summary>
        public bool ClearBreakpointWhileDebugPaused(int slot)
        {
            if (slot < 0 || slot > 3)
                throw new ArgumentOutOfRangeException(nameof(slot), "Slot must be 0-3");
            if (_process?.Target == null) return false;
            bool success = true;
            foreach (uint threadId in GetProcessThreads())
                if (!ClearBreakpointOnThread(threadId, slot, suspendThread: false)) success = false;
            _usedSlots[slot] = false;
            return success;
        }

        /// <summary>
        /// Clears all hardware breakpoints
        /// </summary>
        public void ClearAllBreakpoints()
        {
            for (int i = 0; i < 4; i++)
            {
                if (_usedSlots[i])
                {
                    ClearBreakpoint(i);
                }
            }
        }

        /// <summary>
        /// Gets all thread IDs for the target process
        /// </summary>
        private List<uint> GetProcessThreads()
        {
            var threads = new List<uint>();
            uint processId = (uint)_process.Target.Id;

            IntPtr snapshot = ProcessAccess.CreateToolhelp32Snapshot(ProcessAccess.TH32CS_SNAPTHREAD, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return threads;

            try
            {
                var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };

                if (ProcessAccess.Thread32First(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32OwnerProcessID == processId)
                        {
                            threads.Add(entry.th32ThreadID);
                        }
                        entry.dwSize = (uint)Marshal.SizeOf<THREADENTRY32>();
                    }
                    while (ProcessAccess.Thread32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return threads;
        }

        private bool SetBreakpointOnThread(uint threadId, int slot, ulong address, BreakpointType type, BreakpointSize size, bool suspendThread = true)
        {
            IntPtr hThread = ProcessAccess.OpenThread(
                ProcessAccess.THREAD_GET_CONTEXT | ProcessAccess.THREAD_SET_CONTEXT | ProcessAccess.THREAD_SUSPEND_RESUME,
                false, threadId);

            if (hThread == IntPtr.Zero)
                return false;

            try
            {
                // Suspend the thread
                if (suspendThread && ProcessAccess.SuspendThread(hThread) == 0xFFFFFFFF)
                    return false;

                try
                {
                    if (_process.Is64Bit)
                    {
                        return SetBreakpoint64(hThread, slot, address, type, size);
                    }
                    else
                    {
                        return SetBreakpoint32(hThread, slot, (uint)address, type, size);
                    }
                }
                finally
                {
                    if (suspendThread) ProcessAccess.ResumeThread(hThread);
                }
            }
            finally
            {
                CloseHandle(hThread);
            }
        }

        private bool SetBreakpoint64(IntPtr hThread, int slot, ulong address, BreakpointType type, BreakpointSize size)
        {
            var context = new CONTEXT64
            {
                ContextFlags = ProcessAccess.CONTEXT_ALL,
                FltSave = new byte[512],
                VectorRegister = new M128A[26]
            };

            if (!ProcessAccess.GetThreadContext(hThread, ref context))
                return false;

            // Set the address in the appropriate DR register
            switch (slot)
            {
                case 0: context.Dr0 = address; break;
                case 1: context.Dr1 = address; break;
                case 2: context.Dr2 = address; break;
                case 3: context.Dr3 = address; break;
            }

            // Configure DR7
            // Each breakpoint uses 2 bits for local enable, 2 bits for condition, 2 bits for size
            // Slot 0: bits 0 (L0), 16-17 (cond), 18-19 (size)
            // Slot 1: bits 2 (L1), 20-21 (cond), 22-23 (size)
            // Slot 2: bits 4 (L2), 24-25 (cond), 26-27 (size)
            // Slot 3: bits 6 (L3), 28-29 (cond), 30-31 (size)

            int enableBit = slot * 2;
            int conditionBits = 16 + (slot * 4);
            int sizeBits = 18 + (slot * 4);

            // Clear existing settings for this slot
            context.Dr7 &= ~((ulong)3 << enableBit);     // Clear enable bits
            context.Dr7 &= ~((ulong)3 << conditionBits); // Clear condition bits
            context.Dr7 &= ~((ulong)3 << sizeBits);      // Clear size bits

            // Set local enable
            context.Dr7 |= (ulong)1 << enableBit;

            // Set condition (type)
            context.Dr7 |= (ulong)type << conditionBits;

            // Set size
            context.Dr7 |= (ulong)size << sizeBits;

            // Clear DR6 status bits for this slot
            context.Dr6 &= ~((ulong)1 << slot);

            if (!ProcessAccess.SetThreadContext(hThread, ref context))
                return false;

            // Verify the breakpoint was actually set (anti-cheat may clear it immediately)
            var verify = new CONTEXT64
            {
                ContextFlags = ProcessAccess.CONTEXT_DEBUG_REGISTERS,
                FltSave = new byte[512],
                VectorRegister = new M128A[26]
            };

            if (ProcessAccess.GetThreadContext(hThread, ref verify))
            {
                ulong actualDr = slot switch
                {
                    0 => verify.Dr0,
                    1 => verify.Dr1,
                    2 => verify.Dr2,
                    3 => verify.Dr3,
                    _ => 0
                };

                if (actualDr != address)
                {
                    System.Diagnostics.Debug.WriteLine($"HW BP verification failed: DR{slot} expected 0x{address:X}, got 0x{actualDr:X}");
                    // Don't return false here - the breakpoint might still work even if verification fails
                    // Some anti-cheats clear it briefly then restore it
                }
            }

            return true;
        }

        private bool SetBreakpoint32(IntPtr hThread, int slot, uint address, BreakpointType type, BreakpointSize size)
        {
            var context = new CONTEXT32
            {
                ContextFlags = ProcessAccess.CONTEXT32_ALL,
                FloatSave = new byte[112],
                ExtendedRegisters = new byte[512]
            };

            if (!ProcessAccess.Wow64GetThreadContext(hThread, ref context))
                return false;

            // Set the address in the appropriate DR register
            switch (slot)
            {
                case 0: context.Dr0 = address; break;
                case 1: context.Dr1 = address; break;
                case 2: context.Dr2 = address; break;
                case 3: context.Dr3 = address; break;
            }

            // Configure DR7 (same bit layout as 64-bit, just 32-bit values)
            int enableBit = slot * 2;
            int conditionBits = 16 + (slot * 4);
            int sizeBits = 18 + (slot * 4);

            // Clear existing settings for this slot
            context.Dr7 &= ~((uint)3 << enableBit);
            context.Dr7 &= ~((uint)3 << conditionBits);
            context.Dr7 &= ~((uint)3 << sizeBits);

            // Set local enable
            context.Dr7 |= (uint)1 << enableBit;

            // Set condition (type)
            context.Dr7 |= (uint)type << conditionBits;

            // Set size
            context.Dr7 |= (uint)size << sizeBits;

            // Clear DR6 status bits for this slot
            context.Dr6 &= ~((uint)1 << slot);

            if (!ProcessAccess.Wow64SetThreadContext(hThread, ref context))
                return false;

            // Verify the breakpoint was actually set (anti-cheat may clear it immediately)
            var verify = new CONTEXT32
            {
                ContextFlags = ProcessAccess.CONTEXT32_DEBUG_REGISTERS,
                FloatSave = new byte[112],
                ExtendedRegisters = new byte[512]
            };

            if (ProcessAccess.Wow64GetThreadContext(hThread, ref verify))
            {
                uint actualDr = slot switch
                {
                    0 => verify.Dr0,
                    1 => verify.Dr1,
                    2 => verify.Dr2,
                    3 => verify.Dr3,
                    _ => 0
                };

                if (actualDr != address)
                {
                    System.Diagnostics.Debug.WriteLine($"HW BP verification failed (32-bit): DR{slot} expected 0x{address:X8}, got 0x{actualDr:X8}");
                }
            }

            return true;
        }

        private bool ClearBreakpointOnThread(uint threadId, int slot, bool suspendThread = true)
        {
            IntPtr hThread = ProcessAccess.OpenThread(
                ProcessAccess.THREAD_GET_CONTEXT | ProcessAccess.THREAD_SET_CONTEXT | ProcessAccess.THREAD_SUSPEND_RESUME,
                false, threadId);

            if (hThread == IntPtr.Zero)
                return false;

            try
            {
                if (suspendThread && ProcessAccess.SuspendThread(hThread) == 0xFFFFFFFF)
                    return false;

                try
                {
                    if (_process.Is64Bit)
                    {
                        return ClearBreakpoint64(hThread, slot);
                    }
                    else
                    {
                        return ClearBreakpoint32(hThread, slot);
                    }
                }
                finally
                {
                    if (suspendThread) ProcessAccess.ResumeThread(hThread);
                }
            }
            finally
            {
                CloseHandle(hThread);
            }
        }

        private bool ClearBreakpoint64(IntPtr hThread, int slot)
        {
            var context = new CONTEXT64
            {
                ContextFlags = ProcessAccess.CONTEXT_DEBUG_REGISTERS,
                FltSave = new byte[512],
                VectorRegister = new M128A[26]
            };

            if (!ProcessAccess.GetThreadContext(hThread, ref context))
                return false;

            // Clear the address register
            switch (slot)
            {
                case 0: context.Dr0 = 0; break;
                case 1: context.Dr1 = 0; break;
                case 2: context.Dr2 = 0; break;
                case 3: context.Dr3 = 0; break;
            }

            // Clear DR7 enable bit for this slot
            int enableBit = slot * 2;
            context.Dr7 &= ~((ulong)3 << enableBit);

            return ProcessAccess.SetThreadContext(hThread, ref context);
        }

        private bool ClearBreakpoint32(IntPtr hThread, int slot)
        {
            var context = new CONTEXT32
            {
                ContextFlags = ProcessAccess.CONTEXT32_DEBUG_REGISTERS,
                FloatSave = new byte[112],
                ExtendedRegisters = new byte[512]
            };

            if (!ProcessAccess.Wow64GetThreadContext(hThread, ref context))
                return false;

            // Clear the address register
            switch (slot)
            {
                case 0: context.Dr0 = 0; break;
                case 1: context.Dr1 = 0; break;
                case 2: context.Dr2 = 0; break;
                case 3: context.Dr3 = 0; break;
            }

            // Clear DR7 enable bit for this slot
            int enableBit = slot * 2;
            context.Dr7 &= ~((uint)3 << enableBit);

            return ProcessAccess.Wow64SetThreadContext(hThread, ref context);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
