using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AskAny.Models;
using AskAny.Services;

namespace AskAny;

public partial class SettingsWindow : Window
{
    public int PreviewTabIndex
    {
        set => ShowSettingsSection(Math.Clamp(value, 0, 3));
    }

    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly SearchService _searchService;
    private readonly ModelCatalogService _modelCatalogService;
    private readonly AppConfig _config;
    private readonly List<ProviderConfig> _providers;
    private readonly List<FunctionOption> _functions;
    private ProviderConfig? _selectedProvider;
    private FunctionOption? _selectedFunction;
    private bool _isLoadingProvider;
    private bool _isLoadingFunction;
    private bool _isNavigatingSections;

    // 密文存在却解不开（配置来自其他 Windows 用户或机器）时，输入框会是空的。
    // 必须记住这一点，否则保存时会把原密文覆盖成空串，静默毁掉用户的 Key。
    private readonly HashSet<string> _unreadableApiKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _tavilyKeyUnreadable;
    private bool _zhihuSecretUnreadable;

    public SettingsWindow(
        ConfigService configService,
        AppConfig config,
        AiService aiService,
        SearchService searchService,
        ModelCatalogService modelCatalogService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
        _searchService = searchService;
        _modelCatalogService = modelCatalogService;
        _config = config;
        _providers = config.Providers.Count == 0
            ? ProviderCatalog.CreateDefaultProviders()
            : config.Providers.Select(provider => provider.Clone()).ToList();
        _functions = config.Functions.Count == 0
            ? FunctionCatalog.CreateDefaultFunctions()
            : config.Functions.Select(function => function.Clone()).ToList();

        PresetComboBox.ItemsSource = new[]
        {
            new PresetOption("OpenAI Chat Completions", "openai-chat"),
            new PresetOption("OpenAI Responses API", "openai-responses"),
            new PresetOption("DeepSeek Responses API", "deepseek-responses"),
            new PresetOption("自定义 OpenAI 兼容接口", "custom-chat")
        };
        PresetComboBox.SelectedIndex = 0;

        ProtocolComboBox.ItemsSource = new[]
        {
            new ProtocolOption("Chat Completions（OpenAI 兼容）", ApiProtocol.ChatCompletions),
            new ProtocolOption("Responses API（DeepSeek / OpenAI）", ApiProtocol.Responses)
        };

        ReasoningEffortComboBox.ItemsSource = new[] { "low", "medium", "high", "max" };
        FunctionModeComboBox.ItemsSource =
        new[]
        {
            new FunctionModeOption("标准回答", WorkflowMode.Answer),
            new FunctionModeOption("解释说明", WorkflowMode.Explain),
            new FunctionModeOption("深度思考（启用推理）", WorkflowMode.Think),
            new FunctionModeOption("联网检索", WorkflowMode.SearchNetwork)
        };
        FunctionSearchBackendComboBox.ItemsSource =
        new[]
        {
            new SearchBackendOption("全网（Tavily）", SearchBackend.TavilyGeneral),
            new SearchBackendOption("新闻（Tavily，偏向主流媒体）", SearchBackend.TavilyNews),
            new SearchBackendOption("知乎站内", SearchBackend.Zhihu)
        };
        FunctionGlyphComboBox.ItemsSource =
        new[]
        {
            new FunctionGlyphOption("回答", "\uE8BD"),
            new FunctionGlyphOption("解释", "\uE946"),
            new FunctionGlyphOption("新闻", "\uE909"),
            new FunctionGlyphOption("思考", "\uE735"),
            new FunctionGlyphOption("搜索", "\uE774"),
            new FunctionGlyphOption("查找", "\uE721"),
            new FunctionGlyphOption("写作", "\uE70F"),
            new FunctionGlyphOption("代码", "\uE943"),
            new FunctionGlyphOption("灵感", "\uEA80"),
            new FunctionGlyphOption("文档", "\uE8A5"),
            new FunctionGlyphOption("助手", "\uE99A")
        };

        TavilyKeyBox.Password = ConfigService.Unprotect(config.TavilyApiKeyProtected);
        _tavilyKeyUnreadable = TavilyKeyBox.Password.Length == 0 &&
                               !string.IsNullOrWhiteSpace(config.TavilyApiKeyProtected);
        ZhihuSecretBox.Password = ConfigService.Unprotect(config.ZhihuAccessSecretProtected);
        _zhihuSecretUnreadable = ZhihuSecretBox.Password.Length == 0 &&
                                 !string.IsNullOrWhiteSpace(config.ZhihuAccessSecretProtected);
        TopMostCheck.IsChecked = config.KeepWindowOnTop;
        StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
        HideWhenDeactivatedCheck.IsChecked = config.HideWhenDeactivated;
        AutoFillSelectionCheck.IsChecked = config.AutoFillSelectedText;

        // 全局快捷键：修饰键 + 双击间隔，默认仍是双击 Shift 唤起、双击 Ctrl 截图。
        InitializeHotkeySelectors(config);

        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedItem = _providers.FirstOrDefault(
                                        provider => provider.Id == config.SelectedProviderId)
                                    ?? _providers.FirstOrDefault();
        _selectedFunction = _functions.FirstOrDefault();
        FunctionEditorList.ItemsSource = _functions;
        FunctionEditorList.SelectedItem = _selectedFunction;

        Loaded += (_, _) =>
        {
            SaveCurrentProvider();
            _selectedProvider = ProviderList.SelectedItem as ProviderConfig;
            LoadProviderToForm(_selectedProvider);
            LoadFunctionToForm(_selectedFunction);
            ProviderNameBox.Focus();
        };
    }

