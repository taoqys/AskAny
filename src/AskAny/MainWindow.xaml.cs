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
    private readonly ModelCatalogService _modelCatalogService;
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
    private bool _isFocusingPrompt;
    private bool _isFollowUpInput;
    private bool _isCapturingScreenshot;
    private string _lastAnswer = string.Empty;

    // 流式渲染：读流在线程池上跑，只往缓冲区追加；界面按固定间隔统一重排。
    // 一栏一个 StreamTarget —— 单模型只用 _primaryStream，对比模式再启用 _compareStream。
    private readonly object _streamGate = new();
    private readonly StreamTarget _primaryStream = new();
    private readonly StreamTarget _compareStream = new();
    private DispatcherTimer _streamRenderTimer = null!;

    // 双模型对比：右侧那一栏的模型与本次耗时。
    private ModelChoice? _compareChoice;
    private bool _compareMode;
    private bool _applyingCompareMode;

    // 思考开关：与功能项解耦，跨功能共享，状态存配置。
    private bool _thinkingEnabled;

    // 程序化改动下拉框选中项时，抑制 SelectionChanged 里的保存逻辑。
    private bool _suppressModelSelection;

    private sealed class StreamTarget
    {
        public StringBuilder Content { get; } = new();

        public StringBuilder Reasoning { get; } = new();

        public bool Dirty { get; set; }
    }

    // 当前进行中请求的取消源：Esc 用它中断。
    private CancellationTokenSource? _requestCancellation;

    // 用户取消与「请求超时」都是 OperationCanceledException，必须区分。
    // 注意：内层 finally 会在异常继续上抛之前把 _requestCancellation 置空，
    // 所以不能在 catch 过滤器里读它，只能提前把结论记下来。
    private bool _lastRequestCancelledByUser;

    // 多轮上下文上限：约 6 轮问答。字数预算按模型真实上下文长度动态计算（见下）。
    private const int MaxConversationTurns = 12;
    private const int DefaultConversationCharacters = 24_000;
    private const int MinimumConversationCharacters = 4_000;
    private const int MaximumConversationCharacters = 120_000;

    public MainWindow(
        ConfigService configService,
        AiService aiService,
        SearchService searchService,
        HistoryService historyService,
        ModelCatalogService modelCatalogService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
        _searchService = searchService;
        _historyService = historyService;
        _modelCatalogService = modelCatalogService;

        FunctionList.ItemsSource = _functions;
        FunctionList.SelectedIndex = 0;
        AttachmentItems.ItemsSource = _pendingAttachments;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        _streamRenderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _streamRenderTimer.Tick += (_, _) => RenderStreamingDraft();
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

        // 全局手势的按键映射来自配置，配置一变就让 App 重新套用（不必重启）。
        if (Application.Current is App app)
        {
            app.ApplyHotkeyConfig(_config);
        }

        _thinkingEnabled = _config.ThinkingEnabled;
        RefreshThinkingChips();
        UpdateShortcutHint();
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
            // 请求进行中按 Esc = 取消请求（而不是把窗口藏起来、让请求继续跑并继续计费）。
            // 空闲时仍然是隐藏窗口，保持原有习惯。
            if (_isRunning && _requestCancellation is { } cancellation)
            {
                StatusText.Text = "正在取消…";
                cancellation.Cancel();
            }
            else
            {
                Hide();
            }

            e.Handled = true;
            return;
        }

        // Ctrl+D 切换双模型对比（左右分屏）。
        if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ApplyCompareMode(!_compareMode, save: true);
            e.Handled = true;
            return;
        }

        // Ctrl+N 开始新话题：清空上下文，但仍然是「即用即走」，不做会话列表。
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StartNewTopic();
            e.Handled = true;
            return;
        }

        // 键盘切换模型：Tab 切左边的（主）模型，` 切右边的（对比）模型。
        // Tab 在输入框里本来只用于移动焦点，这里占用它换取「不用鼠标就能换模型」；
        // 只在没有修饰键时接管，Ctrl+Tab 之类仍然交还给系统。
        // Tab / ` 切换模型；按住 Shift 反向。四个键位覆盖「左/右 × 正向/反向」。
        if (e.Key is Key.Tab or Key.OemTilde or Key.Oem3 &&
            Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            CycleModelChoice(
                rightSide: e.Key is Key.OemTilde or Key.Oem3,
                backwards: Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
            return;
        }

        // PgUp / PgDn 切换当前功能对应的那组模式：
        //   「联网检索」→ 切换检索来源（这是它的主要模式轴）
        //   其它功能   → 切换思考 OFF/ON
        // 这样两组都用同一个键位，不会因为功能不同而要记两套键。
        if (e.Key is Key.PageUp or Key.PageDown)
        {
            if (SourceStrip.Visibility == Visibility.Visible)
            {
                CycleSearchBackend(e.Key == Key.PageDown ? 1 : -1);
            }
            else
            {
                CycleThinking();
            }

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
        _lastRequestCancelledByUser = false;
        BusyProgress.Visibility = Visibility.Visible;
        ResponsePanel.Visibility = Visibility.Visible;
        UpdateModeStripVisibility();
        FunctionList.Visibility = Visibility.Collapsed;
        SetResponsePromptDisplay(prompt, images.Count);
        ResponseModeText.Text = _compareMode
            ? $"{option.Name} · 双模型对比"
            : $"{option.Name} · {provider.Name} / {provider.SelectedModel}";
        ResponseMetaText.Text = "正在准备…";
        if (_compareMode)
        {
            PrimaryModelText.Text = $"{provider.Name} · {provider.SelectedModel} · 生成中…";
            CompareModelText.Text = _compareChoice is null
                ? "未选择"
                : $"{_compareChoice.Provider.Name} · {_compareChoice.Model} · 等待中…";
            ResetStream(_compareStream);
            SetCompareMarkdown("正在生成回答…", null);
        }

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
        var searchElapsed = TimeSpan.Zero;

        try
        {
            // 检索与否完全由 SearchBackend 决定，不再看 Mode：
            // 「联网检索」把三个来源合并成一项后，Mode 只负责提示词与是否推理。
            if (option.SearchBackend != SearchBackend.None)
            {
                search = await SearchByBackendAsync(option.SearchBackend, prompt);
                searchElapsed = stopwatch.Elapsed;
                ResponseMetaText.Text = $"已检索 {search.Sources.Count} 条资料，正在整理…";
                SetOutputMarkdown("正在结合检索资料生成回答…", null);
            }

            // 每栏各自计时：从该栏请求真正发出开始，到它自己完成结束。
            var outcome = _compareMode && _compareChoice is not null
                ? await ExecuteCompareAsync(option, prompt, provider, search, images)
                : await ExecuteWithStreamingAsync(option, prompt, provider, search, images);
            var result = outcome.Result;

            var displayAnswer = AppendSources(result.Answer, search);
            var reasoning = string.IsNullOrWhiteSpace(result.Reasoning) ? null : result.Reasoning;
            _lastAnswer = displayAnswer;
            SetOutputMarkdown(displayAnswer, reasoning);
            // 不把图片放进会话：历史轮次不再重发图片（见 AiService 的说明），
            // 存着只会白占内存。
            _conversation.Add(new ConversationTurn("user", prompt));
            _conversation.Add(new ConversationTurn("assistant", result.Answer));
            TrimConversation();
            stopwatch.Stop();

            if (_compareMode)
            {
                PrimaryModelText.Text =
                    $"{provider.Name} · {provider.SelectedModel} · {FormatMetrics(outcome)}";
            }
            else
            {
                var searchText = search is null
                    ? string.Empty
                    : $" · 检索 {searchElapsed.TotalSeconds:F1}s";
                ResponseMetaText.Text = $"{FormatMetrics(outcome)}{searchText} · Enter 追问";
            }

            StatusText.Text = "执行完成";

            // 首轮提问时才确定话题 id 与标题，避免空话题也占一个 id。
            if (string.IsNullOrWhiteSpace(_topicId))
            {
                _topicId = Guid.NewGuid().ToString("N");
                _topicTitle = BuildTopicTitle(prompt);
            }

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
                SourceCount = search?.Sources.Count ?? 0,
                TopicId = _topicId,
                TopicTitle = _topicTitle,
                PromptTokens = result.Usage?.PromptTokens ?? 0,
                CompletionTokens = result.Usage?.CompletionTokens ?? 0,
                TimeToFirstTokenMs = (int)(result.TimeToFirstToken?.TotalMilliseconds ?? 0)
            });
        }
        catch (OperationCanceledException) when (_lastRequestCancelledByUser)
        {
            _lastAnswer = "已取消";
            SetOutputMarkdown("## 已取消\n\n请求已取消。", null);
            ResponseMetaText.Text = "已取消";
            StatusText.Text = "已取消";
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
        var isSearch = FunctionList.SelectedItem is FunctionOption selectedFunction &&
                       IsSearchFunction(selectedFunction);

        SourceStrip.Visibility = isSearch ? Visibility.Visible : Visibility.Collapsed;

        if (isSearch)
        {
            var current = (FunctionOption)FunctionList.SelectedItem;
            ApplySourceChip(SourceGeneralChip, current.SearchBackend == SearchBackend.TavilyGeneral);
            ApplySourceChip(SourceNewsChip, current.SearchBackend == SearchBackend.TavilyNews);
            ApplySourceChip(SourceZhihuChip, current.SearchBackend == SearchBackend.Zhihu);
        }

        RefreshThinkingChips();
    }

    // 思考开关：OFF/ON 两个 chip，状态存在配置里，跨功能共享。
    private void RefreshThinkingChips()
    {
        ApplySourceChip(ThinkingOffChip, !_thinkingEnabled);
        ApplySourceChip(ThinkingOnChip, _thinkingEnabled);

        var capability = ModelCapabilities.Infer(_currentProvider?.SelectedModel ?? string.Empty);
        var forcedLow = !_thinkingEnabled && capability.CanDisableReasoning == false;

        ThinkingOffChip.ToolTip = forcedLow
            ? "关闭思考：当前模型始终思考，会自动退到最小强度 low"
            : "关闭思考：发送 effort=none，跳过推理（PgUp/PgDn）";

        // PgUp/PgDn 切哪一个，取决于当前功能暴露了哪一组模式。
        var isSearch = FunctionList.SelectedItem is FunctionOption option && IsSearchFunction(option);
        ModeHintText.Text = isSearch
            ? "PgUp/PgDn 切来源 · Alt+1/2/3 直达"
            : "PgUp/PgDn 切思考";

        // 打开思考但模型不支持推理时，提前说清楚。
        if (_thinkingEnabled && !capability.SupportsReasoning &&
            !string.IsNullOrWhiteSpace(_currentProvider?.SelectedModel))
        {
            ModeHintText.Text = "当前模型可能不支持推理（仍会按 ON 发送强度）";
        }
    }

    private void ThinkingChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && bool.TryParse(tag, out var enabled))
        {
            SetThinkingEnabled(enabled);
        }
    }

    private void SetThinkingEnabled(bool enabled)
    {
        if (_thinkingEnabled == enabled)
        {
            return;
        }

        _thinkingEnabled = enabled;
        _config.ThinkingEnabled = enabled;
        RefreshThinkingChips();
        SaveConfigInBackground();
        StatusText.Text = enabled ? "思考已开启" : "思考已关闭";
    }

    private void CycleThinking()
    {
        SetThinkingEnabled(!_thinkingEnabled);
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

    // 流式：读流放在线程池上，分片只往缓冲区里追加；渲染交给定时器节流。
    // 每个分片都重排一次 Markdown 会把界面拖垮，所以按固定间隔统一渲染。
    //
    // 单模型和双模型对比共用这一套：一个「栏」就是一个 StreamTarget，
    // 两条流各写各的缓冲区，渲染时一次读回两份。
    private async Task<PaneOutcome> ExecuteWithStreamingAsync(
        FunctionOption option,
        string prompt,
        ProviderConfig provider,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment> images)
    {
        var apiKey = ConfigService.Unprotect(provider.ApiKeyProtected);
        var conversation = _conversation.ToArray();

        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        ResetStream(_primaryStream);
        StartStreamTimer();

        try
        {
            return await RunPaneAsync(
                _primaryStream, option, provider, apiKey, prompt, search, images, conversation,
                cancellation.Token);
        }
        finally
        {
            StopStreamTimer();
            _lastRequestCancelledByUser = cancellation.IsCancellationRequested;
            _requestCancellation = null;
        }
    }

    // 双模型对比：同一个问题、同一份检索资料，同时问两个模型，各写自己那一栏。
    // 检索只做一次（两个模型看到同样的资料才可比），右侧答案只展示、不写历史。
    private async Task<PaneOutcome> ExecuteCompareAsync(
        FunctionOption option,
        string prompt,
        ProviderConfig provider,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment> images)
    {
        var compareChoice = _compareChoice!;
        var apiKey = ConfigService.Unprotect(provider.ApiKeyProtected);
        var compareKey = ConfigService.Unprotect(compareChoice.Provider.ApiKeyProtected);
        var conversation = _conversation.ToArray();

        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        ResetStream(_primaryStream);
        ResetStream(_compareStream);
        CompareBusyProgress.Visibility = Visibility.Visible;
        StartStreamTimer();

        try
        {
            var primaryTask = RunPaneAsync(
                _primaryStream, option, provider, apiKey, prompt, search, images, conversation,
                cancellation.Token);
            var compareTask = RunPaneAsync(
                _compareStream, option, compareChoice.Provider, compareKey, prompt, search, images,
                conversation, cancellation.Token);

            // 两边都要等完：一侧失败不该让另一侧的结果消失。
            PaneOutcome? primary = null;
            string? primaryError = null;
            PaneOutcome? compare = null;
            string? compareError = null;
            OperationCanceledException? cancellationException = null;

            try
            {
                primary = await primaryTask;
            }
            catch (OperationCanceledException exception)
            {
                cancellationException = exception;
            }
            catch (Exception exception)
            {
                primaryError = exception.Message;
            }

            try
            {
                compare = await compareTask;
            }
            catch (OperationCanceledException exception)
            {
                cancellationException ??= exception;
            }
            catch (Exception exception)
            {
                compareError = exception.Message;
            }

            // 右栏用「它自己的」耗时与用量，不能拿两栏的总时间充数。
            if (compare is not null)
            {
                SetCompareMarkdown(AppendSources(compare.Result.Answer, search), null);
                CompareModelText.Text =
                    $"{compareChoice.Provider.Name} · {compareChoice.Model} · {FormatMetrics(compare)}";
            }
            else if (compareError is not null)
            {
                SetCompareMarkdown("## 对比失败\n\n" + compareError, null);
                CompareModelText.Text = $"{compareChoice.Provider.Name} · {compareChoice.Model} · 失败";
            }
            else
            {
                SetCompareMarkdown("## 已取消\n\n请求已取消。", null);
                CompareModelText.Text = $"{compareChoice.Provider.Name} · {compareChoice.Model} · 已取消";
            }

            if (cancellationException is not null)
            {
                throw cancellationException;
            }

            if (primaryError is not null)
            {
                throw new InvalidOperationException(primaryError);
            }

            return primary!;
        }
        finally
        {
            StopStreamTimer();
            CompareBusyProgress.Visibility = Visibility.Collapsed;
            _lastRequestCancelledByUser = cancellation.IsCancellationRequested;
            _requestCancellation = null;
        }
    }

    // 一栏的结果 + 它自己的耗时（从该栏请求发出到该栏完成）。
    private sealed record PaneOutcome(AiResult Result, TimeSpan Elapsed);

    // 回答页每栏那一行指标：耗时 · 首字 · 输入→输出 token。
    private static string FormatMetrics(PaneOutcome outcome)
    {
        var parts = new List<string> { $"{outcome.Elapsed.TotalSeconds:F1}s" };

        if (outcome.Result.TimeToFirstToken is { } firstToken)
        {
            parts.Add($"首字 {firstToken.TotalSeconds:F1}s");
        }

        parts.Add(outcome.Result.Usage is { HasAny: true } usage
            ? $"{usage.PromptTokens}→{usage.CompletionTokens} tok"
            : "tok —");

        return string.Join(" · ", parts);
    }

    // 跑一个回答栏：先流式，失败退回非流式。取消原样上抛，由调用方区分「取消/超时」。
    private async Task<PaneOutcome> RunPaneAsync(
        StreamTarget target,
        FunctionOption option,
        ProviderConfig provider,
        string apiKey,
        string prompt,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment> images,
        IReadOnlyList<ConversationTurn> conversation,
        CancellationToken cancellationToken)
    {
        // 每栏独立秒表：这是「两个回答计时分开」的唯一正确做法。
        var paneStopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await Task.Run(() => _aiService.ExecuteStreamingAsync(
                option,
                prompt,
                provider,
                apiKey,
                search,
                images,
                conversation,
                (content, reasoning) => AppendDelta(target, content, reasoning),
                _thinkingEnabled,
                cancellationToken));

            paneStopwatch.Stop();
            return new PaneOutcome(result, paneStopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 不同提供商/代理对 SSE 的支持程度不一致，流式失败必须退回非流式，
            // 否则「支持流式」本身会变成新的失败点。
            ErrorLog.Write("流式请求失败，回退到非流式", exception);
            ResetStream(target);

            var result = await _aiService.ExecuteAsync(
                option, prompt, provider, apiKey, search, images, conversation,
                _thinkingEnabled, cancellationToken);

            paneStopwatch.Stop();
            return new PaneOutcome(result, paneStopwatch.Elapsed);
        }
    }

    // 在线程池线程上被调用，只做追加，不碰任何界面对象。
    private void AppendDelta(StreamTarget target, string content, string reasoning)
    {
        lock (_streamGate)
        {
            if (content.Length > 0)
            {
                target.Content.Append(content);
            }

            if (reasoning.Length > 0)
            {
                target.Reasoning.Append(reasoning);
            }

            target.Dirty = true;
        }
    }

    private void ResetStream(StreamTarget target)
    {
        lock (_streamGate)
        {
            target.Content.Clear();
            target.Reasoning.Clear();
            target.Dirty = false;
        }
    }

    private void StartStreamTimer()
    {
        _streamRenderTimer.Start();
    }

    private void StopStreamTimer()
    {
        _streamRenderTimer.Stop();
    }

    private void RenderStreamingDraft()
    {
        string? primaryContent = null;
        string? primaryReasoning = null;
        string? compareContent = null;
        string? compareReasoning = null;

        lock (_streamGate)
        {
            if (_primaryStream.Dirty)
            {
                _primaryStream.Dirty = false;
                primaryContent = _primaryStream.Content.ToString();
                primaryReasoning = _primaryStream.Reasoning.ToString();
            }

            if (_compareStream.Dirty)
            {
                _compareStream.Dirty = false;
                compareContent = _compareStream.Content.ToString();
                compareReasoning = _compareStream.Reasoning.ToString();
            }
        }

        if (primaryContent is not null)
        {
            if (_compareMode)
            {
                PrimaryModelText.Text = _currentProvider is null
                    ? $"左栏 · 生成中 {primaryContent.Length} 字"
                    : $"{_currentProvider.Name} · {_currentProvider.SelectedModel} · 生成中 {primaryContent.Length} 字";
            }
            else
            {
                ResponseMetaText.Text = $"正在生成… {primaryContent.Length} 字";
            }

            SetOutputMarkdown(
                primaryContent.Length == 0 ? "正在生成回答…" : primaryContent,
                string.IsNullOrEmpty(primaryReasoning) ? null : primaryReasoning);
            // 生成过程中让视线跟着最新内容走；完成后 SetOutputMarkdown 会回到开头。
            OutputRichText.ScrollToEnd();
        }

        if (compareContent is not null)
        {
            CompareModelText.Text = _compareChoice is null
                ? $"右栏 · 生成中 {compareContent.Length} 字"
                : $"{_compareChoice.Provider.Name} · {_compareChoice.Model} · 生成中 {compareContent.Length} 字";
            SetCompareMarkdown(
                compareContent.Length == 0
                    ? (string.IsNullOrEmpty(compareReasoning) ? "正在生成回答…" : "正在思考…")
                    : compareContent,
                null);
            CompareRichText.ScrollToEnd();
        }
    }

    private void SetCompareMarkdown(string markdown, string? reasoning)
    {
        CompareRichText.Document = MarkdownRenderer.Render(markdown);
        CompareRichText.ScrollToHome();
    }
    // 多轮上下文不能无限增长：token 线性上涨，迟早撞上下文上限或超时。
    // 按轮数与总字数双重限制，并且保证裁剪后第一条是 user ——
    // 从中间截断会让某轮 assistant 没有对应的提问，部分提供商会直接拒绝这种序列。
    private void TrimConversation()
    {
        while (_conversation.Count > MaxConversationTurns)
        {
            _conversation.RemoveAt(0);
        }

        var budget = CurrentConversationCharacterBudget();
        while (_conversation.Count > 2 &&
               _conversation.Sum(turn => turn.Content.Length) > budget)
        {
            _conversation.RemoveAt(0);
        }

        while (_conversation.Count > 0 && _conversation[0].Role != "user")
        {
            _conversation.RemoveAt(0);
        }
    }

    // 按当前模型的真实上下文长度决定历史预算：小上下文模型别撑爆，大上下文模型别白白浪费。
    // 字符数按「1 字符 ≈ 1 token」保守估算（中文实际略低于 1），只把 40% 上下文留给历史。
    private int CurrentConversationCharacterBudget()
    {
        var capability = ModelCapabilities.Infer(_currentProvider?.SelectedModel ?? string.Empty);

        if (capability.ContextTokens is not { } tokens || tokens <= 0)
        {
            return DefaultConversationCharacters;
        }

        return Math.Clamp((int)(tokens * 0.4), MinimumConversationCharacters, MaximumConversationCharacters);
    }

    // 话题：一次连续追问算一个话题。不做成会话列表 —— AskAny 的核心仍是
    // 「双击即用」的一次性提问，话题只用来把多轮上下文与历史串起来。
    private string _topicId = string.Empty;
    private string _topicTitle = string.Empty;

    // Ctrl+N：开始新话题（清空上下文），回到功能列表。
    private void StartNewTopic()
    {
        _conversation.Clear();
        _topicId = string.Empty;
        _topicTitle = string.Empty;

        ResetForNewRequest();
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        UpdateModeStripVisibility();
        FunctionList.SelectedIndex = 0;

        StatusText.Text = "已开始新话题";
    }

    // 历史里「继续该话题」：把该话题的历史问答按时间顺序还原成对话上下文，
    // 这样追问时模型能看到之前的内容，而不是从零开始。
    private async Task RestoreTopicContextAsync(string topicId, string prompt)
    {
        if (string.IsNullOrWhiteSpace(topicId))
        {
            return;
        }

        var entries = await _historyService.LoadAsync();
        var topicEntries = entries
            .Where(entry => entry.TopicId.Equals(topicId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Timestamp)
            .ToList();

        if (topicEntries.Count == 0)
        {
            return;
        }

        _conversation.Clear();
        var restored = 0;
        foreach (var entry in topicEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Prompt))
            {
                continue;
            }

            _conversation.Add(new ConversationTurn("user", entry.Prompt));
            restored++;

            if (!string.IsNullOrWhiteSpace(entry.Response))
            {
                _conversation.Add(new ConversationTurn("assistant", entry.Response));
            }
        }

        TrimConversation();

        _topicId = topicId;
        _topicTitle = topicEntries[0].TopicTitle;
        StatusText.Text = $"已恢复话题上下文（{restored} 轮），可直接追问";
    }

    // 话题标题取首条提问的第一行，压到 24 个字以内，够在历史里认出是哪段对话。
    private static string BuildTopicTitle(string prompt)
    {
        var firstLine = prompt.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "新话题";

        return firstLine.Length <= 24 ? firstLine : firstLine[..24] + "…";
    }

    private void SetOutputMarkdown(string markdown, string? reasoning)
    {
        OutputRichText.Document = MarkdownRenderer.Render(markdown);
        OutputRichText.ScrollToHome();

        // 对比模式下左右两栏都是完整回答，跨栏的「思考过程」只会让人搞不清它属于哪个模型。
        if (string.IsNullOrWhiteSpace(reasoning) || _compareMode)
        {
            ThinkingRichText.Document = MarkdownRenderer.Render(string.Empty);
            ReasoningChip.Visibility = Visibility.Collapsed;
            ReasoningBox.Visibility = Visibility.Collapsed;
            return;
        }

        // 推理过程只占一行：想看的点开，不想看的完全不占高度。
        ReasoningChip.Content = $"已思考 · {reasoning.Length} 字";
        ReasoningChip.Visibility = Visibility.Visible;

        // 用户已经展开时不要因为流式刷新把它收起来。
        if (ReasoningBox.Visibility != Visibility.Visible)
        {
            ReasoningBox.Visibility = Visibility.Collapsed;
        }

        ThinkingRichText.Document = MarkdownRenderer.Render(reasoning);
    }

    private void ReasoningChip_Click(object sender, RoutedEventArgs e)
    {
        ReasoningBox.Visibility = ReasoningBox.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // 模式条（思考 OFF/ON、检索来源、对比开关）只属于「选功能」这一步：
    // 看回答时把它收起来，把高度让给正文。Ctrl+D 在回答页仍然可用。
    private void UpdateModeStripVisibility()
    {
        ModeStrip.Visibility = ResponsePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
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

            // 思考现在是独立开关：只提示「开关与当前模型能力不匹配」的情况。
            if (_currentProvider is { } provider && !string.IsNullOrWhiteSpace(provider.SelectedModel))
            {
                var capability = ModelCapabilities.Infer(provider.SelectedModel);

                if (_thinkingEnabled && !capability.SupportsReasoning)
                {
                    StatusText.Text = $"已选择：{option.Name}（当前模型可能不支持推理）";
                }
                else if (!_thinkingEnabled && capability.CanDisableReasoning == false)
                {
                    // 这类模型关不掉思考：会退到最小强度 low，耗时仍会长一些。
                    StatusText.Text = $"已选择：{option.Name}（该模型始终思考，将按最低强度发送）";
                }
            }
        }

        RefreshSourceStrip();
    }

    // 模型选择框：单栏时只有左边一个；对比模式下并排两个（左 = 主模型，右 = 对比模型）。
    private void LeftModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || LeftModelComboBox.SelectedItem is not ModelChoice choice)
        {
            return;
        }

        ApplyModelChoice(choice);
    }

    private void RightModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || RightModelComboBox.SelectedItem is not ModelChoice choice)
        {
            return;
        }

        _compareChoice = choice;
        _config.CompareProviderId = choice.Provider.Id;
        _config.CompareModel = choice.Model;
        RightModelComboBox.ToolTip = BuildModelToolTip(choice);
        SaveConfigInBackground();
    }

    private void RefreshModelChoices()
    {
        _suppressModelSelection = true;
        try
        {
            // 只列「已启用」的模型：设置里没勾选的模型不出现在主界面。
            _modelChoices = _config.Providers
                .SelectMany(provider => provider.EffectiveModels()
                    .Select(model => new ModelChoice(provider, model)))
                .ToList();

            var preferred = _modelChoices.FirstOrDefault(choice =>
                                choice.Provider.Id == _config.SelectedProviderId &&
                                choice.Model.Equals(
                                    choice.Provider.SelectedModel,
                                    StringComparison.OrdinalIgnoreCase))
                            ?? _modelChoices.FirstOrDefault();

            _currentProvider = preferred?.Provider ?? _config.Providers.FirstOrDefault();

            LeftModelComboBox.ItemsSource = null;
            LeftModelComboBox.ItemsSource = _modelChoices;
            LeftModelComboBox.SelectedItem = preferred;
            LeftModelComboBox.ToolTip = preferred is null
                ? "请先在设置里配置模型"
                : BuildModelToolTip(preferred);

            RefreshCompareOptions();
            UpdateAttachmentTargetText();
        }
        finally
        {
            _suppressModelSelection = false;
        }

        // 配置里的对比模式要在启动/设置回来后恢复。
        ApplyCompareMode(_config.CompareMode, save: false);
    }

    // 右栏候选 = 已启用且不等于左栏当前模型。
    private void RefreshCompareOptions()
    {
        var wasSuppressed = _suppressModelSelection;
        _suppressModelSelection = true;
        try
        {
            var candidates = CompareCandidates();

            RightModelComboBox.ItemsSource = null;
            RightModelComboBox.ItemsSource = candidates;

            var stored = candidates.FirstOrDefault(choice =>
                choice.Provider.Id == _config.CompareProviderId &&
                choice.Model.Equals(_config.CompareModel, StringComparison.OrdinalIgnoreCase));

            _compareChoice = stored ?? candidates.FirstOrDefault();
            RightModelComboBox.SelectedItem = _compareChoice;
            RightModelComboBox.ToolTip = _compareChoice is null
                ? "需要在设置里启用第二个模型"
                : BuildModelToolTip(_compareChoice);
        }
        finally
        {
            _suppressModelSelection = wasSuppressed;
        }
    }

    private static bool IsSameChoice(ModelChoice left, ModelChoice? right)
    {
        return right is not null &&
               left.Provider.Id == right.Provider.Id &&
               left.Model.Equals(right.Model, StringComparison.OrdinalIgnoreCase);
    }

    // 悬停提示：提供商 / 模型 + 能力标签（图片、推理、是否始终思考、上下文长度）。
    private static string BuildModelToolTip(ModelChoice choice)
    {
        var capability = ModelCapabilities.Infer(choice.Model);
        var parts = new List<string> { $"{choice.Provider.Name} / {choice.Model}" };

        if (capability.SupportsVision)
        {
            parts.Add("支持图片");
        }

        if (capability.SupportsReasoning)
        {
            parts.Add(capability.CanDisableReasoning == false ? "推理（始终思考）" : "支持推理");
        }

        if (capability.ContextTokens is { } tokens and > 0)
        {
            parts.Add($"上下文 {tokens / 1000}K");
        }

        return string.Join(" · ", parts);
    }

    // Tab 切左边（主）模型；` 切右边（对比）模型；按住 Shift 反向。
    // 都在已启用模型里循环。
    private void CycleModelChoice(bool rightSide, bool backwards)
    {
        var step = backwards ? -1 : 1;

        if (rightSide)
        {
            if (!_compareMode)
            {
                return;
            }

            var candidates = CompareCandidates();
            if (candidates.Count == 0)
            {
                return;
            }

            var index = candidates.FindIndex(choice => IsSameChoice(choice, _compareChoice));
            var next = candidates[Wrap(index + step, candidates.Count)];
            _compareChoice = next;
            _config.CompareProviderId = next.Provider.Id;
            _config.CompareModel = next.Model;

            _suppressModelSelection = true;
            try
            {
                RightModelComboBox.SelectedItem = next;
                RightModelComboBox.ToolTip = BuildModelToolTip(next);
            }
            finally
            {
                _suppressModelSelection = false;
            }

            SaveConfigInBackground();
            return;
        }

        if (_modelChoices.Count == 0)
        {
            return;
        }

        var primary = CurrentPrimaryChoice();
        var primaryIndex = primary is null
            ? (backwards ? 0 : -1)
            : _modelChoices.FindIndex(choice => IsSameChoice(choice, primary));
        var nextPrimary = _modelChoices[Wrap(primaryIndex + step, _modelChoices.Count)];

        ApplyModelChoice(nextPrimary);
    }

    // 负索引也能正确取模：C# 的 % 对负数返回负值，直接用会越界。
    private static int Wrap(int index, int count)
    {
        return ((index % count) + count) % count;
    }
    // 仅供 CI 截图：把面板切到「对比模式」并填两段示例回答。
    // 纯本地渲染、不发任何请求，用来核对左右分栏的布局。
    public void ShowComparePreview()
    {
        ApplyCompareMode(true, save: false);

        ResponsePanel.Visibility = Visibility.Visible;
        UpdateModeStripVisibility();
        FunctionList.Visibility = Visibility.Collapsed;
        ResponseModeText.Text = "回答 · 双模型对比";
        ResponseMetaText.Text = "1.8 秒 · Enter 追问 · ← 返回";

        // 每栏一行：模型 · 耗时 · 首字 · token，与真实结果一致。
        PrimaryModelText.Text = _currentProvider is null
            ? "左栏 · 1.8s · 首字 0.4s · 480→210 tok"
            : $"{_currentProvider.Name} · {_currentProvider.SelectedModel} · 1.8s · 首字 0.4s · 480→210 tok";
        CompareModelText.Text = _compareChoice is null
            ? "右栏 · 2.1s · 首字 0.6s · 512→240 tok"
            : $"{_compareChoice.Provider.Name} · {_compareChoice.Model} · 2.1s · 首字 0.6s · 512→240 tok";

        SetOutputMarkdown(
            """
            ## 要点

            左栏示例：**先给结论，再补一句理由**。

            - 对比模式下两栏各自流式渲染
            - 检索只做一次，两边看到同样的资料

            行内公式 $E = mc^2$ 也能正常渲染。
            """,
            null);

        SetCompareMarkdown(
            """
            ## 要点

            右栏示例：同一个问题，另一个模型的表述。

            - 用语更直白一些
            - 结论顺序略有不同

            行内公式 $E = mc^2$ 同样正常。
            """,
            null);
    }

    // ── 双模型对比 ────────────────────────────────────────────────

    private void CompareModeButton_Toggled(object sender, RoutedEventArgs e)
    {
        ApplyCompareMode(CompareModeButton.IsChecked == true, save: true);
    }

    private void ApplyCompareMode(bool enabled, bool save)
    {
        if (_applyingCompareMode)
        {
            return;
        }

        _applyingCompareMode = true;
        try
        {
            // 只有一个已启用模型时无从对比，直接拒绝并说明原因。
            var available = CompareCandidates().Count > 0;
            _compareMode = enabled && available;

            CompareModeButton.IsChecked = _compareMode;

            // 分栏
            ComparePane.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
            CompareSplitterColumn.Width = _compareMode ? new GridLength(10) : new GridLength(0);
            CompareColumn.Width = _compareMode
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

            PrimaryModelText.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;

            // 对比模式下两栏各自显示指标，共享的那行元信息就藏起来，避免和两栏打架。
            ResponseMetaText.Visibility = _compareMode ? Visibility.Collapsed : Visibility.Visible;

            // 模型选择框：单栏一个，对比模式两个。
            _suppressModelSelection = true;
            try
            {
                RightModelComboBox.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
                CompareArrowText.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
                LeftModelLabel.Text = _compareMode ? "左" : "模型";
            }
            finally
            {
                _suppressModelSelection = false;
            }

            if (_compareMode)
            {
                RefreshCompareOptions();
                PrimaryModelText.Text = _currentProvider is null
                    ? string.Empty
                    : $"{_currentProvider.Name} · {_currentProvider.SelectedModel}";
            }
            else if (enabled && !available)
            {
                StatusText.Text = "需要在设置里启用至少两个模型才能对比";
            }

            UpdateShortcutHint();

            if (save)
            {
                _config.CompareMode = _compareMode;
                SaveConfigInBackground();
            }
        }
        finally
        {
            _applyingCompareMode = false;
        }
    }

    // 右栏能选的模型：已启用且不等于左栏当前模型。
    private List<ModelChoice> CompareCandidates()
    {
        var primary = CurrentPrimaryChoice();
        return _modelChoices.Where(choice => !IsSameChoice(choice, primary)).ToList();
    }

    private ModelChoice? CurrentPrimaryChoice()
    {
        return _modelChoices.FirstOrDefault(choice =>
                   _currentProvider is not null &&
                   choice.Provider.Id == _currentProvider.Id &&
                   choice.Model.Equals(choice.Provider.SelectedModel, StringComparison.OrdinalIgnoreCase))
               ?? LeftModelComboBox.SelectedItem as ModelChoice
               ?? _modelChoices.FirstOrDefault();
    }

    // 底栏提示随模式变化：单栏时 Tab 切模型，对比时 Tab 切左、` 切右。
    private void UpdateShortcutHint()
    {
        ShortcutHintText.Text = _compareMode
            ? "ESC 关闭 · Tab/⇧Tab 切左 · ` /⇧` 切右"
            : "ESC 关闭 · Tab/⇧Tab 换模型";
    }

    private void ApplyModelChoice(ModelChoice choice)
    {
        _currentProvider = choice.Provider;
        choice.Provider.SelectedModel = choice.Model;
        _config.SelectedProviderId = choice.Provider.Id;

        _suppressModelSelection = true;
        try
        {
            LeftModelComboBox.SelectedItem = choice;
            LeftModelComboBox.ToolTip = BuildModelToolTip(choice);
        }
        finally
        {
            _suppressModelSelection = false;
        }

        // 主模型变了，右栏候选也要跟着排除它。
        RefreshCompareOptions();
        UpdateAttachmentTargetText();
        SaveConfigInBackground();
    }
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private async void OpenSettings()
    {
        _suppressDeactivateHide = true;

        try
        {
            // 传副本：设置窗口直接写传入对象，传本体的话点「取消」也会污染正在使用的配置。
            // 保存成功后由 LoadConfigurationAsync 从磁盘重新读回。
            var settings = new SettingsWindow(
                _configService,
                _config.Clone(),
                _aiService,
                _searchService,
                _modelCatalogService)
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

                // ShowWorkflowList 会清空上下文，所以话题恢复必须放在它之后。
                if (!string.IsNullOrWhiteSpace(entry.TopicId))
                {
                    await RestoreTopicContextAsync(entry.TopicId, entry.Prompt);
                }
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
        UpdateModeStripVisibility();
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
        UpdateModeStripVisibility();
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
