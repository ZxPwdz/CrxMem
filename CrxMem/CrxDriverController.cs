using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace CrxShield
{
    public class CrxDriverController : IDisposable
    {
        private SafeFileHandle m_hDevice;
        private const string DeviceName = @"\\.\CrxShield";

        // IOCTL Codes
        private const uint FILE_DEVICE_UNKNOWN = 0x00000022;
        private const uint METHOD_BUFFERED = 0;
        private const uint FILE_ANY_ACCESS = 0;

        private static uint CTL_CODE(uint DeviceType, uint Function, uint Method, uint Access)
        {
            return ((DeviceType << 16) | (Access << 14) | (Function << 2) | Method);
        }

        private const uint IOCTL_BASE = 0x1337;
        public static readonly uint IOCTL_CRXSHIELD_GET_VERSION = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x0, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_READ_MEMORY = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x1, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_WRITE_MEMORY = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x2, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_GET_PROCESS_BASE = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x3, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_ENUM_THREADS = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x4, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_SET_HWBP = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x5, METHOD_BUFFERED, FILE_ANY_ACCESS);
        public static readonly uint IOCTL_CRXSHIELD_CLEAR_HWBP = CTL_CODE(FILE_DEVICE_UNKNOWN, IOCTL_BASE + 0x6, METHOD_BUFFERED, FILE_ANY_ACCESS);

        // Structures
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct CRXSHIELD_VERSION
        {
            public uint Major;
            public uint Minor;
            public uint Build;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_READ_REQUEST
        {
            public uint ProcessId;
            public ulong Address;
            public uint Size;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_WRITE_REQUEST
        {
            public uint ProcessId;
            public ulong Address;
            public uint Size;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_PROCESS_BASE_REQUEST
        {
            public uint ProcessId;
            public ulong BaseAddress;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_ENUM_THREADS_REQUEST
        {
            public uint ProcessId;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_THREAD_INFO
        {
            public uint ThreadId;
            public ulong StartAddress;
        }

        private const int MAX_ENUM_THREADS = 256;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_ENUM_THREADS_RESPONSE
        {
            public uint Count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_ENUM_THREADS)]
            public CRXSHIELD_THREAD_INFO[] Threads;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_SET_HWBP_REQUEST
        {
            public uint ProcessId;
            public uint Slot;
            public ulong Address;
            public uint Type;
            public uint Length;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_CLEAR_HWBP_REQUEST
        {
            public uint ProcessId;
            public uint Slot;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct CRXSHIELD_HWBP_RESPONSE
        {
            public uint ThreadsUpdated;
            public uint ThreadsFailed;
            public uint LookupFailed;     // PsLookupThreadByThreadId failures
            public uint GetCtxFailed;     // APC queue/timeout or get-context failures
            public uint SetCtxFailed;     // set-context failures inside the target thread
            public int FirstErrorStatus;  // first failing per-thread NTSTATUS (0 if none failed)
        }

        // Win32 mapping of STATUS_BUFFER_OVERFLOW (0x80000005): the driver may
        // return partial thread data with this status; treat it as success-with-truncation.
        private const int ERROR_MORE_DATA = 234;

        // P/Invoke
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped
        );

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        public CrxDriverController()
        {
            m_hDevice = new SafeFileHandle(IntPtr.Zero, true);
        }

        public bool Connect()
        {
            m_hDevice = CreateFile(
                DeviceName,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero
            );

            return !m_hDevice.IsInvalid;
        }

        public void Disconnect()
        {
            if (m_hDevice != null && !m_hDevice.IsInvalid)
            {
                m_hDevice.Close();
                m_hDevice.SetHandleAsInvalid();
            }
        }

        public bool IsConnected()
        {
            return m_hDevice != null && !m_hDevice.IsInvalid;
        }

        public string? LastError { get; private set; }

        public bool GetVersion(out uint Major, out uint Minor, out uint Build)
        {
            Major = 0; Minor = 0; Build = 0;
            LastError = null;
            if (!IsConnected())
            {
                LastError = "Not connected to driver";
                return false;
            }

            var version = new CRXSHIELD_VERSION();
            uint bytesReturned;
            
            // Marshalling struct to pointer for DeviceIoControl
            int size = Marshal.SizeOf(version);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(version, ptr, false);

            try
            {
                if (DeviceIoControl(m_hDevice, IOCTL_CRXSHIELD_GET_VERSION, IntPtr.Zero, 0, ptr, (uint)size, out bytesReturned, IntPtr.Zero))
                {
                    version = Marshal.PtrToStructure<CRXSHIELD_VERSION>(ptr);
                    Major = version.Major;
                    Minor = version.Minor;
                    Build = version.Build;
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                LastError = $"GET_VERSION DeviceIoControl failed (Win32 error {error}): {new Win32Exception(error).Message}";
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return false;
        }

        public bool ReadProcessMemory(uint ProcessId, ulong Address, byte[] Buffer)
        {
            if (!IsConnected() || Buffer == null || Buffer.Length == 0) return false;

            var req = new CRXSHIELD_READ_REQUEST
            {
                ProcessId = ProcessId,
                Address = Address,
                Size = (uint)Buffer.Length
            };

            int reqSize = Marshal.SizeOf(req);
            IntPtr reqPtr = Marshal.AllocHGlobal(reqSize);
            Marshal.StructureToPtr(req, reqPtr, false);

            GCHandle bufferHandle = GCHandle.Alloc(Buffer, GCHandleType.Pinned);

            try
            {
                uint bytesReturned;
                return DeviceIoControl(m_hDevice, IOCTL_CRXSHIELD_READ_MEMORY, reqPtr, (uint)reqSize, bufferHandle.AddrOfPinnedObject(), (uint)Buffer.Length, out bytesReturned, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(reqPtr);
                bufferHandle.Free();
            }
        }

        public bool WriteProcessMemory(uint ProcessId, ulong Address, byte[] Buffer)
        {
            if (!IsConnected() || Buffer == null || Buffer.Length == 0) return false;

            var req = new CRXSHIELD_WRITE_REQUEST
            {
                ProcessId = ProcessId,
                Address = Address,
                Size = (uint)Buffer.Length
            };

            // Allocate a buffer that holds Request + Data
            int reqSize = Marshal.SizeOf(req);
            int totalSize = reqSize + Buffer.Length;
            IntPtr ptr = Marshal.AllocHGlobal(totalSize);

            try
            {
                Marshal.StructureToPtr(req, ptr, false);
                Marshal.Copy(Buffer, 0, IntPtr.Add(ptr, reqSize), Buffer.Length);

                uint bytesReturned;
                return DeviceIoControl(m_hDevice, IOCTL_CRXSHIELD_WRITE_MEMORY, ptr, (uint)totalSize, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public bool GetProcessBaseAddress(uint ProcessId, out ulong BaseAddress)
        {
            BaseAddress = 0;
            if (!IsConnected()) return false;

            var req = new CRXSHIELD_PROCESS_BASE_REQUEST
            {
                ProcessId = ProcessId,
                BaseAddress = 0
            };

            int size = Marshal.SizeOf(req);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(req, ptr, false);

            try
            {
                uint bytesReturned;
                if (DeviceIoControl(m_hDevice, IOCTL_CRXSHIELD_GET_PROCESS_BASE, ptr, (uint)size, ptr, (uint)size, out bytesReturned, IntPtr.Zero))
                {
                    req = Marshal.PtrToStructure<CRXSHIELD_PROCESS_BASE_REQUEST>(ptr);
                    BaseAddress = req.BaseAddress;
                    return true;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return false;
        }

        /// <summary>
        /// Enumerate the target process's threads (TID + start address) from the kernel.
        /// STATUS_BUFFER_OVERFLOW from the driver is surfaced as ERROR_MORE_DATA and is
        /// treated as success-with-truncation: the partial list is still returned.
        /// </summary>
        public bool EnumThreads(uint ProcessId, out List<(uint ThreadId, ulong StartAddress)> Threads)
        {
            Threads = new List<(uint, ulong)>();
            LastError = null;
            if (!IsConnected())
            {
                LastError = "Not connected to driver";
                return false;
            }

            var req = new CRXSHIELD_ENUM_THREADS_REQUEST { ProcessId = ProcessId };
            var resp = new CRXSHIELD_ENUM_THREADS_RESPONSE
            {
                Count = 0,
                Threads = new CRXSHIELD_THREAD_INFO[MAX_ENUM_THREADS]
            };

            int reqSize = Marshal.SizeOf(req);
            int respSize = Marshal.SizeOf(resp);
            IntPtr reqPtr = Marshal.AllocHGlobal(reqSize);
            IntPtr respPtr = Marshal.AllocHGlobal(respSize);
            Marshal.StructureToPtr(req, reqPtr, false);
            Marshal.StructureToPtr(resp, respPtr, false);

            try
            {
                uint bytesReturned;
                bool ok = DeviceIoControl(m_hDevice, IOCTL_CRXSHIELD_ENUM_THREADS, reqPtr, (uint)reqSize, respPtr, (uint)respSize, out bytesReturned, IntPtr.Zero);
                if (!ok && Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
                {
                    int error = Marshal.GetLastWin32Error();
                    LastError = $"ENUM_THREADS DeviceIoControl failed (Win32 error {error}): {new Win32Exception(error).Message}";
                    return false;
                }

                resp = Marshal.PtrToStructure<CRXSHIELD_ENUM_THREADS_RESPONSE>(respPtr);

                // Clamp to what the driver claims, the bytes actually returned, and the array capacity.
                uint available = bytesReturned >= sizeof(uint) ? (bytesReturned - sizeof(uint)) / (uint)Marshal.SizeOf(typeof(CRXSHIELD_THREAD_INFO)) : 0;
                int count = (int)Math.Min(Math.Min(resp.Count, available), MAX_ENUM_THREADS);
                for (int i = 0; i < count; i++)
                    Threads.Add((resp.Threads[i].ThreadId, resp.Threads[i].StartAddress));

                if (!ok)
                    LastError = $"ENUM_THREADS truncated (STATUS_BUFFER_OVERFLOW): returned {count} of {resp.Count} threads";
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(reqPtr);
                Marshal.FreeHGlobal(respPtr);
            }
        }

        /// <summary>
        /// Program a hardware breakpoint (DR0-DR3) into every thread of the target process.
        /// Slot 0-3; Type 0=execute, 1=write, 2=read/write; Length 1/2/4/8.
        /// Per-stage failure counters: LookupFailed (thread lookup), GetCtxFailed (APC
        /// queue/timeout/get-context), SetCtxFailed (set-context in target thread).
        /// FirstErrorStatus carries the first failing per-thread NTSTATUS (0 when none failed).
        /// </summary>
        public bool SetHardwareBreakpoint(uint ProcessId, uint Slot, ulong Address, uint Type, uint Length,
            out uint ThreadsUpdated, out uint ThreadsFailed,
            out uint LookupFailed, out uint GetCtxFailed, out uint SetCtxFailed,
            out int FirstErrorStatus)
        {
            ThreadsUpdated = 0; ThreadsFailed = 0;
            LookupFailed = 0; GetCtxFailed = 0; SetCtxFailed = 0; FirstErrorStatus = 0;
            LastError = null;
            if (!IsConnected())
            {
                LastError = "Not connected to driver";
                return false;
            }
            if (Slot > 3)
            {
                LastError = "Invalid debug register slot (must be 0-3)";
                return false;
            }
            if (Type > 2)
            {
                LastError = "Invalid breakpoint type (0=execute, 1=write, 2=read/write)";
                return false;
            }
            if (Length != 1 && Length != 2 && Length != 4 && Length != 8)
            {
                LastError = "Invalid breakpoint length (must be 1, 2, 4, or 8)";
                return false;
            }

            var req = new CRXSHIELD_SET_HWBP_REQUEST
            {
                ProcessId = ProcessId,
                Slot = Slot,
                Address = Address,
                Type = Type,
                Length = Length
            };

            return HwbpIoctl(IOCTL_CRXSHIELD_SET_HWBP, "SET_HWBP", req,
                out ThreadsUpdated, out ThreadsFailed,
                out LookupFailed, out GetCtxFailed, out SetCtxFailed, out FirstErrorStatus);
        }

        /// <summary>
        /// Clear a hardware breakpoint slot in every thread of the target process.
        /// Slot 0-3, or 0xFFFFFFFF to clear all four slots.
        /// Per-stage failure counters: LookupFailed (thread lookup), GetCtxFailed (APC
        /// queue/timeout/get-context), SetCtxFailed (set-context in target thread).
        /// FirstErrorStatus carries the first failing per-thread NTSTATUS (0 when none failed).
        /// </summary>
        public bool ClearHardwareBreakpoint(uint ProcessId, uint Slot,
            out uint ThreadsUpdated, out uint ThreadsFailed,
            out uint LookupFailed, out uint GetCtxFailed, out uint SetCtxFailed,
            out int FirstErrorStatus)
        {
            ThreadsUpdated = 0; ThreadsFailed = 0;
            LookupFailed = 0; GetCtxFailed = 0; SetCtxFailed = 0; FirstErrorStatus = 0;
            LastError = null;
            if (!IsConnected())
            {
                LastError = "Not connected to driver";
                return false;
            }
            if (Slot > 3 && Slot != 0xFFFFFFFF)
            {
                LastError = "Invalid debug register slot (must be 0-3 or 0xFFFFFFFF for all)";
                return false;
            }

            var req = new CRXSHIELD_CLEAR_HWBP_REQUEST
            {
                ProcessId = ProcessId,
                Slot = Slot
            };

            return HwbpIoctl(IOCTL_CRXSHIELD_CLEAR_HWBP, "CLEAR_HWBP", req,
                out ThreadsUpdated, out ThreadsFailed,
                out LookupFailed, out GetCtxFailed, out SetCtxFailed, out FirstErrorStatus);
        }

        private bool HwbpIoctl<TRequest>(uint ioctl, string opName, TRequest req,
            out uint threadsUpdated, out uint threadsFailed,
            out uint lookupFailed, out uint getCtxFailed, out uint setCtxFailed,
            out int firstErrorStatus)
            where TRequest : struct
        {
            threadsUpdated = 0; threadsFailed = 0;
            lookupFailed = 0; getCtxFailed = 0; setCtxFailed = 0; firstErrorStatus = 0;

            var resp = new CRXSHIELD_HWBP_RESPONSE();
            int reqSize = Marshal.SizeOf(req);
            int respSize = Marshal.SizeOf(resp);
            IntPtr reqPtr = Marshal.AllocHGlobal(reqSize);
            IntPtr respPtr = Marshal.AllocHGlobal(respSize);
            Marshal.StructureToPtr(req, reqPtr, false);
            Marshal.StructureToPtr(resp, respPtr, false);

            try
            {
                uint bytesReturned;
                if (DeviceIoControl(m_hDevice, ioctl, reqPtr, (uint)reqSize, respPtr, (uint)respSize, out bytesReturned, IntPtr.Zero))
                {
                    resp = Marshal.PtrToStructure<CRXSHIELD_HWBP_RESPONSE>(respPtr);
                    threadsUpdated = resp.ThreadsUpdated;
                    threadsFailed = resp.ThreadsFailed;
                    lookupFailed = resp.LookupFailed;
                    getCtxFailed = resp.GetCtxFailed;
                    setCtxFailed = resp.SetCtxFailed;
                    firstErrorStatus = resp.FirstErrorStatus;
                    return true;
                }

                int error = Marshal.GetLastWin32Error();
                LastError = $"{opName} DeviceIoControl failed (Win32 error {error}): {new Win32Exception(error).Message}";
            }
            finally
            {
                Marshal.FreeHGlobal(reqPtr);
                Marshal.FreeHGlobal(respPtr);
            }

            return false;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
