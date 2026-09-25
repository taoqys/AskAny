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

public sealed class FunctionOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public WorkflowMode Mode { get; set; } = WorkflowMode.Answer;
    public string Name { get; set; } = "回答此问题";
    public string Description { get; set; } = "直接、准确地解答当前问题";
    public string Glyph { get; set; } = "\uE8BD";
    public string SystemPrompt { get; set; } = string.Empty;
    public double IconOffsetX { get; set; }
    public double IconOffsetY { get; set; }

    public FunctionOption Clone()
    {
        return new FunctionOption
        {
            Id = Id,
            Mode = Mode,
            Name = Name,
            Description = Description,
            Glyph = Glyph,
            SystemPrompt = SystemPrompt,
            IconOffsetX = IconOffsetX,
            IconOffsetY = IconOffsetY
        };
    }
}

public sealed record ConversationTurn(string Role, string Content);

public sealed record AiResult(string Answer, string? Reasoning = null);

public sealed record ModelChoice(ProviderConfig Provider, string Model)
{
    public string DisplayName => $"{Provider.Name} · {Model}";
}

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
    public List<FunctionOption> Functions { get; set; } = [];
    public string SelectedProviderId { get; set; } = string.Empty;
    public string TavilyApiKeyProtected { get; set; } = string.Empty;
    public bool KeepWindowOnTop { get; set; } = true;
    public bool HideWhenDeactivated { get; set; } = true;
    public bool AutoFillSelectedText { get; set; } = true;
    public bool StartWithWindows { get; set; }

    // Legacy fields are retained so existing installations migrate cleanly.
    public string OpenAiBaseUri { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;
}

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string FunctionId { get; set; } = string.Empty;
    public WorkflowMode Mode { get; set; }
    public string ModeName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public int SourceCount { get; set; }

    [JsonIgnore]
    public string DisplayMeta =>
        $"{Timestamp:MM-dd HH:mm} · {ModeName} · {ProviderName} / {Model}";
}
