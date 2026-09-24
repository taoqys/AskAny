using AskAny.Models;

namespace AskAny.Services;

public static class ProviderCatalog
{
    public static List<ProviderConfig> CreateDefaultProviders()
    {
        return
        [
            CreatePreset("openai-chat"),
            CreatePreset("deepseek-responses")
        ];
    }

    public static ProviderConfig CreatePreset(string presetId)
    {
        return presetId switch
        {
            "openai-chat" => new ProviderConfig
            {
                Id = "openai",
                Name = "OpenAI",
                Protocol = ApiProtocol.ChatCompletions,
                BaseUri = "https://api.openai.com/v1",
                Models = ["gpt-4.1-mini", "gpt-4.1", "gpt-5.1"],
                SelectedModel = "gpt-4.1-mini",
                SupportsReasoningControl = true,
                ReasoningEffort = "high"
            },
            "openai-responses" => new ProviderConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "OpenAI Responses",
                Protocol = ApiProtocol.Responses,
                BaseUri = "https://api.openai.com/v1",
                Models = ["gpt-5.1", "gpt-5.1-mini", "gpt-4.1"],
                SelectedModel = "gpt-5.1",
                SupportsReasoningControl = true,
                ReasoningEffort = "high"
            },
            "deepseek-responses" => new ProviderConfig
            {
                Id = "deepseek",
                Name = "DeepSeek",
                Protocol = ApiProtocol.Responses,
                BaseUri = "https://api.deepseek.com",
                Models = ["deepseek-flash", "deepseek-v4-pro"],
                SelectedModel = "deepseek-flash",
                SupportsReasoningControl = true,
                ReasoningEffort = "high"
            },
            _ => new ProviderConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "自定义提供商",
                Protocol = ApiProtocol.ChatCompletions,
                BaseUri = "https://api.example.com/v1",
                Models = ["model-name"],
                SelectedModel = "model-name",
                SupportsReasoningControl = false,
                ReasoningEffort = "high"
            }
        };
    }
}
