using AskAny.Models;

namespace AskAny.Services;

public sealed record SearchSource(
    string Title,
    string Url,
    string Content,
    string? PublishedDate);

public sealed record SearchPacket(
    string Query,
    IReadOnlyList<SearchSource> Sources);

public sealed class SearchService
{
    private const string SearchEndpoint = "https://api.tavily.com/search";
    private readonly HttpClient _httpClient;

    public SearchService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<SearchPacket> SearchAsync(
        string query,
        bool news,
        string tavilyApiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tavilyApiKey))
        {
            throw new InvalidOperationException("尚未配置 Tavily API Key，无法使用新闻追踪或联网解释。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint);
        request.Headers.Authorization = new("Bearer", tavilyApiKey.Trim());

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["search_depth"] = "advanced",
            ["max_results"] = 6,
            ["include_answer"] = false,
            ["include_raw_content"] = false
        };

        if (news)
        {
            body["topic"] = "news";
            body["days"] = 7;
        }
        else
        {
            body["topic"] = "general";
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonDefaults.Compact),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Tavily 搜索失败（{(int)response.StatusCode}）：{Shorten(payload)}");
        }

        var result = JsonSerializer.Deserialize<TavilyResponse>(payload, JsonDefaults.Compact)
                     ?? new TavilyResponse([]);
        var sources = result.Results
            .Select(item => new SearchSource(
                item.Title ?? "未命名资料",
                item.Url ?? string.Empty,
                item.Content ?? string.Empty,
                item.PublishedDate))
            .ToArray();

        return new SearchPacket(query, sources);
    }

    private static string Shorten(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240] + "…";
    }

    private sealed record TavilyResponse(List<TavilyResult> Results);

    private sealed record TavilyResult(
        string? Title,
        string? Url,
        string? Content,
        string? PublishedDate);
}
