using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace CrxMem.Core
{
    /// <summary>
    /// Manages kernel driver loading/unloading and test signing mode
    /// </summary>
    public class KernelDriverManager : IDisposable
    {
        #region Native API Imports

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[]? lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        // Service Control Manager access rights
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;

        // Service access rights
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_START = 0x0010;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint DELETE = 0x10000;

        // Service types
        private const uint SERVICE_KERNEL_DRIVER = 0x00000001;

        // Service start types
        private const uint SERVICE_DEMAND_START = 0x00000003;

        // Service error control
        private const uint SERVICE_ERROR_NORMAL = 0x00000001;

        // Service states
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint SERVICE_RUNNING = 0x00000004;

        // Service control codes
        private const uint SERVICE_CONTROL_STOP = 0x00000001;

        #endregion

        #region Properties

        /// <summary>
        /// Path to the kernel driver file (.sys)
        /// </summary>
        public string? DriverPath { get; set; }

        /// <summary>
        /// Service name for the driver (used in SCM)
        /// </summary>
        public string ServiceName { get; set; } = "CrxShield";

        /// <summary>
        /// Display name for the driver service
        /// </summary>
        public string DisplayName { get; set; } = "CrxShield Security Research Driver";

        /// <summary>
        /// Whether the driver is currently loaded
        /// </summary>
        public bool IsDriverLoaded => CheckDriverLoaded();

        /// <summary>
        /// Last error message from driver operations
        /// </summary>
        public string? LastError { get; private set; }

        #endregion

        #region Driver Loading/Unloading

        /// <summary>
        /// Loads the kernel driver
        /// </summary>
        /// <returns>True if successful</returns>
        public bool LoadDriver()
        {
            if (string.IsNullOrEmpty(DriverPath))
            {
                LastError = "Driver path not specified";
                return false;
            }

            if (!File.Exists(DriverPath))
            {
                LastError = $"Driver file not found: {DriverPath}";
                return false;
            }

            IntPtr scManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;

            try
            {
                // Open SCM
                scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero)
                {
                    LastError = $"Failed to open Service Control Manager: {GetLastErrorMessage()}";
                    return false;
                }

                // Try to open existing service first
                service = OpenService(scManager, ServiceName, SERVICE_ALL_ACCESS);

                if (service == IntPtr.Zero)
                {
                    // Service doesn't exist, create it
                    service = CreateService(
                        scManager,
                        ServiceName,
                        DisplayName,
                        SERVICE_ALL_ACCESS,
                        SERVICE_KERNEL_DRIVER,
                        SERVICE_DEMAND_START,
                        SERVICE_ERROR_NORMAL,
                        DriverPath,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null);

                    if (service == IntPtr.Zero)
                    {
                        LastError = $"Failed to create driver service: {GetLastErrorMessage()}";
                        return false;
                    }
                }

                // Start the service
                if (!StartService(service, 0, null))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                    {
                        LastError = $"Failed to start driver: {GetLastErrorMessage()}";
                        return false;
                    }
                }

                LastError = null;
                return true;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scManager != IntPtr.Zero) CloseServiceHandle(scManager);
            }
        }

        /// <summary>
        /// Unloads the kernel driver
        /// </summary>
        /// <returns>True if successful</returns>
        public bool UnloadDriver()
        {
            IntPtr scManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;

            try
            {
                scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero)
                {
                    LastError = $"Failed to open Service Control Manager: {GetLastErrorMessage()}";
                    return false;
                }

                service = OpenService(scManager, ServiceName, SERVICE_ALL_ACCESS | DELETE);
                if (service == IntPtr.Zero)
                {
                    // Service doesn't exist, consider it unloaded
                    LastError = null;
                    return true;
                }

                // Stop the service
                SERVICE_STATUS status;
                if (!ControlService(service, SERVICE_CONTROL_STOP, out status))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 1062) // ERROR_SERVICE_NOT_ACTIVE
                    {
                        LastError = $"Failed to stop driver: {GetLastErrorMessage()}";
                        return false;
                    }
                }

                // Delete the service
                if (!DeleteService(service))
                {
                    LastError = $"Failed to delete driver service: {GetLastErrorMessage()}";
                    return false;
                }

                LastError = null;
                return true;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scManager != IntPtr.Zero) CloseServiceHandle(scManager);
            }
        }

        /// <summary>
        /// Reloads the driver (unload + load)
        /// </summary>
        public bool ReloadDriver()
        {
            if (!UnloadDriver()) return false;
            System.Threading.Thread.Sleep(500); // Brief pause to ensure cleanup
            return LoadDriver();
        }

        // Service states not covered above
        private const uint SERVICE_START_PENDING = 0x00000002;
        private const uint SERVICE_STOP_PENDING = 0x00000003;

        /// <summary>
        /// Restarts the driver in place: if the service is running, stops it and
        /// waits (bounded) for STOPPED, then starts it again so the kernel picks
        /// up the current CrxShield.sys image. Unlike <see cref="ReloadDriver"/>
        /// this keeps the existing service registration (no delete/recreate),
        /// mirroring Tools\Restart-CrxShield.cmd.
        /// </summary>
        /// <param name="stopTimeoutMs">Maximum time to wait for the service to reach STOPPED.</param>
        /// <param name="startWin32Error">Win32 error from StartService when the start step fails (0 otherwise).</param>
        public bool RestartDriver(int stopTimeoutMs, out int startWin32Error)
        {
            startWin32Error = 0;
            IntPtr scManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;

            try
            {
                scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero)
                {
                    LastError = $"Failed to open Service Control Manager: {GetLastErrorMessage()}";
                    return false;
                }

                service = OpenService(scManager, ServiceName, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                {
                    LastError = $"Driver service '{ServiceName}' is not installed";
                    return false;
                }

                if (!QueryServiceStatus(service, out SERVICE_STATUS status))
                {
                    LastError = $"Failed to query driver service: {GetLastErrorMessage()}";
                    return false;
                }

                // Step 1: stop if running (or still starting)
                if (status.dwCurrentState == SERVICE_RUNNING || status.dwCurrentState == SERVICE_START_PENDING)
                {
                    if (!ControlService(service, SERVICE_CONTROL_STOP, out status))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 1062) // ERROR_SERVICE_NOT_ACTIVE
                        {
                            LastError = $"Failed to stop driver (error {error}): {GetLastErrorMessage()}";
                            return false;
                        }
                    }
                }

                // Bounded wait for STOPPED (also covers an in-progress STOP_PENDING)
                if (status.dwCurrentState != SERVICE_STOPPED)
                {
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        if (!QueryServiceStatus(service, out status))
                        {
                            LastError = $"Failed to query driver service: {GetLastErrorMessage()}";
                            return false;
                        }

                        if (status.dwCurrentState == SERVICE_STOPPED)
                        {
                            break;
                        }

                        if (sw.ElapsedMilliseconds > stopTimeoutMs)
                        {
                            LastError = $"Timed out after {stopTimeoutMs / 1000}s waiting for the driver to stop " +
                                        "(close anything holding \\\\.\\CrxShield and retry)";
                            return false;
                        }

                        System.Threading.Thread.Sleep(200);
                    }
                }

                // Step 2: start with the current image
                if (!StartService(service, 0, null))
                {
                    int error = Marshal.GetLastWin32Error();
                    startWin32Error = error;
                    if (error != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                    {
                        LastError = error == 577 // ERROR_INVALID_IMAGE_HASH
                            ? "Failed to start driver (error 577): kernel Code Integrity rejected the driver " +
                              "signature. Enable testsigning (Tools\\Start-CrxShield.cmd) and reboot, then retry."
                            : $"Failed to start driver (error {error}): {GetLastErrorMessage()}";
                        return false;
                    }
                }

                LastError = null;
                return true;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scManager != IntPtr.Zero) CloseServiceHandle(scManager);
            }
        }

        /// <summary>
        /// Queries the detailed SCM state of the driver service
        /// </summary>
        public DriverServiceState GetServiceState()
        {
            IntPtr scManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;

            try
            {
                scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero)
                {
                    LastError = $"Failed to open Service Control Manager: {GetLastErrorMessage()}";
                    return DriverServiceState.Unknown;
                }

                service = OpenService(scManager, ServiceName, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero)
                {
                    return DriverServiceState.NotInstalled;
                }

                if (!QueryServiceStatus(service, out SERVICE_STATUS status))
                {
                    LastError = $"Failed to query driver service: {GetLastErrorMessage()}";
                    return DriverServiceState.Unknown;
                }

                return status.dwCurrentState switch
                {
                    SERVICE_RUNNING => DriverServiceState.Running,
                    SERVICE_STOPPED => DriverServiceState.Stopped,
                    2 => DriverServiceState.StartPending,   // SERVICE_START_PENDING
                    3 => DriverServiceState.StopPending,    // SERVICE_STOP_PENDING
                    _ => DriverServiceState.Unknown
                };
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scManager != IntPtr.Zero) CloseServiceHandle(scManager);
            }
        }

        /// <summary>
        /// Checks if the driver is currently loaded
        /// </summary>
        private bool CheckDriverLoaded()
        {
            IntPtr scManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;

            try
            {
                scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                if (scManager == IntPtr.Zero) return false;

                service = OpenService(scManager, ServiceName, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero) return false;

                SERVICE_STATUS status;
                if (!QueryServiceStatus(service, out status)) return false;

                return status.dwCurrentState == SERVICE_RUNNING;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (scManager != IntPtr.Zero) CloseServiceHandle(scManager);
            }
        }

        #endregion

        #region Driver Signature Checking

        /// <summary>
        /// Checks if a driver file is digitally signed
        /// </summary>
        public DriverSignatureStatus CheckDriverSignature(string driverPath)
        {
            if (!File.Exists(driverPath))
                return DriverSignatureStatus.NotFound;

            try
            {
                // Check for embedded signature
                var cert = X509Certificate.CreateFromSignedFile(driverPath);
                if (cert != null)
                {
                    var cert2 = new X509Certificate2(cert);

                    // Check if it's a test certificate
                    if (cert2.Subject.Contains("Test") ||
                        cert2.Issuer.Contains("Test") ||
                        !cert2.Verify())
                    {
                        return DriverSignatureStatus.TestSigned;
                    }

                    return DriverSignatureStatus.ProductionSigned;
                }
            }
            catch
            {
                // No valid signature found
            }

            return DriverSignatureStatus.Unsigned;
        }

        #endregion

        #region Test Signing Mode

        /// <summary>
        /// Checks if Windows Test Signing Mode is enabled
        /// </summary>
        public static bool IsTestSigningEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/enum {current}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // Look for "testsigning" in output
                return output.Contains("testsigning") && output.Contains("Yes");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Enables Test Signing Mode (requires admin, needs reboot)
        /// </summary>
        public static bool EnableTestSigning(out string message)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/set testsigning on",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    message = "Failed to start bcdedit";
                    return false;
                }

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    message = "Test Signing Mode enabled. Please reboot for changes to take effect.";
                    return true;
                }
                else
                {
                    message = $"bcdedit returned error code {process.ExitCode}";
                    return false;
                }
            }
            catch (Win32Exception ex)
            {
                message = $"Failed to run bcdedit: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                message = $"Error: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Disables Test Signing Mode (requires admin, needs reboot)
        /// </summary>
        public static bool DisableTestSigning(out string message)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "bcdedit",
                    Arguments = "/set testsigning off",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    message = "Failed to start bcdedit";
                    return false;
                }

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    message = "Test Signing Mode disabled. Please reboot for changes to take effect.";
                    return true;
                }
                else
                {
                    message = $"bcdedit returned error code {process.ExitCode}";
                    return false;
                }
            }
            catch (Win32Exception ex)
            {
                message = $"Failed to run bcdedit: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                message = $"Error: {ex.Message}";
                return false;
            }
        }

        #endregion

        #region Helper Methods

        private static string GetLastErrorMessage()
        {
            int error = Marshal.GetLastWin32Error();
            return new Win32Exception(error).Message;
        }

        public void Dispose()
        {
            // Cleanup if needed
        }

        #endregion
    }

    /// <summary>
    /// Detailed SCM state of the driver service
    /// </summary>
    public enum DriverServiceState
    {
        /// <summary>State could not be determined (see LastError)</summary>
        Unknown,
        /// <summary>Driver service is not registered in the SCM</summary>
        NotInstalled,
        /// <summary>Driver service exists but is stopped</summary>
        Stopped,
        /// <summary>Driver service is starting</summary>
        StartPending,
        /// <summary>Driver service is loaded and running</summary>
        Running,
        /// <summary>Driver service is stopping</summary>
        StopPending
    }

    /// <summary>
    /// Driver signature verification status
    /// </summary>
    public enum DriverSignatureStatus
    {
        /// <summary>Driver file not found</summary>
        NotFound,
        /// <summary>Driver is not signed</summary>
        Unsigned,
        /// <summary>Driver has a test/self-signed certificate</summary>
        TestSigned,
        /// <summary>Driver has a valid production certificate</summary>
        ProductionSigned
    }

    /// <summary>
    /// KDMapper integration for loading unsigned drivers without test mode
    /// Uses Intel CPU vulnerabilities to bypass driver signature enforcement
    /// </summary>
    public class KDMapperLoader
    {
        private string? _kdmapperPath;
        private string? _vulnerableDriverPath;

        /// <summary>
        /// Path to KDMapper.exe
        /// </summary>
        public string? KDMapperExePath
        {
            get => _kdmapperPath;
            set => _kdmapperPath = value;
        }

        /// <summary>
        /// Path to vulnerable Intel driver (iqvw64e.sys)
        /// </summary>
        public string? VulnerableDriverPath
        {
            get => _vulnerableDriverPath;
            set => _vulnerableDriverPath = value;
        }

        /// <summary>
        /// Last error message
        /// </summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// Checks if KDMapper and vulnerable driver are available
        /// </summary>
        public bool IsAvailable()
        {
            if (string.IsNullOrEmpty(_kdmapperPath) || !File.Exists(_kdmapperPath))
            {
                LastError = "KDMapper.exe not found. Download from: https://github.com/TheCruZ/kdmapper";
                return false;
            }

            // Vulnerable driver is optional - KDMapper includes it
            return true;
        }

        /// <summary>
        /// Loads a driver using KDMapper (bypasses signature checks, no test mode needed)
        /// </summary>
        /// <param name="driverPath">Path to the unsigned driver to load</param>
        /// <param name="output">Console output from KDMapper</param>
        /// <returns>True if successful</returns>
        public bool LoadDriverWithKDMapper(string driverPath, out string output)
        {
            output = "";
            LastError = null;

            if (!File.Exists(driverPath))
            {
                LastError = $"Driver file not found: {driverPath}";
                return false;
            }

            if (!IsAvailable())
                return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _kdmapperPath,
                    Arguments = $"\"{driverPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas" // Requires admin
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    LastError = "Failed to start KDMapper process";
                    return false;
                }

                output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    LastError = $"KDMapper failed with exit code {process.ExitCode}:\n{error}\n{output}";
                    return false;
                }

                // Check for success indicators in output
                if (output.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("mapped", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                LastError = $"KDMapper completed but driver may not be loaded:\n{output}";
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"KDMapper exception: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Auto-detects KDMapper.exe in common locations
        /// </summary>
        public bool AutoDetectKDMapper()
        {
            var searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kdmapper.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "kdmapper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "kdmapper.exe"),
                @"C:\Tools\kdmapper.exe",
                @"C:\kdmapper\kdmapper.exe"
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    _kdmapperPath = path;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets information about KDMapper method
        /// </summary>
        public static string GetKDMapperInfo()
        {
            return @"KDMapper - Manual Driver Mapper

METHOD: Exploits Intel CPU vulnerability (CVE-2015-2291)
BYPASSES: Driver signature enforcement (DSE)
REQUIRES: Admin privileges, Intel CPU
NO REBOOT: Works without test mode or bcdedit
STATUS: Undetected by most anti-cheats (as of 2024)

HOW IT WORKS:
1. Loads signed vulnerable Intel driver (iqvw64e.sys)
2. Exploits driver to disable DSE temporarily
3. Maps your unsigned driver into kernel
4. Removes vulnerable driver

DOWNLOAD: https://github.com/TheCruZ/kdmapper
ALTERNATIVE: https://github.com/ekknod/EC_PRO (EC driver mapper)

NOTES:
- Driver is mapped, not loaded via SCM (won't show in services)
- Driver unloads when you reboot
- Some anti-cheats detect the vulnerable Intel driver
- Works on most Intel CPUs (Core 2 Duo and newer)";
        }
    }
}
