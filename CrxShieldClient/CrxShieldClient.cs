using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CrxShield.Client
{
    /// <summary>
    /// P/Invoke wrapper for CrxShieldClient.dll — the native client for the
    /// CrxShield kernel driver (\\.\CrxShield).
    ///
    /// The native handle is wrapped by <see cref="CrxSafeHandle"/>, so the
    /// device handle is released automatically on Dispose / finalization even
    /// if <see cref="CrxNative.Disconnect"/> is never called.
    ///
    /// Usage:
    ///   using var handle = CrxNative.Connect();
    ///   if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    ///   if (CrxNative.GetVersion(handle, out var maj, out var min, out var bld)) { ... }
    /// </summary>
    public static class CrxNative
    {
        private const string DllName = "CrxShieldClient.dll";

        /// <summary>
        /// SafeHandle wrapper around the opaque CRX_HANDLE returned by
        /// CrxConnect(). ReleaseHandle calls the native CrxDisconnect.
        /// </summary>
        public sealed class CrxSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public CrxSafeHandle() : base(ownsHandle: true) { }

            protected override bool ReleaseHandle()
            {
                CrxDisconnect(handle);
                return true;
            }
        }

        [DllImport(DllName, EntryPoint = "CrxConnect", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr CrxConnectNative();

        [DllImport(DllName, EntryPoint = "CrxDisconnect", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern void CrxDisconnect(IntPtr h);

        [DllImport(DllName, EntryPoint = "CrxGetVersion", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CrxGetVersion(CrxSafeHandle h, out uint major, out uint minor, out uint build);

        [DllImport(DllName, EntryPoint = "CrxReadMemory", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CrxReadMemory(CrxSafeHandle h, uint pid, ulong address, IntPtr buffer, uint size);

        [DllImport(DllName, EntryPoint = "CrxWriteMemory", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CrxWriteMemory(CrxSafeHandle h, uint pid, ulong address, IntPtr buffer, uint size);

        [DllImport(DllName, EntryPoint = "CrxGetProcessBase", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CrxGetProcessBase(CrxSafeHandle h, uint pid, out ulong baseAddress);

        [DllImport(DllName, EntryPoint = "CrxGetLastError", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint CrxGetLastErrorNative(CrxSafeHandle h);

        /// <summary>Maximum single read/write transfer (16 MB), enforced by the driver.</summary>
        public const uint MaxTransferSize = 16u * 1024u * 1024u;

        /// <summary>
        /// Open a connection to \\.\CrxShield. Check
        /// <see cref="SafeHandle.IsInvalid"/> on the result; on failure call
        /// <see cref="Marshal.GetLastWin32Error"/> for the Win32 error.
        /// </summary>
        public static CrxSafeHandle Connect()
        {
            IntPtr raw = CrxConnectNative();
            var handle = new CrxSafeHandle();
            handle.SetHandle(raw);
            return handle;
        }

        /// <summary>
        /// Explicitly close the handle. Optional — Dispose/finalization does
        /// the same. Calling with an invalid handle is a no-op.
        /// </summary>
        public static void Disconnect(CrxSafeHandle h)
        {
            h?.Dispose();
        }

        /// <summary>Query the driver version (major/minor/build).</summary>
        public static bool GetVersion(CrxSafeHandle h, out uint major, out uint minor, out uint build)
            => CrxGetVersion(h, out major, out minor, out build);

        /// <summary>Read up to <see cref="MaxTransferSize"/> bytes from the target process.</summary>
        public static unsafe bool ReadMemory(CrxSafeHandle h, uint pid, ulong address, byte[] buffer, uint size)
        {
            if (buffer == null || size == 0 || size > buffer.Length)
                return false;
            fixed (byte* p = buffer)
                return CrxReadMemory(h, pid, address, (IntPtr)p, size);
        }

        /// <summary>Write up to <see cref="MaxTransferSize"/> bytes into the target process.</summary>
        public static unsafe bool WriteMemory(CrxSafeHandle h, uint pid, ulong address, byte[] buffer, uint size)
        {
            if (buffer == null || size == 0 || size > buffer.Length)
                return false;
            fixed (byte* p = buffer)
                return CrxWriteMemory(h, pid, address, (IntPtr)p, size);
        }

        /// <summary>Query the main module base address of the target process.</summary>
        public static bool GetProcessBase(CrxSafeHandle h, uint pid, out ulong baseAddress)
            => CrxGetProcessBase(h, pid, out baseAddress);

        /// <summary>Per-handle Win32 error code from the last failing call on this handle.</summary>
        public static uint GetLastError(CrxSafeHandle h)
            => CrxGetLastErrorNative(h);

        /// <summary>Per-handle last error as a readable message.</summary>
        public static string GetLastErrorMessage(CrxSafeHandle h)
        {
            int err = unchecked((int)GetLastError(h));
            return new Win32Exception(err).Message;
        }
    }
}
