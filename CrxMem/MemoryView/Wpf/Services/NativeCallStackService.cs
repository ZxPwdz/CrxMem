using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CrxMem.Core;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record SymbolResolution(
    ulong Address,
    string DisplayName,
    string? ModuleName,
    ulong? ModuleRelativeAddress,
    string? SourceFile = null,
    int? SourceLine = null,
    ulong Displacement = 0);

public sealed record CallStackFrame(
    int Index,
    ulong InstructionAddress,
    ulong StackAddress,
    SymbolResolution Symbol,
    bool IsHeuristic = false);

public sealed record UnwindResult(
    bool IsAvailable,
    IReadOnlyList<CallStackFrame> Frames,
    string? Error = null)
{
    public static UnwindResult Unavailable(string error) => new(false, Array.Empty<CallStackFrame>(), error);
}

public interface ISymbolService : IDisposable
{
    bool MicrosoftSymbolServerEnabled { get; }
    void SetMicrosoftSymbolServerEnabled(bool enabled);
    Task<SymbolResolution> ResolveAsync(ulong address, CancellationToken cancellationToken);
}

public interface ICallStackService
{
    Task<UnwindResult> UnwindAsync(
        DebugSessionSnapshot session,
        RegisterSnapshot registers,
        int maximumFrames,
        CancellationToken cancellationToken);
}

public sealed class WindowsDbgHelpService : ISymbolService, ICallStackService
{
    private const uint MachineI386 = 0x014c;
    private const uint MachineAmd64 = 0x8664;
    private const uint SymoptUndname = 0x00000002;
    private const uint SymoptDeferredLoads = 0x00000004;
    private const uint SymoptLoadLines = 0x00000010;
    private const uint SymoptFailCriticalErrors = 0x00000200;
    private const int AddressModeFlat = 3;
    private const int MaximumSymbolName = 1024;
    private static readonly SemaphoreSlim DbgHelpGate = new(1, 1);
    private readonly ProcessAccess _process;
    private readonly IModuleService _modules;
    private bool _initialized;
    private bool _initializedWithMicrosoftSymbols;
    private bool _disposed;

