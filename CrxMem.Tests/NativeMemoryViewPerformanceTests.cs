using System.Diagnostics;
using System.Globalization;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;
using Xunit.Abstractions;

namespace CrxMem.Tests;

public sealed class NativeMemoryViewPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptIn_LocalTargetNavigationMedianIsWithinBudget()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_PERF_TESTS"), "1", StringComparison.Ordinal))
            return;
        string targetPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"));
        using var target = new Process
        {
            StartInfo = new ProcessStartInfo(targetPath)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }
        };
        Assert.True(target.Start());
        try
        {
            string ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            string probeText = ready.Split(' ').Single(part => part.StartsWith("PROBE="))[8..];
            ulong probe = ulong.Parse(probeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            var service = new MemoryViewDataService(process);
            await service.LoadSnapshotAsync(probe, CancellationToken.None); // warm-up
            var samples = new List<double>();
            for (int index = 0; index < 7; index++)
            {
                process.InvalidateReadCache();
                var stopwatch = Stopwatch.StartNew();
                MemoryViewSnapshot snapshot = await service.LoadSnapshotAsync(probe, CancellationToken.None);
                stopwatch.Stop();
                Assert.NotEmpty(snapshot.Instructions);
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            double median = samples[samples.Count / 2];
            output.WriteLine($"Navigation samples (ms): {string.Join(", ", samples.Select(value => value.ToString("F2")))}");
            output.WriteLine($"Median: {median:F2} ms");
            Assert.True(median < 250, $"Median first-useful-disassembly time was {median:F2} ms.");
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }
}
