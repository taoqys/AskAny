using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AskAny.Models;

namespace AskAny.Services;

// 从供应商拉取「模型列表」，并顺带给出能力推断。
//
// 参照 Cherry Studio 的做法：优先拉供应商自己的 /models（最权威的实时列表）。
// URL 规则也照抄它的思路 —— 基址带版本段（/v1、/paas/v2 之类）就直接拼 /models，
// 否则再补一个 /v1/models 作为候选；基址里若已经带了 /chat/completions、/responses
// 这类操作路径，先剥掉再拼（例如 OpenRouter 的基址）。
public sealed class ModelCatalogService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly string[] OperationSuffixes =
    [
        "/chat/completions",
        "/completions",
        "/responses"
    ];

    private readonly HttpClient _httpClient;

    public ModelCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public sealed record ModelListResult(
        IReadOnlyList<string> Models,
        IReadOnlyList<string> VisionModels,
        IReadOnlyList<string> ReasoningModels);

    public async Task<ModelListResult> FetchAsync(
        ProviderConfig provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildModelEndpoints(provider.BaseUri);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException("请先在设置中填写接口地址。");
        }

        foreach (var endpoint in candidates)
        {
            var payload = await TryGetAsync(endpoint, apiKey, cancellationToken);
            if (payload is null)
            {
                // 该路径不存在，换下一个候选
                continue;
            }

            var models = ParseModelIds(payload);
            if (models.Count == 0)
            {
                throw new InvalidOperationException($"{endpoint} 返回了空的模型列表。");
            }

            var visionModels = new List<string>();
            var reasoningModels = new List<string>();
            foreach (var model in models)
            {
                var capability = ModelCapabilities.Infer(model);
                if (capability.SupportsVision)
                {
                    visionModels.Add(model);
                }

                if (capability.SupportsReasoning)
                {
                    reasoningModels.Add(model);
                }
            }

            return new ModelListResult(models, visionModels, reasoningModels);
        }

        throw new InvalidOperationException(
            $"该提供商没有提供模型列表接口（已尝试 {string.Join("、", candidates)}），请手动填写模型列表。");
    }

    // 返回 null 表示该地址上没有模型列表接口（404/405），应换下一个候选；
    // 其它失败直接抛出，避免把「密钥错了」误当成「接口不存在」。
    private async Task<string?> TryGetAsync(
        string endpoint,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);

        if (response.StatusCode is HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(HttpFailure.Describe(response.StatusCode, payload));
        }

        return payload;
    }

    internal static IReadOnlyList<string> BuildModelEndpoints(string baseUri)
    {
        var normalized = (baseUri ?? string.Empty).Trim().TrimEnd('/');
        if (normalized.Length == 0)
        {
            return [];
        }

        foreach (var suffix in OperationSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return EndsWithVersionSegment(normalized)
            ? [normalized + "/models"]
            : [normalized + "/models", normalized + "/v1/models"];
    }

    private static bool EndsWithVersionSegment(string value)
    {
        var slash = value.LastIndexOf('/');
        if (slash < 0 || slash == value.Length - 1)
        {
            return false;
        }

        var segment = value[(slash + 1)..];
        return segment.Length >= 2 &&
               (segment[0] == 'v' || segment[0] == 'V') &&
               segment.Skip(1).All(char.IsDigit);
    }

    internal static IReadOnlyList<string> ParseModelIds(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        JsonElement array = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (TryGetProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            array = data;
        }
        else if (TryGetProperty(root, "models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            array = models;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in array.EnumerateArray())
        {
            string? id = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Object => ReadId(element),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            id = id.Trim();
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static string? ReadId(JsonElement element)
    {
        foreach (var name in new[] { "id", "name", "model" })
        {
            if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    // JsonElement.TryGetProperty 区分大小写，部分服务端会返回 Id / Name。
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }
}
