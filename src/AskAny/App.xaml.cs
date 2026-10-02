using System.Diagnostics;
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
    private DispatcherTimer? _hotkeyWatchdog;
    private bool _hotkeyFailureNotified;
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
        // 诊断模式：用真实配置各发一次非流式与流式请求，用来核对请求构造与流式解析。
        var isSelfTest = screenshotMode.Equals("--selftest", StringComparison.OrdinalIgnoreCase);
        // --instance= 只对截图/诊断模式开放。普通启动下如果允许它绕过单实例锁，
        // 两个实例会各装一个全局键盘钩子，双击 Shift 会被响应两次、弹出两个面板。
        var isDiagnosticMode = isScreenshot || isDumpConfig || isSelfTest;
        var instanceName = isDiagnosticMode
            ? e.Args
                .FirstOrDefault(argument => argument.StartsWith("--instance=", StringComparison.OrdinalIgnoreCase))
                ?.Split('=', 2)[1]
            : null;
        var mutexName = string.IsNullOrWhiteSpace(instanceName)
            ? @"Local\AskAny.Desktop"
            : $@"Local\AskAny.Desktop.{instanceName}";

        if (!isDiagnosticMode)
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
        // 托盘常驻应用挂掉时用户几乎拿不到线索，这里把三条异常通道都记下来。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        if (isDumpConfig)
        {
            DumpConfig(e.Args);
            Shutdown();
            return;
        }

        if (isSelfTest)
        {
            RunSelfTest(e.Args);
            Shutdown();
            return;
        }

        // 超时改由各服务按请求类型分别控制：HttpClient 的 Timeout 覆盖整个响应读取过程，
        // 流式长答案会被它在中途掐断。
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
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
            var settings = new SettingsWindow(
                configService,
                previewConfig,
                new AiService(_httpClient),
                new SearchService(_httpClient))
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

        // --screenshot <输出> [功能索引]：可选指定选中哪一项，
        // 用于截到来源切换条这类「只在特定选中项下出现」的界面。
        if (isScreenshot &&
            e.Args.Length >= 3 &&
            int.TryParse(e.Args[2], out var previewFunctionIndex))
        {
            mainWindow.PreviewFunctionIndex = previewFunctionIndex;
        }

        mainWindow.Show();
        mainWindow.UpdateLayout();

        if (isScreenshot)
        {
            // 不能在这里阻塞等待初次加载：LoadConfigurationAsync 的 await 需要回到 UI 线程，
            // 阻塞 UI 线程会直接死锁（--dump-config 栽过同一个坑）。
            // 改为异步等待、截完自行关闭（ShutdownMode 是 OnExplicitShutdown）。
            SafeTask.Run(
                () => SaveScreenshotAfterLoadAsync(mainWindow, e.Args[1]),
                "截图预览");
            return;
        }

        mainWindow.Hide();
        CreateTrayIcon(mainWindow);

        _doubleTapHook = new GlobalDoubleTapHook();
        _doubleTapHook.DoubleShiftPressed += (_, _) => CaptureSelectionAndShow(mainWindow);
        _doubleTapHook.DoubleCtrlPressed += (_, _) => CaptureScreenshotAndShow(mainWindow);
        InstallHotkeyWithFeedback();

        StartHotkeyWatchdog();
    }

    // 注册失败不能让整个应用挂掉：记日志 + 托盘气泡提示，用户还能从托盘菜单重试。
    private void InstallHotkeyWithFeedback(bool notify = false)
    {
        if (_doubleTapHook is null)
        {
            return;
        }

        try
        {
            _doubleTapHook.Reinstall();

            if (notify)
            {
                ErrorLog.Write("全局快捷键已重新注册");
                ShowTrayBalloon("AskAny", "全局快捷键已重新注册。");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorLog.Write("注册全局快捷键失败", exception);

            if (notify || !_hotkeyFailureNotified)
            {
                _hotkeyFailureNotified = true;
                ShowTrayBalloon(
                    "AskAny 快捷键不可用",
                    "无法注册全局键盘监听，双击 Shift / 双击 Ctrl 将无效。可从托盘菜单重试。");
            }
        }
    }

    // 钩子被系统摘掉是没有通知的，只能定期重装来自愈。间隔取 30 分钟：
    // 重装瞬间（微秒级）理论上会漏掉按键，频率越低影响越小。
    private void StartHotkeyWatchdog()
    {
        _hotkeyWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _hotkeyWatchdog.Tick += (_, _) => InstallHotkeyWithFeedback();
        _hotkeyWatchdog.Start();
    }

    private void ShowTrayBalloon(string title, string text)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // 有配置文件时 LoadConfigurationAsync 是真正异步的。原来 Show() 之后立刻截图，
    // 截到的是「加载前」状态（模型下拉还是空的），CI 预览会误导人。
    private async Task SaveScreenshotAfterLoadAsync(MainWindow mainWindow, string outputPath)
    {
        try
        {
            await mainWindow.InitialLoadCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            ErrorLog.Write("截图预览：等待初次加载超时");
        }

        mainWindow.UpdateLayout();
        SaveScreenshot(mainWindow, outputPath);
        Shutdown();
    }

    // 用法：AskAny.exe --selftest <输出 txt>
    // 用真实配置各发一次非流式与流式请求，把结果写成报告。
    // 目的是核对「请求构造 + 响应解析 + 流式分片解析」在真实提供商上是否成立 ——
    // 这些路径没有单测能覆盖到真实 API 的差异。会产生少量计费调用。
    private static void RunSelfTest(string[] args)
    {
        var outputPath = args.Length >= 2 ? args[1] : string.Empty;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var report = new StringBuilder();

        try
        {
            // 必须在线程池上跑：LoadAsync 等的 await 需要回到 UI 线程，
            // 在 UI 线程上同步等待会死锁（--dump-config 曾栽在这里）。
            Task.Run(async () =>
            {
                var config = await new ConfigService().LoadAsync();
                var provider = config.Providers.FirstOrDefault(item => item.Id == config.SelectedProviderId)
                               ?? config.Providers.FirstOrDefault();

                if (provider is null)
                {
                    report.AppendLine("没有可用的提供商。");
                    return;
                }

                report.AppendLine($"提供商：{provider.Name}");
                report.AppendLine($"协议  ：{provider.Protocol}");
                report.AppendLine($"地址  ：{provider.BaseUri}");
                report.AppendLine($"模型  ：{provider.SelectedModel}");
                report.AppendLine();

                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var service = new AiService(http);
                var key = ConfigService.Unprotect(provider.ApiKeyProtected);
                var function = FunctionCatalog.CreateDefaultFunctions()[0];

                try
                {
                    var plain = await service.ExecuteAsync(
                        function, "只回复“连接成功”四个字。", provider, key, null);
                    report.AppendLine($"非流式：成功，回答 {plain.Answer.Length} 字");
                }
                catch (Exception exception)
                {
                    report.AppendLine($"非流式：失败 —— {exception.Message}");
                }

                var deltas = 0;
                try
                {
                    var streamed = await service.ExecuteStreamingAsync(
                        function,
                        "数到三。",
                        provider,
                        key,
                        null,
                        null,
                        null,
                        (_, _) => deltas++);
                    report.AppendLine($"流式  ：成功，{deltas} 个分片，最终 {streamed.Answer.Length} 字");
                }
                catch (Exception exception)
                {
                    report.AppendLine($"流式  ：失败（真实使用时会回退到非流式）—— {exception.Message}");
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            report.AppendLine($"自检异常：{exception.GetType().Name}: {exception.Message}");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
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
        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () => SafeTask.Run(
                () => mainWindow.ShowScreenshotFromHotkeyAsync(),
                "全局截图快捷键"));
    }

    private void CaptureSelectionAndShow(MainWindow mainWindow)
    {
        // 必须把捕获放到钩子回调之外执行：UI Automation 查询在浏览器上可能耗时数百毫秒，
        // 而低级键盘钩子回调有超时限制，超时会被系统直接摘掉钩子（表现为双击 Shift 失效）。
        // 排到 Dispatcher 队列后钩子立刻返回；捕获仍在 Show 之前完成，浏览器此刻仍有焦点。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            () =>
            {
                var selectedText = SelectionCaptureService.TryCapture();
                mainWindow.ShowFromHotkey(selectedText);
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
        menu.Items.Add("重新注册全局快捷键", null, (_, _) => InstallHotkeyWithFeedback(notify: true));
        menu.Items.Add("打开日志", null, (_, _) => OpenErrorLog());
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

    // 日志不存在时也把目录打开，让用户知道去哪看。
    private static void OpenErrorLog()
    {
        try
        {
            if (File.Exists(ErrorLog.LogPath))
            {
                Process.Start(new ProcessStartInfo(ErrorLog.LogPath) { UseShellExecute = true });
                return;
            }

            var directory = Path.GetDirectoryName(ErrorLog.LogPath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or IOException)
        {
            ErrorLog.Write("打开日志", exception);
        }
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
        _hotkeyWatchdog?.Stop();
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
        ErrorLog.Write("DispatcherUnhandledException", e.Exception);

        MessageBox.Show(
            $"{e.Exception.Message}\n\n详细信息已写入：\n{ErrorLog.LogPath}",
            "AskAny",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
