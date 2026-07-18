using System.Globalization;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class NativePatchServiceTests
{
    [Fact]
    public async Task OptIn_WindowsApplyRestore_AgainstIsolatedPinnedBuffer()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_PATCH_TESTS"), "1", StringComparison.Ordinal))
            return;

        string targetPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"));
        Assert.True(File.Exists(targetPath), $"Patch target was not built: {targetPath}");
        using var target = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(targetPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        Assert.True(target.Start());
        try
        {
            string ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            string addressText = ready.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Single(part => part.StartsWith("BUFFER=", StringComparison.Ordinal))[9..];
            ulong buffer = ulong.Parse(addressText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ulong address = buffer + 128; // The target mutates only byte zero.

            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            using var service = new PatchService(new ProcessPatchMemoryAdapter(process));
            byte[] original = process.Read(new IntPtr(unchecked((long)address)), 4);
            byte[] replacement = new byte[] { 0xF1, 0xF2, 0xF3, 0xF4 };

            PatchResult applied = await service.ApplyAsync(new PatchRequest(address, original, replacement));
            Assert.True(applied.Succeeded, applied.Message);
            PatchResult restored = await service.RestoreAsync(applied.Record!.Id);

            Assert.True(restored.Succeeded, restored.Message);
            process.InvalidateReadCache();
            Assert.Equal(original, process.Read(new IntPtr(unchecked((long)address)), original.Length));
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }
}
