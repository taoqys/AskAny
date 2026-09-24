namespace AskAny.Models;

public enum WorkflowMode
{
    Answer,
    Explain,
    TrackNews,
    Think,
    ExplainOnline
}

public enum ApiProtocol
{
    ChatCompletions,
    Responses
}

public sealed record FunctionOption(
    WorkflowMode Mode,
    string Name,
    string Description,
    string Glyph);

public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "OpenAI";
    public ApiProtocol Protocol { get; set; } = ApiProtocol.ChatCompletions;
    public string BaseUri { get; set; } = "https://api.openai.com/v1";
    public string ApiKeyProtected { get; set; } = string.Empty;
    public List<string> Models { get; set; } = [];
    public string SelectedModel { get; set; } = string.Empty;
    public bool SupportsReasoningControl { get; set; } = true;
    public string ReasoningEffort { get; set; } = "high";

    public ProviderConfig Clone()
    {
        return new ProviderConfig
        {
            Id = Id,
            Name = Name,
            Protocol = Protocol,
            BaseUri = BaseUri,
            ApiKeyProtected = ApiKeyProtected,
            Models = [.. Models],
            SelectedModel = SelectedModel,
            SupportsReasoningControl = SupportsReasoningControl,
            ReasoningEffort = ReasoningEffort
        };
    }
}

public sealed class AppConfig
{
    public List<ProviderConfig> Providers { get; set; } = [];
    public string SelectedProviderId { get; set; } = string.Empty;
    public string TavilyApiKeyProtected { get; set; } = string.Empty;
    public bool KeepWindowOnTop { get; set; } = true;
    public bool HideWhenDeactivated { get; set; } = true;
    public bool AutoFillSelectedText { get; set; } = true;

    // Legacy fields are retained so existing installations migrate cleanly.
    public string OpenAiBaseUri { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public WorkflowMode Mode { get; set; }
    public string ModeName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public int SourceCount { get; set; }

    [JsonIgnore]
    public string DisplayMeta =>
        $"{Timestamp:MM-dd HH:mm} · {ModeName} · {ProviderName} / {Model}";
}
