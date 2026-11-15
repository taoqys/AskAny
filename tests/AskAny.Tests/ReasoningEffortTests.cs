using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 有些模型「始终思考」，拒绝 effort=none（智谱 glm-5.3-flashx 会返回 1210）。
// 这类模型不能发这个字段，否则整个请求 400 —— 能力表里用 d=false 标出。
public class ReasoningEffortTests
{
    private const string ChatPayload =
        """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""";

    private const string ResponsesPayload =
        """{"output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}""";

    private static ProviderConfig ChatProvider(string model)
    {
        var provider = AiFixture.ChatProvider();
        provider.SelectedModel = model;
        provider.Models.Add(model);
        provider.SupportsReasoningControl = true;
        return provider;
    }

    private static ProviderConfig ResponsesProvider(string model)
    {
        var provider = AiFixture.ResponsesProvider();
        provider.SelectedModel = model;
        provider.Models.Add(model);
        return provider;
    }

    private static async Task<string> SendAsync(ProviderConfig provider, WorkflowMode mode, string payload)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, payload);
        var service = new AiService(new HttpClient(handler));

        await service.ExecuteAsync(
            AiFixture.FunctionFor(mode),
            "问题",
            provider,
            "key",
            null);

        return handler.LastRequestBody!;
    }

    [Fact]
    public void SnapshotKnowsWhichModelsAlwaysThink()
    {
        // 快照对这两个模型的判定与实际行为一致。
        Assert.False(ModelCapabilities.Infer("glm-5.3-flashx").CanDisableReasoning);
        Assert.True(ModelCapabilities.Infer("deepseek-flash").CanDisableReasoning);
    }

    [Fact]
    public async Task AlwaysThinkingModelOmitsReasoningEffortInChatCompletions()
    {
        var body = await SendAsync(
            ChatProvider("glm-5.3-flashx"), WorkflowMode.Answer, ChatPayload);

        Assert.DoesNotContain("reasoning_effort", body);
    }

    [Fact]
    public async Task AlwaysThinkingModelOmitsReasoningInResponses()
    {
        var body = await SendAsync(
            ResponsesProvider("glm-5.3-flashx"), WorkflowMode.Answer, ResponsesPayload);

        Assert.DoesNotContain("\"reasoning\"", body);
    }

    [Fact]
    public async Task DisableableModelStillAsksReasoningOff()
    {
        var body = await SendAsync(
            ChatProvider("deepseek-flash"), WorkflowMode.Answer, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("none", body);
    }

    // 未知模型（快照里没有）保持原行为：照旧发 none，避免改动既有可用配置。
    [Fact]
    public async Task UnknownModelKeepsSendingReasoningOff()
    {
        var body = await SendAsync(
            ChatProvider("totally-unknown-model-9000"), WorkflowMode.Answer, ChatPayload);

        Assert.Contains("reasoning_effort", body);
    }

    // 深度思考模式下，能控制推理的模型仍然要收到配置的强度。
    [Fact]
    public async Task ThinkingModeSendsConfiguredEffort()
    {
        var provider = ChatProvider("deepseek-flash");
        provider.ReasoningEffort = "medium";

        var body = await SendAsync(provider, WorkflowMode.Think, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("medium", body);
    }
}
