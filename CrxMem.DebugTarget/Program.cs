using System.Runtime.InteropServices;

namespace CrxMem.DebugTarget;

internal static class Program
{
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
        try
        {
            Console.WriteLine(
                $"READY PID={Environment.ProcessId} BUFFER=0x{pinnedBuffer.AddrOfPinnedObject().ToInt64():X} SIZE={StableBuffer.Length}");
            Console.Out.Flush();
            while (!shutdown.IsCancellationRequested)
            {
                StableBuffer[0]++;
                Thread.Sleep(25);
            }
            return 0;
        }
        finally
        {
            pinnedBuffer.Free();
        }
    }
}
