using System.Globalization;
using System.Net.Http.Headers;
using AskAny.Models;

namespace AskAny.Services;

public sealed record SearchSource(
    string Title,
    string Url,
    string Content,
    [property: JsonPropertyName("published_date")] string? PublishedDate);

public sealed record SearchPacket(
    string Query,
    IReadOnlyList<SearchSource> Sources);

public sealed class SearchService
{
    private const string SearchEndpoint = "https://api.tavily.com/search";
    private const string ZhihuSearchEndpoint = "https://developer.zhihu.com/api/v1/content/zhihu_search";

    // 官方文档：Count 默认 10，最大 10，超出会被服务端截断。
    private const int ZhihuMaxResults = 10;
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

    // 知乎站内搜索。与 Tavily 的差异：需要 Bearer + 秒级时间戳两个头，
    // 返回体是 PascalCase 自有信封（Code / Message / Data.Items），且 Count 上限为 10。
    public async Task<SearchPacket> SearchZhihuAsync(
        string query,
        string accessSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessSecret))
        {
            throw new InvalidOperationException("尚未配置知乎 Access Secret，无法使用知乎搜索。");
        }

        var requestUri =
            $"{ZhihuSearchEndpoint}?Query={Uri.EscapeDataString(query)}&Count={ZhihuMaxResults}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessSecret.Trim());
        request.Headers.TryAddWithoutValidation(
            "X-Request-Timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"知乎搜索失败（{(int)response.StatusCode}）：{Shorten(payload)}");
        }

        var envelope = JsonSerializer.Deserialize<ZhihuEnvelope>(payload, JsonDefaults.Compact)
                       ?? new ZhihuEnvelope(0, null, null);

        if (envelope.Code != 0)
        {
            throw new InvalidOperationException(DescribeZhihuError(envelope));
        }

        var sources = (envelope.Data?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .Select(item => new SearchSource(
                string.IsNullOrWhiteSpace(item.Title) ? "未命名内容" : item.Title!,
                item.Url!,
                item.ContentText ?? string.Empty,
                FormatEditTime(item.EditTime)))
            .ToArray();

        return new SearchPacket(query, sources);
    }

    // 20001 既可能是密钥错，也可能是本机时钟偏差超过 10 分钟，必须把两种成因都提示出来。
    private static string DescribeZhihuError(ZhihuEnvelope envelope)
    {
        var message = string.IsNullOrWhiteSpace(envelope.Message) ? "未知错误" : envelope.Message;

        return envelope.Code switch
        {
            20001 => $"知乎鉴权失败（20001）：{message}。请检查 Access Secret，" +
                     "并确认本机时间准确——时间戳与服务端相差超过 10 分钟也会返回 20001。",
            30001 => $"知乎接口触发频率限制（30001）：{message}",
            _ => $"知乎搜索失败（{envelope.Code}）：{message}"
        };
    }

    private static string? FormatEditTime(int unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                .ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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
        [property: JsonPropertyName("published_date")] string? PublishedDate);

    private sealed record ZhihuEnvelope(
        int Code,
        string? Message,
        ZhihuData? Data);

    private sealed record ZhihuData(List<ZhihuItem>? Items);

    private sealed record ZhihuItem(
        string? Title,
        string? ContentText,
        string? Url,
        int EditTime);
}