    public WindowsDbgHelpService(ProcessAccess process, IModuleService modules)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
    }

    public bool MicrosoftSymbolServerEnabled { get; private set; }

    public void SetMicrosoftSymbolServerEnabled(bool enabled)
    {
        if (MicrosoftSymbolServerEnabled == enabled) return;
        MicrosoftSymbolServerEnabled = enabled;
    }

    public async Task<SymbolResolution> ResolveAsync(ulong address, CancellationToken cancellationToken)
    {
        await DbgHelpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            return ResolveCore(address);
        }
        finally { DbgHelpGate.Release(); }
    }

    public async Task<UnwindResult> UnwindAsync(
        DebugSessionSnapshot session,
        RegisterSnapshot registers,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        if (!session.CanReadThreadContext || session.SelectedThreadId != registers.ThreadId)
            return UnwindResult.Unavailable("A matching selected thread in a paused debugger session is required.");
        if (!_process.IsOpen || _process.Target == null || session.ProcessId != _process.Target.Id)
            return UnwindResult.Unavailable("The debug session no longer matches the selected process.");

        maximumFrames = Math.Clamp(maximumFrames, 1, 512);
        await DbgHelpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();
            return WalkCore(session.SelectedThreadId!.Value, registers, maximumFrames, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnwindResult.Unavailable($"Native unwind failed: {ex.Message}");
        }
        finally { DbgHelpGate.Release(); }
    }

    private UnwindResult WalkCore(
        int threadId,
        RegisterSnapshot registers,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        IntPtr thread = ProcessAccess.OpenThread(ProcessAccess.THREAD_GET_CONTEXT, false, unchecked((uint)threadId));
        if (thread == IntPtr.Zero)
            return UnwindResult.Unavailable(
                $"Unable to open thread {threadId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

        IntPtr contextPointer = IntPtr.Zero;
        try
        {
            var frame = CreateFrame();
            uint machine;
            if (registers.Is64Bit)
            {
                var context = new CONTEXT64
                {
                    ContextFlags = ProcessAccess.CONTEXT_ALL,
                    FltSave = new byte[512],
                    VectorRegister = new M128A[26]
                };
                if (!ProcessAccess.GetThreadContext(thread, ref context))
                    return UnwindResult.Unavailable(
                        $"GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                machine = MachineAmd64;
                frame.AddrPC = Flat(context.Rip);
                frame.AddrFrame = Flat(context.Rbp);
                frame.AddrStack = Flat(context.Rsp);
                contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CONTEXT64>());
                Marshal.StructureToPtr(context, contextPointer, false);
            }
            else
            {
                var context = new CONTEXT32
                {
                    ContextFlags = ProcessAccess.CONTEXT32_ALL,
                    FloatSave = new byte[112],
                    ExtendedRegisters = new byte[512]
                };
                if (!ProcessAccess.Wow64GetThreadContext(thread, ref context))
                    return UnwindResult.Unavailable(
                        $"Wow64GetThreadContext failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                machine = MachineI386;
                frame.AddrPC = Flat(context.Eip);
                frame.AddrFrame = Flat(context.Ebp);
                frame.AddrStack = Flat(context.Esp);
                contextPointer = Marshal.AllocHGlobal(Marshal.SizeOf<CONTEXT32>());
                Marshal.StructureToPtr(context, contextPointer, false);
            }

            var frames = new List<CallStackFrame>(maximumFrames);
            var seen = new HashSet<ulong>();
            AddFrame(frames, seen, frame.AddrPC.Offset, frame.AddrStack.Offset);
            while (frames.Count < maximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StackWalk64(machine, _process.Handle, thread, ref frame, contextPointer,
                        IntPtr.Zero, SymFunctionTableAccess64, SymGetModuleBase64, IntPtr.Zero)) break;
                if (frame.AddrPC.Offset == 0 || !seen.Add(frame.AddrPC.Offset)) break;
                frames.Add(new CallStackFrame(frames.Count, frame.AddrPC.Offset, frame.AddrStack.Offset,
                    ResolveCore(frame.AddrPC.Offset)));
            }
            return frames.Count == 0
                ? UnwindResult.Unavailable("DbgHelp returned no stack frames.")
                : new UnwindResult(true, frames);
        }
        finally
        {
            if (contextPointer != IntPtr.Zero) Marshal.FreeHGlobal(contextPointer);
            ProcessAccess.CloseHandle(thread);
        }
    }

    private void AddFrame(List<CallStackFrame> frames, HashSet<ulong> seen, ulong address, ulong stack)
    {
        if (address == 0 || !seen.Add(address)) return;
        frames.Add(new CallStackFrame(frames.Count, address, stack, ResolveCore(address)));
    }

    private SymbolResolution ResolveCore(ulong address)
    {
        ModuleDescriptor? module = _modules.FindByAddress(address);
        string moduleName = module?.Name ?? "Unknown";
        ulong? rva = module == null ? null : address - module.BaseAddress;
        string display = module == null ? $"0x{address:X}" : $"{module.Name}+0x{rva:X}";
        ulong displacement = 0;
        string? sourceFile = null;
        int? sourceLine = null;

        int symbolSize = Marshal.SizeOf<SYMBOL_INFO>() + MaximumSymbolName;
        IntPtr symbolBuffer = Marshal.AllocHGlobal(symbolSize);
        try
        {
            Marshal.WriteInt32(symbolBuffer, Marshal.OffsetOf<SYMBOL_INFO>(nameof(SYMBOL_INFO.SizeOfStruct)).ToInt32(),
                Marshal.SizeOf<SYMBOL_INFO>());
            Marshal.WriteInt32(symbolBuffer, Marshal.OffsetOf<SYMBOL_INFO>(nameof(SYMBOL_INFO.MaxNameLen)).ToInt32(),
                MaximumSymbolName);
            if (SymFromAddr(_process.Handle, address, out displacement, symbolBuffer))
            {
                SYMBOL_INFO info = Marshal.PtrToStructure<SYMBOL_INFO>(symbolBuffer);
                IntPtr namePointer = symbolBuffer + Marshal.OffsetOf<SYMBOL_INFO>(nameof(SYMBOL_INFO.Name)).ToInt32();
                string? name = Marshal.PtrToStringAnsi(namePointer, checked((int)info.NameLen));
                if (!string.IsNullOrWhiteSpace(name))
                    display = displacement == 0 ? name : $"{name}+0x{displacement:X}";
            }
        }
        finally { Marshal.FreeHGlobal(symbolBuffer); }

        var line = new IMAGEHLP_LINE64 { SizeOfStruct = (uint)Marshal.SizeOf<IMAGEHLP_LINE64>() };
        if (SymGetLineFromAddr64(_process.Handle, address, out _, ref line) && line.FileName != IntPtr.Zero)
        {
            sourceFile = Marshal.PtrToStringAnsi(line.FileName);
            sourceLine = checked((int)line.LineNumber);
        }
        return new SymbolResolution(address, display, module?.Name, rva, sourceFile, sourceLine, displacement);
    }

    private void EnsureInitialized()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsDbgHelpService));
        if (_initialized && _initializedWithMicrosoftSymbols != MicrosoftSymbolServerEnabled)
        {
            SymCleanup(_process.Handle);
            _initialized = false;
        }
        if (_initialized) return;
        SymSetOptions(SymoptUndname | SymoptDeferredLoads | SymoptLoadLines | SymoptFailCriticalErrors);
        string? searchPath = BuildSearchPath();
        if (!SymInitialize(_process.Handle, searchPath, true))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SymInitialize failed.");
        _initialized = true;
        _initializedWithMicrosoftSymbols = MicrosoftSymbolServerEnabled;
    }

    private string? BuildSearchPath()
    {
        string[] local = _modules.GetModules().Select(module => Path.GetDirectoryName(module.FilePath))
            .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray();
        var paths = new List<string>(local);
        if (MicrosoftSymbolServerEnabled)
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrxMem", "Symbols");
            Directory.CreateDirectory(cache);
            paths.Add($"srv*{cache}*https://msdl.microsoft.com/download/symbols");
        }
        return paths.Count == 0 ? null : string.Join(';', paths);
    }

    public void Dispose()
    {
        if (_disposed) return;
        DbgHelpGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_initialized) { SymCleanup(_process.Handle); _initialized = false; }
        }
        finally { DbgHelpGate.Release(); }
    }

    private static ADDRESS64 Flat(ulong address) => new() { Offset = address, Mode = AddressModeFlat };
    private static STACKFRAME64 CreateFrame() => new()
    {
        Params = new ulong[4],
        Reserved = new ulong[3],
        KdHelp = new KDHELP64 { Reserved0 = new ulong[2] }
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr FunctionTableAccessRoutine64(IntPtr process, ulong address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate ulong GetModuleBaseRoutine64(IntPtr process, ulong address);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymInitialize(IntPtr process, string? searchPath, bool invadeProcess);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymCleanup(IntPtr process);
    [DllImport("dbghelp.dll")]
    private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool SymGetLineFromAddr64(IntPtr process, ulong address, out uint displacement, ref IMAGEHLP_LINE64 line);
    [DllImport("dbghelp.dll")]
    private static extern IntPtr SymFunctionTableAccess64(IntPtr process, ulong address);
    [DllImport("dbghelp.dll")]
    private static extern ulong SymGetModuleBase64(IntPtr process, ulong address);
    [DllImport("dbghelp.dll")]
    private static extern bool StackWalk64(
        uint machineType, IntPtr process, IntPtr thread, ref STACKFRAME64 stackFrame,
        IntPtr contextRecord, IntPtr readMemoryRoutine,
        FunctionTableAccessRoutine64 functionTableAccessRoutine,
        GetModuleBaseRoutine64 getModuleBaseRoutine, IntPtr translateAddress);

    [StructLayout(LayoutKind.Sequential)]
    private struct ADDRESS64 { public ulong Offset; public ushort Segment; public int Mode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KDHELP64
    {
        public ulong Thread;
        public uint ThCallbackStack, ThCallbackBStore, NextCallback, FramePointer;
        public ulong KiCallUserMode, KeUserCallbackDispatcher, SystemRangeStart, KiUserExceptionDispatcher;
        public ulong StackBase, StackLimit;
        public uint BuildVersion, RetpolineStubFunctionTableSize;
        public ulong RetpolineStubFunctionTable;
        public uint RetpolineStubOffset, RetpolineStubSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public ulong[] Reserved0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STACKFRAME64
    {
        public ADDRESS64 AddrPC, AddrReturn, AddrFrame, AddrStack, AddrBStore;
        public IntPtr FuncTableEntry;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public ulong[] Params;
        public int Far, Virtual;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public ulong[] Reserved;
        public KDHELP64 KdHelp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYMBOL_INFO
    {
        public uint SizeOfStruct, TypeIndex;
        public ulong Reserved1, Reserved2;
        public uint Index, Size;
        public ulong ModBase;
        public uint Flags;
        public ulong Value, Address;
        public uint Register, Scope, Tag, NameLen, MaxNameLen;
        public byte Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IMAGEHLP_LINE64
    {
        public uint SizeOfStruct;
        public IntPtr Key;
        public uint LineNumber;
        public IntPtr FileName;
        public ulong Address;
    }
}
