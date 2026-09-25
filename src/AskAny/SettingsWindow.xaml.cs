using System.Windows;
using AskAny.Models;
using AskAny.Services;

namespace AskAny;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly AppConfig _config;
    private readonly List<ProviderConfig> _providers;
    private ProviderConfig? _selectedProvider;
    private bool _isLoadingProvider;

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

        TavilyKeyBox.Password = ConfigService.Unprotect(config.TavilyApiKeyProtected);
        TopMostCheck.IsChecked = config.KeepWindowOnTop;
        StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
        HideWhenDeactivatedCheck.IsChecked = config.HideWhenDeactivated;
        AutoFillSelectionCheck.IsChecked = config.AutoFillSelectedText;

        ProviderList.ItemsSource = _providers;
        ProviderList.SelectedItem = _providers.FirstOrDefault(
                                        provider => provider.Id == config.SelectedProviderId)
                                    ?? _providers.FirstOrDefault();

        Loaded += (_, _) =>
        {
            SaveCurrentProvider();
            _selectedProvider = ProviderList.SelectedItem as ProviderConfig;
            LoadProviderToForm(_selectedProvider);
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

        _config.Providers = _providers.Select(provider => provider.Clone()).ToList();
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

    private sealed record PresetOption(string Name, string Id);

    private sealed record ProtocolOption(string Name, ApiProtocol Protocol);
}
