using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CrxShield;

namespace CrxMem.Core
{
    /// <summary>
    /// Handles opening and accessing target process memory
    /// </summary>
    public class ProcessAccess : IMemoryAccessor, IDisposable
    {
        #region Windows API
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize,
            uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize,
            uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll")]
        private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes,
            uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out IntPtr lpThreadId);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_CREATE_THREAD = 0x0002;
        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;

        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_READONLY = 0x02;

        private const uint INFINITE = 0xFFFFFFFF;

        // Debug API imports
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugActiveProcess(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugActiveProcessStop(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DebugSetProcessKillOnExit(bool KillOnExit);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT64 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT64 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Wow64GetThreadContext(IntPtr hThread, ref CONTEXT32 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Wow64SetThreadContext(IntPtr hThread, ref CONTEXT32 lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        // Module enumeration P/Invokes (use handle directly, avoids permission issues)
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameExW(IntPtr hProcess, IntPtr hModule, [Out] char[] lpBaseName, uint nSize);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetModuleBaseNameW(IntPtr hProcess, IntPtr hModule, [Out] char[] lpBaseName, uint nSize);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        private const uint LIST_MODULES_ALL = 0x03;
        private const uint LIST_MODULES_32BIT = 0x01;
        private const uint LIST_MODULES_64BIT = 0x02;

        // Debug constants
        public const uint DBG_CONTINUE = 0x00010002;
        public const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
        public const uint EXCEPTION_SINGLE_STEP = 0x80000004;
        public const uint EXCEPTION_BREAKPOINT = 0x80000003;
        public const uint TH32CS_SNAPTHREAD = 0x00000004;
        public const uint THREAD_ALL_ACCESS = 0x001FFFFF;
        public const uint THREAD_GET_CONTEXT = 0x0008;
        public const uint THREAD_SET_CONTEXT = 0x0010;
        public const uint THREAD_SUSPEND_RESUME = 0x0002;

        // Context flags
        public const uint CONTEXT_AMD64 = 0x00100000;
        public const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x0001;
        public const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x0002;
        public const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_AMD64 | 0x0010;
        public const uint CONTEXT_FULL = CONTEXT_CONTROL | CONTEXT_INTEGER;
        public const uint CONTEXT_ALL = CONTEXT_FULL | CONTEXT_DEBUG_REGISTERS;

        public const uint CONTEXT_i386 = 0x00010000;
        public const uint CONTEXT32_CONTROL = CONTEXT_i386 | 0x0001;
        public const uint CONTEXT32_INTEGER = CONTEXT_i386 | 0x0002;
        public const uint CONTEXT32_DEBUG_REGISTERS = CONTEXT_i386 | 0x0010;
        public const uint CONTEXT32_FULL = CONTEXT32_CONTROL | CONTEXT32_INTEGER;
        public const uint CONTEXT32_ALL = CONTEXT32_FULL | CONTEXT32_DEBUG_REGISTERS;

        // Debug event types
        public const uint EXCEPTION_DEBUG_EVENT = 1;
        public const uint CREATE_THREAD_DEBUG_EVENT = 2;
        public const uint CREATE_PROCESS_DEBUG_EVENT = 3;
        public const uint EXIT_THREAD_DEBUG_EVENT = 4;
        public const uint EXIT_PROCESS_DEBUG_EVENT = 5;
        public const uint LOAD_DLL_DEBUG_EVENT = 6;
        public const uint UNLOAD_DLL_DEBUG_EVENT = 7;
        public const uint OUTPUT_DEBUG_STRING_EVENT = 8;
        public const uint RIP_EVENT = 9;
        #endregion

        public IntPtr Handle { get; private set; }
        public Process Target { get; private set; }
        public bool Is64Bit { get; private set; }
        public bool IsOpen => Handle != IntPtr.Zero;
        public bool IsSuspended { get; private set; }

        // Read cache for kernel mode to reduce expensive driver calls
        private readonly Dictionary<long, CachedRead> _readCache = new();
        private readonly object _cacheLock = new();
        private const int CachePageSize = 4096; // Cache in 4KB pages
        private const int MaxCacheEntries = 256; // Max ~1MB cache
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMilliseconds(50); // 50ms cache lifetime

        private class CachedRead
        {
            public byte[] Data;
            public DateTime Timestamp;
        }

        /// <summary>
        /// Opens a process for memory access (with full access for injection)
        /// </summary>
        public bool Open(int processId)
        {
            try
            {
                Target = Process.GetProcessById(processId);
                Handle = OpenProcess(
                    PROCESS_ALL_ACCESS, // Request full access for injection capabilities
                    false,
                    processId
                );

                if (Handle == IntPtr.Zero)
                    return false;

                // Check if target is 64-bit
                if (Environment.Is64BitOperatingSystem)
                {
                    // IsWow64Process returns true if the process is 32-bit running on 64-bit Windows
                    bool isWow64 = false;
                    bool success = IsWow64Process(Handle, out isWow64);
                    if (success)
                    {
                        Is64Bit = !isWow64; // If WoW64, it's 32-bit; otherwise 64-bit
                    }
                    else
                    {
                        // Failed to detect, assume 64-bit on 64-bit OS
                        Is64Bit = true;
                        System.Diagnostics.Debug.WriteLine($"IsWow64Process failed, assuming 64-bit. Error: {Marshal.GetLastWin32Error()}");
                    }
                    System.Diagnostics.Debug.WriteLine($"Architecture detection: IsWow64={isWow64}, Is64Bit={Is64Bit}");
                }
                else
                {
                    Is64Bit = false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Opens a process by name
        /// </summary>
        public bool Open(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0)
                return false;

            return Open(processes[0].Id);
        }

        /// <summary>
        /// Read memory from the target process
        /// </summary>
        public byte[] Read(IntPtr address, int size)
        {
            // Use kernel driver if enabled
            if (AntiCheatBypassForm.UseKernelMode && AntiCheatBypassForm.DriverController != null && Target != null)
            {
                var kernelResult = ReadWithKernelCache(address, size);
                if (kernelResult != null)
                    return kernelResult;
                // Fall through to usermode if kernel read failed
            }

            // Standard usermode read
            if (!IsOpen)
                return null; // Don't throw, just return null

            byte[] userBuffer = new byte[size];
            if (ReadProcessMemory(Handle, address, userBuffer, size, out int bytesRead))
            {
                if (bytesRead == size)
                    return userBuffer;

                // Partial read - return what we got
                byte[] result = new byte[bytesRead];
                Array.Copy(userBuffer, result, bytesRead);
                return result;
            }

            return null;
        }

        /// <summary>
        /// Read memory using kernel driver with page-level caching
        /// This dramatically reduces the number of expensive kernel calls
        /// </summary>
        private byte[] ReadWithKernelCache(IntPtr address, int size)
        {
            if (Target == null || AntiCheatBypassForm.DriverController == null)
                return null;

            long addr = address.ToInt64();
            byte[] result = new byte[size];
            int resultOffset = 0;
            int remaining = size;

            while (remaining > 0)
            {
                // Calculate page-aligned address
                long pageBase = (addr / CachePageSize) * CachePageSize;
                int offsetInPage = (int)(addr - pageBase);
                int bytesToCopy = Math.Min(remaining, CachePageSize - offsetInPage);

                byte[] pageData = GetCachedPage(pageBase);
                if (pageData == null)
                    return null; // Read failed

                // Copy data from cached page to result
                Array.Copy(pageData, offsetInPage, result, resultOffset, bytesToCopy);

                addr += bytesToCopy;
                resultOffset += bytesToCopy;
                remaining -= bytesToCopy;
            }

            return result;
        }

        /// <summary>
        /// Get a cached page or read it from the driver
        /// </summary>
        private byte[] GetCachedPage(long pageBase)
        {
            lock (_cacheLock)
            {
                // Check cache
                if (_readCache.TryGetValue(pageBase, out CachedRead cached))
                {
                    // Check if still valid
                    if (DateTime.Now - cached.Timestamp < CacheExpiry)
                    {
                        return cached.Data;
                    }
                    // Expired, remove it
                    _readCache.Remove(pageBase);
                }

                // Read from driver
                byte[] pageData = new byte[CachePageSize];
                if (!AntiCheatBypassForm.DriverController.ReadProcessMemory(
                    (uint)Target.Id, (ulong)pageBase, pageData))
                {
                    return null;
                }

                // Cache it
                if (_readCache.Count >= MaxCacheEntries)
                {
                    // Clear old entries
                    ClearExpiredCache();

                    // If still too many, clear all
                    if (_readCache.Count >= MaxCacheEntries)
                    {
                        _readCache.Clear();
                    }
                }

                _readCache[pageBase] = new CachedRead
                {
                    Data = pageData,
                    Timestamp = DateTime.Now
                };

                return pageData;
            }
        }

        /// <summary>
        /// Clear expired cache entries
        /// </summary>
        private void ClearExpiredCache()
        {
            var now = DateTime.Now;
            var keysToRemove = new List<long>();

            foreach (var kvp in _readCache)
            {
                if (now - kvp.Value.Timestamp >= CacheExpiry)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _readCache.Remove(key);
            }
        }

        /// <summary>
        /// Invalidate the read cache (call when you need fresh data)
        /// </summary>
        public void InvalidateReadCache()
        {
            lock (_cacheLock)
            {
                _readCache.Clear();
            }
        }

        /// <summary>
        /// Write memory to the target process
        /// </summary>
        public bool Write(IntPtr address, byte[] data)
        {
            // Use kernel driver if enabled
            if (AntiCheatBypassForm.UseKernelMode && AntiCheatBypassForm.DriverController != null && Target != null)
            {
                // Invalidate cache for the affected pages
                InvalidateCacheForRange(address.ToInt64(), data.Length);

                return AntiCheatBypassForm.DriverController.WriteProcessMemory((uint)Target.Id, (ulong)address.ToInt64(), data);
            }

            // Standard usermode write
            if (!IsOpen)
                throw new InvalidOperationException("Process not opened");

            return WriteProcessMemory(Handle, address, data, data.Length, out int bytesWritten)
                   && bytesWritten == data.Length;
        }

        /// <summary>
        /// Invalidate cache entries for a memory range
        /// </summary>
        private void InvalidateCacheForRange(long address, int size)
        {
            lock (_cacheLock)
            {
                long startPage = (address / CachePageSize) * CachePageSize;
                long endPage = ((address + size - 1) / CachePageSize) * CachePageSize;

                for (long page = startPage; page <= endPage; page += CachePageSize)
                {
                    _readCache.Remove(page);
                }
            }
        }

        /// <summary>
        /// Read a value from memory
        /// </summary>
        public T Read<T>(IntPtr address) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buffer = Read(address, size);
            if (buffer == null)
                throw new Exception("Failed to read memory");

            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Write a value to memory
        /// </summary>
        public bool Write<T>(IntPtr address, T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];

            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
                return Write(address, buffer);
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Write a value to memory with VirtualProtectEx to ensure write permissions
        /// This is more reliable for writing to protected memory regions
        /// </summary>
        public bool WriteWithProtection<T>(IntPtr address, T value) where T : struct
        {
            if (!IsOpen) return false;

            int size = Marshal.SizeOf<T>();
            byte[] buffer = new byte[size];

            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);

                // Change memory protection to allow writing
                if (!VirtualProtectEx(Handle, address, (uint)size, PAGE_READWRITE, out uint oldProtect))
                {
                    // Failed to change protection, try writing anyway
                    return Write(address, buffer);
                }

                // Write the value
                bool success = WriteProcessMemory(Handle, address, buffer, size, out int bytesWritten) && bytesWritten == size;

                // Restore original protection
                VirtualProtectEx(Handle, address, (uint)size, oldProtect, out _);

                return success;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// Write a byte array with memory protection change (for writing to code sections)
        /// Uses PAGE_EXECUTE_READWRITE to handle executable memory and flushes instruction cache
        /// </summary>
        public bool WriteWithProtection(IntPtr address, byte[] data)
        {
            if (!IsOpen || data == null || data.Length == 0) return false;

            // Try PAGE_EXECUTE_READWRITE first (for code sections), fall back to PAGE_READWRITE
            uint newProtect = PAGE_EXECUTE_READWRITE;
            if (!VirtualProtectEx(Handle, address, (uint)data.Length, newProtect, out uint oldProtect))
            {
                // Try PAGE_READWRITE as fallback
                newProtect = PAGE_READWRITE;
                if (!VirtualProtectEx(Handle, address, (uint)data.Length, newProtect, out oldProtect))
                {
                    // Failed to change protection, try writing anyway
                    System.Diagnostics.Debug.WriteLine($"[WriteWithProtection] VirtualProtectEx failed at {address.ToInt64():X}, attempting direct write");
                    return Write(address, data);
                }
            }

            // Write the data
            bool success = WriteProcessMemory(Handle, address, data, data.Length, out int bytesWritten) && bytesWritten == data.Length;

            // Restore original protection
            VirtualProtectEx(Handle, address, (uint)data.Length, oldProtect, out _);

            // Flush instruction cache if we wrote to executable memory
            // This is CRITICAL for code modifications to take effect without crashing
            if (success)
            {
                FlushInstructionCache(Handle, address, (UIntPtr)data.Length);
                System.Diagnostics.Debug.WriteLine($"[WriteWithProtection] Successfully wrote {data.Length} bytes at {address.ToInt64():X} and flushed icache");
            }

            return success;
        }

        public void Close()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
        }

        // Cache for modules to avoid expensive refresh on every call
        private System.Collections.Generic.List<ModuleInfo>? _cachedModules;
        private DateTime _modulesCacheTime = DateTime.MinValue;
        private static readonly TimeSpan ModuleCacheExpiry = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Get the base address of the main module for the target process.
        /// When kernel mode is enabled, this uses the driver and doesn't require OpenProcess.
        /// </summary>
        /// <param name="processId">Process ID to get base address for (uses Target.Id if not specified)</param>
        /// <returns>Base address of the main module, or IntPtr.Zero on failure</returns>
        public IntPtr GetProcessBaseAddress(int? processId = null)
        {
            int pid = processId ?? Target?.Id ?? 0;
            if (pid == 0)
                return IntPtr.Zero;

            // Use kernel driver if enabled - doesn't need OpenProcess!
            if (AntiCheatBypassForm.UseKernelMode && AntiCheatBypassForm.DriverController != null)
            {
                if (AntiCheatBypassForm.DriverController.GetProcessBaseAddress((uint)pid, out ulong baseAddress))
                {
                    return new IntPtr((long)baseAddress);
                }
                return IntPtr.Zero;
            }

            // Standard usermode - requires the process to be opened
            if (Target != null)
            {
                try
                {
                    return Target.MainModule?.BaseAddress ?? IntPtr.Zero;
                }
                catch
                {
                    return IntPtr.Zero;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Static method to get process base address by PID using kernel driver.
        /// This does NOT require OpenProcess - only the PID is needed.
        /// </summary>
        public static IntPtr GetProcessBaseAddressKernel(int processId)
        {
            if (AntiCheatBypassForm.UseKernelMode && AntiCheatBypassForm.DriverController != null)
            {
                if (AntiCheatBypassForm.DriverController.GetProcessBaseAddress((uint)processId, out ulong baseAddress))
                {
                    return new IntPtr((long)baseAddress);
                }
            }
            return IntPtr.Zero;
        }

        public System.Collections.Generic.List<MemoryRegion> GetMemoryRegions()
        {
            if (Handle == IntPtr.Zero) return new System.Collections.Generic.List<MemoryRegion>();
            return MemoryRegion.EnumerateRegions(Handle);
        }

        public IReadOnlyList<MemoryRegion> EnumerateMemoryRegions(IntPtr startAddress, IntPtr endAddress)
        {
            var regions = new List<MemoryRegion>();
            if (Handle == IntPtr.Zero || endAddress.ToInt64() <= startAddress.ToInt64())
                return regions;

            IntPtr current = startAddress;
            while (current.ToInt64() < endAddress.ToInt64())
            {
                MemoryRegion? region = MemoryRegion.Query(Handle, current);
                if (region == null)
                    break;

                regions.Add(region);

                long nextAddress = region.BaseAddress.ToInt64() + region.Size;
                if (nextAddress <= current.ToInt64())
                    break;

                current = new IntPtr(nextAddress);
            }

            return regions;
        }

        /// <summary>
        /// Get all loaded modules in the target process.
        /// Uses P/Invoke with the process handle directly to avoid permission issues
        /// that occur with System.Diagnostics.Process.Modules
        /// </summary>
        public System.Collections.Generic.List<ModuleInfo> GetModules(bool forceRefresh = false)
        {
            // Return cached modules if still valid
            if (!forceRefresh && _cachedModules != null &&
                DateTime.Now - _modulesCacheTime < ModuleCacheExpiry)
            {
                return _cachedModules;
            }

            var modules = new System.Collections.Generic.List<ModuleInfo>();

            if (!IsOpen)
                return modules;

            try
            {
                // Use P/Invoke directly with our handle to avoid permission issues
                // First, get the number of modules
                IntPtr[] moduleHandles = new IntPtr[1024];

                // Use appropriate filter flag based on target architecture
                // For 32-bit processes on 64-bit OS, use LIST_MODULES_32BIT
                uint filterFlag = LIST_MODULES_ALL;
                if (Environment.Is64BitOperatingSystem && !Is64Bit)
                {
                    filterFlag = LIST_MODULES_32BIT;
                    System.Diagnostics.Debug.WriteLine($"[GetModules] Using LIST_MODULES_32BIT for 32-bit target");
                }

                if (!EnumProcessModulesEx(Handle, moduleHandles, (uint)(moduleHandles.Length * IntPtr.Size), out uint bytesNeeded, filterFlag))
                {
                    int error = Marshal.GetLastWin32Error();
                    System.Diagnostics.Debug.WriteLine($"[GetModules] EnumProcessModulesEx failed: {error}");

                    // Try with LIST_MODULES_ALL as fallback
                    if (filterFlag != LIST_MODULES_ALL)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GetModules] Retrying with LIST_MODULES_ALL");
                        if (!EnumProcessModulesEx(Handle, moduleHandles, (uint)(moduleHandles.Length * IntPtr.Size), out bytesNeeded, LIST_MODULES_ALL))
                        {
                            System.Diagnostics.Debug.WriteLine($"[GetModules] EnumProcessModulesEx fallback also failed: {Marshal.GetLastWin32Error()}");
                            return modules;
                        }
                    }
                    else
                    {
                        return modules;
                    }
                }

                int moduleCount = (int)(bytesNeeded / IntPtr.Size);
                char[] nameBuffer = new char[260];
                char[] pathBuffer = new char[1024];

                for (int i = 0; i < moduleCount && i < moduleHandles.Length; i++)
                {
                    IntPtr hModule = moduleHandles[i];
                    if (hModule == IntPtr.Zero)
                        continue;

                    // Get module base name
                    uint nameLen = GetModuleBaseNameW(Handle, hModule, nameBuffer, (uint)nameBuffer.Length);
                    if (nameLen == 0)
                        continue;

                    string moduleName = new string(nameBuffer, 0, (int)nameLen);

                    // Get module info (base address and size)
                    if (GetModuleInformation(Handle, hModule, out MODULEINFO modInfo, (uint)Marshal.SizeOf<MODULEINFO>()))
                    {
                        uint pathLength = GetModuleFileNameExW(Handle, hModule, pathBuffer, (uint)pathBuffer.Length);
                        modules.Add(new ModuleInfo
                        {
                            ModuleName = moduleName,
                            BaseAddress = modInfo.lpBaseOfDll,
                            Size = (int)modInfo.SizeOfImage,
                            FilePath = pathLength > 0 ? new string(pathBuffer, 0, (int)pathLength) : null
                        });
                    }
                }

                // Cache the result
                _cachedModules = modules;
                _modulesCacheTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetModules] Exception: {ex.Message}");
            }

            return modules;
        }

        /// <summary>
        /// Invalidate the module cache (call when switching processes)
        /// </summary>
        public void InvalidateModuleCache()
        {
            _cachedModules = null;
            _modulesCacheTime = DateTime.MinValue;
        }

        /// <summary>
        /// Find which module an address belongs to
        /// </summary>
        public ModuleInfo? GetModuleForAddress(IntPtr address)
        {
            var modules = GetModules(); // Uses cache
            long addr = address.ToInt64();

            foreach (var module in modules)
            {
                long baseAddr = module.BaseAddress.ToInt64();
                if (addr >= baseAddr && addr < baseAddr + module.Size)
                {
                    return module;
                }
            }

            return null;
        }

        /// <summary>
        /// Allocate memory in the target process
        /// </summary>
        public IntPtr AllocateMemory(uint size, uint protect = PAGE_EXECUTE_READWRITE)
        {
            if (!IsOpen)
                throw new InvalidOperationException("Process not opened");

            return VirtualAllocEx(Handle, IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, protect);
        }

        /// <summary>
        /// Free memory in the target process
        /// </summary>
        public bool FreeMemory(IntPtr address)
        {
            if (!IsOpen)
                throw new InvalidOperationException("Process not opened");

            return VirtualFreeEx(Handle, address, 0, MEM_RELEASE);
        }

        /// <summary>
        /// Change memory protection in the target process
        /// </summary>
        public bool ProtectMemory(IntPtr address, uint size, uint newProtect, out uint oldProtect)
        {
            if (!IsOpen)
                throw new InvalidOperationException("Process not opened");

            return VirtualProtectEx(Handle, address, size, newProtect, out oldProtect);
        }

        /// <summary>
        /// Create a remote thread in the target process
        /// </summary>
        public IntPtr CreateThread(IntPtr startAddress, IntPtr parameter)
        {
            if (!IsOpen)
                throw new InvalidOperationException("Process not opened");

            return CreateRemoteThread(Handle, IntPtr.Zero, 0, startAddress, parameter, 0, out _);
        }

        /// <summary>
        /// Get address of LoadLibraryA in kernel32.dll
        /// </summary>
        public static IntPtr GetLoadLibraryAddress()
        {
            IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
            return GetProcAddress(hKernel32, "LoadLibraryA");
        }

        /// <summary>
        /// Get address of LoadLibraryW in kernel32.dll
        /// </summary>
        public static IntPtr GetLoadLibraryWAddress()
        {
            IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
            return GetProcAddress(hKernel32, "LoadLibraryW");
        }

        /// <summary>
        /// Get LoadLibraryW address for the target process (handles cross-architecture injection)
        /// </summary>
        private IntPtr GetTargetLoadLibraryWAddress()
        {
            // For same-architecture injection, kernel32 base is the same
            // For cross-architecture (64->32), we need to find the 32-bit kernel32

            System.Diagnostics.Debug.WriteLine($"GetTargetLoadLibraryWAddress: Environment.Is64BitProcess={Environment.Is64BitProcess}, Is64Bit={Is64Bit}");

            if (Environment.Is64BitProcess && !Is64Bit)
            {
                // We're 64-bit, target is 32-bit - need to find 32-bit kernel32.dll
                // Get the target's kernel32.dll base address from its module list
                System.Diagnostics.Debug.WriteLine("Cross-architecture injection: 64-bit -> 32-bit");
                var modules = GetModules(true);
                System.Diagnostics.Debug.WriteLine($"Found {modules.Count} modules in target process");

                IntPtr targetKernel32Base = IntPtr.Zero;
                foreach (var mod in modules)
                {
                    System.Diagnostics.Debug.WriteLine($"  Module: {mod.ModuleName} at 0x{mod.BaseAddress:X}");
                    if (mod.ModuleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        targetKernel32Base = mod.BaseAddress;
                        // Don't break - continue to list all modules for debugging
                    }
                }

                if (targetKernel32Base == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("Could not find kernel32.dll in target process!");
                    LastInjectionError = $"Could not find kernel32.dll in target's module list (found {modules.Count} modules)";
                    return IntPtr.Zero;
                }

                System.Diagnostics.Debug.WriteLine($"Target (32-bit) kernel32.dll at: 0x{targetKernel32Base:X}");

                // Load the 32-bit kernel32.dll to get the export offset
                // The 32-bit kernel32 is in SysWOW64 folder
                string kernel32Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SysWOW64", "kernel32.dll");

                if (!System.IO.File.Exists(kernel32Path))
                {
                    System.Diagnostics.Debug.WriteLine($"32-bit kernel32.dll not found at: {kernel32Path}");
                    LastInjectionError = $"32-bit kernel32.dll not found at: {kernel32Path}";
                    return IntPtr.Zero;
                }

                System.Diagnostics.Debug.WriteLine($"Parsing PE exports from: {kernel32Path}");

                // Can't use LoadLibraryEx to load 32-bit DLL in 64-bit process (error 193)
                // Instead, manually parse the PE export table to find LoadLibraryW's RVA
                uint loadLibraryW_RVA = GetExportRVAFromFile(kernel32Path, "LoadLibraryW");
                if (loadLibraryW_RVA == 0)
                {
                    System.Diagnostics.Debug.WriteLine("Could not find LoadLibraryW in PE export table");
                    LastInjectionError = "Could not find LoadLibraryW export in 32-bit kernel32.dll";
                    return IntPtr.Zero;
                }

                System.Diagnostics.Debug.WriteLine($"LoadLibraryW RVA: 0x{loadLibraryW_RVA:X}");

                // Apply RVA to target's kernel32 base
                IntPtr targetLoadLibraryW = new IntPtr(targetKernel32Base.ToInt64() + loadLibraryW_RVA);
                System.Diagnostics.Debug.WriteLine($"LoadLibraryW in target process: 0x{targetLoadLibraryW:X}");

                return targetLoadLibraryW;
            }
            else
            {
                // Same architecture - use simple approach
                System.Diagnostics.Debug.WriteLine("Same architecture injection - using local kernel32");
                return GetLoadLibraryWAddress();
            }
        }

        /// <summary>
        /// Diagnostic method to test cross-architecture LoadLibraryW resolution
        /// </summary>
        public string TestCrossArchResolution()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Environment.Is64BitProcess: {Environment.Is64BitProcess}");
            sb.AppendLine($"Target Is64Bit: {Is64Bit}");
            sb.AppendLine($"Will use cross-arch: {Environment.Is64BitProcess && !Is64Bit}");

            if (Environment.Is64BitProcess && !Is64Bit)
            {
                var modules = GetModules(true);
                sb.AppendLine($"Modules found: {modules.Count}");

                IntPtr kernel32Base = IntPtr.Zero;
                foreach (var mod in modules)
                {
                    if (mod.ModuleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        kernel32Base = mod.BaseAddress;
                        sb.AppendLine($"kernel32.dll found at: 0x{kernel32Base:X}");
                        break;
                    }
                }

                if (kernel32Base == IntPtr.Zero)
                {
                    sb.AppendLine("ERROR: kernel32.dll not found!");
                    return sb.ToString();
                }

                string kernel32Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SysWOW64", "kernel32.dll");
                sb.AppendLine($"32-bit kernel32 path: {kernel32Path}");
                sb.AppendLine($"File exists: {System.IO.File.Exists(kernel32Path)}");

                // Use PE export parsing instead of LoadLibraryEx (which fails with error 193 for cross-arch)
                sb.AppendLine("Using PE export table parsing...");
                uint loadLibraryW_RVA = GetExportRVAFromFile(kernel32Path, "LoadLibraryW");
                if (loadLibraryW_RVA == 0)
                {
                    sb.AppendLine("ERROR: Could not find LoadLibraryW export in PE file");
                    return sb.ToString();
                }

                sb.AppendLine($"LoadLibraryW RVA: 0x{loadLibraryW_RVA:X}");
                IntPtr targetAddr = new IntPtr(kernel32Base.ToInt64() + loadLibraryW_RVA);
                sb.AppendLine($"Target LoadLibraryW: 0x{targetAddr:X}");
                sb.AppendLine("SUCCESS: Cross-arch resolution working!");
            }
            else
            {
                IntPtr addr = GetLoadLibraryWAddress();
                sb.AppendLine($"Same architecture - LoadLibraryW: 0x{addr:X}");
            }

            return sb.ToString();
        }

        private const uint DONT_RESOLVE_DLL_REFERENCES = 0x00000001;
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        /// <summary>
        /// Parse a PE file and get the RVA of an exported function by name
        /// </summary>
        private static uint GetExportRVAFromFile(string filePath, string functionName)
        {
            try
            {
                using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                using (var reader = new System.IO.BinaryReader(fs))
                {
                    // Read DOS header
                    if (reader.ReadUInt16() != 0x5A4D) // MZ signature
                        return 0;

                    // Get PE header offset
                    fs.Seek(0x3C, System.IO.SeekOrigin.Begin);
                    uint peOffset = reader.ReadUInt32();

                    // Read PE signature
                    fs.Seek(peOffset, System.IO.SeekOrigin.Begin);
                    if (reader.ReadUInt32() != 0x4550) // PE\0\0 signature
                        return 0;

                    // Read COFF header
                    ushort machine = reader.ReadUInt16();
                    ushort numberOfSections = reader.ReadUInt16();
                    fs.Seek(12, System.IO.SeekOrigin.Current); // Skip timestamp, symbol table ptr, number of symbols
                    ushort sizeOfOptionalHeader = reader.ReadUInt16();
                    ushort characteristics = reader.ReadUInt16();

                    // Read optional header
                    ushort magic = reader.ReadUInt16();
                    bool is64Bit = (magic == 0x20B); // PE32+ (64-bit)

                    // Skip to data directories
                    if (is64Bit)
                    {
                        fs.Seek(peOffset + 24 + 112, System.IO.SeekOrigin.Begin); // PE32+ export directory at offset 112
                    }
                    else
                    {
                        fs.Seek(peOffset + 24 + 96, System.IO.SeekOrigin.Begin); // PE32 export directory at offset 96
                    }

                    // Read export directory RVA and size
                    uint exportDirRVA = reader.ReadUInt32();
                    uint exportDirSize = reader.ReadUInt32();

                    if (exportDirRVA == 0)
                        return 0;

                    // Read section headers to convert RVA to file offset
                    fs.Seek(peOffset + 24 + sizeOfOptionalHeader, System.IO.SeekOrigin.Begin);

                    uint exportDirFileOffset = 0;
                    for (int i = 0; i < numberOfSections; i++)
                    {
                        byte[] sectionName = reader.ReadBytes(8);
                        uint virtualSize = reader.ReadUInt32();
                        uint virtualAddress = reader.ReadUInt32();
                        uint sizeOfRawData = reader.ReadUInt32();
                        uint pointerToRawData = reader.ReadUInt32();
                        fs.Seek(16, System.IO.SeekOrigin.Current); // Skip relocs, line numbers, etc.

                        if (exportDirRVA >= virtualAddress && exportDirRVA < virtualAddress + virtualSize)
                        {
                            exportDirFileOffset = pointerToRawData + (exportDirRVA - virtualAddress);
                            break;
                        }
                    }

                    if (exportDirFileOffset == 0)
                        return 0;

                    // Read export directory
                    fs.Seek(exportDirFileOffset, System.IO.SeekOrigin.Begin);
                    fs.Seek(12, System.IO.SeekOrigin.Current); // Skip characteristics, timestamp, version
                    uint nameRVA = reader.ReadUInt32();
                    uint ordinalBase = reader.ReadUInt32();
                    uint numberOfFunctions = reader.ReadUInt32();
                    uint numberOfNames = reader.ReadUInt32();
                    uint addressTableRVA = reader.ReadUInt32();
                    uint namePointerTableRVA = reader.ReadUInt32();
                    uint ordinalTableRVA = reader.ReadUInt32();

                    // Convert RVAs to file offsets (simplified - assumes all in same section)
                    Func<uint, uint> rvaToOffset = (rva) =>
                    {
                        fs.Seek(peOffset + 24 + sizeOfOptionalHeader, System.IO.SeekOrigin.Begin);
                        for (int i = 0; i < numberOfSections; i++)
                        {
                            fs.Seek(8, System.IO.SeekOrigin.Current); // section name
                            uint virtualSize = reader.ReadUInt32();
                            uint virtualAddress = reader.ReadUInt32();
                            uint sizeOfRawData = reader.ReadUInt32();
                            uint pointerToRawData = reader.ReadUInt32();
                            fs.Seek(16, System.IO.SeekOrigin.Current);

                            if (rva >= virtualAddress && rva < virtualAddress + virtualSize)
                                return pointerToRawData + (rva - virtualAddress);
                        }
                        return 0;
                    };

                    // Search for the function name
                    for (uint i = 0; i < numberOfNames; i++)
                    {
                        // Read name pointer
                        fs.Seek(rvaToOffset(namePointerTableRVA) + i * 4, System.IO.SeekOrigin.Begin);
                        uint nameRvaEntry = reader.ReadUInt32();
                        uint nameOffset = rvaToOffset(nameRvaEntry);

                        // Read the function name
                        fs.Seek(nameOffset, System.IO.SeekOrigin.Begin);
                        var nameBytes = new System.Collections.Generic.List<byte>();
                        byte b;
                        while ((b = reader.ReadByte()) != 0)
                            nameBytes.Add(b);
                        string name = System.Text.Encoding.ASCII.GetString(nameBytes.ToArray());

                        if (name == functionName)
                        {
                            // Found it! Get the ordinal
                            fs.Seek(rvaToOffset(ordinalTableRVA) + i * 2, System.IO.SeekOrigin.Begin);
                            ushort ordinal = reader.ReadUInt16();

                            // Get the function RVA from address table
                            fs.Seek(rvaToOffset(addressTableRVA) + ordinal * 4, System.IO.SeekOrigin.Begin);
                            uint functionRVA = reader.ReadUInt32();

                            System.Diagnostics.Debug.WriteLine($"Found {functionName} at RVA 0x{functionRVA:X} (ordinal {ordinal + ordinalBase})");
                            return functionRVA;
                        }
                    }

                    return 0; // Not found
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetExportRVAFromFile failed: {ex.Message}");
                return 0;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        /// <summary>
        /// Inject a DLL into the target process
        /// </summary>
        /// <param name="dllPath">Full path to the DLL to inject</param>
        /// <returns>Handle to the remote module, or IntPtr.Zero on failure</returns>
        /// <summary>
        /// Last error from DLL injection (for debugging)
        /// </summary>
        public string LastInjectionError { get; private set; } = "";

        public IntPtr InjectDll(string dllPath)
        {
            LastInjectionError = "";

            if (!IsOpen)
            {
                LastInjectionError = "Process not opened";
                throw new InvalidOperationException("Process not opened");
            }

            // Verify the DLL exists and log its full path
            if (!System.IO.File.Exists(dllPath))
            {
                LastInjectionError = $"DLL file not found: {dllPath}";
                System.Diagnostics.Debug.WriteLine(LastInjectionError);
                return IntPtr.Zero;
            }

            // Get the full absolute path
            dllPath = System.IO.Path.GetFullPath(dllPath);
            System.Diagnostics.Debug.WriteLine($"Injecting DLL: {dllPath} (size: {new System.IO.FileInfo(dllPath).Length} bytes)");
            System.Diagnostics.Debug.WriteLine($"Target process: {Target?.ProcessName} (PID: {Target?.Id}, 64-bit: {Is64Bit})");

            // Convert to Unicode bytes for LoadLibraryW
            byte[] dllPathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");

            // Allocate memory in target process for the DLL path
            IntPtr remotePath = VirtualAllocEx(Handle, IntPtr.Zero, (uint)dllPathBytes.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remotePath == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LastInjectionError = $"VirtualAllocEx failed: error {err}";
                System.Diagnostics.Debug.WriteLine(LastInjectionError);
                return IntPtr.Zero;
            }

            try
            {
                // Write the DLL path to the target process
                if (!WriteProcessMemory(Handle, remotePath, dllPathBytes, dllPathBytes.Length, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    LastInjectionError = $"WriteProcessMemory failed: error {err}";
                    System.Diagnostics.Debug.WriteLine(LastInjectionError);
                    VirtualFreeEx(Handle, remotePath, 0, MEM_RELEASE);
                    return IntPtr.Zero;
                }

                // Get LoadLibraryW address (handles cross-architecture injection)
                IntPtr loadLibraryW = GetTargetLoadLibraryWAddress();
                if (loadLibraryW == IntPtr.Zero)
                {
                    LastInjectionError = "Failed to get LoadLibraryW address for target process";
                    System.Diagnostics.Debug.WriteLine(LastInjectionError);
                    VirtualFreeEx(Handle, remotePath, 0, MEM_RELEASE);
                    return IntPtr.Zero;
                }

                System.Diagnostics.Debug.WriteLine($"LoadLibraryW at: 0x{loadLibraryW:X}");
                System.Diagnostics.Debug.WriteLine($"Remote path at: 0x{remotePath:X}");

                // Create remote thread to call LoadLibraryW
                IntPtr hThread = CreateRemoteThread(Handle, IntPtr.Zero, 0, loadLibraryW, remotePath, 0, out _);
                if (hThread == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    LastInjectionError = $"CreateRemoteThread failed: error {err}";
                    System.Diagnostics.Debug.WriteLine(LastInjectionError);
                    VirtualFreeEx(Handle, remotePath, 0, MEM_RELEASE);
                    return IntPtr.Zero;
                }

                // Wait for the thread to complete (5 second timeout)
                uint waitResult = WaitForSingleObject(hThread, 5000);
                if (waitResult != 0) // WAIT_OBJECT_0 = 0
                {
                    LastInjectionError = $"WaitForSingleObject returned {waitResult} (timeout or error)";
                    System.Diagnostics.Debug.WriteLine(LastInjectionError);
                }

                // Get the thread's exit code (the module handle)
                // NOTE: On 64-bit, this only gets lower 32 bits - module could be at high address!
                GetExitCodeThread(hThread, out uint exitCode);
                CloseHandle(hThread);

                // Free the path memory
                VirtualFreeEx(Handle, remotePath, 0, MEM_RELEASE);

                System.Diagnostics.Debug.WriteLine($"LoadLibraryW exit code (lower 32 bits): 0x{exitCode:X}");

                // On 64-bit, the exit code is truncated. We need to verify by checking if module is loaded.
                string dllFileName = System.IO.Path.GetFileName(dllPath);
                InvalidateModuleCache();
                System.Threading.Thread.Sleep(100); // Give it time to appear
                var modules = GetModules(true);
                ModuleInfo? loadedModule = null;
                foreach (var mod in modules)
                {
                    if (mod.ModuleName.Equals(dllFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        loadedModule = mod;
                        break;
                    }
                }

                if (loadedModule != null)
                {
                    System.Diagnostics.Debug.WriteLine($"DLL successfully loaded at: 0x{loadedModule.BaseAddress.ToInt64():X}");
                    return loadedModule.BaseAddress;
                }

                // Module not found - injection truly failed
                uint remoteError = GetRemoteLastError();
                string errorDesc = GetWin32ErrorDescription(remoteError);
                LastInjectionError = $"LoadLibraryW failed - error {remoteError}: {errorDesc}";
                System.Diagnostics.Debug.WriteLine(LastInjectionError);
                System.Diagnostics.Debug.WriteLine($"Possible causes: DLL architecture mismatch, missing dependencies, or path issue");

                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LastInjectionError = $"Exception: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(LastInjectionError);
                VirtualFreeEx(Handle, remotePath, 0, MEM_RELEASE);
                return IntPtr.Zero;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

        /// <summary>
        /// Call GetLastError in the remote process to get the actual error code
        /// </summary>
        private uint GetRemoteLastError()
        {
            try
            {
                IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                IntPtr getLastErrorAddr = GetProcAddress(hKernel32, "GetLastError");
                if (getLastErrorAddr == IntPtr.Zero)
                    return 0;

                IntPtr hThread = CreateRemoteThread(Handle, IntPtr.Zero, 0, getLastErrorAddr, IntPtr.Zero, 0, out _);
                if (hThread == IntPtr.Zero)
                    return 0;

                WaitForSingleObject(hThread, 1000);
                GetExitCodeThread(hThread, out uint errorCode);
                CloseHandle(hThread);
                return errorCode;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Get a human-readable description for a Win32 error code
        /// </summary>
        private static string GetWin32ErrorDescription(uint errorCode)
        {
            return errorCode switch
            {
                0 => "Success (or error was cleared)",
                2 => "File not found",
                3 => "Path not found",
                5 => "Access denied",
                14 => "Out of memory",
                126 => "Module not found (missing dependency DLL)",
                127 => "Procedure not found",
                193 => "Not a valid Win32 application (wrong architecture - 32/64 bit mismatch)",
                998 => "Invalid access to memory location",
                1114 => "DLL initialization failed",
                1157 => "One or more DLL dependencies not found",
                _ => $"Unknown error"
            };
        }

        /// <summary>
        /// Get all threads in the target process
        /// </summary>
        public System.Collections.Generic.List<ThreadInfo> GetThreads()
        {
            var threads = new System.Collections.Generic.List<ThreadInfo>();

            if (Target == null)
                return threads;

            try
            {
                Target.Refresh();
                foreach (ProcessThread thread in Target.Threads)
                {
                    threads.Add(new ThreadInfo
                    {
                        Id = thread.Id,
                        StartAddress = thread.StartAddress,
                        Priority = thread.BasePriority,
                        State = thread.ThreadState.ToString()
                    });
                }
            }
            catch
            {
                // Process may have exited or we don't have permissions
            }

            return threads;
        }

        /// <summary>
        /// Suspend all threads in the target process
        /// </summary>
        public bool Suspend()
        {
            if (!IsOpen) return false;

            int result = NtSuspendProcess(Handle);
            if (result == 0) // STATUS_SUCCESS
            {
                IsSuspended = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resume all threads in the target process
        /// </summary>
        public bool Resume()
        {
            if (!IsOpen) return false;

            int result = NtResumeProcess(Handle);
            if (result == 0) // STATUS_SUCCESS
            {
                IsSuspended = false;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// Information about a thread
    /// </summary>
    public class ThreadInfo
    {
        public int Id { get; set; }
        public IntPtr StartAddress { get; set; }
        public int Priority { get; set; }
        public string State { get; set; } = "";

        public override string ToString()
        {
            return $"Thread {Id}";
        }
    }

    /// <summary>
    /// Information about a loaded module (DLL/EXE)
    /// </summary>
    public class ModuleInfo
    {
        public string ModuleName { get; set; } = "";
        public IntPtr BaseAddress { get; set; }
        public int Size { get; set; }
        public string? FilePath { get; set; }

        /// <summary>
        /// Alias for Size property (for compatibility)
        /// </summary>
        public int ModuleMemorySize => Size;

        public override string ToString()
        {
            return ModuleName;
        }
    }

    #region Debug Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEBUG_EVENT
    {
        public uint dwDebugEventCode;
        public uint dwProcessId;
        public uint dwThreadId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 164)]
        public byte[] u; // Union of debug info structures
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
        public ulong[] ExceptionInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EXCEPTION_DEBUG_INFO
    {
        public EXCEPTION_RECORD ExceptionRecord;
        public uint dwFirstChance;
    }

    /// <summary>
    /// 64-bit thread context structure with debug registers
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct CONTEXT64
    {
        public ulong P1Home;
        public ulong P2Home;
        public ulong P3Home;
        public ulong P4Home;
        public ulong P5Home;
        public ulong P6Home;

        public uint ContextFlags;
        public uint MxCsr;

        public ushort SegCs;
        public ushort SegDs;
        public ushort SegEs;
        public ushort SegFs;
        public ushort SegGs;
        public ushort SegSs;
        public uint EFlags;

        public ulong Dr0;
        public ulong Dr1;
        public ulong Dr2;
        public ulong Dr3;
        public ulong Dr6;
        public ulong Dr7;

        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rsp;
        public ulong Rbp;
        public ulong Rsi;
        public ulong Rdi;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;

        public ulong Rip;

        // XSAVE format area
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] FltSave;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public M128A[] VectorRegister;
        public ulong VectorControl;

        public ulong DebugControl;
        public ulong LastBranchToRip;
        public ulong LastBranchFromRip;
        public ulong LastExceptionToRip;
        public ulong LastExceptionFromRip;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct M128A
    {
        public ulong Low;
        public long High;
    }

    /// <summary>
    /// 32-bit thread context structure with debug registers (for WoW64 processes)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CONTEXT32
    {
        public uint ContextFlags;

        public uint Dr0;
        public uint Dr1;
        public uint Dr2;
        public uint Dr3;
        public uint Dr6;
        public uint Dr7;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)]
        public byte[] FloatSave;

        public uint SegGs;
        public uint SegFs;
        public uint SegEs;
        public uint SegDs;

        public uint Edi;
        public uint Esi;
        public uint Ebx;
        public uint Edx;
        public uint Ecx;
        public uint Eax;

        public uint Ebp;
        public uint Eip;
        public uint SegCs;
        public uint EFlags;
        public uint Esp;
        public uint SegSs;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] ExtendedRegisters;
    }

    #endregion
}
