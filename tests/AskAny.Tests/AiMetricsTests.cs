using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 用量与首字时间是纯观测数据，但它们是「回答页指标」的唯一来源，解析错了就会显示假数字。
public class AiMetricsTests
{
    private const string ChatWithUsage =
        """
        {"choices":[{"message":{"role":"assistant","content":"答案"}}],
         "usage":{"prompt_tokens":123,"completion_tokens":45,"total_tokens":168}}
        """;

    private const string ResponsesWithUsage =
        """
        {"output":[{"type":"message","content":[{"type":"output_text","text":"答案"}]}],
         "usage":{"input_tokens":200,"output_tokens":30,"total_tokens":230}}
        """;

    private static AiService Build(HttpMessageHandler handler)
    {
        return new AiService(new HttpClient(handler));
    }

    private static Task<AiResult> SendChatWithUsageAsync()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ChatWithUsage);
        return Build(handler).ExecuteAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null);
    }

    [Fact]
    public async Task ChatCompletionsUsageIsParsed()
    {
        var result = await SendChatWithUsageAsync();

        Assert.NotNull(result.Usage);
        Assert.Equal(123, result.Usage!.PromptTokens);
        Assert.Equal(45, result.Usage.CompletionTokens);
        Assert.Equal(168, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task ResponsesUsageIsParsed()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ResponsesWithUsage);

        var result = await Build(handler).ExecuteAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ResponsesProvider(),
            "key",
            null);

        Assert.NotNull(result.Usage);
        Assert.Equal(200, result.Usage!.PromptTokens);
        Assert.Equal(30, result.Usage.CompletionTokens);
    }

    // 提供商没回 usage 时不能编一个数字出来。
    [Fact]
    public async Task MissingUsageStaysNull()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"答案"}}]}""");

        var result = await Build(handler).ExecuteAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer), "问题", AiFixture.ChatProvider(), "key", null);

        Assert.Null(result.Usage);
    }

    // 非流式没有「首字」语义，整体耗时即是它。
    [Fact]
    public async Task NonStreamingReportsElapsedAsTimeToFirstToken()
    {
        var result = await SendChatWithUsageAsync();

        Assert.NotNull(result.TimeToFirstToken);
        Assert.True(result.TimeToFirstToken >= TimeSpan.Zero);
    }

    [Fact]
    public async Task StreamingRequestsUsage()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            AiFixture.ServerSentEvents(
                """{"choices":[{"delta":{"content":"答"}}]}""",
                "[DONE]"),
            "text/event-stream");

        await Build(handler).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.Contains("stream_options", handler.LastRequestBody);
        Assert.Contains("include_usage", handler.LastRequestBody);
    }

    // 最后一个分片只带 usage、没有正文，也必须被收下。
    [Fact]
    public async Task ChatCompletionsStreamUsageIsParsed()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            AiFixture.ServerSentEvents(
                """{"choices":[{"delta":{"content":"答"}}]}""",
                """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
                "[DONE]"),
            "text/event-stream");

        var result = await Build(handler).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.NotNull(result.Usage);
        Assert.Equal(10, result.Usage!.PromptTokens);
        Assert.Equal(2, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task ResponsesStreamUsageIsParsedFromCompletedEvent()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            AiFixture.ServerSentEvents(
                """{"type":"response.output_text.delta","delta":"答"}""",
                """{"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":7}}}""",
                "[DONE]"),
            "text/event-stream");

        var result = await Build(handler).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ResponsesProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.NotNull(result.Usage);
        Assert.Equal(50, result.Usage!.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
    }

    // 首字时间：第一个分片到达时记录，必须早于整体耗时。
    [Fact]
    public async Task StreamingRecordsTimeToFirstToken()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            AiFixture.ServerSentEvents(
                """{"choices":[{"delta":{"content":"一"}}]}""",
                """{"choices":[{"delta":{"content":"二"}}]}""",
                "[DONE]"),
            "text/event-stream");

        var result = await Build(handler).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.NotNull(result.TimeToFirstToken);
        Assert.True(result.TimeToFirstToken >= TimeSpan.Zero);
    }

    // 有的 OpenAI 兼容网关不认 stream_options：第一次 400 时应去掉它重试，而不是直接失败。
    [Fact]
    public async Task StreamOptionsRejectionFallsBackToPlainStream()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, "unknown field stream_options"),
            (HttpStatusCode.OK, AiFixture.ServerSentEvents(
                """{"choices":[{"delta":{"content":"好"}}]}""",
                "[DONE]")));

        var result = await Build(handler).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.Equal("好", result.Answer);
        Assert.Equal(2, handler.Calls);
        Assert.DoesNotContain("stream_options", handler.Bodies[1]);
    }

    // 非 Chat Completions 协议不做这个重试，避免掩掉真实错误。
    [Fact]
    public async Task StreamOptionsFallbackOnlyAppliesToChatCompletions()
    {
        var handler = new SequencedHandler(
            (HttpStatusCode.BadRequest, "bad"),
            (HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).ExecuteStreamingAsync(
                AiFixture.FunctionFor(WorkflowMode.Answer),
                "问题",
                AiFixture.ResponsesProvider(),
                "key",
                null,
                null,
                null,
                (_, _) => { }));

        Assert.Equal(1, handler.Calls);
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Payload)[] _responses;
        private int _index;

        public SequencedHandler(params (HttpStatusCode Status, string Payload)[] responses)
        {
            _responses = responses;
        }

        public int Calls { get; private set; }

        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var response = _responses[Math.Min(_index, _responses.Length - 1)];
            _index++;

            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Payload)
            };
        }
    }
}
