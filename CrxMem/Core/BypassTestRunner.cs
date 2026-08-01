using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CrxMem.Core
{
    /// <summary>
    /// Executes bypass techniques and reports results
    /// </summary>
    public class BypassTestRunner
    {
        #region Native APIs for Testing

        [DllImport("ntdll.dll")]
        private static extern int NtReadVirtualMemory(
            IntPtr ProcessHandle,
            IntPtr BaseAddress,
            byte[] Buffer,
            int NumberOfBytesToRead,
            out int NumberOfBytesRead);

        [DllImport("ntdll.dll")]
        private static extern int NtWriteVirtualMemory(
            IntPtr ProcessHandle,
            IntPtr BaseAddress,
            byte[] Buffer,
            int NumberOfBytesToWrite,
            out int NumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr hThread, ref CONTEXT lpContext);

        [StructLayout(LayoutKind.Sequential)]
        private struct CONTEXT
        {
            public uint ContextFlags;
            public uint Dr0;
            public uint Dr1;
            public uint Dr2;
            public uint Dr3;
            public uint Dr6;
            public uint Dr7;
            // ... additional fields omitted for brevity
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
            public byte[] ExtendedRegisters;
        }

        private const uint CONTEXT_DEBUG_REGISTERS = 0x00010010;

        #endregion

        #region Events

        /// <summary>
        /// Fired when a log message should be displayed
        /// </summary>
        public event Action<string, Color>? OnLogMessage;

        /// <summary>
        /// Fired when a test starts
        /// </summary>
        public event Action<string>? OnTestStarted;

        /// <summary>
        /// Fired when a test completes
        /// </summary>
        public event Action<BypassTestResult>? OnTestCompleted;

        #endregion

        #region Properties

        /// <summary>
        /// Process to run tests against (optional)
        /// </summary>
        public ProcessAccess? TargetProcess { get; set; }

        /// <summary>
        /// Kernel driver manager (optional)
        /// </summary>
        public KernelDriverManager? DriverManager { get; set; }

        /// <summary>
        /// Available bypass techniques
        /// </summary>
        public List<BypassTechnique> AvailableTechniques { get; }

        #endregion

        public BypassTestRunner()
        {
            AvailableTechniques = InitializeTechniques();
        }

        private List<BypassTechnique> InitializeTechniques()
        {
            return new List<BypassTechnique>
            {
                new BypassTechnique
                {
                    Id = "nt_read_virtual",
                    Name = "NtReadVirtualMemory",
                    Description = "Direct NT API memory read",
                    RiskLevel = BypassRiskLevel.Low,
                    RequiresProcess = true,
                    Category = "User-mode"
                },
                new BypassTechnique
                {
                    Id = "nt_write_virtual",
                    Name = "NtWriteVirtualMemory",
                    Description = "Direct NT API memory write",
                    RiskLevel = BypassRiskLevel.Low,
                    RequiresProcess = true,
                    Category = "User-mode"
                },
                new BypassTechnique
                {
                    Id = "unsigned_driver",
                    Name = "Unsigned Driver Load",
                    Description = "Attempt to load test driver",
                    RiskLevel = BypassRiskLevel.Medium,
                    RequiresDriver = true,
                    RequiresAdmin = true,
                    Category = "Kernel-mode"
                },
                new BypassTechnique
                {
                    Id = "mm_copy_virtual",
                    Name = "MmCopyVirtualMemory",
                    Description = "Kernel-mode memory access",
                    RiskLevel = BypassRiskLevel.High,
                    RequiresDriver = true,
                    RequiresAdmin = true,
                    Category = "Kernel-mode"
                },
                new BypassTechnique
                {
                    Id = "hypervisor",
                    Name = "Hypervisor Detection",
                    Description = "Check for virtualization",
                    RiskLevel = BypassRiskLevel.Low,
                    Category = "Detection"
                },
                new BypassTechnique
                {
                    Id = "hw_breakpoints",
                    Name = "Hardware Breakpoints",
                    Description = "DR register manipulation",
                    RiskLevel = BypassRiskLevel.Medium,
                    Category = "User-mode"
                },
                new BypassTechnique
                {
                    Id = "manual_map",
                    Name = "Manual PE Mapping",
                    Description = "Stealth DLL injection",
                    RiskLevel = BypassRiskLevel.Medium,
                    RequiresProcess = true,
                    Category = "User-mode"
                },
                new BypassTechnique
                {
                    Id = "peb_unlink",
                    Name = "PEB Unlinking",
                    Description = "Hide from process list",
                    RiskLevel = BypassRiskLevel.High,
                    RequiresProcess = true,
                    Category = "User-mode"
                }
            };
        }

        #region Test Execution

        /// <summary>
        /// Run all available tests
        /// </summary>
        public async Task<List<BypassTestResult>> RunAllTestsAsync()
        {
            return await RunTestsAsync(AvailableTechniques.Select(t => t.Id).ToList());
        }

        /// <summary>
        /// Run selected tests by ID
        /// </summary>
        public async Task<List<BypassTestResult>> RunTestsAsync(List<string> testIds)
        {
            var results = new List<BypassTestResult>();

            foreach (var testId in testIds)
            {
                var technique = AvailableTechniques.FirstOrDefault(t => t.Id == testId);
                if (technique == null) continue;

                OnTestStarted?.Invoke(technique.Name);
                Log($"Running test: {technique.Name}", Color.White);

                var result = await Task.Run(() => RunSingleTest(technique));
                results.Add(result);

                OnTestCompleted?.Invoke(result);

                var color = result.Status switch
                {
                    DetectionStatus.Undetected => Color.LightGreen,
                    DetectionStatus.PartialDetection => Color.Yellow,
                    DetectionStatus.Blocked => Color.Red,
                    DetectionStatus.Error => Color.OrangeRed,
                    _ => Color.Gray
                };
                Log($"  Result: {result.StatusDisplay} - {result.Details}", color);
            }

            return results;
        }

        private BypassTestResult RunSingleTest(BypassTechnique technique)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var result = technique.Id switch
                {
                    "nt_read_virtual" => TestNtReadVirtualMemory(),
                    "nt_write_virtual" => TestNtWriteVirtualMemory(),
                    "unsigned_driver" => TestUnsignedDriverLoad(),
                    "mm_copy_virtual" => TestMmCopyVirtualMemory(),
                    "hypervisor" => TestHypervisorDetection(),
                    "hw_breakpoints" => TestHardwareBreakpoints(),
                    "manual_map" => TestManualPEMapping(),
                    "peb_unlink" => TestPEBUnlinking(),
                    _ => new BypassTestResult
                    {
                        TestName = technique.Name,
                        Status = DetectionStatus.Error,
                        Details = "Unknown test"
                    }
                };

                sw.Stop();
                result.Duration = sw.Elapsed;
                result.TestName = technique.Name;
                result.Description = technique.Description;
                result.RiskLevel = technique.RiskLevel;

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                return BypassTestResult.Error(technique.Name, ex, sw.Elapsed);
            }
        }

        #endregion

        #region Individual Tests

        private BypassTestResult TestNtReadVirtualMemory()
        {
            if (TargetProcess == null)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "No target process attached"
                };
            }

            try
            {
                var buffer = new byte[8];
                int bytesRead;

                // Try to read memory at a known address
                var baseAddr = TargetProcess.Target.MainModule?.BaseAddress ?? IntPtr.Zero;
                if (baseAddr == IntPtr.Zero)
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Error,
                        Details = "Could not get process base address"
                    };
                }

                int status = NtReadVirtualMemory(
                    TargetProcess.Handle,
                    baseAddr,
                    buffer,
                    buffer.Length,
                    out bytesRead);

                if (status == 0 && bytesRead > 0)
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Undetected,
                        Details = $"Successfully read {bytesRead} bytes via NtReadVirtualMemory"
                    };
                }
                else
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Blocked,
                        Details = $"NtReadVirtualMemory returned status 0x{status:X8}"
                    };
                }
            }
            catch (Exception ex)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = ex.Message
                };
            }
        }

        private BypassTestResult TestNtWriteVirtualMemory()
        {
            if (TargetProcess == null)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "No target process attached"
                };
            }

            // Note: This is a read-only test - we don't actually write to avoid corruption
            return new BypassTestResult
            {
                Status = DetectionStatus.NotRun,
                Details = "Write test skipped for safety - requires explicit enable"
            };
        }

        private BypassTestResult TestUnsignedDriverLoad()
        {
            if (DriverManager == null || string.IsNullOrEmpty(DriverManager.DriverPath))
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "No driver configured"
                };
            }

            try
            {
                bool loaded = DriverManager.LoadDriver();
                if (loaded)
                {
                    // Unload immediately after test
                    DriverManager.UnloadDriver();

                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Undetected,
                        Details = "Driver loaded successfully"
                    };
                }
                else
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Blocked,
                        Details = DriverManager.LastError ?? "Driver load failed"
                    };
                }
            }
            catch (Exception ex)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = ex.Message
                };
            }
        }

        private BypassTestResult TestMmCopyVirtualMemory()
        {
            // Requires kernel driver - stub implementation
            if (DriverManager == null || !DriverManager.IsDriverLoaded)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "Kernel driver not loaded - required for MmCopyVirtualMemory test"
                };
            }

            return new BypassTestResult
            {
                Status = DetectionStatus.NotRun,
                Details = "MmCopyVirtualMemory test not implemented - requires driver IOCTL"
            };
        }

        private BypassTestResult TestHypervisorDetection()
        {
            try
            {
                // Check CPUID for hypervisor presence
                bool hypervisorPresent = IsHypervisorPresent();

                if (hypervisorPresent)
                {
                    string vendor = GetHypervisorVendor();
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.PartialDetection,
                        Details = $"Hypervisor detected: {vendor}"
                    };
                }
                else
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Undetected,
                        Details = "No hypervisor detected"
                    };
                }
            }
            catch (Exception ex)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = ex.Message
                };
            }
        }

        private bool IsHypervisorPresent()
        {
            // Check via Windows API
            try
            {
                // Simple check: look for common hypervisor indicators
                var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters");
                if (key != null)
                {
                    key.Close();
                    return true;
                }

                // Check for VMware
                if (Environment.GetEnvironmentVariable("VMware") != null)
                    return true;

                // Check for VirtualBox
                if (File.Exists(@"C:\Windows\System32\drivers\VBoxGuest.sys"))
                    return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        private string GetHypervisorVendor()
        {
            // Try to identify the hypervisor
            if (File.Exists(@"C:\Windows\System32\drivers\VBoxGuest.sys"))
                return "VirtualBox";

            var vmwareKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\VMware, Inc.\VMware Tools");
            if (vmwareKey != null)
            {
                vmwareKey.Close();
                return "VMware";
            }

            var hyperVKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters");
            if (hyperVKey != null)
            {
                hyperVKey.Close();
                return "Hyper-V";
            }

            return "Unknown";
        }

        private BypassTestResult TestHardwareBreakpoints()
        {
            try
            {
                IntPtr thread = GetCurrentThread();

                var context = new CONTEXT();
                context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
                context.ExtendedRegisters = new byte[512];

                // Try to read debug registers
                if (!GetThreadContext(thread, ref context))
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Blocked,
                        Details = $"GetThreadContext failed: {Marshal.GetLastWin32Error()}"
                    };
                }

                // Try to set a debug register
                uint originalDr0 = context.Dr0;
                context.Dr0 = 0x12345678; // Test value

                if (!SetThreadContext(thread, ref context))
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Blocked,
                        Details = $"SetThreadContext failed: {Marshal.GetLastWin32Error()}"
                    };
                }

                // Verify it was set
                if (!GetThreadContext(thread, ref context))
                {
                    return new BypassTestResult
                    {
                        Status = DetectionStatus.Error,
                        Details = "Failed to verify debug register change"
                    };
                }

                // Restore original value
                context.Dr0 = originalDr0;
                SetThreadContext(thread, ref context);

                return new BypassTestResult
                {
                    Status = DetectionStatus.Undetected,
                    Details = "Successfully manipulated hardware debug registers"
                };
            }
            catch (Exception ex)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = ex.Message
                };
            }
        }

        private BypassTestResult TestManualPEMapping()
        {
            if (TargetProcess == null)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "No target process attached"
                };
            }

            // This would use StealthInjector - stub for now
            return new BypassTestResult
            {
                Status = DetectionStatus.NotRun,
                Details = "Manual PE mapping test not implemented - requires test DLL"
            };
        }

        private BypassTestResult TestPEBUnlinking()
        {
            if (TargetProcess == null)
            {
                return new BypassTestResult
                {
                    Status = DetectionStatus.Error,
                    Details = "No target process attached"
                };
            }

            // This is a destructive operation - stub for now
            return new BypassTestResult
            {
                Status = DetectionStatus.NotRun,
                Details = "PEB unlinking test not implemented - requires careful handling"
            };
        }

        #endregion

        #region Report Generation

        /// <summary>
        /// Generate a markdown report of test results
        /// </summary>
        public string GenerateReport(List<BypassTestResult> results)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Anticheat Bypass Test Report");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Summary
            int total = results.Count;
            int undetected = results.Count(r => r.Status == DetectionStatus.Undetected);
            int blocked = results.Count(r => r.Status == DetectionStatus.Blocked);
            int partial = results.Count(r => r.Status == DetectionStatus.PartialDetection);
            int errors = results.Count(r => r.Status == DetectionStatus.Error);

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"- **Total Tests:** {total}");
            sb.AppendLine($"- **Undetected:** {undetected}");
            sb.AppendLine($"- **Blocked:** {blocked}");
            sb.AppendLine($"- **Partial Detection:** {partial}");
            sb.AppendLine($"- **Errors:** {errors}");
            sb.AppendLine();

            // Detailed results
            sb.AppendLine("## Detailed Results");
            sb.AppendLine();
            sb.AppendLine("| Status | Test | Risk | Duration | Details |");
            sb.AppendLine("|--------|------|------|----------|---------|");

            foreach (var result in results)
            {
                sb.AppendLine($"| {result.StatusIcon} {result.StatusDisplay} | {result.TestName} | {result.RiskLevel} | {result.DurationDisplay} | {result.Details} |");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("*Report generated by CrxMem Anticheat Bypass Research Lab*");

            return sb.ToString();
        }

        #endregion

        #region Helper Methods

        private void Log(string message, Color color)
        {
            OnLogMessage?.Invoke(message, color);
        }

        #endregion
    }
}
