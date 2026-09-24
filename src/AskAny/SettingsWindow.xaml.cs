using System.Windows;
using AskAny.Models;
using AskAny.Services;

namespace AskAny;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly AppConfig _config;

    public SettingsWindow(
        ConfigService configService,
        AppConfig config,
        AiService aiService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
        _config = config;

        BaseUriBox.Text = config.OpenAiBaseUri;
        ModelBox.Text = config.Model;
        OpenAiKeyBox.Password = ConfigService.Unprotect(config.OpenAiApiKeyProtected);
        TavilyKeyBox.Password = ConfigService.Unprotect(config.TavilyApiKeyProtected);
        TopMostCheck.IsChecked = config.KeepWindowOnTop;

        Loaded += (_, _) => BaseUriBox.Focus();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        SettingsStatusText.Text = "正在测试模型连接…";

        try
        {
            await _aiService.ValidateAsync(
                BaseUriBox.Text,
                ModelBox.Text,
                OpenAiKeyBox.Password);

            SettingsStatusText.Text = "模型连接成功";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
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
        if (string.IsNullOrWhiteSpace(BaseUriBox.Text))
        {
            SettingsStatusText.Text = "请填写接口地址";
            BaseUriBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelBox.Text))
        {
            SettingsStatusText.Text = "请填写模型名称";
            ModelBox.Focus();
            return;
        }

        _config.OpenAiBaseUri = BaseUriBox.Text.Trim();
        _config.Model = ModelBox.Text.Trim();
        _config.OpenAiApiKeyProtected = ConfigService.Protect(OpenAiKeyBox.Password);
        _config.TavilyApiKeyProtected = ConfigService.Protect(TavilyKeyBox.Password);
        _config.KeepWindowOnTop = TopMostCheck.IsChecked == true;

        SaveButton.IsEnabled = false;
        await _configService.SaveAsync(_config);
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
