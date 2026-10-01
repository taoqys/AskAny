using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AskAny.Models;
using AskAny.Native;
using AskAny.Services;
using Microsoft.Win32;

namespace AskAny;

public partial class MainWindow : Window
{
    private const string ImageOnlyPrompt = "请详细描述并分析这张图片。";

    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly SearchService _searchService;
    private readonly HistoryService _historyService;
    private List<FunctionOption> _functions = FunctionCatalog.CreateDefaultFunctions();

    private AppConfig _config = new();
    private ProviderConfig? _currentProvider;
    private FunctionOption? _activeFunction;
    private List<ModelChoice> _modelChoices = [];
    private readonly List<ConversationTurn> _conversation = [];
    private readonly List<ImageAttachment> _pendingAttachments = [];
    private bool _isRunning;
    private bool _allowClose;
    private bool _suppressDeactivateHide;
    private bool _isRefreshingModels;
    private bool _isFocusingPrompt;
    private bool _isFollowUpInput;
    private bool _isCapturingScreenshot;
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
        AttachmentItems.ItemsSource = _pendingAttachments;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // 截图预览要等初次加载完成再渲染，否则截到的是「加载前」状态。
    private readonly TaskCompletionSource _initialLoadCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitialLoadCompleted => _initialLoadCompletion.Task;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadConfigurationAsync();
        }
        catch (Exception exception)
        {
            // 配置读取失败不该弹模态框挡住界面，记日志即可。
            ErrorLog.Write("加载配置", exception);
        }
        finally
        {
            _initialLoadCompletion.TrySetResult();
        }
    }

    private async Task LoadConfigurationAsync()
    {
        _config = await _configService.LoadAsync();
        RefreshFunctions();

        if (StartupService.IsEnabled() != _config.StartWithWindows)
        {
            StartupService.SetEnabled(_config.StartWithWindows);
        }

        RefreshModelChoices();

        Topmost = _config.KeepWindowOnTop;
    }

    public void ShowFromHotkey(string? selectedText = null)
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        ResetForNewRequest();
        ShowPanelAtCursor();

        if (_config.AutoFillSelectedText && !string.IsNullOrWhiteSpace(selectedText))
        {
            PromptBox.Text = selectedText.Trim();
            PromptBox.CaretIndex = PromptBox.Text.Length;
        }

        FocusPromptEditor();
    }

    // 全局快捷键（双击 Ctrl）触发：面板可能本来是隐藏的，截完必须弹出来，
    // 否则截到的图片既看不见，也没法接着提问。
    public async Task ShowScreenshotFromHotkeyAsync()
    {
        if (!_config.ScreenshotHotkeyEnabled || _isCapturingScreenshot || _isRunning)
        {
            return;
        }

        if (!IsVisible)
        {
            ResetForNewRequest();
        }

        await CaptureRegionAsync(forceShow: true);
    }

    private void ShowPanelAtCursor()
    {
        WindowState = WindowState.Normal;
        Show();
        CursorPlacementService.PlaceWindow(this);
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => CursorPlacementService.PlaceWindow(this));
        Activate();
        Topmost = true;
        Topmost = _config.KeepWindowOnTop;
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

    // 截图预览用（与 SettingsWindow.PreviewTabIndex 同一思路）：让 CI 能截到
    // 选中「联网检索」时的界面，否则来源切换条永远没机会出现在截图里。
    public int PreviewFunctionIndex
    {
        set
        {
            if (_functions.Count == 0)
            {
                return;
            }

            FunctionList.SelectedIndex = Math.Clamp(value, 0, _functions.Count - 1);
            FunctionList.ScrollIntoView(FunctionList.SelectedItem);
            RefreshSourceStrip();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        // PgUp / PgDn 循环切换来源：不用记数字映射，两个大键盲按也能命中。
        // 仅在来源条可见（选中检索类功能）时接管，否则交还给列表滚动。
        if ((e.Key is Key.PageUp or Key.PageDown) &&
            SourceStrip.Visibility == Visibility.Visible)
        {
            CycleSearchBackend(e.Key == Key.PageDown ? 1 : -1);
            e.Handled = true;
            return;
        }

        // Alt+1/2/3 直接切到指定来源。按着 Alt 时 WPF 把按键放在 SystemKey 上、
        // 而 Key 只会是 Key.System，所以这里必须读 SystemKey，否则永远匹配不上。
        if (e.Key == Key.System && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var source = e.SystemKey switch
            {
                Key.D1 or Key.NumPad1 => SearchBackend.TavilyGeneral,
                Key.D2 or Key.NumPad2 => SearchBackend.TavilyNews,
                Key.D3 or Key.NumPad3 => SearchBackend.Zhihu,
                _ => SearchBackend.None
            };

            if (source != SearchBackend.None && TrySetSearchBackend(source))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.V &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            PromptEditorBorder.Visibility == Visibility.Visible)
        {
            if (TryPasteImageFromClipboard())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.A &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            SafeTask.Run(() => CaptureRegionAsync(), "区域截图", message => StatusText.Text = message);
            return;
        }

        if (ResponsePanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Left && !_isRunning)
            {
                ShowWorkflowList();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && !_isRunning)
            {
                if (_isFollowUpInput)
                {
                    CommitModelSelection();
                    _ = ExecuteFollowUpAsync();
                }
                else
                {
                    BeginFollowUpInput();
                }

                e.Handled = true;
                return;
            }
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
                SafeTask.Run(() => ExecuteSelectedAsync(), "执行请求", message => StatusText.Text = message);
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
            FindVisualParent<Button>((DependencyObject)e.OriginalSource) is not null ||
            FindVisualParent<TextBoxBase>((DependencyObject)e.OriginalSource) is not null)
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
        if (string.IsNullOrWhiteSpace(prompt) && _pendingAttachments.Count == 0)
        {
            StatusText.Text = "请输入问题或添加图片";
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

        if (_pendingAttachments.Count > 0 &&
            !_currentProvider.SupportsVisionModel(_currentProvider.SelectedModel))
        {
            StatusText.Text = "当前模型未启用图片能力，请切换模型或到设置中配置";
            return;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = ImageOnlyPrompt;
        }

        var attachments = _pendingAttachments.ToArray();
        ClearPendingAttachments();
        _conversation.Clear();
        _activeFunction = option;
        await ExecuteTurnAsync(prompt, option, _currentProvider, attachments);
    }

    private async Task ExecuteFollowUpAsync()
    {
        if (_isRunning)
        {
            return;
        }

        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) && _pendingAttachments.Count == 0)
        {
            StatusText.Text = "请输入追问或添加图片";
            PromptBox.Focus();
            return;
        }

        if (_activeFunction is null || _currentProvider is null)
        {
            StatusText.Text = "当前对话已结束，请重新选择功能";
            return;
        }

        if (_pendingAttachments.Count > 0 &&
            !_currentProvider.SupportsVisionModel(_currentProvider.SelectedModel))
        {
            StatusText.Text = "当前模型未启用图片能力，请切换模型或到设置中配置";
            return;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = ImageOnlyPrompt;
        }

        var attachments = _pendingAttachments.ToArray();
        ClearPendingAttachments();
        _isFollowUpInput = false;
        await ExecuteTurnAsync(prompt, _activeFunction, _currentProvider, attachments);
    }

    private async Task ExecuteTurnAsync(
        string prompt,
        FunctionOption option,
        ProviderConfig provider,
        IReadOnlyList<ImageAttachment> images)
    {
        _isRunning = true;
        BusyProgress.Visibility = Visibility.Visible;
        ResponsePanel.Visibility = Visibility.Visible;
        FunctionList.Visibility = Visibility.Collapsed;
        SetResponsePromptDisplay(prompt, images.Count);
        ResponseModeText.Text = $"{option.Name} · {provider.Name} / {provider.SelectedModel}";
        ResponseMetaText.Text = "正在准备…";
        SetOutputMarkdown(
            option.SearchBackend switch
            {
                SearchBackend.Zhihu => "正在检索知乎…",
                SearchBackend.None => "正在生成回答…",
                _ => "正在检索网络资料…"
            },
            null);
        StatusText.Text = "正在执行";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SearchPacket? search = null;

        try
        {
            // 检索与否完全由 SearchBackend 决定，不再看 Mode：
            // 「联网检索」把三个来源合并成一项后，Mode 只负责提示词与是否推理。
            if (option.SearchBackend != SearchBackend.None)
            {
                search = await SearchByBackendAsync(option.SearchBackend, prompt);
                ResponseMetaText.Text = $"已检索 {search.Sources.Count} 条资料，正在整理…";
                SetOutputMarkdown("正在结合检索资料生成回答…", null);
            }

            var result = await _aiService.ExecuteAsync(
                option,
                prompt,
                provider,
                ConfigService.Unprotect(provider.ApiKeyProtected),
                search,
                images,
                _conversation.ToArray());

            var displayAnswer = AppendSources(result.Answer, search);
            var reasoning = option.Mode == WorkflowMode.Think ? result.Reasoning : null;
            _lastAnswer = displayAnswer;
            SetOutputMarkdown(displayAnswer, reasoning);
            _conversation.Add(new ConversationTurn("user", prompt, images));
            _conversation.Add(new ConversationTurn("assistant", result.Answer));
            stopwatch.Stop();
            var sourceText = search is null ? string.Empty : $"，{search.Sources.Count} 条来源";
            ResponseMetaText.Text =
                $"{stopwatch.Elapsed.TotalSeconds:F1} 秒{sourceText} · Enter 追问 · ← 返回";
            StatusText.Text = "执行完成";

            await _historyService.AddAsync(new HistoryEntry
            {
                FunctionId = option.Id,
                Mode = option.Mode,
                ModeName = option.Name,
                ProviderName = provider.Name,
                Model = provider.SelectedModel,
                Prompt = prompt,
                Response = displayAnswer,
                Reasoning = result.Reasoning ?? string.Empty,
                ImageCount = images.Count,
                SourceCount = search?.Sources.Count ?? 0
            });
        }
        catch (Exception exception)
        {
            // 不按异常类型过滤：畸形地址（UriFormatException）或返回 HTML（JsonException）
            // 原本会漏出这个 catch，用户看不到任何提示，界面停在「正在生成回答…」。
            var error = exception is TaskCanceledException
                ? "请求超时，请稍后重试。"
                : exception.Message;
            _lastAnswer = error;
            SetOutputMarkdown("## 执行失败\n\n" + error, null);
            ResponseMetaText.Text = "执行失败";
            StatusText.Text = "执行失败";
        }
        finally
        {
            BusyProgress.Visibility = Visibility.Collapsed;
            _isRunning = false;
        }
    }

    private Task<SearchPacket> SearchByBackendAsync(SearchBackend source, string prompt)
    {
        if (source == SearchBackend.Zhihu)
        {
            return _searchService.SearchZhihuAsync(
                prompt,
                ConfigService.Unprotect(_config.ZhihuAccessSecretProtected));
        }

        return _searchService.SearchAsync(
            prompt,
            source == SearchBackend.TavilyNews,
            ConfigService.Unprotect(_config.TavilyApiKeyProtected));
    }

    private static bool IsSearchFunction(FunctionOption function)
    {
        return function.Mode == WorkflowMode.SearchNetwork ||
               function.SearchBackend != SearchBackend.None;
    }

    // 只有选中检索类功能时才显示来源切换条；其余功能没有可切换的来源。
    private void RefreshSourceStrip()
    {
        if (FunctionList.SelectedItem is not FunctionOption option || !IsSearchFunction(option))
        {
            SourceStrip.Visibility = Visibility.Collapsed;
            return;
        }

        SourceStrip.Visibility = Visibility.Visible;
        ApplySourceChip(SourceGeneralChip, option.SearchBackend == SearchBackend.TavilyGeneral);
        ApplySourceChip(SourceNewsChip, option.SearchBackend == SearchBackend.TavilyNews);
        ApplySourceChip(SourceZhihuChip, option.SearchBackend == SearchBackend.Zhihu);
    }

    private void ApplySourceChip(Button chip, bool isActive)
    {
        chip.Background = isActive
            ? (Brush)FindResource("AccentSoftBrush")
            : Brushes.Transparent;
        chip.Foreground = isActive
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("MutedBrush");
        chip.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void SourceChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } &&
            Enum.TryParse<SearchBackend>(tag, out var source))
        {
            TrySetSearchBackend(source);
        }
    }

    // 来源固定顺序循环，PgUp / PgDn 用；顺序与来源条上从左到右一致。
    private static readonly SearchBackend[] SearchBackendCycle =
    [
        SearchBackend.TavilyGeneral,
        SearchBackend.TavilyNews,
        SearchBackend.Zhihu
    ];

    private void CycleSearchBackend(int offset)
    {
        if (FunctionList.SelectedItem is not FunctionOption option || !IsSearchFunction(option))
        {
            return;
        }

        var current = Array.IndexOf(SearchBackendCycle, option.SearchBackend);
        var next = current < 0
            ? 0
            : ((current + offset) % SearchBackendCycle.Length + SearchBackendCycle.Length)
              % SearchBackendCycle.Length;
        TrySetSearchBackend(SearchBackendCycle[next]);
    }

    private bool TrySetSearchBackend(SearchBackend source)
    {
        if (FunctionList.SelectedItem is not FunctionOption option || !IsSearchFunction(option))
        {
            return false;
        }

        if (option.SearchBackend == source)
        {
            return true;
        }

        option.SearchBackend = source;
        RefreshSourceStrip();

        // _functions 是 _config.Functions 的编辑副本，改动必须同步回配置才会持久化。
        _config.Functions = _functions.Select(function => function.Clone()).ToList();
        SaveConfigInBackground();
        return true;
    }

    // 模型切换等操作后要落盘，但保存失败必须让用户看见 —— 否则会以为设置已经生效。
    private void SaveConfigInBackground()
    {
        SafeTask.Run(
            () => _configService.SaveAsync(_config),
            "保存配置",
            message => StatusText.Text = message);
    }

    private void SetOutputMarkdown(string markdown, string? reasoning)
    {
        OutputRichText.Document = MarkdownRenderer.Render(markdown);
        OutputRichText.ScrollToHome();

        if (string.IsNullOrWhiteSpace(reasoning))
        {
            ThinkingRichText.Document = MarkdownRenderer.Render(string.Empty);
            ThinkingExpander.Visibility = Visibility.Collapsed;
            ThinkingExpander.IsExpanded = false;
            return;
        }

        ThinkingRichText.Document = MarkdownRenderer.Render(reasoning);
        ThinkingExpander.Visibility = Visibility.Visible;
        ThinkingExpander.IsExpanded = false;
    }

    private static string AppendSources(string answer, SearchPacket? search)
    {
        if (search is null || search.Sources.Count == 0)
        {
            return answer;
        }

        var builder = new StringBuilder(answer);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("## 来源");
        builder.AppendLine();

        for (var index = 0; index < search.Sources.Count; index++)
        {
            var source = search.Sources[index];
            var title = SanitizeSourceTitle(source.Title);
            var label = string.IsNullOrWhiteSpace(source.Url)
                ? title
                : $"[{title}](<{source.Url}>)";
            builder.Append(index + 1)
                .Append(". ")
                .Append(label);

            if (!string.IsNullOrWhiteSpace(source.PublishedDate))
            {
                builder.Append(" · ").Append(source.PublishedDate);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    // 来源标题直接来自检索结果，可能含换行、方括号、井号。
    // 换行会让标题跑出列表项、井号在行首会变成标题，都必须清掉；
    // 链接地址则用尖括号包裹，否则地址里出现 ) 会提前截断链接。
    private static string SanitizeSourceTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "未命名来源";
        }

        var flattened = title.ReplaceLineEndings(" ").Trim();

        return flattened
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .TrimStart('#')
            .Trim();
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = AttachmentButton
        };

        var screenshotItem = new MenuItem { Header = "区域截图（双击 Ctrl 或 Ctrl+Shift+A）" };
        screenshotItem.Click += async (_, _) => await CaptureRegionAsync();
        var fileItem = new MenuItem { Header = "选择图片" };
        fileItem.Click += (_, _) => ChooseImageFiles();
        var pasteItem = new MenuItem { Header = "从剪贴板粘贴" };
        pasteItem.Click += (_, _) => TryPasteImageFromClipboard();

        menu.Items.Add(screenshotItem);
        menu.Items.Add(fileItem);
        menu.Items.Add(pasteItem);
        menu.IsOpen = true;
    }

    private void ChooseImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = AddImageFilesAsync(dialog.FileNames);
        }
    }

    private bool TryPasteImageFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return false;
            }

            var image = Clipboard.GetImage();
            if (image is null)
            {
                return false;
            }

            var attachment = ImageAttachmentService.FromBitmapSource(
                image,
                $"剪贴板-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                preferPng: true);
            return AddAttachment(attachment);
        }
        catch (Exception)
        {
            StatusText.Text = "无法读取剪贴板中的图片";
            return false;
        }
    }

    private Task CaptureRegionAsync()
    {
        return CaptureRegionAsync(forceShow: false);
    }

    private async Task CaptureRegionAsync(bool forceShow)
    {
        if (_isCapturingScreenshot || _isRunning)
        {
            return;
        }

        _isCapturingScreenshot = true;
        _suppressDeactivateHide = true;
        var wasVisible = IsVisible;
        var shouldShow = forceShow || wasVisible;
        try
        {
            Hide();
            await Task.Delay(140);
            var attachment = ScreenCaptureService.CaptureRegion();
            if (attachment is not null)
            {
                AddAttachment(attachment);
            }
            else
            {
                // 用户取消了截图：原本隐藏的面板保持隐藏，不打扰。
                shouldShow = wasVisible;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"截图失败：{exception.Message}";
        }
        finally
        {
            _isCapturingScreenshot = false;
            _suppressDeactivateHide = false;
            if (shouldShow)
            {
                if (wasVisible)
                {
                    // 面板本来就在：保持原位，不要跳到鼠标处打断输入。
                    Show();
                    Activate();
                }
                else
                {
                    // 全局快捷键唤起：跟随鼠标出现。
                    ShowPanelAtCursor();
                }

                FocusPromptEditor();
            }
        }
    }

    private async Task AddImageFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_pendingAttachments.Count >= ImageAttachmentService.MaximumImageCount)
            {
                StatusText.Text = $"最多添加 {ImageAttachmentService.MaximumImageCount} 张图片";
                break;
            }

            try
            {
                var attachment = await ImageAttachmentService.FromFileAsync(path);
                AddAttachment(attachment, refresh: false);
            }
            catch (Exception)
            {
                StatusText.Text = $"无法添加图片：{Path.GetFileName(path)}";
            }
        }

        RefreshAttachmentPanel();
        FocusPromptEditor();
    }

    private bool AddAttachment(ImageAttachment attachment, bool refresh = true)
    {
        if (_pendingAttachments.Count >= ImageAttachmentService.MaximumImageCount)
        {
            StatusText.Text = $"最多添加 {ImageAttachmentService.MaximumImageCount} 张图片";
            return false;
        }

        _pendingAttachments.Add(attachment);
        if (refresh)
        {
            RefreshAttachmentPanel();
        }

        return true;
    }

    private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ImageAttachment attachment })
        {
            return;
        }

        _pendingAttachments.Remove(attachment);
        RefreshAttachmentPanel();
    }

    private void RefreshAttachmentPanel()
    {
        AttachmentItems.ItemsSource = null;
        AttachmentItems.ItemsSource = _pendingAttachments;
        AttachmentPanel.Visibility = _pendingAttachments.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateAttachmentTargetText();
    }

    private void ClearPendingAttachments()
    {
        if (_pendingAttachments.Count == 0)
        {
            AttachmentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _pendingAttachments.Clear();
        RefreshAttachmentPanel();
    }

    private void UpdateAttachmentTargetText()
    {
        if (_pendingAttachments.Count == 0 ||
            _currentProvider is null ||
            string.IsNullOrWhiteSpace(_currentProvider.SelectedModel))
        {
            return;
        }

        if (_currentProvider.SupportsVisionModel(_currentProvider.SelectedModel))
        {
            AttachmentTargetText.Foreground = new SolidColorBrush(Color.FromRgb(116, 117, 122));
            AttachmentTargetText.Text =
                $"将发送到：{_currentProvider.Name} · {_currentProvider.SelectedModel}";
            return;
        }

        AttachmentTargetText.Foreground = new SolidColorBrush(Color.FromRgb(190, 96, 42));
        AttachmentTargetText.Text = "当前模型未启用图片能力，请切换模型或到设置中配置";
    }

    private void Input_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Input_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        _ = AddImageFilesAsync(files);
        e.Handled = true;
    }

    private void FunctionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FunctionList.SelectedItem is FunctionOption option)
        {
            StatusText.Text = $"已选择：{option.Name}，Enter 执行";
        }

        RefreshSourceStrip();
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingModels || ModelComboBox.SelectedItem is not ModelChoice choice)
        {
            return;
        }

        ApplyModelChoice(choice);
    }

    private void ModelComboBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitModelSelection();
    }

    private void CommitModelSelection()
    {
        var typedValue = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(typedValue))
        {
            return;
        }

        var selectedChoice = _modelChoices.FirstOrDefault(choice =>
            choice.DisplayName.Equals(typedValue, StringComparison.OrdinalIgnoreCase) ||
            choice.Model.Equals(typedValue, StringComparison.OrdinalIgnoreCase));
        if (selectedChoice is not null)
        {
            ModelComboBox.SelectedItem = selectedChoice;
            ApplyModelChoice(selectedChoice);
            return;
        }

        var provider = _currentProvider ?? _config.Providers.FirstOrDefault();
        if (provider is null)
        {
            return;
        }

        if (!provider.Models.Contains(typedValue, StringComparer.OrdinalIgnoreCase))
        {
            provider.Models.Add(typedValue);
        }

        _currentProvider = provider;
        provider.SelectedModel = typedValue;
        _config.SelectedProviderId = provider.Id;
        RefreshModelChoices();
        SaveConfigInBackground();
    }

    private void RefreshModelChoices()
    {
        _isRefreshingModels = true;
        try
        {
            _modelChoices = _config.Providers
                .SelectMany(provider => provider.Models
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => new ModelChoice(provider, model)))
                .ToList();

            ModelComboBox.ItemsSource = null;
            ModelComboBox.ItemsSource = _modelChoices;

            var preferred = _modelChoices.FirstOrDefault(choice =>
                                choice.Provider.Id == _config.SelectedProviderId &&
                                choice.Model.Equals(
                                    choice.Provider.SelectedModel,
                                    StringComparison.OrdinalIgnoreCase))
                            ?? _modelChoices.FirstOrDefault();

            _currentProvider = preferred?.Provider ?? _config.Providers.FirstOrDefault();
            ModelComboBox.SelectedItem = preferred;
            ModelComboBox.Text = preferred?.DisplayName ?? string.Empty;
            ModelComboBox.ToolTip = preferred?.DisplayName ?? "请先配置模型";
            UpdateAttachmentTargetText();
        }
        finally
        {
            _isRefreshingModels = false;
        }
    }

    private void ApplyModelChoice(ModelChoice choice)
    {
        _currentProvider = choice.Provider;
        choice.Provider.SelectedModel = choice.Model;
        _config.SelectedProviderId = choice.Provider.Id;
        UpdateAttachmentTargetText();
        SaveConfigInBackground();
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
            // 传副本：设置窗口直接写传入对象，传本体的话点「取消」也会污染正在使用的配置。
            // 保存成功后由 LoadConfigurationAsync 从磁盘重新读回。
            var settings = new SettingsWindow(_configService, _config.Clone(), _aiService)
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
                                        function => function.Id == entry.FunctionId)
                                    ?? _functions.FirstOrDefault(
                                        function => function.Mode == entry.Mode)
                                    ?? _functions.FirstOrDefault();
                ShowWorkflowList();
                PromptBox.Focus();
            }
        }
        finally
        {
            _suppressDeactivateHide = false;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkflowList();
    }

    private void ShowWorkflowList()
    {
        if (_isRunning)
        {
            return;
        }

        _conversation.Clear();
        _activeFunction = null;
        _isFollowUpInput = false;
        PromptInfoText.Visibility = Visibility.Collapsed;
        PromptInfoText.Text = string.Empty;
        PromptEditorBorder.Visibility = Visibility.Visible;
        PromptPlaceholder.Text = "输入问题…";
        PromptRowDefinition.Height = GridLength.Auto;
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        ClearPendingAttachments();
        StatusText.Text = "↑ ↓ 选择功能，Enter 执行";
        FocusPromptEditor();
    }

    private void ResetForNewRequest()
    {
        _lastAnswer = string.Empty;
        _conversation.Clear();
        _activeFunction = null;
        _isFollowUpInput = false;
        PromptBox.Clear();
        PromptInfoText.Text = string.Empty;
        PromptInfoText.Visibility = Visibility.Collapsed;
        PromptEditorBorder.Visibility = Visibility.Visible;
        PromptPlaceholder.Text = "输入问题…";
        PromptRowDefinition.Height = GridLength.Auto;
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        SelectFirstFunction();
        RefreshSourceStrip();
        ClearPendingAttachments();
        StatusText.Text = "↑ ↓ 选择功能，Enter 执行";
    }

    // 每次唤起都把功能列表重置回第一项，避免沿用上次用过的功能被 Enter 误执行。
    private void SelectFirstFunction()
    {
        if (FunctionList.Items.Count == 0)
        {
            return;
        }

        FunctionList.SelectedIndex = 0;
        FunctionList.ScrollIntoView(FunctionList.SelectedItem);
    }

    private void RefreshFunctions()
    {
        var selectedId = (FunctionList.SelectedItem as FunctionOption)?.Id;
        _functions = _config.Functions.Count == 0
            ? FunctionCatalog.CreateDefaultFunctions()
            : _config.Functions.Select(function => function.Clone()).ToList();

        FunctionList.ItemsSource = null;
        FunctionList.ItemsSource = _functions;
        FunctionList.SelectedItem = _functions.FirstOrDefault(
                                        function => function.Id == selectedId)
                                    ?? _functions.FirstOrDefault();
        RefreshSourceStrip();
    }

    private void SetResponsePromptDisplay(string prompt, int imageCount)
    {
        _isFollowUpInput = false;
        PromptInfoText.Text = imageCount == 0
            ? prompt
            : $"{prompt} · {imageCount} 张图片";
        PromptInfoText.Visibility = Visibility.Visible;
        PromptEditorBorder.Visibility = Visibility.Collapsed;
        AttachmentPanel.Visibility = Visibility.Collapsed;
        PromptRowDefinition.Height = GridLength.Auto;
    }

    private void BeginFollowUpInput()
    {
        if (_isRunning || ResponsePanel.Visibility != Visibility.Visible)
        {
            return;
        }

        _isFollowUpInput = true;
        PromptBox.Clear();
        ClearPendingAttachments();
        PromptPlaceholder.Text = "继续提问…";
        PromptInfoText.Visibility = Visibility.Collapsed;
        PromptEditorBorder.Visibility = Visibility.Visible;
        PromptRowDefinition.Height = GridLength.Auto;
        ResponseMetaText.Text = "输入追问后按 Enter 发送 · ← 返回";
        FocusPromptEditor();
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

    // 1A：整行单击直接执行。此前行尾的 ▶ 只是装饰 TextBlock，点行只选中、不执行，
    // 视觉承诺和实际行为对不上；现在整行就是执行入口，▶ 改为悬停时淡入提示。
    private void FunctionItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not FunctionOption)
        {
            return;
        }

        e.Handled = true;
        FunctionList.SelectedItem = item.DataContext;
        SafeTask.Run(() => ExecuteSelectedAsync(), "执行请求", message => StatusText.Text = message);
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
