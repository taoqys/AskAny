using System.Windows;
using System.Windows.Input;
using AskAny.Models;
using AskAny.Services;
using Microsoft.Win32;

namespace AskAny;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AiService _aiService;
    private readonly SearchService _searchService;
    private readonly IReadOnlyList<FunctionOption> _functions =
    [
        new(WorkflowMode.Explain, "解释说明", "拆解概念、背景和关键要点", "\uE946"),
        new(WorkflowMode.Answer, "回答问题", "直接、准确地解答当前问题", "\uE8BD"),
        new(WorkflowMode.TrackNews, "新闻追踪", "搜索最近动态并整理事件脉络", "\uE909"),
        new(WorkflowMode.Think, "深度思考", "先分析要点，再给出明确结论", "\uE735"),
        new(WorkflowMode.ExplainOnline, "联网解释", "结合网络资料解释问题并标注来源", "\uE774")
    ];

    private AppConfig _config = new();
    private bool _isRunning;
    private bool _allowClose;

    public MainWindow(
        ConfigService configService,
        AiService aiService,
        SearchService searchService)
    {
        InitializeComponent();

        _configService = configService;
        _aiService = aiService;
        _searchService = searchService;

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
        Topmost = _config.KeepWindowOnTop;
        UpdatePinButton();
    }

    public void ShowFromHotkey()
    {
        if (IsVisible && IsActive)
        {
            Hide();
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + Math.Max(12, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(12, (workArea.Height - Height) / 3);

        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = _config.KeepWindowOnTop;
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text.Length;
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
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = ExecuteSelectedAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F1)
        {
            OpenSettings();
            e.Handled = true;
        }
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

        _isRunning = true;
        BusyProgress.Visibility = Visibility.Visible;
        ResponsePanel.Visibility = Visibility.Visible;
        FunctionList.Visibility = Visibility.Collapsed;
        ResponseModeText.Text = option.Name;
        ResponseMetaText.Text = "正在准备…";
        OutputBox.Text = option.Mode is WorkflowMode.TrackNews or WorkflowMode.ExplainOnline
            ? "正在检索网络资料…"
            : "正在生成回答…";
        StatusText.Text = "正在执行";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            SearchPacket? search = null;
            if (option.Mode is WorkflowMode.TrackNews or WorkflowMode.ExplainOnline)
            {
                var tavilyKey = ConfigService.Unprotect(_config.TavilyApiKeyProtected);
                search = await _searchService.SearchAsync(
                    prompt,
                    option.Mode == WorkflowMode.TrackNews,
                    tavilyKey);

                ResponseMetaText.Text = $"已检索 {search.Sources.Count} 条资料，正在整理…";
                OutputBox.Text = "正在结合检索资料生成回答…";
            }

            var answer = await _aiService.ExecuteAsync(
                option.Mode,
                prompt,
                _config.OpenAiBaseUri,
                _config.Model,
                ConfigService.Unprotect(_config.OpenAiApiKeyProtected),
                search);

            OutputBox.Text = answer;
            stopwatch.Stop();
            var sourceText = search is null ? string.Empty : $"，{search.Sources.Count} 条来源";
            ResponseMetaText.Text = $"{stopwatch.Elapsed.TotalSeconds:F1} 秒{sourceText}";
            StatusText.Text = "执行完成";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            OutputBox.Text = exception is TaskCanceledException
                ? "请求超时，请稍后重试。"
                : exception.Message;
            ResponseMetaText.Text = "执行失败";
            StatusText.Text = "执行失败";
        }
        finally
        {
            BusyProgress.Visibility = Visibility.Collapsed;
            _isRunning = false;
        }
    }

    private void PromptBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        PromptPlaceholder.Visibility = string.IsNullOrEmpty(PromptBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void FunctionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FunctionList.SelectedItem is FunctionOption option)
        {
            StatusText.Text = $"已选择：{option.Name}，Enter 执行";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private async void OpenSettings()
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
        ResponsePanel.Visibility = Visibility.Collapsed;
        FunctionList.Visibility = Visibility.Visible;
        StatusText.Text = "↑ ↓ 选择功能，Enter 执行";
        PromptBox.Focus();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            return;
        }

        Clipboard.SetText(OutputBox.Text);
        StatusText.Text = "回答已复制";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
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

        await File.WriteAllTextAsync(dialog.FileName, OutputBox.Text);
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

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExit();
    }

    private void RequestExit()
    {
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
