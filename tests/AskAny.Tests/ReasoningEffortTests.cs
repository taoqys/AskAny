using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 思考是独立开关（OFF/ON），不再是一个功能项：
//   ON  → 按设置里的强度请求
//   OFF → 能关的模型发 none；「始终思考」的模型（如智谱 glm-5.3-flashx，
//         它拒绝 none 并返回 1210）退到它能接受的最小强度 low。
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

    private static async Task<string> SendAsync(
        ProviderConfig provider,
        bool thinkingEnabled,
        string payload,
        WorkflowMode mode = WorkflowMode.Answer)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, payload);
        var service = new AiService(new HttpClient(handler));

        await service.ExecuteAsync(
            AiFixture.FunctionFor(mode),
            "问题",
            provider,
            "key",
            null,
            null,
            null,
            thinkingEnabled);

        return handler.LastRequestBody!;
    }

    [Fact]
    public void SnapshotKnowsWhichModelsAlwaysThink()
    {
        // 快照对这两个模型的判定与实际行为一致。
        Assert.False(ModelCapabilities.Infer("glm-5.3-flashx").CanDisableReasoning);
        Assert.True(ModelCapabilities.Infer("deepseek-flash").CanDisableReasoning);
    }

    // 关不掉思考的模型：不再省略字段，而是发它能接受的最小强度 low。
    [Fact]
    public async Task AlwaysThinkingModelGetsLowEffortInChatCompletions()
    {
        var body = await SendAsync(
            ChatProvider("glm-5.3-flashx"), thinkingEnabled: false, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("low", body);
        Assert.DoesNotContain("none", body);
    }

    [Fact]
    public async Task AlwaysThinkingModelGetsLowEffortInResponses()
    {
        var body = await SendAsync(
            ResponsesProvider("glm-5.3-flashx"), thinkingEnabled: false, ResponsesPayload);

        Assert.Contains("\"reasoning\"", body);
        Assert.Contains("low", body);
        Assert.DoesNotContain("none", body);
    }

    [Fact]
    public async Task DisableableModelAsksReasoningOff()
    {
        var body = await SendAsync(
            ChatProvider("deepseek-flash"), thinkingEnabled: false, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("none", body);
    }

    // 未知模型（快照里没有）按「能关」处理：照旧发 none，不改既有可用配置。
    [Fact]
    public async Task UnknownModelSendsReasoningOff()
    {
        var body = await SendAsync(
            ChatProvider("totally-unknown-model-9000"), thinkingEnabled: false, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("none", body);
    }

    // 打开思考后，即使模型原本关不掉思考，也走配置的强度。
    [Fact]
    public async Task ThinkingOnSendsConfiguredEffort()
    {
        var provider = ChatProvider("deepseek-flash");
        provider.ReasoningEffort = "medium";

        var body = await SendAsync(provider, thinkingEnabled: true, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("medium", body);
    }

    [Fact]
    public async Task ThinkingOnUsesConfiguredEffortForAlwaysThinkingModel()
    {
        var provider = ChatProvider("glm-5.3-flashx");
        provider.ReasoningEffort = "high";

        var body = await SendAsync(provider, thinkingEnabled: true, ChatPayload);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("high", body);
    }

    // 老配置里若还留着「深度思考」功能项，即使开关是 OFF 也按开启处理（向后兼容）。
    [Fact]
    public async Task LegacyThinkFunctionStillThinks()
    {
        var provider = ChatProvider("deepseek-flash");
        provider.ReasoningEffort = "high";

        var body = await SendAsync(
            provider, thinkingEnabled: false, ChatPayload, WorkflowMode.Think);

        Assert.Contains("reasoning_effort", body);
        Assert.Contains("high", body);
    }
}
