using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media.Imaging;
using CrxMem.MemoryView.Wpf.Controls;
using CrxMem.MemoryView.Wpf.Models;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryViewPerformanceTests
{
    [Fact]
    public void VirtualizedDisassembly_RendersOnlyViewportForOneHundredThousandRows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
                    Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"));
                _ = Application.Current ?? new Application();
                var renderer = new VirtualizedDisassemblyControl
                {
                    ItemsSource = Enumerable.Range(0, 100_000).Select(index =>
                        new DisassemblyPreviewRow("", ((ulong)index).ToString("X16"), "90", "nop", "", (ulong)index))
                        .ToArray()
                };
                renderer.Measure(new Size(1200, 500));
                renderer.Arrange(new Rect(0, 0, 1200, 500));
                var stopwatch = Stopwatch.StartNew();
                var bitmap = new RenderTargetBitmap(1200, 500, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(renderer);
                stopwatch.Stop();

                Assert.InRange(renderer.LastRenderedRowCount, 1, renderer.VisibleRowCapacity);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Viewport render took {stopwatch.Elapsed}.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
