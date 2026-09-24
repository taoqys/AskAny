using System.Net.Http.Headers;
using AskAny.Models;

namespace AskAny.Services;

public sealed class AiService
{
    private readonly HttpClient _httpClient;

    public AiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ExecuteAsync(
        WorkflowMode mode,
        string prompt,
        string baseUri,
        string model,
        string apiKey,
        SearchPacket? search,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("请先输入问题。");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException("请先在设置中填写模型名称。");
        }

        var systemPrompt = BuildSystemPrompt(mode, search);
        var endpoint = BuildEndpoint(baseUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        var body = new ChatRequest(
            model.Trim(),
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", prompt)
            ],
            mode == WorkflowMode.Think ? 0.3 : 0.6);

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonDefaults.Compact),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"模型请求失败（{(int)response.StatusCode}）：{Shorten(payload)}");
        }

        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(payload, JsonDefaults.Compact);
        var content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        return string.IsNullOrWhiteSpace(content) ? "模型没有返回文本内容。" : content;
    }

    public async Task ValidateAsync(
        string baseUri,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            WorkflowMode.Answer,
            "只回复“连接成功”四个字。",
            baseUri,
            model,
            apiKey,
            null,
            cancellationToken);
    }

    private static string BuildEndpoint(string baseUri)
    {
        if (string.IsNullOrWhiteSpace(baseUri))
        {
            throw new InvalidOperationException("请先填写 OpenAI 兼容接口地址。");
        }

        var normalized = baseUri.Trim().TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized + "/chat/completions";
    }

    private static string BuildSystemPrompt(WorkflowMode mode, SearchPacket? search)
    {
        var role = mode switch
        {
            WorkflowMode.Answer =>
                "你是一个严谨、直接的中文 AI 助手。优先给出可执行、准确、简洁的回答。",
            WorkflowMode.Explain =>
                "你是一位善于表达的中文教师。用清晰、通俗的方式解释概念，按必要性给出定义、背景、例子和易错点。",
            WorkflowMode.TrackNews =>
                "你是中文新闻分析助手。根据提供的最新检索资料整理事件动态，按重要性组织内容，区分事实、背景和可能影响。",
            WorkflowMode.Think =>
                "你是严谨的中文分析助手。先给出不超过六点的分析要点，再明确写出最终结论。不要虚构不确定信息。",
            WorkflowMode.ExplainOnline =>
                "你是中文研究型解释助手。结合提供的联网检索资料解释问题，明确区分资料事实、推断和你的结论。",
            _ => "你是一个中文 AI 助手。"
        };

        if (search is null)
        {
            return role + "\n使用 Markdown 适度组织回答；如信息不确定，请明确说明。";
        }

        var sources = search.Sources
            .Select((source, index) =>
                $"[{index + 1}] {source.Title}\n时间：{source.PublishedDate ?? "未知"}\n链接：{source.Url}\n摘要：{source.Content}")
            .ToArray();

        return role +
               "\n请只把下列资料作为外部事实来源，并用 [1]、[2] 标注来源编号；若资料不足请明确说明。" +
               "\n\n检索主题：" + search.Query +
               "\n\n联网资料：\n" + string.Join("\n\n", sources);
    }

    private static string Shorten(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 320 ? normalized : normalized[..320] + "…";
    }

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ChatRequest(
        string Model,
        ChatMessage[] Messages,
        double Temperature);

    private sealed record ChatResponse(List<ChatChoice>? Choices);

    private sealed record ChatChoice(ChatMessage Message);
}
