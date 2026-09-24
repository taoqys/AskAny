namespace AskAny.Models;

public enum WorkflowMode
{
    Answer,
    Explain,
    TrackNews,
    Think,
    ExplainOnline
}

public sealed record FunctionOption(
    WorkflowMode Mode,
    string Name,
    string Description,
    string Glyph);

public sealed class AppConfig
{
    public string OpenAiBaseUri { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;
    public string TavilyApiKeyProtected { get; set; } = string.Empty;
    public bool KeepWindowOnTop { get; set; } = true;
}
