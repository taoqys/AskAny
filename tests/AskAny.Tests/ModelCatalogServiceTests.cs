using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

public class ModelCatalogServiceTests
{
    private const string OpenAiShapedPayload = """
        {"object":"list","data":[
          {"id":"gpt-4.1-mini","object":"model"},
          {"id":"gpt-4.1","object":"model"},
          {"id":"deepseek-chat","object":"model"}
        ]}
        """;

    private static ProviderConfig Provider(string baseUri)
    {
        return new ProviderConfig
        {
            Name = "P",
            BaseUri = baseUri,
            Models = { "stale-model" },
            SelectedModel = "stale-model"
        };
    }

    private static ModelCatalogService Build(HttpMessageHandler handler)
    {
        return new ModelCatalogService(new HttpClient(handler));
    }

    [Fact]
    public async Task ModelsAreParsedAndSorted()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, OpenAiShapedPayload);

        var result = await Build(handler).FetchAsync(Provider("https://api.example.com/v1"), "key");

        Assert.Equal(
            new[] { "deepseek-chat", "gpt-4.1", "gpt-4.1-mini" },
            result.Models.ToArray());
    }

    [Fact]
    public async Task CapabilitiesAreInferredForFetchedModels()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, OpenAiShapedPayload);

        var result = await Build(handler).FetchAsync(Provider("https://api.example.com/v1"), "key");

        Assert.Contains("gpt-4.1-mini", result.VisionModels);
        Assert.DoesNotContain("deepseek-chat", result.VisionModels);
    }

    [Fact]
    public async Task BearerTokenIsSent()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, OpenAiShapedPayload);

        await Build(handler).FetchAsync(Provider("https://api.example.com/v1"), "secret-key");

        Assert.Equal("Bearer", handler.LastRequestHeaders!.Authorization!.Scheme);
        Assert.Equal("secret-key", handler.LastRequestHeaders.Authorization.Parameter);
    }

    // 基址带版本段 → 直接拼 /models
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/models")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1/models")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/models")]
    [InlineData("https://example.com/paas/v2", "https://example.com/paas/v2/models")]
    public void EndpointWithVersionSegment(string baseUri, string expectedFirst)
    {
        Assert.Equal(expectedFirst, ModelCatalogService.BuildModelEndpoints(baseUri)[0]);
    }

    // 基址没有版本段 → 先试 /models，再退到 /v1/models
    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("https://example.com/gateway")]
    public void EndpointWithoutVersionSegmentHasFallback(string baseUri)
    {
        var endpoints = ModelCatalogService.BuildModelEndpoints(baseUri);

        Assert.Equal(2, endpoints.Count);
        Assert.EndsWith("/models", endpoints[0]);
        Assert.EndsWith("/v1/models", endpoints[1]);
    }

    // 基址里已经带了操作路径（OpenRouter 这类）要剥掉再拼。
    [Fact]
    public void OperationSuffixIsStrippedFromBaseUri()
    {
        var endpoints = ModelCatalogService.BuildModelEndpoints(
            "https://openrouter.ai/api/v1/chat/completions");

        Assert.Equal("https://openrouter.ai/api/v1/models", endpoints[0]);
    }

    [Fact]
    public void ResponsesSuffixIsStrippedFromBaseUri()
    {
        var endpoints = ModelCatalogService.BuildModelEndpoints("https://api.deepseek.com/responses");

        Assert.Equal("https://api.deepseek.com/models", endpoints[0]);
    }

    // 第一个候选 404 时自动换备选地址。
    [Fact]
    public async Task NotFoundFallsThroughToNextCandidate()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.NotFound, "not found"),
            (HttpStatusCode.OK, """{"data":[{"id":"m1"}]}"""));

        var result = await Build(handler).FetchAsync(Provider("https://api.example.com"), "key");

        Assert.Equal(new[] { "m1" }, result.Models.ToArray());
        Assert.Equal(2, handler.Calls);
    }

    // 401 应该立刻报出来，而不是被当成「接口不存在」而静默换地址。
    [Fact]
    public async Task UnauthorizedIsSurfacedImmediately()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "{}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).FetchAsync(Provider("https://api.example.com"), "bad"));

        Assert.Contains("401", exception.Message);
        Assert.Contains("API Key", exception.Message);
    }

    [Fact]
    public async Task AllCandidatesMissingGivesManualFallbackHint()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.NotFound, "nope"),
            (HttpStatusCode.NotFound, "nope"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).FetchAsync(Provider("https://api.example.com"), "key"));

        Assert.Contains("手动填写", exception.Message);
    }

    [Fact]
    public async Task EmptyListIsReported()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"data":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).FetchAsync(Provider("https://api.example.com/v1"), "key"));
    }

    [Fact]
    public async Task MissingBaseUriIsRejected()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, OpenAiShapedPayload);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).FetchAsync(Provider("  "), "key"));
    }

    // 除了 OpenAI 的 data[].id，也兼容 models[].name、裸数组与大小写不同的键。
    [Theory]
    [InlineData("""{"models":[{"name":"m-a"},{"name":"m-b"}]}""")]
    [InlineData("""["m-a","m-b"]""")]
    [InlineData("""{"Data":[{"Id":"m-a"},{"Id":"m-b"}]}""")]
    public void AlternativePayloadShapesAreAccepted(string payload)
    {
        Assert.Equal(new[] { "m-a", "m-b" }, ModelCatalogService.ParseModelIds(payload).ToArray());
    }

    [Fact]
    public void DuplicateModelsAreCollapsed()
    {
        var ids = ModelCatalogService.ParseModelIds(
            """{"data":[{"id":"m"},{"id":"m"},{"id":"M"}]}""");

        Assert.Single(ids);
    }

    // 按顺序返回不同响应，用来验证「换候选地址」的行为。
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Payload)[] _responses;
        private int _index;

        public SequenceHandler(params (HttpStatusCode Status, string Payload)[] responses)
        {
            _responses = responses;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var response = _responses[Math.Min(_index, _responses.Length - 1)];
            _index++;

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Payload)
            });
        }
    }
}
