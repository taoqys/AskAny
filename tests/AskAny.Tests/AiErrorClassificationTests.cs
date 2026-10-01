using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// HTTP 失败不能只把原始 payload 甩给用户，要指出下一步做什么。
public class AiErrorClassificationTests
{
    private static Task<AiResult> SendWithStatusAsync(HttpStatusCode status, string payload)
    {
        var handler = new StubHttpMessageHandler(status, payload);
        var service = new AiService(new HttpClient(handler));

        return service.ExecuteAsync(
            AiFixture.FunctionFor(WorkflowMode.Answer),
            "问题",
            AiFixture.ChatProvider(),
            "key",
            null);
    }

    [Fact]
    public async Task UnauthorizedPointsAtApiKey()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.Unauthorized, "{}"));

        Assert.Contains("API Key", exception.Message);
    }

    [Fact]
    public async Task ForbiddenPointsAtApiKey()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.Forbidden, "{}"));

        Assert.Contains("API Key", exception.Message);
    }

    [Fact]
    public async Task NotFoundPointsAtEndpointOrModel()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.NotFound, "{}"));

        Assert.Contains("接口地址", exception.Message);
        Assert.Contains("模型", exception.Message);
    }

    [Fact]
    public async Task RateLimitIsCalledOut()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.TooManyRequests, "{}"));

        Assert.Contains("限流", exception.Message);
    }

    [Fact]
    public async Task BadRequestMentionsModelOrParameter()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.BadRequest, "{}"));

        Assert.Contains("模型名", exception.Message);
    }

    [Fact]
    public async Task ServerErrorSuggestsRetry()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.InternalServerError, "{}"));

        Assert.Contains("服务端错误", exception.Message);
    }

    [Fact]
    public async Task PayloadIsTruncatedToKeepMessageReadable()
    {
        var huge = new string('x', 5000);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SendWithStatusAsync(HttpStatusCode.BadGateway, huge));

        Assert.True(exception.Message.Length < 800, $"消息过长：{exception.Message.Length}");
        Assert.Contains("502", exception.Message);
    }
}
