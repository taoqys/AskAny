using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AskAny.Models;
using AskAny.Native;
using AskAny.Services;
using Forms = System.Windows.Forms;

namespace AskAny;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private HttpClient? _httpClient;
    private GlobalDoubleTapHook? _doubleTapHook;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshotMode = e.Args.Length >= 2 ? e.Args[0] : string.Empty;
        var isScreenshot = screenshotMode.Equals("--screenshot", StringComparison.OrdinalIgnoreCase) ||
                           screenshotMode.Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase) ||
                           screenshotMode.Equals("--screenshot-markdown", StringComparison.OrdinalIgnoreCase);
        // 诊断模式：把配置读进来跑完整套迁移后另存为 JSON，用来核对迁移结果而不碰真实配置。
        var isDumpConfig = screenshotMode.Equals("--dump-config", StringComparison.OrdinalIgnoreCase);
        var instanceName = e.Args
            .FirstOrDefault(argument => argument.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];
        var mutexName = string.IsNullOrWhiteSpace(instanceName)
            ? @"Local\AskAny.Desktop"
            : $@"Local\AskAny.Desktop.{instanceName}";

        if (!isScreenshot && !isDumpConfig)
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

        if (isDumpConfig)
        {
            DumpConfig(e.Args);
            Shutdown();
            return;
        }

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        var configService = new ConfigService();
        var historyService = new HistoryService();
        if (screenshotMode.Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase))
        {
            var provider = ProviderCatalog.CreatePreset("openai-chat");
            var previewConfig = new AppConfig
            {
                Providers = [provider],
                SelectedProviderId = provider.Id
            };
            var settings = new SettingsWindow(configService, previewConfig, new AiService(_httpClient))
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            settings.Show();
            if (e.Args.Length >= 3 && int.TryParse(e.Args[2], out var tabIndex))
            {
                settings.PreviewTabIndex = tabIndex;
            }

            settings.UpdateLayout();
            SaveScreenshot(settings, e.Args[1]);
            Shutdown();
            return;
        }

        if (screenshotMode.Equals("--screenshot-markdown", StringComparison.OrdinalIgnoreCase))
        {
            var preview = new Window
            {
                Title = "Markdown Preview",
                Width = 760,
                Height = 620,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White,
                Content = new System.Windows.Controls.RichTextBox
                {
                    Document = MarkdownRenderer.Render(
                        """
                        ## 公式渲染

                        行内公式：质能方程是 $E = mc^2$。

                        独立公式：

                        $$
                        \int_{-\infty}^{\infty} e^{-x^2} dx = \sqrt{\pi}
                        $$

                        | 项目 | 公式 |
                        | --- | --- |
                        | 二次方程 | $x = \frac{-b \pm \sqrt{b^2-4ac}}{2a}$ |
                        """),
                    IsReadOnly = true,
                    IsDocumentEnabled = true,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(24),
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
                }
            };
            preview.Show();
            preview.UpdateLayout();
            SaveScreenshot(preview, e.Args[1]);
            Shutdown();
            return;
        }

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

        _doubleTapHook = new GlobalDoubleTapHook();
        _doubleTapHook.DoubleShiftPressed += (_, _) => CaptureSelectionAndShow(mainWindow);
        _doubleTapHook.DoubleCtrlPressed += (_, _) => CaptureScreenshotAndShow(mainWindow);
        _doubleTapHook.Install();
    }

    // 用法：AskAny.exe --dump-config <输入配置> <输出 json>
    // 走的是和正常启动完全相同的 ConfigService.LoadAsync（含全部迁移），只是不启动界面、
    // 也不写回原文件——用来核对「老配置迁移成什么样」，且不会动到用户的真实配置。
    private static void DumpConfig(string[] args)
    {
        if (args.Length < 3)
        {
            return;
        }

        var inputPath = args[1];
        var outputPath = args[2];
        if (!File.Exists(inputPath))
        {
            return;
        }

        // 必须放到线程池上执行：LoadAsync 的 await 会捕获 WPF 的同步上下文，
        // 直接在 UI 线程上 .GetResult() 阻塞等待会死锁（续体永远回不到被阻塞的线程）。
        var normalized = Task.Run(() => new ConfigService(inputPath).LoadAsync())
            .GetAwaiter()
            .GetResult();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(normalized, JsonDefaults.Options),
            Encoding.UTF8);
    }

    private void CaptureScreenshotAndShow(MainWindow mainWindow)
    {
        if (Dispatcher.CheckAccess())
        {
            _ = mainWindow.ShowScreenshotFromHotkeyAsync();
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () => _ = mainWindow.ShowScreenshotFromHotkeyAsync());
    }

    private void CaptureSelectionAndShow(MainWindow mainWindow)
    {
        var selectedText = SelectionCaptureService.TryCapture();
        if (Dispatcher.CheckAccess())
        {
            mainWindow.ShowFromHotkey(selectedText);
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () => mainWindow.ShowFromHotkey(selectedText));
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
        _doubleTapHook?.Dispose();
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
