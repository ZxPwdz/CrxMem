using System.Runtime.InteropServices;

namespace CrxMem.DebugTarget;

internal static class Program
{
    private const uint MemCommitReserve = 0x3000;
    private const uint PageExecuteReadWrite = 0x40;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ProbeDelegate(IntPtr buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocationType, uint protection);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

    private static readonly byte[] StableBuffer = Enumerable.Range(0, 4096)
        .Select(index => unchecked((byte)index))
        .ToArray();

    private static int Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            shutdown.Cancel();
        };

        GCHandle pinnedBuffer = GCHandle.Alloc(StableBuffer, GCHandleType.Pinned);
        IntPtr probe = VirtualAlloc(IntPtr.Zero, (UIntPtr)16, MemCommitReserve, PageExecuteReadWrite);
        try
        {
            if (probe == IntPtr.Zero) return Marshal.GetLastWin32Error();
            // x64 Windows: inc byte ptr [rcx]; ret. The target owns this deterministic benign probe.
            Marshal.Copy(new byte[] { 0xFE, 0x01, 0xC3 }, 0, probe, 3);
            var invokeProbe = Marshal.GetDelegateForFunctionPointer<ProbeDelegate>(probe);
            Console.WriteLine(
                $"READY PID={Environment.ProcessId} BUFFER=0x{pinnedBuffer.AddrOfPinnedObject().ToInt64():X} SIZE={StableBuffer.Length} PROBE=0x{probe.ToInt64():X}");
            Console.Out.Flush();
            while (!shutdown.IsCancellationRequested)
            {
                StableBuffer[0]++;
                invokeProbe(pinnedBuffer.AddrOfPinnedObject() + 1);
                Thread.Sleep(25);
            }
            return 0;
        }
        finally
        {
            if (probe != IntPtr.Zero) VirtualFree(probe, UIntPtr.Zero, 0x8000);
            pinnedBuffer.Free();
        }
    }
}
