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

    public async Task<AiResult> ExecuteAsync(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string apiKey,
        SearchPacket? search,
        IReadOnlyList<ConversationTurn>? conversation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("请先输入问题。");
        }

        if (string.IsNullOrWhiteSpace(provider.SelectedModel))
        {
            throw new InvalidOperationException("请先在窗口底部选择模型。");
        }

        var systemPrompt = BuildSystemPrompt(function, search);
        var endpoint = BuildEndpoint(provider.BaseUri, provider.Protocol);
        var requestBody = BuildRequestBody(
            function,
            prompt,
            provider,
            systemPrompt,
            conversation);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonDefaults.Compact),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"模型请求失败（{(int)response.StatusCode}）：{Shorten(payload)}");
        }

        return provider.Protocol switch
        {
            ApiProtocol.Responses => ParseResponses(payload),
            _ => ParseChatCompletions(payload)
        };
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
            cancellationToken);
    }

    private static object BuildRequestBody(
        FunctionOption function,
        string prompt,
        ProviderConfig provider,
        string systemPrompt,
        IReadOnlyList<ConversationTurn>? conversation)
    {
        var isThinking = function.Mode == WorkflowMode.Think;

        if (provider.Protocol == ApiProtocol.Responses)
        {
            var input = new List<object>();
            foreach (var turn in conversation ?? [])
            {
                if (turn.Role is "user" or "assistant" &&
                    !string.IsNullOrWhiteSpace(turn.Content))
                {
                    input.Add(new Dictionary<string, object?>
                    {
                        ["role"] = turn.Role,
                        ["content"] = turn.Content
                    });
                }
            }

            input.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = prompt
            });

            var body = new Dictionary<string, object?>
            {
                ["model"] = provider.SelectedModel.Trim(),
                ["instructions"] = systemPrompt,
                ["input"] = input
            };

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
            new ChatMessage("system", systemPrompt)
        };
        foreach (var turn in conversation ?? [])
        {
            if (turn.Role is "user" or "assistant" &&
                !string.IsNullOrWhiteSpace(turn.Content))
            {
                messages.Add(new ChatMessage(turn.Role, turn.Content));
            }
        }

        messages.Add(new ChatMessage("user", prompt));

        var chatBody = new Dictionary<string, object?>
        {
            ["model"] = provider.SelectedModel.Trim(),
            ["messages"] = messages
        };

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
