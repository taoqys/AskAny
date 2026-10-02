using System.Linq;
using AskAny.Models;
using AskAny.Services;

namespace AskAny.Tests.Support;

internal static class AiFixture
{
    public static ProviderConfig ChatProvider()
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

    public static ProviderConfig ResponsesProvider()
    {
        var provider = ChatProvider();
        provider.Protocol = ApiProtocol.Responses;
        return provider;
    }

    public static FunctionOption FunctionFor(WorkflowMode mode)
    {
        var created = FunctionCatalog.CreateDefaultFunctions()
            .FirstOrDefault(function => function.Mode == mode);

        return created ?? new FunctionOption { Mode = mode, SystemPrompt = "提示词" };
    }

    // SSE 文本：事件之间用空行分隔，并混入心跳注释与 event: 行，
    // 用来验证解析器只认 data: 行。
    public static string ServerSentEvents(params string[] dataPayloads)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(": keep-alive\n\n");
        builder.Append("event: message\n");

        foreach (var payload in dataPayloads)
        {
            builder.Append("data: ").Append(payload).Append("\n\n");
        }

        return builder.ToString();
    }
}