    // 快捷键下拉：三个修饰键 + 四档双击间隔。
    private static readonly HotkeyModifierOption[] HotkeyModifierOptions =
    [
        new(HotkeyModifier.Shift, "Shift"),
        new(HotkeyModifier.Control, "Ctrl"),
        new(HotkeyModifier.Alt, "Alt")
    ];

    private static readonly int[] HotkeyIntervalOptions = [200, 300, 400, 500];

    private void InitializeHotkeySelectors(AppConfig config)
    {
        SelectionModifierBox.ItemsSource = HotkeyModifierOptions;
        SelectionModifierBox.DisplayMemberPath = nameof(HotkeyModifierOption.DisplayName);
        ScreenshotModifierBox.ItemsSource = HotkeyModifierOptions;
        ScreenshotModifierBox.DisplayMemberPath = nameof(HotkeyModifierOption.DisplayName);

        HotkeyIntervalBox.ItemsSource = HotkeyIntervalOptions
            .Select(ms => new HotkeyIntervalOption(ms))
            .ToArray();
        HotkeyIntervalBox.DisplayMemberPath = nameof(HotkeyIntervalOption.DisplayName);

        SelectionModifierBox.SelectedItem = HotkeyModifierOptions
            .First(option => option.Modifier == config.SelectionHotkeyModifier);
        ScreenshotModifierBox.SelectedItem = HotkeyModifierOptions
            .First(option => option.Modifier == config.ScreenshotHotkeyModifier);
        HotkeyIntervalBox.SelectedItem = HotkeyIntervalBox.Items
            .Cast<HotkeyIntervalOption>()
            .First(option => option.Milliseconds == config.HotkeyDoubleTapIntervalMs)
            ?? HotkeyIntervalBox.Items
                .Cast<HotkeyIntervalOption>()
                .OrderBy(option => Math.Abs(option.Milliseconds - config.HotkeyDoubleTapIntervalMs))
                .First();

        ScreenshotHotkeyCheck.IsChecked = config.ScreenshotHotkeyEnabled;
        ScreenshotModifierBox.IsEnabled = config.ScreenshotHotkeyEnabled;

        SelectionModifierBox.SelectionChanged += (_, _) => RefreshHotkeyHint();
        ScreenshotModifierBox.SelectionChanged += (_, _) => RefreshHotkeyHint();
        ScreenshotHotkeyCheck.Checked += (_, _) => RefreshHotkeyHint();
        ScreenshotHotkeyCheck.Unchecked += (_, _) => RefreshHotkeyHint();

        RefreshHotkeyHint();
    }

    private void RefreshHotkeyHint()
    {
        var selection = (SelectionModifierBox.SelectedItem as HotkeyModifierOption)?.DisplayName ?? "Shift";
        var screenshot = (ScreenshotModifierBox.SelectedItem as HotkeyModifierOption)?.DisplayName ?? "Ctrl";
        var enabled = ScreenshotHotkeyCheck.IsChecked == true;

        ScreenshotModifierBox.IsEnabled = enabled;

        if (!enabled)
        {
            HotkeyHintText.Text = $"双击 {selection} 唤起面板；区域截图已关闭。";
            return;
        }

        if (string.Equals(selection, screenshot, StringComparison.Ordinal))
        {
            HotkeyHintText.Text = "两个手势不能使用同一个修饰键，请换一个，否则截图手势不会生效。";
            return;
        }

        HotkeyHintText.Text = $"双击 {selection} 唤起面板，双击 {screenshot} 区域截图。";
    }

    private sealed record HotkeyModifierOption(HotkeyModifier Modifier, string DisplayName);

