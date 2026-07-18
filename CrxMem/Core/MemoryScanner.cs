using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace CrxMem.Core
{
    /// <summary>
    /// Handles memory scanning operations (first scan, next scan, etc.)
    /// </summary>
    public class MemoryScanner
    {
        private readonly IMemoryAccessor _process;
        private List<ScanResult> _results = new List<ScanResult>();
        private Stack<List<ScanResult>> _scanHistory = new Stack<List<ScanResult>>();
        private CancellationTokenSource? _cancellationTokenSource;
        private volatile bool _isScanning;

        public IReadOnlyList<ScanResult> Results => _results.AsReadOnly();
        public int ResultCount => _results.Count;
        public bool CanUndo => _scanHistory.Count > 0;
        public bool IsScanning => _isScanning;

        public event Action<int, int>? ProgressChanged; // current, total
        public event Action<string>? StatusChanged;

        public MemoryScanner(IMemoryAccessor process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        /// <summary>
        /// Cancel the current scan operation
        /// </summary>
        public void CancelScan()
        {
            _cancellationTokenSource?.Cancel();
        }

        /// <summary>
        /// Prepare a new cancellation token for the next scan
        /// </summary>
        private CancellationToken BeginScan()
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            _isScanning = true;
            return _cancellationTokenSource.Token;
        }

        /// <summary>
        /// Mark scan as complete
        /// </summary>
        private void EndScan()
        {
            _isScanning = false;
        }

        /// <summary>
        /// Performs first scan across all readable memory regions
        /// </summary>
        public void FirstScan(ScanType scanType, ScanValueType valueType, string value,
            bool fastScan = true, bool? writableOnly = true, bool? copyOnWrite = null, bool? executableOnly = null, bool? activeMemoryOnly = null,
            IntPtr startAddress = default, IntPtr endAddress = default)
        {
            var cancellationToken = BeginScan();

            try
            {
                // Save current results for undo
                if (_results.Count > 0)
                {
                    _scanHistory.Push(new List<ScanResult>(_results));
                }

                _results.Clear();

                if (startAddress == IntPtr.Zero)
                    startAddress = new IntPtr(0x10000); // Skip null page

                if (endAddress == IntPtr.Zero)
                    endAddress = _process.Is64Bit
                        ? new IntPtr(0x7FFFFFFFFFFF)
                        : new IntPtr(0x7FFFFFFF);

                StatusChanged?.Invoke("Enumerating memory regions...");
                var regions = EnumerateMemoryRegions(startAddress, endAddress);

                // Apply memory filters based on scan options
                var filteredRegions = regions.Where(r => r.IsReadable && r.IsCommitted);

                // Writable filter (3-state: checked=writable only, unchecked=non-writable only, null=both)
                if (writableOnly.HasValue)
                {
                    if (writableOnly.Value)
                        filteredRegions = filteredRegions.Where(r => r.IsWritable);
                    else
                        filteredRegions = filteredRegions.Where(r => !r.IsWritable);
                }

                // CopyOnWrite filter (3-state)
                if (copyOnWrite.HasValue)
                {
                    if (copyOnWrite.Value)
                        filteredRegions = filteredRegions.Where(r => r.IsCopyOnWrite);
                    else
                        filteredRegions = filteredRegions.Where(r => !r.IsCopyOnWrite);
                }

                // Executable filter (3-state)
                if (executableOnly.HasValue)
                {
                    if (executableOnly.Value)
                        filteredRegions = filteredRegions.Where(r => r.IsExecutable);
                    else
                        filteredRegions = filteredRegions.Where(r => !r.IsExecutable);
                }

                // Removed size filters to match Cheat Engine behavior - scan ALL memory regions
                // This allows finding values in small regions and large mapped files

                var scanRegions = filteredRegions.ToList();
                StatusChanged?.Invoke($"Scanning {scanRegions.Count} memory regions...");

                int current = 0;
                int total = scanRegions.Count;

                foreach (var region in scanRegions)
                {
                    // Check for cancellation
                    if (cancellationToken.IsCancellationRequested)
                    {
                        StatusChanged?.Invoke($"Scan cancelled. Found {_results.Count:N0} results so far.");
                        return;
                    }

                    current++;

                    // Only update UI every 5 regions to avoid freezing
                    if (current % 5 == 0 || current == total)
                    {
                        ProgressChanged?.Invoke(current, total);
                        StatusChanged?.Invoke($"Scanning region {current}/{total} at {region.BaseAddress.ToInt64():X8}...");
                    }

                    try
                    {
                        // Limit region size to prevent huge allocations
                        int size = (int)Math.Min(region.Size, 104857600); // Max 100MB per region

                        byte[] buffer = _process.Read(region.BaseAddress, size);
                        if (buffer == null || buffer.Length == 0)
                            continue;

                        ScanRegion(region.BaseAddress, buffer, scanType, valueType, value, fastScan, cancellationToken);

                        // Break early if we found too many results
                        if (_results.Count >= 50000)
                        {
                            StatusChanged?.Invoke("Found 50,000+ results, stopping scan...");
                            break;
                        }
                    }
                    catch
                    {
                        // Skip regions we can't read
                        continue;
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    StatusChanged?.Invoke($"Scan complete! Found {_results.Count:N0} results.");
                }
            }
            finally
            {
                EndScan();
            }
        }

        /// <summary>
        /// Performs next scan on previous results
        /// </summary>
        public void NextScan(ScanType scanType, ScanValueType valueType, string value)
        {
            var cancellationToken = BeginScan();

            try
            {
                // Save current results for undo
                if (_results.Count > 0)
                {
                    _scanHistory.Push(new List<ScanResult>(_results));
                }

                StatusChanged?.Invoke($"Re-scanning {_results.Count:N0} addresses...");

                var newResults = new List<ScanResult>();
                int valueSize = GetValueSize(valueType);
                int current = 0;
                int total = _results.Count;

                foreach (var result in _results)
                {
                    // Check for cancellation
                    if (cancellationToken.IsCancellationRequested)
                    {
                        StatusChanged?.Invoke($"Scan cancelled. Found {newResults.Count:N0} results so far.");
                        _results = newResults;
                        return;
                    }

                    current++;

                    // Update progress every 10% or every 1000 addresses
                    if (current % Math.Max(1, total / 10) == 0 || current % 1000 == 0 || current == total)
                    {
                        ProgressChanged?.Invoke(current, total);
                        StatusChanged?.Invoke($"Scanning {current}/{total} addresses...");
                    }

                    try
                    {
                        byte[] buffer = _process.Read(result.Address, valueSize);
                        if (buffer == null || buffer.Length == 0)
                            continue;

                        // Compare current value against previous value and scan criteria
                        if (CompareValue(buffer, 0, scanType, valueType, value, result.Value))
                        {
                            newResults.Add(new ScanResult
                            {
                                Address = result.Address,
                                Value = buffer // Store current value as previous for next scan
                            });
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                _results = newResults;

                if (!cancellationToken.IsCancellationRequested)
                {
                    StatusChanged?.Invoke($"Next scan complete! Found {_results.Count:N0} results.");
                }
            }
            finally
            {
                EndScan();
            }
        }

        /// <summary>
        /// Enumerate all memory regions in the target process
        /// </summary>
        private List<MemoryRegion> EnumerateMemoryRegions(IntPtr start, IntPtr end)
        {
            return _process.EnumerateMemoryRegions(start, end).ToList();
        }

        /// <summary>
        /// Scan a single memory region
        /// </summary>
        private void ScanRegion(IntPtr baseAddress, byte[] buffer, ScanType scanType,
            ScanValueType valueType, string value, bool fastScan, CancellationToken cancellationToken = default)
        {
            int valueSize = GetValueSize(valueType);
            int maxOffset = buffer.Length - valueSize;

            // Fast scan: use alignment to skip bytes (4x-8x faster!)
            // For example, 4-byte values are typically aligned to 4-byte boundaries
            int alignment = fastScan ? GetAlignment(valueType) : 1;

            for (int i = 0; i <= maxOffset; i += alignment)
            {
                // Check for cancellation periodically (every 64KB)
                if ((i & 0xFFFF) == 0 && cancellationToken.IsCancellationRequested)
                    return;

                if (CompareValue(buffer, i, scanType, valueType, value))
                {
                    _results.Add(new ScanResult
                    {
                        Address = new IntPtr(baseAddress.ToInt64() + i),
                        Value = buffer.Skip(i).Take(valueSize).ToArray()
                    });

                    // Limit results to prevent memory issues
                    if (_results.Count >= 50000)
                        return;
                }
            }
        }

        /// <summary>
        /// Get alignment for fast scanning (how many bytes to skip per iteration)
        /// </summary>
        private int GetAlignment(ScanValueType type)
        {
            return type switch
            {
                ScanValueType.Byte => 1,      // Can't align byte scans
                ScanValueType.Int16 => 2,     // 2-byte alignment (2x faster)
                ScanValueType.Int32 => 4,     // 4-byte alignment (4x faster)
                ScanValueType.Int64 => 4,     // 4-byte alignment (still good speedup)
                ScanValueType.Float => 4,     // 4-byte alignment (4x faster)
                ScanValueType.Double => 4,    // 4-byte alignment (4x faster)
                ScanValueType.String => 1,    // Can't align string scans
                _ => 1
            };
        }

        /// <summary>
        /// Compare a value at offset in buffer against scan criteria
        /// </summary>
        private bool CompareValue(byte[] buffer, int offset, ScanType scanType,
            ScanValueType valueType, string searchValue, byte[]? previousValue = null)
        {
            switch (valueType)
            {
                case ScanValueType.Byte:
                    return CompareNumeric<byte>(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.Int16:
                    return CompareNumeric<short>(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.Int32:
                    return CompareNumeric<int>(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.Int64:
                    return CompareNumeric<long>(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.Float:
                    return CompareFloat(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.Double:
                    return CompareDouble(buffer, offset, scanType, searchValue, previousValue);
                case ScanValueType.String:
                    return CompareString(buffer, offset, scanType, searchValue);
                default:
                    return false;
            }
        }

        private bool CompareNumeric<T>(byte[] buffer, int offset, ScanType scanType,
            string searchValue, byte[]? previousValue) where T : struct, IComparable<T>
        {
            T currentValue = ReadValue<T>(buffer, offset);

            switch (scanType)
            {
                case ScanType.Exact:
                    if (!TryParse<T>(searchValue, out T target))
                        return false;
                    return currentValue.CompareTo(target) == 0;

                case ScanType.BiggerThan:
                    if (!TryParse<T>(searchValue, out T targetBT))
                        return false;
                    return currentValue.CompareTo(targetBT) > 0;

                case ScanType.SmallerThan:
                    if (!TryParse<T>(searchValue, out T targetST))
                        return false;
                    return currentValue.CompareTo(targetST) < 0;

                case ScanType.Between:
                    var parts = searchValue.Split('-');
                    if (parts.Length != 2 || !TryParse<T>(parts[0], out T min) || !TryParse<T>(parts[1], out T max))
                        return false;
                    return currentValue.CompareTo(min) >= 0 && currentValue.CompareTo(max) <= 0;

                case ScanType.Unknown:
                    return true;

                case ScanType.Changed:
                    if (previousValue == null)
                        return false;
                    T prevValue = ReadValue<T>(previousValue, 0);
                    return currentValue.CompareTo(prevValue) != 0;

                case ScanType.Unchanged:
                    if (previousValue == null)
                        return false;
                    T prevValueU = ReadValue<T>(previousValue, 0);
                    return currentValue.CompareTo(prevValueU) == 0;

                case ScanType.Increased:
                    if (previousValue == null)
                        return false;
                    T prevValueI = ReadValue<T>(previousValue, 0);
                    return currentValue.CompareTo(prevValueI) > 0;

                case ScanType.Decreased:
                    if (previousValue == null)
                        return false;
                    T prevValueD = ReadValue<T>(previousValue, 0);
                    return currentValue.CompareTo(prevValueD) < 0;

                case ScanType.IncreasedBy:
                    if (!TryParse<T>(searchValue, out T deltaInc))
                        return false;
                    if (previousValue == null)
                        return false;
                    T prevValueInc = ReadValue<T>(previousValue, 0);
                    // Check if current = previous + delta
                    dynamic curr = currentValue;
                    dynamic prev = prevValueInc;
                    dynamic delt = deltaInc;
                    return Math.Abs(curr - prev - delt) < 0.001;

                case ScanType.DecreasedBy:
                    if (!TryParse<T>(searchValue, out T deltaDec))
                        return false;
                    if (previousValue == null)
                        return false;
                    T prevValueDec = ReadValue<T>(previousValue, 0);
                    dynamic currD = currentValue;
                    dynamic prevD = prevValueDec;
                    dynamic deltD = deltaDec;
                    return Math.Abs(prevD - currD - deltD) < 0.001;

                default:
                    return false;
            }
        }

        private bool CompareFloat(byte[] buffer, int offset, ScanType scanType,
            string searchValue, byte[]? previousValue)
        {
            float currentValue = BitConverter.ToSingle(buffer, offset);

            // Skip invalid float values (NaN, Infinity)
            if (float.IsNaN(currentValue) || float.IsInfinity(currentValue))
                return false;

            switch (scanType)
            {
                case ScanType.Exact:
                    if (!float.TryParse(searchValue, out float target))
                        return false;
                    // Use relaxed tolerance like Cheat Engine (0.01 instead of 0.0001)
                    // This handles floating-point rounding errors better
                    return Math.Abs(currentValue - target) < 0.01f;

                case ScanType.BiggerThan:
                    if (!float.TryParse(searchValue, out float targetBT))
                        return false;
                    return currentValue > targetBT;

                case ScanType.SmallerThan:
                    if (!float.TryParse(searchValue, out float targetST))
                        return false;
                    return currentValue < targetST;

                case ScanType.Unknown:
                    return true;

                case ScanType.Changed:
                    if (previousValue == null)
                        return false;
                    float prevValue = BitConverter.ToSingle(previousValue, 0);
                    if (float.IsNaN(prevValue) || float.IsInfinity(prevValue))
                        return false;
                    return Math.Abs(currentValue - prevValue) >= 0.01f;

                case ScanType.Unchanged:
                    if (previousValue == null)
                        return false;
                    float prevValueU = BitConverter.ToSingle(previousValue, 0);
                    if (float.IsNaN(prevValueU) || float.IsInfinity(prevValueU))
                        return false;
                    return Math.Abs(currentValue - prevValueU) < 0.01f;

                default:
                    return false;
            }
        }

        private bool CompareDouble(byte[] buffer, int offset, ScanType scanType,
            string searchValue, byte[]? previousValue)
        {
            double currentValue = BitConverter.ToDouble(buffer, offset);

            // Skip invalid double values (NaN, Infinity)
            if (double.IsNaN(currentValue) || double.IsInfinity(currentValue))
                return false;

            switch (scanType)
            {
                case ScanType.Exact:
                    if (!double.TryParse(searchValue, out double target))
                        return false;
                    // Use relaxed tolerance like Cheat Engine (0.0001 instead of 0.0000001)
                    return Math.Abs(currentValue - target) < 0.0001;

                case ScanType.BiggerThan:
                    if (!double.TryParse(searchValue, out double targetBT))
                        return false;
                    return currentValue > targetBT;

                case ScanType.SmallerThan:
                    if (!double.TryParse(searchValue, out double targetST))
                        return false;
                    return currentValue < targetST;

                case ScanType.Unknown:
                    return true;

                case ScanType.Changed:
                    if (previousValue == null)
                        return false;
                    double prevValue = BitConverter.ToDouble(previousValue, 0);
                    if (double.IsNaN(prevValue) || double.IsInfinity(prevValue))
                        return false;
                    return Math.Abs(currentValue - prevValue) >= 0.0001;

                case ScanType.Unchanged:
                    if (previousValue == null)
                        return false;
                    double prevValueU = BitConverter.ToDouble(previousValue, 0);
                    if (double.IsNaN(prevValueU) || double.IsInfinity(prevValueU))
                        return false;
                    return Math.Abs(currentValue - prevValueU) < 0.0001;

                default:
                    return false;
            }
        }

        private bool CompareString(byte[] buffer, int offset, ScanType scanType, string searchValue)
        {
            if (scanType != ScanType.Exact)
                return false;

            byte[] searchBytes = System.Text.Encoding.UTF8.GetBytes(searchValue);
            if (offset + searchBytes.Length > buffer.Length)
                return false;

            for (int i = 0; i < searchBytes.Length; i++)
            {
                if (buffer[offset + i] != searchBytes[i])
                    return false;
            }
            return true;
        }

        private T ReadValue<T>(byte[] buffer, int offset) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset);
                return Marshal.PtrToStructure<T>(ptr);
            }
            finally
            {
                handle.Free();
            }
        }

        private bool TryParse<T>(string value, out T result) where T : struct
        {
            result = default(T);
            try
            {
                if (typeof(T) == typeof(byte))
                {
                    if (byte.TryParse(value, out byte b))
                    {
                        result = (T)(object)b;
                        return true;
                    }
                }
                else if (typeof(T) == typeof(short))
                {
                    if (short.TryParse(value, out short s))
                    {
                        result = (T)(object)s;
                        return true;
                    }
                }
                else if (typeof(T) == typeof(int))
                {
                    if (int.TryParse(value, out int i))
                    {
                        result = (T)(object)i;
                        return true;
                    }
                }
                else if (typeof(T) == typeof(long))
                {
                    if (long.TryParse(value, out long l))
                    {
                        result = (T)(object)l;
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private int GetValueSize(ScanValueType type)
        {
            return type switch
            {
                ScanValueType.Byte => 1,
                ScanValueType.Int16 => 2,
                ScanValueType.Int32 => 4,
                ScanValueType.Int64 => 8,
                ScanValueType.Float => 4,
                ScanValueType.Double => 8,
                ScanValueType.String => 256, // Max string length for scanning
                _ => 4
            };
        }

        /// <summary>
        /// Clears all scan results (for New Scan button)
        /// </summary>
        public void ClearResults()
        {
            _results.Clear();
            _scanHistory.Clear();
        }

        /// <summary>
        /// Undoes the last scan operation (restores previous results)
        /// </summary>
        public void UndoScan()
        {
            if (_scanHistory.Count > 0)
            {
                _results = _scanHistory.Pop();
                StatusChanged?.Invoke($"Undo successful! Restored {_results.Count:N0} results.");
            }
        }

        /// <summary>
        /// Scans for Array of Bytes pattern (e.g., "48 8B 05 ?? ?? ?? ??")
        /// </summary>
        public List<IntPtr> ScanArrayOfBytes(string pattern)
        {
            var results = new List<IntPtr>();
            var patternBytes = ParseAOBPattern(pattern);

            StatusChanged?.Invoke("Scanning for byte pattern...");
            var regions = EnumerateMemoryRegions(new IntPtr(0x10000), _process.Is64Bit ?
                new IntPtr(0x7FFFFFFFFFFF) : new IntPtr(0x7FFFFFFF));

            int current = 0;
            int total = regions.Count;

            foreach (var region in regions.Where(r => r.IsReadable))
            {
                current++;
                if (current % 5 == 0 || current == total)
                {
                    ProgressChanged?.Invoke(current, total);
                    StatusChanged?.Invoke($"Scanning region {current}/{total} for pattern...");
                }

                try
                {
                    byte[] buffer = _process.Read(region.BaseAddress, (int)Math.Min(region.Size, 50_000_000));
                    if (buffer == null) continue;

                    for (int i = 0; i <= buffer.Length - patternBytes.Length; i++)
                    {
                        if (MatchPattern(buffer, i, patternBytes))
                        {
                            results.Add(new IntPtr(region.BaseAddress.ToInt64() + i));
                            if (results.Count >= 10000) return results;
                        }
                    }
                }
                catch { }
            }

            StatusChanged?.Invoke($"Found {results.Count} pattern matches");
            return results;
        }

        private (byte value, bool wildcard)[] ParseAOBPattern(string pattern)
        {
            var parts = pattern.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<(byte, bool)>();

            foreach (var part in parts)
            {
                if (part == "?" || part == "??")
                    result.Add((0, true));
                else
                    result.Add((Convert.ToByte(part, 16), false));
            }

            return result.ToArray();
        }

        private bool MatchPattern(byte[] buffer, int offset, (byte value, bool wildcard)[] pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (!pattern[i].wildcard && buffer[offset + i] != pattern[i].value)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Represents a single scan result
    /// </summary>
    public class ScanResult
    {
        public IntPtr Address { get; set; }
        public byte[] Value { get; set; }

        public override string ToString()
        {
            return $"0x{Address.ToInt64():X}";
        }
    }

    /// <summary>
    /// Types of scans supported
    /// </summary>
    public enum ScanType
    {
        Exact,
        BiggerThan,
        SmallerThan,
        Between,
        Unknown,
        Changed,
        Unchanged,
        Increased,
        Decreased,
        IncreasedBy,
        DecreasedBy
    }

    /// <summary>
    /// Value types supported for scanning
    /// </summary>
    public enum ScanValueType
    {
        Byte,
        Int16,
        Int32,
        Int64,
        Float,
        Double,
        String
    }
}
