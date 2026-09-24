using System.Windows;
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

        _singleInstanceMutex = new Mutex(true, @"Local\AskAny.Desktop", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

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
        mainWindow.Hide();

        _doubleShiftHook = new GlobalDoubleShiftHook();
        _doubleShiftHook.DoubleShiftPressed += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, mainWindow.ShowFromHotkey);
        _doubleShiftHook.Install();
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
