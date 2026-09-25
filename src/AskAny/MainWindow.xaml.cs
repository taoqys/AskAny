using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AskAny.Models;
using AskAny.Services;
using Microsoft.Win32;

namespace AskAny;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly SearchService _searchService;
    private readonly HistoryService _historyService;
    private readonly IReadOnlyList<FunctionOption> _functions =
    [
        new(WorkflowMode.Explain, "解释说明", "拆解概念、背景和关键要点", "\uE946", 0, -1.5),
        new(WorkflowMode.Answer, "回答问题", "直接、准确地解答当前问题", "\uE8BD", 0, -2.5),
        new(WorkflowMode.TrackNews, "新闻追踪", "搜索最近动态并整理事件脉络", "\uE909", 0, -3),
        new(WorkflowMode.Think, "深度思考", "仅此模式请求模型的扩展推理", "\uE735", 0, -2.5),
        new(WorkflowMode.ExplainOnline, "联网解释", "结合网络资料解释问题并标注来源", "\uE774", 0, -3)
    ];

    private AppConfig _config = new();
    private ProviderConfig? _currentProvider;
    private bool _isRunning;
    private bool _allowClose;
    private bool _suppressDeactivateHide;
    private bool _isLoadingProviders;
    private bool _isFocusingPrompt;
    private string _lastAnswer = string.Empty;

    public MainWindow(
        ConfigService configService,
        AiService aiService,
        SearchService searchService,
        HistoryService historyService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
        _searchService = searchService;
        _historyService = historyService;

        FunctionList.ItemsSource = _functions;
        FunctionList.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        _config = await _configService.LoadAsync();

        _isLoadingProviders = true;
        ProviderComboBox.ItemsSource = null;
        ProviderComboBox.ItemsSource = _config.Providers;
        _currentProvider = _config.Providers.FirstOrDefault(
                               provider => provider.Id == _config.SelectedProviderId)
                           ?? _config.Providers.FirstOrDefault();
        ProviderComboBox.SelectedItem = _currentProvider;
        RefreshModelComboBox();
        _isLoadingProviders = false;

        Topmost = _config.KeepWindowOnTop;
        UpdatePinButton();
    }

    public void ShowFromHotkey(string? selectedText = null)
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        ResetForNewRequest();

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(12, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(12, (workArea.Height - Height) / 3);

        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = _config.KeepWindowOnTop;

        if (_config.AutoFillSelectedText && !string.IsNullOrWhiteSpace(selectedText))
        {
            PromptBox.Text = selectedText.Trim();
            PromptBox.CaretIndex = PromptBox.Text.Length;
        }

        FocusPromptEditor();
    }

    public void ShowSettingsFromTray()
    {
        ShowFromHotkey();
        OpenSettings();
    }

    public void ShowHistoryFromTray()
    {
        ShowFromHotkey();
        ShowHistory();
    }

    public void RequestExit()
    {
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            if (FunctionList.Visibility == Visibility.Visible)
            {
                MoveSelection(-1);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (FunctionList.Visibility == Visibility.Visible)
            {
                MoveSelection(1);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (PromptEditorBorder.Visibility == Visibility.Visible)
            {
                CommitModelSelection();
                _ = ExecuteSelectedAsync();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            OpenSettings();
            e.Handled = true;
        }
    }

    private async void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_config.HideWhenDeactivated || _suppressDeactivateHide)
        {
            return;
        }

        await Task.Delay(120);
        if (!IsActive && IsVisible && !_suppressDeactivateHide)
        {
            Hide();
        }
    }

    private void Window_Activated(object sender, EventArgs e)
    {
        if (PromptEditorBorder.Visibility == Visibility.Visible)
        {
            FocusPromptEditor();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            FindVisualParent<Button>((DependencyObject)e.OriginalSource) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the mouse button is released during the call.
        }
    }

    private async void FocusPromptEditor()
    {
        if (_isFocusingPrompt || PromptEditorBorder.Visibility != Visibility.Visible)
        {
            return;
        }

        _isFocusingPrompt = true;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!IsVisible || PromptEditorBorder.Visibility != Visibility.Visible)
                {
                    return;
                }

                Activate();
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    ForceForegroundWindow(handle);
                }

                PromptBox.Focus();
                Keyboard.Focus(PromptBox);
                PromptBox.CaretIndex = PromptBox.Text.Length;

                await Task.Delay(90);
                if (IsActive && PromptBox.IsKeyboardFocused)
                {
                    return;
                }
            }
        }
        finally
        {
            _isFocusingPrompt = false;
        }
    }

    private static void ForceForegroundWindow(IntPtr windowHandle)
    {
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;
        var foregroundLockChanged = false;
        var previousForegroundLockTimeout = 0u;

        try
        {
            if (SystemParametersInfo(
                    0x2000,
                    0,
                    ref previousForegroundLockTimeout,
                    0))
            {
                foregroundLockChanged = SystemParametersInfo(
                    0x2001,
                    0,
                    IntPtr.Zero,
                    0x0002);
            }

            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            }

            ShowWindow(windowHandle, 9);
            BringWindowToTop(windowHandle);
            SetWindowPos(
                windowHandle,
                new IntPtr(-1),
                0,
                0,
                0,
                0,
                0x0001 | 0x0002 | 0x0040);
            if (!SetForegroundWindow(windowHandle))
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SwitchToThisWindow(windowHandle, true);
                SetForegroundWindow(windowHandle);
            }

            SetFocus(windowHandle);
        }
        finally
        {
            if (foregroundLockChanged)
            {
                SystemParametersInfo(
                    0x2001,
                    0,
                    new IntPtr(previousForegroundLockTimeout),
                    0x0002);
            }

            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
            {
                return parent;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void MoveSelection(int offset)
    {
        if (FunctionList.Items.Count == 0)
        {
            return;
        }

        var next = FunctionList.SelectedIndex + offset;
        if (next < 0)
        {
            next = FunctionList.Items.Count - 1;
        }
        else if (next >= FunctionList.Items.Count)
        {
            next = 0;
        }

        FunctionList.SelectedIndex = next;
        FunctionList.ScrollIntoView(FunctionList.SelectedItem);
    }

    private async Task ExecuteSelectedAsync()
    {
        if (_isRunning)
        {
            return;
        }

        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "请先输入问题";
            PromptBox.Focus();
            return;
        }

        if (FunctionList.SelectedItem is not FunctionOption option)
        {
            StatusText.Text = "请选择一个功能";
            return;
        }

        if (_currentProvider is null)
        {
            StatusText.Text = "请先配置提供商";
            return;
        }

        _isRunning = true;
        BusyProgress.Visibility = Visibility.Visible;
        ResponsePanel.Visibility = Visibility.Visible;
        FunctionList.Visibility = Visibility.Collapsed;
        SetResponsePromptDisplay(prompt);
        ResponseModeText.Text = $"{option.Name} · {_currentProvider.Name} / {_currentProvider.SelectedModel}";
        ResponseMetaText.Text = "正在准备…";
        SetOutputMarkdown(option.Mode is WorkflowMode.TrackNews or WorkflowMode.ExplainOnline
            ? "正在检索网络资料…"
            : "正在生成回答…");
        StatusText.Text = "正在执行";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SearchPacket? search = null;

        try
        {
            if (option.Mode is WorkflowMode.TrackNews or WorkflowMode.ExplainOnline)
            {
                var tavilyKey = ConfigService.Unprotect(_config.TavilyApiKeyProtected);
                search = await _searchService.SearchAsync(
                    prompt,
                    option.Mode == WorkflowMode.TrackNews,
                    tavilyKey);

                ResponseMetaText.Text = $"已检索 {search.Sources.Count} 条资料，正在整理…";
                SetOutputMarkdown("正在结合检索资料生成回答…");
            }

            var answer = await _aiService.ExecuteAsync(
                option.Mode,
                prompt,
                _currentProvider,
                ConfigService.Unprotect(_currentProvider.ApiKeyProtected),
                search);

            _lastAnswer = answer;
            SetOutputMarkdown(answer);
            stopwatch.Stop();
            var sourceText = search is null ? string.Empty : $"，{search.Sources.Count} 条来源";
            ResponseMetaText.Text = $"{stopwatch.Elapsed.TotalSeconds:F1} 秒{sourceText}";
            StatusText.Text = "执行完成";

            await _historyService.AddAsync(new HistoryEntry
            {
                Mode = option.Mode,
                ModeName = option.Name,
                ProviderName = _currentProvider.Name,
                Model = _currentProvider.SelectedModel,
                Prompt = prompt,
                Response = answer,
                SourceCount = search?.Sources.Count ?? 0
            });
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            var error = exception is TaskCanceledException
                ? "请求超时，请稍后重试。"
                : exception.Message;
            _lastAnswer = error;
            SetOutputMarkdown("## 执行失败\n\n" + error);
            ResponseMetaText.Text = "执行失败";
            StatusText.Text = "执行失败";
        }
        finally
        {
            BusyProgress.Visibility = Visibility.Collapsed;
            _isRunning = false;
        }
    }

    private void SetOutputMarkdown(string markdown)
    {
        OutputRichText.Document = MarkdownRenderer.Render(markdown);
        OutputRichText.ScrollToHome();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FunctionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FunctionList.SelectedItem is FunctionOption option)
        {
            StatusText.Text = $"已选择：{option.Name}，Enter 执行";
        }
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProviders || ProviderComboBox.SelectedItem is not ProviderConfig provider)
        {
            return;
        }

        _currentProvider = provider;
        _config.SelectedProviderId = provider.Id;
        RefreshModelComboBox();
        _ = _configService.SaveAsync(_config);
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingProviders || _currentProvider is null)
        {
            return;
        }

        if (ModelComboBox.SelectedItem is string selectedModel &&
            !string.IsNullOrWhiteSpace(selectedModel))
        {
            _currentProvider.SelectedModel = selectedModel;
            _ = _configService.SaveAsync(_config);
        }
    }

    private void ModelComboBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitModelSelection();
    }

    private void CommitModelSelection()
    {
        if (_currentProvider is null)
        {
            return;
        }

        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        if (!_currentProvider.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            _currentProvider.Models.Add(model);
        }

        _currentProvider.SelectedModel = model;
        _ = _configService.SaveAsync(_config);
    }

    private void RefreshModelComboBox()
    {
        if (_currentProvider is null)
        {
            ModelComboBox.ItemsSource = null;
            return;
        }

        var wasLoading = _isLoadingProviders;
        _isLoadingProviders = true;
        ModelComboBox.ItemsSource = null;
        ModelComboBox.ItemsSource = _currentProvider.Models;
        ModelComboBox.Text = _currentProvider.SelectedModel;
        _isLoadingProviders = wasLoading;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private async void OpenSettings()
    {
        CommitModelSelection();
        _suppressDeactivateHide = true;

        try
        {
            var settings = new SettingsWindow(_configService, _config, _aiService)
            {
                Owner = this
            };

            if (settings.ShowDialog() == true)
            {
                await LoadConfigurationAsync();
                StatusText.Text = "设置已保存";
            }
        }
        finally
        {
            _suppressDeactivateHide = false;
        }
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ShowHistory();
    }

    private async void ShowHistory()
    {
        _suppressDeactivateHide = true;

        try
        {
            var historyWindow = new HistoryWindow(_historyService)
            {
                Owner = this
            };

            if (historyWindow.ShowDialog() == true &&
                historyWindow.SelectedEntryToReuse is { } entry)
            {
                PromptBox.Text = entry.Prompt;
                FunctionList.SelectedItem = _functions.FirstOrDefault(
                    function => function.Mode == entry.Mode);
                ShowWorkflowList();
                PromptBox.Focus();
            }
        }
        finally
        {
            _suppressDeactivateHide = false;
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkflowList();
    }

    private void ShowWorkflowList()
    {
        PromptInfoText.Visibility = Visibility.Collapsed;
        PromptInfoText.Text = string.Empty;
        PromptEditorBorder.Visibility = Visibility.Visible;
        PromptRowDefinition.Height = new GridLength(120);
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        StatusText.Text = "↑ ↓ 选择功能，Enter 执行";
        FocusPromptEditor();
    }

    private void ResetForNewRequest()
    {
        _lastAnswer = string.Empty;
        PromptBox.Clear();
        PromptInfoText.Text = string.Empty;
        PromptInfoText.Visibility = Visibility.Collapsed;
        PromptEditorBorder.Visibility = Visibility.Visible;
        PromptRowDefinition.Height = new GridLength(120);
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        StatusText.Text = "↑ ↓ 选择功能，Enter 执行";
    }

    private void SetResponsePromptDisplay(string prompt)
    {
        PromptInfoText.Text = prompt;
        PromptInfoText.Visibility = Visibility.Visible;
        PromptEditorBorder.Visibility = Visibility.Collapsed;
        PromptRowDefinition.Height = new GridLength(58);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastAnswer))
        {
            return;
        }

        Clipboard.SetText(_lastAnswer);
        StatusText.Text = "回答已复制";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastAnswer))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存回答",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            FileName = MakeFileName(PromptBox.Text)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await File.WriteAllTextAsync(dialog.FileName, _lastAnswer);
        StatusText.Text = "回答已保存";
    }

    private static string MakeFileName(string prompt)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(prompt
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();

        if (cleaned.Length == 0)
        {
            return "AskAny-回答.md";
        }

        return (cleaned.Length > 40 ? cleaned[..40] : cleaned) + ".md";
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _config.KeepWindowOnTop = !_config.KeepWindowOnTop;
        Topmost = _config.KeepWindowOnTop;
        UpdatePinButton();
        await _configService.SaveAsync(_config);
    }

    private void UpdatePinButton()
    {
        PinGlyph.Text = _config.KeepWindowOnTop ? "\uE77A" : "\uE77B";
        PinButton.ToolTip = _config.KeepWindowOnTop ? "取消窗口置顶" : "固定窗口置顶";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint attachThreadId,
        uint attachToThreadId,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.Bool)] bool altTab);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref uint value,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        IntPtr value,
        uint flags);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);
}
