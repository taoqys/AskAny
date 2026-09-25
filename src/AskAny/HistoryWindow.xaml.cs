using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AskAny.Models;
using AskAny.Services;

namespace AskAny;

public partial class HistoryWindow : Window
{
    private readonly HistoryService _historyService;
    private List<HistoryEntry> _entries = [];

    public HistoryEntry? SelectedEntryToReuse { get; private set; }

    public HistoryWindow(HistoryService historyService)
    {
        InitializeComponent();
        _historyService = historyService;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _entries = await _historyService.LoadAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(keyword)
            ? _entries
            : _entries.Where(entry =>
                entry.Prompt.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Response.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.Model.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                entry.ProviderName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        HistoryList.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
        }
        else
        {
            DetailTitleText.Text = "没有记录";
            DetailMetaText.Text = string.Empty;
            DetailRichText.Document = MarkdownRenderer.Render("暂无历史记录。");
        }

        HistoryStatusText.Text = $"共 {filtered.Count} 条";
    }

    private HistoryEntry? SelectedEntry => HistoryList.SelectedItem as HistoryEntry;

    private void SearchBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter();
    }

    private void HistoryList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        DetailTitleText.Text = entry.Prompt;
        DetailMetaText.Text =
            $"{entry.Timestamp:yyyy-MM-dd HH:mm} · {entry.ModeName} · {entry.ProviderName} / {entry.Model}" +
            (entry.SourceCount > 0 ? $" · {entry.SourceCount} 条来源" : string.Empty);
        DetailRichText.Document = MarkdownRenderer.Render(entry.Response);
        DetailRichText.ScrollToHome();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ReuseSelected();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is null)
        {
            return;
        }

        Clipboard.SetText(SelectedEntry.Response);
        HistoryStatusText.Text = "已复制";
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is null)
        {
            return;
        }

        await _historyService.DeleteAsync(SelectedEntry.Id);
        await ReloadAsync();
        HistoryStatusText.Text = "已删除";
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空全部历史记录吗？",
            "AskAny",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _historyService.ClearAsync();
        await ReloadAsync();
        HistoryStatusText.Text = "历史记录已清空";
    }

    private void ReuseButton_Click(object sender, RoutedEventArgs e)
    {
        ReuseSelected();
    }

    private void ReuseSelected()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        SelectedEntryToReuse = SelectedEntry;
        DialogResult = true;
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
}
