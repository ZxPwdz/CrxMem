using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace CrxMem.Tests;

public sealed class MainWindowReferenceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "CrxMem.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReferenceTheme_LoadsDarkControlResources()
    {
        RunOnSta(() =>
        {
            EnsureWpfEnvironment();
            var dictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CrxMem;component/Themes/MainWindow.Reference.xaml", UriKind.Absolute)
            };

            Assert.Equal(Color.FromRgb(0x07, 0x14, 0x21), Assert.IsType<SolidColorBrush>(dictionary["Main.Window"]).Color);
            Assert.Equal(Color.FromRgb(0x25, 0x88, 0xF8), Assert.IsType<SolidColorBrush>(dictionary["Main.Accent"]).Color);
            Assert.NotNull(dictionary[typeof(TextBox)]);
            Assert.NotNull(dictionary[typeof(ComboBox)]);
            Assert.NotNull(dictionary[typeof(ComboBoxItem)]);
            Assert.NotNull(dictionary[typeof(CheckBox)]);
            Assert.NotNull(dictionary[typeof(MenuItem)]);
            Assert.NotNull(dictionary[typeof(ScrollBar)]);
            var dataGridStyle = Assert.IsType<Style>(dictionary["Main.DataGrid"]);
            Assert.Contains(dataGridStyle.Setters.OfType<Setter>(),
                setter => setter.Property == DataGrid.ColumnHeaderHeightProperty && Equals(setter.Value, 34d));
            Assert.Contains(dataGridStyle.Setters.OfType<Setter>(),
                setter => setter.Property == DataGrid.RowHeightProperty && Equals(setter.Value, 24d));
        });
    }

    [Fact]
    public void MainWindow_PreservesNamedControlsStateAndResponsiveLayout()
    {
        RunOnSta(() =>
        {
            EnsureWpfEnvironment();
            string layoutPath = Path.Combine(_testDirectory, "MainWindow.wpf-layout.json");
            var window = new MainWindow(new MainWindowLayoutStore(layoutPath), initializeRuntimeServices: false);
            try
            {
                Assert.Equal(1152, window.Width);
                Assert.Equal(768, window.Height);
                Assert.Equal(960, window.MinWidth);
                Assert.Equal(640, window.MinHeight);
                var results = Assert.IsType<DataGrid>(window.FindName("lvFoundAddressesWpf"));
                var addressList = Assert.IsType<DataGrid>(window.FindName("lvAddressListWpf"));
                Assert.Equal(DataGridSelectionMode.Extended, results.SelectionMode);
                Assert.Equal(DataGridSelectionMode.Extended, addressList.SelectionMode);
                Assert.True(results.EnableRowVirtualization);
                Assert.True(results.EnableColumnVirtualization);
                Assert.True(addressList.EnableRowVirtualization);
                Assert.True(addressList.EnableColumnVirtualization);
                Assert.Equal(new[] { "Address", "Value", "Previous" }, results.Columns.Select(column => column.Header?.ToString()).ToArray());
                Assert.Equal(new[] { "", "Description", "Address", "Type", "Value" }, addressList.Columns.Select(column => column.Header?.ToString()).ToArray());
                Assert.False(Assert.IsType<Button>(window.FindName("btnFirstScan")).IsEnabled);
                Assert.False(Assert.IsType<Button>(window.FindName("btnNextScan")).IsEnabled);
                Assert.Contains("No process selected", Assert.IsType<TextBlock>(window.FindName("txtProcessName")).Text);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                foreach ((int width, int height) in new[] { (960, 640), (1024, 700), (1152, 768), (1280, 860) })
                {
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                    Assert.True(results.ActualWidth > 300);
                    Assert.True(addressList.ActualHeight >= 75);
                }

                foreach (double dpi in new[] { 96d, 120d, 144d, 192d })
                {
                    root.Measure(new Size(1152, 768));
                    root.Arrange(new Rect(0, 0, 1152, 768));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1152, 768, dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    Assert.Equal(1152, bitmap.PixelWidth);
                    string? renderPath = Environment.GetEnvironmentVariable("CRXMEM_MAINWINDOW_RENDER_PATH");
                    if (dpi == 96 && !string.IsNullOrWhiteSpace(renderPath))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using FileStream stream = File.Create(renderPath);
                        encoder.Save(stream);
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LayoutStore_RoundTripsAndRecoversFromCorruptData()
    {
        string path = Path.Combine(_testDirectory, "MainWindow.layout.json");
        var store = new MainWindowLayoutStore(path);
        store.Save(new MainWindowLayoutSettings
        {
            WindowWidth = 1450,
            WindowHeight = 1085,
            WindowLeft = 50,
            WindowTop = 75,
            Maximized = true,
            AddressListHeight = 320
        });

        MainWindowLayoutSettings loaded = store.Load();
        Assert.Equal(1450, loaded.WindowWidth);
        Assert.Equal(1085, loaded.WindowHeight);
        Assert.Equal(320, loaded.AddressListHeight);
        Assert.True(loaded.Maximized);

        File.WriteAllText(path, "{ invalid json");
        MainWindowLayoutSettings recovered = store.Load();
        Assert.Equal(1152, recovered.WindowWidth);
        Assert.Equal(768, recovered.WindowHeight);
    }

    [Fact]
    public void LayoutStore_MigratesV1DefaultButPreservesCustomBounds()
    {
        Directory.CreateDirectory(_testDirectory);
        string defaultPath = Path.Combine(_testDirectory, "MainWindow.v1-default.json");
        File.WriteAllText(defaultPath,
            """{"SchemaVersion":1,"WindowWidth":1280,"WindowHeight":860,"WindowLeft":20,"WindowTop":30,"Maximized":false,"AddressListHeight":215}""");

        MainWindowLayoutSettings migratedDefault = new MainWindowLayoutStore(defaultPath).Load();
        Assert.Equal(3, migratedDefault.SchemaVersion);
        Assert.Equal(1152, migratedDefault.WindowWidth);
        Assert.Equal(768, migratedDefault.WindowHeight);
        Assert.Equal(190, migratedDefault.AddressListHeight);
        Assert.Equal(20, migratedDefault.WindowLeft);
        Assert.Equal(30, migratedDefault.WindowTop);

        string customPath = Path.Combine(_testDirectory, "MainWindow.v1-custom.json");
        File.WriteAllText(customPath,
            """{"SchemaVersion":1,"WindowWidth":1400,"WindowHeight":900,"WindowLeft":40,"WindowTop":50,"Maximized":true,"AddressListHeight":300}""");

        MainWindowLayoutSettings migratedCustom = new MainWindowLayoutStore(customPath).Load();
        Assert.Equal(3, migratedCustom.SchemaVersion);
        Assert.Equal(1400, migratedCustom.WindowWidth);
        Assert.Equal(900, migratedCustom.WindowHeight);
        Assert.Equal(300, migratedCustom.AddressListHeight);
        Assert.True(migratedCustom.Maximized);

        string v2Path = Path.Combine(_testDirectory, "MainWindow.v2-tall.json");
        File.WriteAllText(v2Path,
            """{"SchemaVersion":2,"WindowWidth":960,"WindowHeight":881,"WindowLeft":459,"WindowTop":101,"Maximized":false,"AddressListHeight":305}""");

        MainWindowLayoutSettings migratedV2 = new MainWindowLayoutStore(v2Path).Load();
        Assert.Equal(3, migratedV2.SchemaVersion);
        Assert.Equal(960, migratedV2.WindowWidth);
        Assert.Equal(768, migratedV2.WindowHeight);
        Assert.Equal(190, migratedV2.AddressListHeight);
        Assert.Equal(459, migratedV2.WindowLeft);
        Assert.Equal(101, migratedV2.WindowTop);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true);
    }

    private static void EnsureWpfEnvironment()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
            Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"));
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
