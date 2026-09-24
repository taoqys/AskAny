using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AskAny.Native;
using AskAny.Services;
using Forms = System.Windows.Forms;

namespace AskAny;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private HttpClient? _httpClient;
    private GlobalDoubleShiftHook? _doubleShiftHook;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var isScreenshot = e.Args.Length >= 2 &&
                           e.Args[0].Equals("--screenshot", StringComparison.OrdinalIgnoreCase);
        var instanceName = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];
        var mutexName = string.IsNullOrWhiteSpace(instanceName)
            ? @"Local\AskAny.Desktop"
            : $@"Local\AskAny.Desktop.{instanceName}";

        if (!isScreenshot)
        {
            var singleInstanceMutex = new Mutex(true, mutexName, out var isFirstInstance);
            if (!isFirstInstance)
            {
                singleInstanceMutex.Dispose();
                Shutdown();
                return;
            }

            _singleInstanceMutex = singleInstanceMutex;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        var configService = new ConfigService();
        var historyService = new HistoryService();
        var mainWindow = new MainWindow(
            configService,
            new AiService(_httpClient),
            new SearchService(_httpClient),
            historyService);

        mainWindow.Show();
        mainWindow.UpdateLayout();

        if (isScreenshot)
        {
            SaveScreenshot(mainWindow, e.Args[1]);
            Shutdown();
            return;
        }

        mainWindow.Hide();
        CreateTrayIcon(mainWindow);

        _doubleShiftHook = new GlobalDoubleShiftHook();
        _doubleShiftHook.DoubleShiftPressed += (_, _) => CaptureSelectionAndShow(mainWindow);
        _doubleShiftHook.Install();
    }

    private void CaptureSelectionAndShow(MainWindow mainWindow)
    {
        _ = Task.Run(() =>
        {
            var selectedText = SelectionCaptureService.TryCapture();
            Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                () => mainWindow.ShowFromHotkey(selectedText));
        });
    }

    private void CreateTrayIcon(MainWindow mainWindow)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 AskAny", null, (_, _) => Dispatch(() => mainWindow.ShowFromHotkey()));
        menu.Items.Add("历史记录", null, (_, _) => Dispatch(mainWindow.ShowHistoryFromTray));
        menu.Items.Add("设置", null, (_, _) => Dispatch(mainWindow.ShowSettingsFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("双击 Shift 唤起") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatch(mainWindow.RequestExit));

        using var stream = GetResourceStream(
            new Uri("pack://application:,,,/Assets/AskAny.ico")).Stream;
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "AskAny AI 助手",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatch(() => mainWindow.ShowFromHotkey());
    }

    private void Dispatch(Action action)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
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
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

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
