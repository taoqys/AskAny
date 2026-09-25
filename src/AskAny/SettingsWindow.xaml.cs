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
    private readonly AppConfig _config;
    private readonly List<ProviderConfig> _providers;
    private readonly List<FunctionOption> _functions;
    private ProviderConfig? _selectedProvider;
    private FunctionOption? _selectedFunction;
    private bool _isLoadingProvider;
    private bool _isLoadingFunction;
    private bool _isNavigatingSections;

    public SettingsWindow(
        ConfigService configService,
        AppConfig config,
        AiService aiService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
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
            new FunctionModeOption("联网解释", WorkflowMode.ExplainOnline),
            new FunctionModeOption("新闻追踪", WorkflowMode.TrackNews),
            new FunctionModeOption("深度思考", WorkflowMode.Think)
        };
        FunctionGlyphComboBox.ItemsSource =
        new[]
        {
            new FunctionGlyphOption("回答", "\uE8BD"),
            new FunctionGlyphOption("解释", "\uE946"),
            new FunctionGlyphOption("新闻", "\uE909"),
            new FunctionGlyphOption("思考", "\uE735"),
            new FunctionGlyphOption("搜索", "\uE774"),
            new FunctionGlyphOption("写作", "\uE70F"),
            new FunctionGlyphOption("代码", "\uE943"),
            new FunctionGlyphOption("灵感", "\uEA80"),
            new FunctionGlyphOption("文档", "\uE8A5"),
            new FunctionGlyphOption("助手", "\uE99A")
        };

        TavilyKeyBox.Password = ConfigService.Unprotect(config.TavilyApiKeyProtected);
        TopMostCheck.IsChecked = config.KeepWindowOnTop;
        StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
        HideWhenDeactivatedCheck.IsChecked = config.HideWhenDeactivated;
        AutoFillSelectionCheck.IsChecked = config.AutoFillSelectedText;

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
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
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

        _config.Providers = _providers.Select(provider => provider.Clone()).ToList();
        _config.Functions = _functions.Select(function => function.Clone()).ToList();
        _config.SelectedProviderId = _selectedProvider!.Id;
        _config.TavilyApiKeyProtected = ConfigService.Protect(TavilyKeyBox.Password);
        _config.KeepWindowOnTop = TopMostCheck.IsChecked == true;
        _config.HideWhenDeactivated = HideWhenDeactivatedCheck.IsChecked == true;
        _config.AutoFillSelectedText = AutoFillSelectionCheck.IsChecked == true;

        if (!StartupService.SetEnabled(StartWithWindowsCheck.IsChecked == true))
        {
            SettingsStatusText.Text = "无法修改开机启动项，请检查系统权限";
            SaveButton.IsEnabled = true;
            return;
        }

        _config.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

        SaveButton.IsEnabled = false;
        await _configService.SaveAsync(_config);
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
        LoadProviderToForm(provider);
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

        _providers.Remove(_selectedProvider);
        ProviderList.ItemsSource = null;
        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedIndex = 0;
        _selectedProvider = ProviderList.SelectedItem as ProviderConfig;
        LoadProviderToForm(_selectedProvider);
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
            ModelsBox.Text = string.Join(Environment.NewLine, provider.Models);
            ReasoningCheck.IsChecked = provider.SupportsReasoningControl;
            ReasoningEffortComboBox.SelectedItem = ReasoningEffortComboBox.Items
                .Cast<string>()
                .FirstOrDefault(effort => effort.Equals(
                    provider.ReasoningEffort,
                    StringComparison.OrdinalIgnoreCase)) ?? "high";
            ReasoningEffortComboBox.IsEnabled = provider.SupportsReasoningControl;
            SettingsStatusText.Text = string.Empty;
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

        _selectedProvider.Name = string.IsNullOrWhiteSpace(ProviderNameBox.Text)
            ? "未命名提供商"
            : ProviderNameBox.Text.Trim();
        if (ProtocolComboBox.SelectedItem is ProtocolOption protocol)
        {
            _selectedProvider.Protocol = protocol.Protocol;
        }

        _selectedProvider.BaseUri = BaseUriBox.Text.Trim();
        _selectedProvider.ApiKeyProtected = ConfigService.Protect(ApiKeyBox.Password);
        _selectedProvider.Models = ParseModels(ModelsBox.Text);
        if (_selectedProvider.Models.Count == 0)
        {
            _selectedProvider.Models = ["model-name"];
        }

        if (!_selectedProvider.Models.Contains(
                _selectedProvider.SelectedModel,
                StringComparer.OrdinalIgnoreCase))
        {
            _selectedProvider.SelectedModel = _selectedProvider.Models[0];
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

        if (_selectedProvider.Models.Count == 0)
        {
            SettingsStatusText.Text = "请至少填写一个模型";
            ModelsBox.Focus();
            return false;
        }

        return true;
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
                .FirstOrDefault(option => option.Mode == function.Mode);
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

    private sealed record FunctionGlyphOption(string Name, string Glyph);
}
