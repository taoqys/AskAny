using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 用假 HttpMessageHandler 驱动真实的请求构造与响应解析，不需要网络与真实密钥。
// zhida 与 OpenAI 兼容，response 形状一致：choices[0].message.content + reasoning_content。
public class AiServiceParsingTests
{
    private const string ChatCompletionsPayload = """
        {"choices":[{"message":{"role":"assistant","content":"答案正文","reasoning_content":"推理正文"}}]}
        """;

    private const string ResponsesPayload = """
        {"output":[
          {"type":"message","content":[{"type":"output_text","text":"响应答案"}]},
          {"type":"reasoning","content":[{"type":"reasoning_text","text":"响应推理"}]}
        ]}
        """;

    private static ProviderConfig ChatProvider()
    {
        return new ProviderConfig
        {
            Id = "p",
            Name = "P",
            Protocol = ApiProtocol.ChatCompletions,
            BaseUri = "https://example.com/v1",
            Models = { "m" },
            SelectedModel = "m",
            SupportsReasoningControl = true,
            ReasoningEffort = "high"
        };
    }

    private static ProviderConfig ResponsesProvider()
    {
        var provider = ChatProvider();
        provider.Protocol = ApiProtocol.Responses;
        return provider;
    }

    private static FunctionOption FunctionFor(WorkflowMode mode)
    {
        var created = FunctionCatalog.CreateDefaultFunctions()
            .FirstOrDefault(function => function.Mode == mode);
        if (created is not null)
        {
            return created;
        }

        return new FunctionOption { Mode = mode, SystemPrompt = "提示词" };
    }

    [Fact]
    public async Task ChatCompletionsContentAndReasoningAreParsed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        var service = new AiService(new HttpClient(handler));

        var result = await service.ExecuteAsync(
            FunctionFor(WorkflowMode.Answer),
            "问题",
            ChatProvider(),
            "key",
            null);

        Assert.Equal("答案正文", result.Answer);
        Assert.Equal("推理正文", result.Reasoning);
    }

    [Fact]
    public async Task ResponsesOutputAndReasoningAreParsed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ResponsesPayload);
        var service = new AiService(new HttpClient(handler));

        var result = await service.ExecuteAsync(
            FunctionFor(WorkflowMode.Think),
            "问题",
            ResponsesProvider(),
            "key",
            null);

        Assert.Equal("响应答案", result.Answer);
        Assert.Equal("响应推理", result.Reasoning);
    }

    [Fact]
    public async Task EndpointSuffixDependsOnProtocol()
    {
        var chatHandler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(chatHandler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Answer), "问题", ChatProvider(), "key", null);
        Assert.EndsWith("/chat/completions", chatHandler.LastRequestUri!.AbsoluteUri);

        var responsesHandler = new StubHttpMessageHandler(HttpStatusCode.OK, ResponsesPayload);
        await new AiService(new HttpClient(responsesHandler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Think), "问题", ResponsesProvider(), "key", null);
        Assert.EndsWith("/responses", responsesHandler.LastRequestUri!.AbsoluteUri);
    }

    // 非思考模式必须显式关闭推理，否则 DeepSeek 这类默认开思考的提供商会一直思考。
    [Fact]
    public async Task NonThinkingFunctionExplicitlyDisablesReasoning()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(handler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Answer), "问题", ChatProvider(), "key", null);

        Assert.Contains("\"reasoning_effort\":\"none\"", handler.LastRequestBody);
        Assert.Contains("\"temperature\":0.6", handler.LastRequestBody);
    }

    // 只有「深度思考」请求扩展推理，并使用配置的强度。
    [Fact]
    public async Task ThinkingFunctionRequestsConfiguredReasoningEffort()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(handler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Think), "问题", ChatProvider(), "key", null);

        Assert.Contains("\"reasoning_effort\":\"high\"", handler.LastRequestBody);
        Assert.DoesNotContain("\"temperature\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task UnsupportedReasoningControlSendsNoReasoningField()
    {
        var provider = ChatProvider();
        provider.SupportsReasoningControl = false;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(handler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Answer), "问题", provider, "key", null);

        Assert.DoesNotContain("reasoning_effort", handler.LastRequestBody);
    }

    [Fact]
    public async Task BearerTokenIsAttached()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(handler)).ExecuteAsync(
            FunctionFor(WorkflowMode.Answer), "问题", ChatProvider(), "secret-key", null);

        Assert.Equal("Bearer", handler.LastRequestHeaders!.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.LastRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public async Task HttpErrorSurfacesStatusCodeAndBody()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"bad key"}}""");
        var service = new AiService(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(
                FunctionFor(WorkflowMode.Answer), "问题", ChatProvider(), "key", null));

        Assert.Contains("401", exception.Message);
        Assert.Contains("bad key", exception.Message);
    }

    [Fact]
    public async Task EmptyPromptIsRejectedBeforeSending()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        var service = new AiService(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(
                FunctionFor(WorkflowMode.Answer), "   ", ChatProvider(), "key", null));
    }

    [Fact]
    public async Task MissingModelIsRejectedBeforeSending()
    {
        var provider = ChatProvider();
        provider.SelectedModel = string.Empty;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        var service = new AiService(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecuteAsync(
                FunctionFor(WorkflowMode.Answer), "问题", provider, "key", null));
    }

    // 检索结果会被拼进系统提示词，并要求模型用 [1][2] 标注来源。
    // 注意：System.Text.Json 默认会把非 ASCII 转义成 \uXXXX，
    // 所以不能直接在序列化后的 body 里找中文，必须解析出来再断言。
    [Fact]
    public async Task SearchResultsAreInjectedIntoSystemPrompt()
    {
        var search = new SearchPacket("查询", new[]
        {
            new SearchSource("来源标题", "https://example.com/a", "来源摘要", "2025-01-01")
        });

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatCompletionsPayload);
        await new AiService(new HttpClient(handler)).ExecuteAsync(
            FunctionFor(WorkflowMode.SearchNetwork), "问题", ChatProvider(), "key", search);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var systemPrompt = document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString();

        Assert.NotNull(systemPrompt);
        Assert.Contains("来源标题", systemPrompt);
        Assert.Contains("https://example.com/a", systemPrompt);
        Assert.Contains("[1]", systemPrompt);
    }
}