    private sealed record HotkeyIntervalOption(int Milliseconds)
    {
        public string DisplayName => $"{Milliseconds} 毫秒";
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveCurrentProvider())
        {
            return;
        }

        TestButton.IsEnabled = false;
        SettingsStatusText.Text = "正在测试当前提供商…";

        try
        {
            await _aiService.ValidateAsync(
                _selectedProvider!,
                ConfigService.Unprotect(_selectedProvider!.ApiKeyProtected));

            SettingsStatusText.Text = "连接成功";

            // 连接刚验证过，顺手把模型列表与能力拉下来 —— 用户要的「自动获取」。
            // 失败就静默（不打断「连接成功」这条好消息），只记日志。
            SafeTask.Run(() => FetchModelsAsync(manual: false), "自动获取模型列表");
        }
        catch (Exception exception)
        {
            // 这里刻意不过滤异常类型：地址畸形会抛 UriFormatException，网关返回 HTML 会抛
            // JsonException，两者逃出 async void 只会弹一个原始错误框，
            // 状态栏还会停在「正在测试当前提供商…」不动。
            SettingsStatusText.Text = exception is TaskCanceledException
                ? "连接超时"
                : exception.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveCurrentProvider())
        {
            return;
        }

        if (!TrySaveCurrentFunction())
        {
            return;
        }

        // 开机启动项会真的写注册表，必须先做：失败时要在改动配置之前退出，
        // 否则会留下「注册表已改、配置未存」的不一致状态。
        var startWithWindows = StartWithWindowsCheck.IsChecked == true;
        if (!StartupService.SetEnabled(startWithWindows))
        {
            SettingsStatusText.Text = "无法修改开机启动项，请检查系统权限";
            return;
        }

        // 两个手势用同一个修饰键时，截图会被唤起手势吃掉，先拦下来。
        if (ScreenshotHotkeyCheck.IsChecked == true &&
            SelectionModifierBox.SelectedItem is HotkeyModifierOption selectionOption &&
            ScreenshotModifierBox.SelectedItem is HotkeyModifierOption screenshotOption &&
            selectionOption.Modifier == screenshotOption.Modifier)
        {
            SettingsStatusText.Text = "唤起与截图不能使用同一个修饰键";
            return;
        }

        _config.Providers = _providers.Select(provider => provider.Clone()).ToList();
        _config.Functions = _functions.Select(function => function.Clone()).ToList();
        _config.SelectedProviderId = _selectedProvider!.Id;
        if (TavilyKeyBox.Password.Length > 0 || !_tavilyKeyUnreadable)
        {
            _config.TavilyApiKeyProtected = ConfigService.Protect(TavilyKeyBox.Password);
        }

        if (ZhihuSecretBox.Password.Length > 0 || !_zhihuSecretUnreadable)
        {
            _config.ZhihuAccessSecretProtected = ConfigService.Protect(ZhihuSecretBox.Password);
        }
        _config.KeepWindowOnTop = TopMostCheck.IsChecked == true;
        _config.HideWhenDeactivated = HideWhenDeactivatedCheck.IsChecked == true;
        _config.AutoFillSelectedText = AutoFillSelectionCheck.IsChecked == true;
        _config.ScreenshotHotkeyEnabled = ScreenshotHotkeyCheck.IsChecked == true;
        _config.StartWithWindows = startWithWindows;

        if (SelectionModifierBox.SelectedItem is HotkeyModifierOption selectionModifier)
        {
            _config.SelectionHotkeyModifier = selectionModifier.Modifier;
        }

        if (ScreenshotModifierBox.SelectedItem is HotkeyModifierOption screenshotModifier)
        {
            _config.ScreenshotHotkeyModifier = screenshotModifier.Modifier;
        }

        if (HotkeyIntervalBox.SelectedItem is HotkeyIntervalOption interval)
        {
            _config.HotkeyDoubleTapIntervalMs = interval.Milliseconds;
        }

        SaveButton.IsEnabled = false;
        try
        {
            await _configService.SaveAsync(_config);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // 写盘失败时恢复按钮，让用户能改完再试，而不是卡在禁用状态。
            SettingsStatusText.Text = $"保存失败：{exception.Message}";
            SaveButton.IsEnabled = true;
            return;
        }

