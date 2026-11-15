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
        bool thinkingEnabled = false,
        CancellationToken cancellationToken = default)
    {
        Validate(function, prompt, provider);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var request = BuildRequest(
            function, prompt, provider, apiKey, search, images, conversation,
            stream: false, thinkingEnabled, includeStreamUsage: false);

        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(HttpFailure.Describe(response.StatusCode, payload));
        }

        stopwatch.Stop();

        // 非流式没有「首字」语义，整体耗时即首字时间。
        return provider.Protocol switch
        {
            ApiProtocol.Responses => ParseResponses(payload, stopwatch.Elapsed),
            _ => ParseChatCompletions(payload, stopwatch.Elapsed)
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
        bool thinkingEnabled = false,
        CancellationToken cancellationToken = default)
    {
        Validate(function, prompt, provider);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(StreamingHeaderTimeout);

        // stream_options.include_usage 能拿到精确 token 数，但不是所有 OpenAI 兼容实现都认。
        // 先带着发；如果被 400 拒绝，去掉它重试一次 —— 不能因为要多一个统计字段就让整条请求失败。
        var response = await SendStreamingAsync(
            function, prompt, provider, apiKey, search, images, conversation, thinkingEnabled,
            includeStreamUsage: true, headerTimeout.Token);

        if (response.StatusCode == HttpStatusCode.BadRequest &&
            provider.Protocol == ApiProtocol.ChatCompletions)
        {
            var rejected = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            ErrorLog.Write($"提供商不接受 stream_options，去掉后重试：{HttpFailure.Shorten(rejected, 160)}");

            response = await SendStreamingAsync(
                function, prompt, provider, apiKey, search, images, conversation, thinkingEnabled,
                includeStreamUsage: false, headerTimeout.Token);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    HttpFailure.Describe(response.StatusCode, errorPayload));
            }

            return await ReadStreamAsync(
                response, provider.Protocol, onDelta, stopwatch, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string apiKey,
        SearchPacket? search,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<ConversationTurn>? conversation,
        bool thinkingEnabled,
        bool includeStreamUsage,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(
            function, prompt, provider, apiKey, search, images, conversation,
            stream: true, thinkingEnabled, includeStreamUsage);

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    // 读 SSE：边读边回调，同时记录首字时间与用量。
    private async Task<AiResult> ReadStreamAsync(
        HttpResponseMessage response,
        ApiProtocol protocol,
        Action<string, string> onDelta,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bodyTimeout.CancelAfter(StreamingBodyTimeout);

        await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        TokenUsage? usage = null;
        TimeSpan? timeToFirstToken = null;

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

            var chunk = ParseStreamChunk(protocol, payload);

            // 用量分片可能没有正文（chat completions 的最后一个分片就是这种），
            // 所以不能因为「没有正文」就跳过。
            if (chunk.Usage is { HasAny: true })
            {
                usage = chunk.Usage;
            }

            if (!string.IsNullOrEmpty(chunk.Content))
            {
                timeToFirstToken ??= stopwatch.Elapsed;
                content.Append(chunk.Content);
                onDelta(chunk.Content, string.Empty);
            }

            if (!string.IsNullOrEmpty(chunk.Reasoning))
            {
                timeToFirstToken ??= stopwatch.Elapsed;
                reasoning.Append(chunk.Reasoning);
                onDelta(string.Empty, chunk.Reasoning);
            }
        }

        stopwatch.Stop();

        if (content.Length == 0 && reasoning.Length == 0)
        {
            throw new InvalidOperationException("流式响应没有返回任何内容。");
        }

        return new AiResult(
            content.Length == 0 ? "模型没有返回文本内容。" : content.ToString().Trim(),
            reasoning.Length == 0 ? null : reasoning.ToString().Trim(),
            usage,
            timeToFirstToken ?? stopwatch.Elapsed);
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
            thinkingEnabled: false,
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
        bool stream,
        bool thinkingEnabled,
        bool includeStreamUsage)
    {
        var systemPrompt = BuildSystemPrompt(function, search);
        var endpoint = BuildEndpoint(provider.BaseUri, provider.Protocol);
        var requestBody = BuildRequestBody(
            function, prompt, provider, systemPrompt, images, conversation, stream,
            thinkingEnabled, includeStreamUsage);

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

    private readonly record struct StreamChunk(
        string? Content,
        string? Reasoning,
        TokenUsage? Usage);

    private static StreamChunk ParseStreamChunk(
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

                // Responses 的用量在结束事件里：{"type":"response.completed","response":{"usage":{...}}}
                if (type == "response.completed" &&
                    root.TryGetProperty("response", out var completedResponse) &&
                    completedResponse.TryGetProperty("usage", out var completedUsage))
                {
                    return new StreamChunk(null, null, ParseResponsesUsage(completedUsage));
                }

                var delta = ReadString(root, "delta");
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(delta))
                {
                    return default;
                }

                if (type == "response.output_text.delta")
                {
                    return new StreamChunk(delta, null, null);
                }

                // 推理分片的事件名在不同提供商之间不完全一致，
                // 统一按「含 reasoning 且以 .delta 结尾」匹配。
                if (type.Contains("reasoning", StringComparison.Ordinal) &&
                    type.EndsWith(".delta", StringComparison.Ordinal))
                {
                    return new StreamChunk(null, delta, null);
                }

                return default;
            }

            // Chat Completions：用量在最后一个分片的 usage 里（可能没有 choices）。
            TokenUsage? streamUsage = null;
            if (root.TryGetProperty("usage", out var chatUsage) &&
                chatUsage.ValueKind == JsonValueKind.Object)
            {
                streamUsage = ParseChatUsage(chatUsage);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return new StreamChunk(null, null, streamUsage);
            }

            if (!choices[0].TryGetProperty("delta", out var chatDelta))
            {
                return new StreamChunk(null, null, streamUsage);
            }

            // reasoning_content 是 DeepSeek 的字段名，部分兼容实现用 reasoning。
            return new StreamChunk(
                ReadString(chatDelta, "content"),
                ReadString(chatDelta, "reasoning_content") ?? ReadString(chatDelta, "reasoning"),
                streamUsage);
        }
        catch (JsonException)
        {
            // 无法解析的分片直接忽略，不要因此中断整条流。
            return default;
        }
    }

    private static TokenUsage? ParseChatUsage(JsonElement usage)
    {
        var prompt = ReadInt(usage, "prompt_tokens");
        var completion = ReadInt(usage, "completion_tokens");
        return prompt == 0 && completion == 0 ? null : new TokenUsage(prompt, completion);
    }

    private static TokenUsage? ParseResponsesUsage(JsonElement usage)
    {
        var input = ReadInt(usage, "input_tokens");
        var output = ReadInt(usage, "output_tokens");
        return input == 0 && output == 0 ? null : new TokenUsage(input, output);
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : 0;
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
        bool stream,
        bool thinkingEnabled,
        bool includeStreamUsage)
    {
        // 思考现在是独立开关。老配置里若还留着「深度思考」功能项，也按开启处理。
        var isThinking = thinkingEnabled || function.Mode == WorkflowMode.Think;

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
                    ["effort"] = ResolveReasoningEffort(provider, thinking: false)
                };
            }
            else if (provider.SupportsReasoningControl)
            {
                body["reasoning"] = new Dictionary<string, object?>
                {
                    ["effort"] = ResolveReasoningEffort(provider, thinking: true)
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

            // 精确 token 用量要靠这个开关；不认它的提供商会 400，上层会去掉重试。
            if (includeStreamUsage)
            {
                chatBody["stream_options"] = new Dictionary<string, object?>
                {
                    ["include_usage"] = true
                };
            }
        }

        if (!isThinking)
        {
            chatBody["temperature"] = 0.6;
            if (provider.SupportsReasoningControl)
            {
                chatBody["reasoning_effort"] = ResolveReasoningEffort(provider, thinking: false);
            }
        }
        else if (provider.SupportsReasoningControl)
        {
            chatBody["reasoning_effort"] = ResolveReasoningEffort(provider, thinking: true);
        }

        return chatBody;
    }

    // 思考开关对应的推理强度：
    //   ON  → 用设置里的强度（默认 high）
    //   OFF → 能关就发 none；「始终思考」的模型拒绝 none（智谱 glm-5.3-flashx 会返回 1210），
    //         退到它能接受的最小强度 low。
    private static string ResolveReasoningEffort(ProviderConfig provider, bool thinking)
    {
        if (thinking)
        {
            return NormalizeReasoningEffort(provider.ReasoningEffort);
        }

        return ModelCapabilities.Infer(provider.SelectedModel).CanDisableReasoning == false
            ? "low"
            : "none";
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

    private static AiResult ParseChatCompletions(string payload, TimeSpan elapsed)
    {
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(payload, JsonDefaults.Compact);
        var message = chatResponse?.Choices?.FirstOrDefault()?.Message;
        var content = message?.Content?.Trim();
        var reasoning = message?.ReasoningContent?.Trim();
        return new AiResult(
            string.IsNullOrWhiteSpace(content) ? "模型没有返回文本内容。" : content,
            string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
            chatResponse?.Usage?.ToTokenUsage(),
            elapsed);
    }

    private static AiResult ParseResponses(string payload, TimeSpan elapsed)
    {
        var response = JsonSerializer.Deserialize<ResponsesEnvelope>(payload, JsonDefaults.Compact);
        if (response?.Output is null || response.Output.Count == 0)
        {
            return new AiResult("模型没有返回文本内容。", null, response?.Usage?.ToTokenUsage(), elapsed);
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
                : string.Join(Environment.NewLine + Environment.NewLine, reasoningParts),
            response.Usage?.ToTokenUsage(),
            elapsed);
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

    private sealed record ChatMessage(
        string Role,
        string Content,
        [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null);

    private sealed record ChatResponse(List<ChatChoice>? Choices, ChatUsage? Usage);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens)
    {
        public TokenUsage? ToTokenUsage()
        {
            var prompt = PromptTokens ?? 0;
            var completion = CompletionTokens ?? 0;
            return prompt == 0 && completion == 0 ? null : new TokenUsage(prompt, completion);
        }
    }

    private sealed record ChatChoice(ChatMessage Message);

    private sealed record ResponsesEnvelope(List<ResponseOutputItem>? Output, ResponsesUsage? Usage);

    private sealed record ResponsesUsage(
        [property: JsonPropertyName("input_tokens")] int? InputTokens,
        [property: JsonPropertyName("output_tokens")] int? OutputTokens)
    {
        public TokenUsage? ToTokenUsage()
        {
            var input = InputTokens ?? 0;
            var output = OutputTokens ?? 0;
            return input == 0 && output == 0 ? null : new TokenUsage(input, output);
        }
    }

    private sealed record ResponseOutputItem(
        string? Type,
        List<ResponseContent>? Content);

    private sealed record ResponseContent(
        string? Type,
        string? Text);
}
