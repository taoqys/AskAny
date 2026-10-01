using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using AskAny.Models;

namespace AskAny.Services;

public sealed class AiService
{
    // HttpClient 自身的 Timeout 会覆盖整个响应读取过程，流式长答案会被它掐断，
    // 因此 App 把 Timeout 设为无限，改由这里按请求类型分别控制。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StreamingHeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamingBodyTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;

    public AiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AiResult> ExecuteAsync(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string apiKey,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment>? images = null,
        IReadOnlyList<ConversationTurn>? conversation = null,
        CancellationToken cancellationToken = default)
    {
        Validate(function, prompt, provider);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = BuildRequest(
            function, prompt, provider, apiKey, search, images, conversation, stream: false);

        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeFailure(response.StatusCode, payload));
        }

        return provider.Protocol switch
        {
            ApiProtocol.Responses => ParseResponses(payload),
            _ => ParseChatCompletions(payload)
        };
    }

    // 流式请求。onDelta(内容增量, 推理增量) 会被频繁调用（每收到一个分片一次），
    // 调用方应自行做节流渲染。失败时直接抛异常，由调用方决定是否回退到非流式。
    public async Task<AiResult> ExecuteStreamingAsync(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string apiKey,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<ConversationTurn>? conversation,
        Action<string, string> onDelta,
        CancellationToken cancellationToken = default)
    {
        Validate(function, prompt, provider);

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(StreamingHeaderTimeout);

        using var request = BuildRequest(
            function, prompt, provider, apiKey, search, images, conversation, stream: true);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            headerTimeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(DescribeFailure(response.StatusCode, errorPayload));
        }

        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeout.CancelAfter(StreamingBodyTimeout);

        await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(bodyTimeout.Token);
            if (line is null)
            {
                break;
            }

            // 只处理 data: 行：心跳注释（": keep-alive"）与 event: 行直接跳过。
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload.Equals("[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            var (deltaContent, deltaReasoning) = ParseStreamChunk(provider.Protocol, payload);

            if (!string.IsNullOrEmpty(deltaContent))
            {
                content.Append(deltaContent);
                onDelta(deltaContent, string.Empty);
            }

            if (!string.IsNullOrEmpty(deltaReasoning))
            {
                reasoning.Append(deltaReasoning);
                onDelta(string.Empty, deltaReasoning);
            }
        }

        if (content.Length == 0 && reasoning.Length == 0)
        {
            throw new InvalidOperationException("流式响应没有返回任何内容。");
        }

        return new AiResult(
            content.Length == 0 ? "模型没有返回文本内容。" : content.ToString().Trim(),
            reasoning.Length == 0 ? null : reasoning.ToString().Trim());
    }

    public async Task ValidateAsync(
        ProviderConfig provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            FunctionCatalog.CreateDefaultFunctions()[0],
            "只回复“连接成功”四个字。",
            provider,
            apiKey,
            null,
            null,
            null,
            cancellationToken);
    }

    private static void Validate(FunctionOption function, string prompt, ProviderConfig provider)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("请先输入问题。");
        }

        if (string.IsNullOrWhiteSpace(provider.SelectedModel))
        {
            throw new InvalidOperationException("请先在窗口底部选择模型。");
        }
    }

    private HttpRequestMessage BuildRequest(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string apiKey,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<ConversationTurn>? conversation,
        bool stream)
    {
        var systemPrompt = BuildSystemPrompt(function, search);
        var endpoint = BuildEndpoint(provider.BaseUri, provider.Protocol);
        var requestBody = BuildRequestBody(
            function, prompt, provider, systemPrompt, images, conversation, stream);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonDefaults.Compact),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    // 把 HTTP 失败翻译成「下一步该做什么」，而不是把原始 payload 甩给用户。
    private static string DescribeFailure(HttpStatusCode status, string payload)
    {
        var code = (int)status;
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "认证失败：请到「设置 → 模型服务」检查 API Key。",
            HttpStatusCode.NotFound =>
                "接口地址或模型不存在：请检查「接口地址」与模型名。",
            HttpStatusCode.TooManyRequests =>
                "触发限流或额度不足：稍后重试，或检查账户额度。",
            HttpStatusCode.RequestEntityTooLarge =>
                "请求内容过大：减少图片或开启新对话后重试。",
            HttpStatusCode.BadRequest =>
                "请求被拒绝：模型名或参数可能不被该提供商支持。",
            _ when code >= 500 =>
                "服务端错误：稍后重试。",
            _ => null
        };

        var detail = Shorten(payload);
        return hint is null
            ? $"模型请求失败（{code}）：{detail}"
            : $"模型请求失败（{code}）：{hint}　原始响应：{detail}";
    }

    private static (string? Content, string? Reasoning) ParseStreamChunk(
        ApiProtocol protocol,
        string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 流中途的错误：Chat Completions 形如 {"error":{...}}，
            // Responses 形如 {"type":"error",...} 或 response.failed。
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "流式响应中断：" + (ReadString(error, "message") ?? "未知错误"));
            }

            if (protocol == ApiProtocol.Responses)
            {
                var type = ReadString(root, "type");
                if (type == "response.failed")
                {
                    throw new InvalidOperationException("流式响应失败：模型或服务端返回了失败事件。");
                }

                var delta = ReadString(root, "delta");
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(delta))
                {
                    return (null, null);
                }

                if (type == "response.output_text.delta")
                {
                    return (delta, null);
                }

                // 推理分片的事件名在不同提供商之间不完全一致，
                // 统一按「含 reasoning 且以 .delta 结尾」匹配。
                if (type.Contains("reasoning", StringComparison.Ordinal) &&
                    type.EndsWith(".delta", StringComparison.Ordinal))
                {
                    return (null, delta);
                }

                return (null, null);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return (null, null);
            }

            if (!choices[0].TryGetProperty("delta", out var chatDelta))
            {
                return (null, null);
            }

            // reasoning_content 是 DeepSeek 的字段名，部分兼容实现用 reasoning。
            return (
                ReadString(chatDelta, "content"),
                ReadString(chatDelta, "reasoning_content") ?? ReadString(chatDelta, "reasoning"));
        }
        catch (JsonException)
        {
            // 无法解析的分片直接忽略，不要因此中断整条流。
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static object BuildRequestBody(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string systemPrompt,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<ConversationTurn>? conversation,
        bool stream)
    {
        var isThinking = function.Mode == WorkflowMode.Think;

        if (provider.Protocol == ApiProtocol.Responses)
        {
            var input = new List<object>();
            foreach (var turn in conversation ?? [])
            {
                if (turn.Role is not ("user" or "assistant") ||
                    string.IsNullOrWhiteSpace(turn.Content))
                {
                    continue;
                }

                input.Add(new Dictionary<string, object?>
                {
                    ["role"] = turn.Role,
                    // 历史轮次不再重复携带图片。图片是内联 base64，多轮追问会把同一张图
                    // 反复按全量重发；图片只在该轮发送一次。
                    ["content"] = turn.Content
                });
            }

            input.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = BuildResponsesContent(prompt, images)
            });

            var body = new Dictionary<string, object?>
            {
                ["model"] = provider.SelectedModel.Trim(),
                ["instructions"] = systemPrompt,
                ["input"] = input
            };

            if (stream)
            {
                body["stream"] = true;
            }

            if (!isThinking)
            {
                body["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = "none"
                };
            }
            else if (provider.SupportsReasoningControl)
            {
                body["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = NormalizeReasoningEffort(provider.ReasoningEffort)
                };
            }

            if (!isThinking)
            {
                body["temperature"] = 0.6;
            }

            return body;
        }

        var messages = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };
        foreach (var turn in conversation ?? [])
        {
            if (turn.Role is not ("user" or "assistant") ||
                string.IsNullOrWhiteSpace(turn.Content))
            {
                continue;
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = turn.Role,
                // 同上：历史轮次只带文本，图片仅在该轮发送。
                ["content"] = turn.Content
            });
        }

        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = BuildChatContent(prompt, images)
        });

        var chatBody = new Dictionary<string, object?>
        {
            ["model"] = provider.SelectedModel.Trim(),
            ["messages"] = messages
        };

        if (stream)
        {
            chatBody["stream"] = true;
        }

        if (!isThinking)
        {
            chatBody["temperature"] = 0.6;
            if (provider.SupportsReasoningControl)
            {
                chatBody["reasoning_effort"] = "none";
            }
        }
        else if (provider.SupportsReasoningControl)
        {
            chatBody["reasoning_effort"] = NormalizeReasoningEffort(provider.ReasoningEffort);
        }

        return chatBody;
    }

    private static object BuildResponsesContent(
        string text,
        IReadOnlyList<ImageAttachment>? images)
    {
        if (images is null || images.Count == 0)
        {
            return text;
        }

        var content = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "input_text",
                ["text"] = text
            }
        };
        content.AddRange(images.Select(image => new Dictionary<string, object?>
        {
            ["type"] = "input_image",
            ["image_url"] = ToDataUrl(image)
        }));
        return content;
    }

    private static object BuildChatContent(
        string text,
        IReadOnlyList<ImageAttachment>? images)
    {
        if (images is null || images.Count == 0)
        {
            return text;
        }

        var content = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            }
        };
        content.AddRange(images.Select(image => new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = ToDataUrl(image)
            }
        }));
        return content;
    }

    private static string ToDataUrl(ImageAttachment image)
    {
        return $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Bytes)}";
    }

    private static AiResult ParseChatCompletions(string payload)
    {
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(payload, JsonDefaults.Compact);
        var message = chatResponse?.Choices?.FirstOrDefault()?.Message;
        var content = message?.Content?.Trim();
        var reasoning = message?.ReasoningContent?.Trim();
        return new AiResult(
            string.IsNullOrWhiteSpace(content) ? "模型没有返回文本内容。" : content,
            string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
    }

    private static AiResult ParseResponses(string payload)
    {
        var response = JsonSerializer.Deserialize<ResponsesEnvelope>(payload, JsonDefaults.Compact);
        if (response?.Output is null || response.Output.Count == 0)
        {
            return new AiResult("模型没有返回文本内容。");
        }

        var answerParts = response.Output
            .Where(item => item.Type == "message")
            .SelectMany(item => item.Content ?? [])
            .Where(content => content.Type == "output_text" && !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => content.Text!.Trim())
            .ToArray();

        var answer = string.Join(Environment.NewLine + Environment.NewLine, answerParts);
        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = response.Output.FirstOrDefault()?.Content?.FirstOrDefault()?.Text?.Trim()
                     ?? "模型没有返回文本内容。";
        }

        var reasoningParts = response.Output
            .Where(item => item.Type == "reasoning")
            .SelectMany(item => item.Content ?? [])
            .Where(content => content.Type == "reasoning_text" && !string.IsNullOrWhiteSpace(content.Text))
            .Select(content => content.Text!.Trim())
            .ToArray();

        return new AiResult(
            answer,
            reasoningParts.Length == 0
                ? null
                : string.Join(Environment.NewLine + Environment.NewLine, reasoningParts));
    }

    private static string BuildEndpoint(string baseUri, ApiProtocol protocol)
    {
        if (string.IsNullOrWhiteSpace(baseUri))
        {
            throw new InvalidOperationException("请先在设置中填写接口地址。");
        }

        var normalized = baseUri.Trim().TrimEnd('/');
        var suffix = protocol == ApiProtocol.Responses ? "/responses" : "/chat/completions";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + suffix;
    }

    private static string NormalizeReasoningEffort(string effort)
    {
        return effort.Trim().ToLowerInvariant() switch
        {
            "none" => "none",
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "max" => "max",
            _ => "high"
        };
    }

    private static string BuildSystemPrompt(FunctionOption function, SearchPacket? search)
    {
        var role = string.IsNullOrWhiteSpace(function.SystemPrompt)
            ? FunctionCatalog.GetDefaultSystemPrompt(function.Mode)
            : function.SystemPrompt.Trim();

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

    private sealed record ChatMessage(
        string Role,
        string Content,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null);

    private sealed record ChatResponse(List<ChatChoice>? Choices);

    private sealed record ChatChoice(ChatMessage Message);

    private sealed record ResponsesEnvelope(List<ResponseOutputItem>? Output);

    private sealed record ResponseOutputItem(
        string? Type,
        List<ResponseContent>? Content);

    private sealed record ResponseContent(
        string? Type,
        string? Text);
}
