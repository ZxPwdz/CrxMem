using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Views;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryViewRealProcessSmokeTests
{
    [Fact]
    public void RealCurrentProcess_WindowLoadsAndClosesWithoutDispatcherFailure()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            DispatcherUnhandledExceptionEventHandler? handler = null;
            try
            {
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
                    Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"));
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                handler = (_, args) =>
                {
                    failure = args.Exception;
                    args.Handled = true;
                };
                dispatcher.UnhandledException += handler;
                using var process = new ProcessAccess();
                Assert.True(process.Open(Environment.ProcessId));
                var window = new MemoryViewWindow(process);
                window.Show();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                    (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
                Assert.True(window.IsLoaded);
                Assert.True(window.IsVisible);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (handler != null)
                    Dispatcher.CurrentDispatcher.UnhandledException -= handler;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
