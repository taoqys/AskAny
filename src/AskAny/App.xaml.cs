using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AskAny.Native;
using AskAny.Services;

namespace AskAny;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private HttpClient? _httpClient;
    private GlobalDoubleShiftHook? _doubleShiftHook;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var singleInstanceMutex = new Mutex(true, @"Local\AskAny.Desktop", out var isFirstInstance);
        if (!isFirstInstance)
        {
            singleInstanceMutex.Dispose();
            Shutdown();
            return;
        }

        _singleInstanceMutex = singleInstanceMutex;

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        var configService = new ConfigService();
        var mainWindow = new MainWindow(
            configService,
            new AiService(_httpClient),
            new SearchService(_httpClient));

        mainWindow.Show();
        mainWindow.UpdateLayout();

        if (e.Args.Length >= 2 &&
            e.Args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
        {
            SaveScreenshot(mainWindow, e.Args[1]);
            Shutdown();
            return;
        }

        mainWindow.Hide();

        _doubleShiftHook = new GlobalDoubleShiftHook();
        _doubleShiftHook.DoubleShiftPressed += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, mainWindow.ShowFromHotkey);
        _doubleShiftHook.Install();
    }

    private static void SaveScreenshot(Window window, string outputPath)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(outputPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _doubleShiftHook?.Dispose();
        _httpClient?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "AskAny",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