        DialogResult = true;
    }

    private void ProviderList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingProvider ||
            ProviderList.SelectedItem is not ProviderConfig provider ||
            ReferenceEquals(provider, _selectedProvider))
        {
            return;
        }

        if (_selectedProvider is not null)
        {
            SaveCurrentProvider();
        }

        _selectedProvider = provider;
        // 列表项没有实现 INotifyPropertyChanged，改名后必须重建 ItemsSource 才会重绘；
        // 只切选中项时原来不刷新，导致刚改的名字在整场会话里都显示旧值。
        RefreshProviderList();
        LoadProviderToForm(provider);
    }

    private void RefreshProviderList()
    {
        _isLoadingProvider = true;
        try
        {
            ProviderList.ItemsSource = null;
            ProviderList.ItemsSource = _providers;
            ProviderList.SelectedItem = _selectedProvider;
        }
        finally
        {
            _isLoadingProvider = false;
        }
    }

    private void ReasoningCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ReasoningEffortComboBox is not null)
        {
            ReasoningEffortComboBox.IsEnabled = ReasoningCheck.IsChecked == true;
        }
    }

    private void AddProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not PresetOption preset)
        {
            return;
        }

        if (_selectedProvider is not null)
        {
            SaveCurrentProvider();
        }

        var provider = ProviderCatalog.CreatePreset(preset.Id);
        if (_providers.Any(item => item.Id == provider.Id))
        {
            provider.Id = Guid.NewGuid().ToString("N");
        }

        _providers.Add(provider);
        ProviderList.ItemsSource = null;
        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedItem = provider;
    }

    private void DeleteProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            return;
        }

        if (_providers.Count <= 1)
        {
            SettingsStatusText.Text = "至少需要保留一个提供商";
            return;
        }

        var result = MessageBox.Show(
            this,
            $"确定删除“{_selectedProvider.Name}”吗？",
            "AskAny",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = _providers.IndexOf(_selectedProvider);
        _providers.Remove(_selectedProvider);
        _selectedProvider = _providers[Math.Clamp(index, 0, _providers.Count - 1)];
        RefreshProviderList();
        LoadProviderToForm(_selectedProvider);
    }

    private void FunctionModeComboBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateSearchBackendAvailability();
    }

    // 「检索来源」只在「联网检索」下有意义；其余执行方式一律不检索。
    private void UpdateSearchBackendAvailability()
    {
        var isNetwork = (FunctionModeComboBox.SelectedItem as FunctionModeOption)?.Mode
                        == WorkflowMode.SearchNetwork;

        FunctionSearchBackendComboBox.IsEnabled = isNetwork;
        SearchBackendLabel.Opacity = isNetwork ? 1d : 0.5d;

        if (isNetwork && FunctionSearchBackendComboBox.SelectedItem is null)
        {
            FunctionSearchBackendComboBox.SelectedIndex = 0;
        }
    }

    // 历史检索模式在编辑界面统一显示为「联网检索」，保存时自然迁移到新模型。
    private static WorkflowMode DisplayMode(FunctionOption function)
    {
        return FunctionCatalog.IsLegacySearchMode(function.Mode)
            ? WorkflowMode.SearchNetwork
            : function.Mode;
    }

    private async void FetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await FetchModelsAsync(manual: true);
    }

    // 从供应商的 /models 拉实时列表，并按内置能力表标注支持图片的模型。
    // 结果写回表单而不是直接改 provider：保存与否仍由用户决定。
    private async Task FetchModelsAsync(bool manual)
    {
        if (!TrySaveCurrentProvider() || _selectedProvider is null)
        {
            return;
        }

        var provider = _selectedProvider;
        FetchModelsButton.IsEnabled = false;
        if (manual)
        {
            ModelFetchStatusText.Text = "正在获取模型列表…";
        }

        try
        {
            var result = await _modelCatalogService.FetchAsync(
                provider,
                ConfigService.Unprotect(provider.ApiKeyProtected));

            ModelsBox.Text = string.Join(Environment.NewLine, result.Models);
            VisionModelsBox.Text = string.Join(Environment.NewLine, result.VisionModels);

            // 重新生成勾选列表：默认全选，用户再手动挑要启用哪些。
            RebuildEnabledModelsList(provider);

            ModelFetchStatusText.Text =
                $"已获取 {result.Models.Count} 个模型：{result.VisionModels.Count} 个支持图片、" +
                $"{result.ReasoningModels.Count} 个支持推理。能力来自内置快照，可手动修改。";
        }
        catch (Exception exception)
        {
            if (manual)
            {
                ModelFetchStatusText.Text = exception.Message;
            }
            else
            {
                ErrorLog.Write("自动获取模型列表", exception);
            }
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    private async void ZhihuQuotaButton_Click(object sender, RoutedEventArgs e)
    {
        ZhihuQuotaButton.IsEnabled = false;
        ZhihuQuotaText.Text = "正在查询…";

        try
        {
            var secret = ZhihuSecretBox.Password;
            var items = await _searchService.GetZhihuQuotaAsync(secret);

            ZhihuQuotaText.Text = items.Count == 0
                ? "该账号还没有已配置额度的知乎接口。"
                : string.Join(
                    "　",
                    items.Select(item =>
                        $"{item.ApiName} 剩余 {item.RemainingQuota}/{item.TotalQuota}（已用 {item.TotalUsed}）"));
        }
        catch (Exception exception)
        {
            ZhihuQuotaText.Text = exception.Message;
        }
        finally
        {
            ZhihuQuotaButton.IsEnabled = true;
        }
    }

    // 主界面 tab 上显示的模型清单：全量模型 + 能力标签 + 是否启用。
    private void RebuildEnabledModelsList(ProviderConfig? provider)
    {
        var models = ParseModels(ModelsBox.Text);
        var previous = (EnabledModelsList.ItemsSource as IEnumerable<ModelEnableItem> ?? [])
            .ToDictionary(item => item.Name, item => item.IsEnabled, StringComparer.OrdinalIgnoreCase);

        // 该提供商勾选过的集合；为空表示「全部启用」。
        var enabled = (provider?.EnabledModels ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = models
            .Select(name => new ModelEnableItem(
                name,
                DescribeCapability(name),
                enabled.Count == 0
                    ? (previous.TryGetValue(name, out var keep) ? keep : true)
                    : enabled.Contains(name)))
            .ToList();

        EnabledModelsList.ItemsSource = items;
        UpdateEnabledModelsHint(items, models.Count);
    }

    private void UpdateEnabledModelsHint(IReadOnlyCollection<ModelEnableItem> items, int total)
    {
        var enabledCount = items.Count(item => item.IsEnabled);

        EnabledModelsHint.Text = total == 0
            ? "先填写或从供应商获取模型列表。"
            : $"共 {total} 个模型，已启用 {enabledCount} 个。主界面只显示已启用的模型 tab。";
    }

    // 能力标签用内置快照推断：图片 / 推理 / 始终思考 / 上下文长度。
    private static string DescribeCapability(string model)
    {
        var capability = ModelCapabilities.Infer(model);
        var parts = new List<string>();

        if (capability.SupportsVision)
        {
            parts.Add("图片");
        }

        if (capability.SupportsReasoning)
        {
            parts.Add(capability.CanDisableReasoning == false ? "推理(始终)" : "推理");
        }

        if (capability.ContextTokens is { } tokens and > 0)
        {
            parts.Add(FormatContext(tokens));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatContext(int tokens)
    {
        return tokens >= 1_000_000
            ? $"{tokens / 1_000_000d:0.#}M"
            : $"{Math.Round(tokens / 1000d):0}K";
    }

    private void ModelsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 手改了模型清单后，重新生成勾选列表；已勾选状态能保留。
        if (!_isLoadingProvider)
        {
            RebuildEnabledModelsList(_selectedProvider);
        }
    }

    private void SelectAllModelsButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllModelsEnabled(true);
    }

    private void SelectNoModelsButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllModelsEnabled(false);
    }

    private void SetAllModelsEnabled(bool enabled)
    {
        if (EnabledModelsList.ItemsSource is not IEnumerable<ModelEnableItem> items)
        {
            return;
        }

        var list = items.ToList();
        foreach (var item in list)
        {
            item.IsEnabled = enabled;
        }

        // 记录型绑定不会自己刷新勾选状态，重建一次 ItemsSource 最稳。
        EnabledModelsList.ItemsSource = null;
        EnabledModelsList.ItemsSource = list;
        UpdateEnabledModelsHint(list, list.Count);
    }

    private void EnabledModelsList_Changed(object sender, RoutedEventArgs e)
    {
        if (EnabledModelsList.ItemsSource is IEnumerable<ModelEnableItem> items)
        {
            var list = items.ToList();
            UpdateEnabledModelsHint(list, list.Count);
        }
    }

    private sealed class ModelEnableItem
    {
        public ModelEnableItem(string name, string capabilityText, bool isEnabled)
        {
            Name = name;
            CapabilityText = capabilityText;
            IsEnabled = isEnabled;
        }

        public string Name { get; }

        public string CapabilityText { get; }

        public bool IsEnabled { get; set; }
    }

    private void LoadProviderToForm(ProviderConfig? provider)
    {
        _isLoadingProvider = true;
        try
        {
            if (provider is null)
            {
                return;
            }

            ProviderNameBox.Text = provider.Name;
            ProtocolComboBox.SelectedItem = ((IEnumerable<ProtocolOption>)ProtocolComboBox.ItemsSource)
                .FirstOrDefault(option => option.Protocol == provider.Protocol);
            BaseUriBox.Text = provider.BaseUri;
            ApiKeyBox.Password = ConfigService.Unprotect(provider.ApiKeyProtected);
            if (ApiKeyBox.Password.Length == 0 && !string.IsNullOrWhiteSpace(provider.ApiKeyProtected))
            {
                _unreadableApiKeys.Add(provider.Id);
            }
            else
            {
                _unreadableApiKeys.Remove(provider.Id);
            }

            ModelsBox.Text = string.Join(Environment.NewLine, provider.Models);
            VisionModelsBox.Text = string.Join(Environment.NewLine, provider.VisionModels);
            RebuildEnabledModelsList(provider);
            ReasoningCheck.IsChecked = provider.SupportsReasoningControl;
            ReasoningEffortComboBox.SelectedItem = ReasoningEffortComboBox.Items
                .Cast<string>()
                .FirstOrDefault(effort => effort.Equals(
                    provider.ReasoningEffort,
                    StringComparison.OrdinalIgnoreCase)) ?? "high";
            ReasoningEffortComboBox.IsEnabled = provider.SupportsReasoningControl;
            SettingsStatusText.Text = _unreadableApiKeys.Contains(provider.Id)
                ? "API Key 无法解密（配置可能来自其他用户或机器），请重新填写"
                : string.Empty;
        }
        finally
        {
            _isLoadingProvider = false;
        }
    }

    private void SaveCurrentProvider()
    {
        if (_selectedProvider is null || _isLoadingProvider)
        {
            return;
        }

        // 这里不再把空值替换成「未命名提供商」/「model-name」占位符：
        // 那样会让 TrySaveCurrentProvider 的名称与模型校验永远不可达，
        // 用户的清空意图被静默翻译成一个占位值写进配置。
        _selectedProvider.Name = ProviderNameBox.Text.Trim();
        if (ProtocolComboBox.SelectedItem is ProtocolOption protocol)
        {
            _selectedProvider.Protocol = protocol.Protocol;
        }

        _selectedProvider.BaseUri = BaseUriBox.Text.Trim();
        if (ApiKeyBox.Password.Length > 0 || !_unreadableApiKeys.Contains(_selectedProvider.Id))
        {
            _selectedProvider.ApiKeyProtected = ConfigService.Protect(ApiKeyBox.Password);
        }

        _selectedProvider.Models = ParseModels(ModelsBox.Text);
        if (_selectedProvider.Models.Count > 0 &&
            !_selectedProvider.Models.Contains(
                _selectedProvider.SelectedModel,
                StringComparer.OrdinalIgnoreCase))
        {
            _selectedProvider.SelectedModel = _selectedProvider.Models[0];
        }

        _selectedProvider.VisionModels = ParseModels(VisionModelsBox.Text);
        _selectedProvider.VisionModelsConfigured = true;

        // 启用集合来自勾选列表。全部启用时写空，表示「全部启用」（老配置语义）。
        var checkedByName = (EnabledModelsList.ItemsSource as IEnumerable<ModelEnableItem> ?? [])
            .ToDictionary(item => item.Name, item => item.IsEnabled, StringComparer.OrdinalIgnoreCase);

        // 勾选列表里还没有的模型（刚获取 / 刚手填、尚未重建列表）默认视为启用，
        // 否则会在用户不知情的情况下把它们禁掉。
        var enabledNames = _selectedProvider.Models
            .Where(name => !checkedByName.TryGetValue(name, out var isEnabled) || isEnabled)
            .ToList();

        _selectedProvider.EnabledModels =
            enabledNames.Count == _selectedProvider.Models.Count ? [] : enabledNames;

        // 当前选中的模型必须还在启用集合里，否则主界面会选到一个不存在的 tab。
        var effective = _selectedProvider.EffectiveModels();
        if (effective.Count > 0 &&
            !effective.Contains(_selectedProvider.SelectedModel, StringComparer.OrdinalIgnoreCase))
        {
            _selectedProvider.SelectedModel = effective[0];
        }

        _selectedProvider.SupportsReasoningControl = ReasoningCheck.IsChecked == true;
        _selectedProvider.ReasoningEffort = ReasoningEffortComboBox.SelectedItem as string ?? "high";
    }

    private bool TrySaveCurrentProvider()
    {
        SaveCurrentProvider();
        if (_selectedProvider is null)
        {
            SettingsStatusText.Text = "请先添加并选择一个提供商";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_selectedProvider.Name))
        {
            SettingsStatusText.Text = "请填写提供商名称";
            ProviderNameBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(_selectedProvider.BaseUri))
        {
            SettingsStatusText.Text = "请填写接口地址";
            BaseUriBox.Focus();
            return false;
        }

        // 只校验非空是不够的：地址缺协议头或拼错时，AiService 里拼出的相对地址
        // 会在发送阶段才失败，用户看到的是运行期报错而不是这里的一句提示。
        if (!IsSupportedBaseUri(_selectedProvider.BaseUri))
        {
            SettingsStatusText.Text = "接口地址需要是完整的 http:// 或 https:// 地址";
            BaseUriBox.Focus();
            return false;
        }

        if (_selectedProvider.Models.Count == 0)
        {
            SettingsStatusText.Text = "请至少填写一个模型";
            ModelsBox.Focus();
            return false;
        }

        return true;
    }

    private static bool IsSupportedBaseUri(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void FunctionEditorList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isLoadingFunction ||
            FunctionEditorList.SelectedItem is not FunctionOption function ||
            ReferenceEquals(function, _selectedFunction))
        {
            return;
        }

        SaveCurrentFunction(refreshList: false);
        _selectedFunction = function;
        // 同理：函数列表也要重建，否则改名 / 换图标后列表项仍显示旧值。
        RefreshFunctionEditorList();
        LoadFunctionToForm(function);
    }

    private void FunctionEditorItem_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void AddFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentFunction();
        var function = FunctionCatalog.CreateCustom();
        _functions.Add(function);
        _selectedFunction = function;
        RefreshFunctionEditorList();
        LoadFunctionToForm(function);
        FunctionNameBox.Focus();
        FunctionNameBox.SelectAll();
    }

    private void DeleteFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFunction is null)
        {
            return;
        }

        if (_functions.Count <= 1)
        {
            SettingsStatusText.Text = "至少需要保留一个功能选项";
            return;
        }

        var result = MessageBox.Show(
            this,
            $"确定删除“{_selectedFunction.Name}”吗？",
            "AskAny",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var index = _functions.IndexOf(_selectedFunction);
        _functions.Remove(_selectedFunction);
        _selectedFunction = _functions[Math.Clamp(index, 0, _functions.Count - 1)];
        RefreshFunctionEditorList();
        LoadFunctionToForm(_selectedFunction);
    }

    private void MoveFunctionUpButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedFunction(-1);
    }

    private void MoveFunctionDownButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSelectedFunction(1);
    }

    private void MoveSelectedFunction(int offset)
    {
        if (_selectedFunction is null)
        {
            return;
        }

        SaveCurrentFunction();
        var index = _functions.IndexOf(_selectedFunction);
        var targetIndex = index + offset;
        if (index < 0 || targetIndex < 0 || targetIndex >= _functions.Count)
        {
            return;
        }

        (_functions[index], _functions[targetIndex]) = (_functions[targetIndex], _functions[index]);
        RefreshFunctionEditorList();
        FunctionEditorList.ScrollIntoView(_selectedFunction);
        UpdateFunctionOrderButtons();
    }

    private void ResetFunctionPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionModeComboBox.SelectedItem is not FunctionModeOption mode)
        {
            return;
        }

        FunctionPromptBox.Text = FunctionCatalog.GetDefaultSystemPrompt(mode.Mode);
    }

    private void LoadFunctionToForm(FunctionOption? function)
    {
        _isLoadingFunction = true;
        try
        {
            if (function is null)
            {
                return;
            }

            FunctionNameBox.Text = function.Name;
            FunctionPromptBox.Text = function.SystemPrompt;
            FunctionModeComboBox.SelectedItem =
                ((IEnumerable<FunctionModeOption>)FunctionModeComboBox.ItemsSource)
                .FirstOrDefault(option => option.Mode == DisplayMode(function));

            // 历史检索模式（新闻追踪 / 联网解释 / 知乎搜索）在界面上统一按「联网检索」展示，
            // 来源由其 SearchBackend 决定。非检索的执行方式下留空并禁用 —— 显示一个具体来源
            //（如「全网」）会让人以为「标准回答」也会去联网检索。
            var isNetwork = DisplayMode(function) == WorkflowMode.SearchNetwork;
            var backend = isNetwork && function.SearchBackend == SearchBackend.None
                ? SearchBackend.TavilyGeneral
                : function.SearchBackend;

            FunctionSearchBackendComboBox.SelectedItem = isNetwork
                ? ((IEnumerable<SearchBackendOption>)FunctionSearchBackendComboBox.ItemsSource)
                  .FirstOrDefault(option => option.Source == backend)
                  ?? FunctionSearchBackendComboBox.Items[0]
                : null;
            UpdateSearchBackendAvailability();
            FunctionGlyphComboBox.SelectedItem =
                ((IEnumerable<FunctionGlyphOption>)FunctionGlyphComboBox.ItemsSource)
                .FirstOrDefault(option => option.Glyph == function.Glyph)
                ?? FunctionGlyphComboBox.Items[0];
        }
        finally
        {
            _isLoadingFunction = false;
            UpdateFunctionOrderButtons();
        }
    }

    private void SaveCurrentFunction(bool refreshList = true)
    {
        if (_selectedFunction is null || _isLoadingFunction)
        {
            return;
        }

        var mode = (FunctionModeComboBox.SelectedItem as FunctionModeOption)?.Mode
                   ?? _selectedFunction.Mode;
        var glyph = (FunctionGlyphComboBox.SelectedItem as FunctionGlyphOption)?.Glyph
                    ?? _selectedFunction.Glyph;

        _selectedFunction.Mode = mode;
        _selectedFunction.SearchBackend = mode == WorkflowMode.SearchNetwork
            ? (FunctionSearchBackendComboBox.SelectedItem as SearchBackendOption)?.Source
              ?? SearchBackend.TavilyGeneral
            : SearchBackend.None;
        _selectedFunction.Name = string.IsNullOrWhiteSpace(FunctionNameBox.Text)
            ? FunctionCatalog.GetModeName(mode)
            : FunctionNameBox.Text.Trim();
        _selectedFunction.Glyph = string.IsNullOrWhiteSpace(glyph)
            ? "\uE8BD"
            : glyph;
        _selectedFunction.SystemPrompt = string.IsNullOrWhiteSpace(FunctionPromptBox.Text)
            ? FunctionCatalog.GetDefaultSystemPrompt(mode)
            : FunctionPromptBox.Text.Trim();
        if (refreshList)
        {
            FunctionEditorList.Items.Refresh();
        }
    }

    private bool TrySaveCurrentFunction()
    {
        SaveCurrentFunction();
        if (_selectedFunction is null)
        {
            SettingsStatusText.Text = "请先新增或选择一个功能选项";
            return false;
        }

        return true;
    }

    private void RefreshFunctionEditorList()
    {
        _isLoadingFunction = true;
        try
        {
            FunctionEditorList.ItemsSource = null;
            FunctionEditorList.ItemsSource = _functions;
            FunctionEditorList.SelectedItem = _selectedFunction;
        }
        finally
        {
            _isLoadingFunction = false;
        }

        UpdateFunctionOrderButtons();
    }

    private void UpdateFunctionOrderButtons()
    {
        var index = _selectedFunction is null ? -1 : _functions.IndexOf(_selectedFunction);
        MoveFunctionUpButton.IsEnabled = index > 0;
        MoveFunctionDownButton.IsEnabled = index >= 0 && index < _functions.Count - 1;
        DeleteFunctionButton.IsEnabled = _functions.Count > 1;
    }

    private void SettingsNav_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isNavigatingSections || SettingsNav.SelectedIndex < 0)
        {
            return;
        }

        ShowSettingsSection(SettingsNav.SelectedIndex);
    }

    private void ShowSettingsSection(int index)
    {
        if (SettingsNav is null ||
            ModelServicesPanel is null ||
            FunctionOptionsPanel is null ||
            ReasoningSearchPanel is null ||
            WindowPreferencesPanel is null)
        {
            return;
        }

        _isNavigatingSections = true;
        try
        {
            index = Math.Clamp(index, 0, 3);
            SettingsNav.SelectedIndex = index;
            ModelServicesPanel.Visibility = index == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            FunctionOptionsPanel.Visibility = index == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            ReasoningSearchPanel.Visibility = index == 2
                ? Visibility.Visible
                : Visibility.Collapsed;
            WindowPreferencesPanel.Visibility = index == 3
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _isNavigatingSections = false;
        }
    }

    private static List<string> ParseModels(string input)
    {
        return input
            .ReplaceLineEndings("\n")
            .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(model => model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            FindVisualParent<ButtonBase>((DependencyObject)e.OriginalSource) is not null)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
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

            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private sealed record PresetOption(string Name, string Id);

    private sealed record ProtocolOption(string Name, ApiProtocol Protocol);

    private sealed record FunctionModeOption(string Name, WorkflowMode Mode);

    private sealed record SearchBackendOption(string Name, SearchBackend Source);

    private sealed record FunctionGlyphOption(string Name, string Glyph);
}
