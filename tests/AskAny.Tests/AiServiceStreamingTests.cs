using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

public class AiServiceStreamingTests
{
    private static async Task<(AiResult Result, List<string> Content, List<string> Reasoning)>
        StreamAsync(ProviderConfig provider, WorkflowMode mode, string body, HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(
            status, body, "text/event-stream");
        var service = new AiService(new HttpClient(handler));

        var content = new List<string>();
        var reasoning = new List<string>();

        var result = await service.ExecuteStreamingAsync(
            AiFixture.FunctionFor(mode),
            "问题",
            provider,
            "key",
            null,
            null,
            null,
            (deltaContent, deltaReasoning) =>
            {
                if (deltaContent.Length > 0)
                {
                    content.Add(deltaContent);
                }

                if (deltaReasoning.Length > 0)
                {
                    reasoning.Add(deltaReasoning);
                }
            });

        return (result, content, reasoning);
    }

    [Fact]
    public async Task ChatCompletionsDeltasAreAccumulated()
    {
        var body = AiFixture.ServerSentEvents(
            """{"choices":[{"delta":{"content":"你"}}]}""",
            """{"choices":[{"delta":{"content":"好"}}]}""",
            "[DONE]");

        var (result, content, _) = await StreamAsync(
            AiFixture.ChatProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK);

        Assert.Equal("你好", result.Answer);
        Assert.Equal(new[] { "你", "好" }, content);
    }

    [Fact]
    public async Task ChatCompletionsReasoningDeltaIsSeparated()
    {
        var body = AiFixture.ServerSentEvents(
            """{"choices":[{"delta":{"reasoning_content":"先想"}}]}""",
            """{"choices":[{"delta":{"content":"再答"}}]}""",
            "[DONE]");

        var (result, content, reasoning) = await StreamAsync(
            AiFixture.ChatProvider(), WorkflowMode.Think, body, HttpStatusCode.OK);

        Assert.Equal("再答", result.Answer);
        Assert.Equal("先想", result.Reasoning);
        Assert.Equal(new[] { "先想" }, reasoning);
        Assert.Equal(new[] { "再答" }, content);
    }

    // 部分兼容实现把推理字段叫 reasoning 而不是 reasoning_content。
    [Fact]
    public async Task ChatCompletionsAcceptsReasoningAlias()
    {
        var body = AiFixture.ServerSentEvents(
            """{"choices":[{"delta":{"reasoning":"思考"}}]}""",
            "[DONE]");

        var (result, _, _) = await StreamAsync(
            AiFixture.ChatProvider(), WorkflowMode.Think, body, HttpStatusCode.OK);

        Assert.Equal("思考", result.Reasoning);
    }

    [Fact]
    public async Task ResponsesTextAndReasoningDeltasAreParsed()
    {
        var body = AiFixture.ServerSentEvents(
            """{"type":"response.reasoning_text.delta","delta":"推理"}""",
            """{"type":"response.output_text.delta","delta":"答案"}""",
            """{"type":"response.completed"}""",
            "[DONE]");

        var (result, content, reasoning) = await StreamAsync(
            AiFixture.ResponsesProvider(), WorkflowMode.Think, body, HttpStatusCode.OK);

        Assert.Equal("答案", result.Answer);
        Assert.Equal("推理", result.Reasoning);
        Assert.Equal(new[] { "答案" }, content);
        Assert.Equal(new[] { "推理" }, reasoning);
    }

    // 无关事件（response.created / output_item.added 等）必须被忽略而不是报错。
    [Fact]
    public async Task ResponsesIgnoresUnrelatedEvents()
    {
        var body = AiFixture.ServerSentEvents(
            """{"type":"response.created"}""",
            """{"type":"response.output_item.added","item":{"id":"x"}}""",
            """{"type":"response.output_text.delta","delta":"正文"}""",
            "[DONE]");

        var (result, _, _) = await StreamAsync(
            AiFixture.ResponsesProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK);

        Assert.Equal("正文", result.Answer);
    }

    [Fact]
    public async Task StreamingRequestAsksForStream()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            AiFixture.ServerSentEvents("""{"choices":[{"delta":{"content":"x"}}]}""", "[DONE]"),
            "text/event-stream");

        await new AiService(new HttpClient(handler)).ExecuteStreamingAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null,
            null,
            null,
            (_, _) => { });

        Assert.Contains("\"stream\":true", handler.LastRequestBody);
    }

    [Fact]
    public async Task NonStreamingRequestDoesNotAskForStream()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"答案"}}]}""");

        await new AiService(new HttpClient(handler)).ExecuteAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null);

        Assert.DoesNotContain("\"stream\"", handler.LastRequestBody);
    }

    // 流中途的错误必须抛出来，否则界面会把半截回答当成完整答案。
    [Fact]
    public async Task InBandErrorIsRaised()
    {
        var body = AiFixture.ServerSentEvents(
            """{"choices":[{"delta":{"content":"半截"}}]}""",
            """{"error":{"message":"boom"}}""",
            "[DONE]");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamAsync(AiFixture.ChatProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK));

        Assert.Contains("boom", exception.Message);
    }

    [Fact]
    public async Task ResponsesFailureEventIsRaised()
    {
        var body = AiFixture.ServerSentEvents(
            """{"type":"response.failed"}""",
            "[DONE]");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamAsync(AiFixture.ResponsesProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK));
    }

    // 空流要让调用方知道该回退到非流式，而不是显示一个空回答。
    [Fact]
    public async Task EmptyStreamIsRejected()
    {
        var body = AiFixture.ServerSentEvents("[DONE]");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamAsync(AiFixture.ChatProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK));
    }

    [Fact]
    public async Task UnparseableChunksAreSkippedWithoutBreakingTheStream()
    {
        var body = AiFixture.ServerSentEvents(
            "not json at all",
            """{"choices":[{"delta":{"content":"好"}}]}""",
            "[DONE]");

        var (result, _, _) = await StreamAsync(
            AiFixture.ChatProvider(), WorkflowMode.Answer, body, HttpStatusCode.OK);

        Assert.Equal("好", result.Answer);
    }

    // 流式请求的 HTTP 失败同样要给出可操作的提示。
    [Fact]
    public async Task StreamingHttpFailureIsClassified()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamAsync(
                AiFixture.ChatProvider(),
                WorkflowMode.Answer,
                "{}",
                HttpStatusCode.Unauthorized));

        Assert.Contains("401", exception.Message);
        Assert.Contains("API Key", exception.Message);
    }
}
