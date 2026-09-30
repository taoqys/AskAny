using System.Windows.Media.Imaging;

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

public sealed class ImageAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "image.png";
    public string MediaType { get; set; } = "image/png";
    public byte[] Bytes { get; set; } = [];
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public BitmapSource? Preview { get; set; }
}

public sealed record ConversationTurn(
    string Role,
    string Content,
    IReadOnlyList<ImageAttachment>? Images = null);

public sealed record AiResult(string Answer, string? Reasoning = null);

public sealed record ModelChoice(ProviderConfig Provider, string Model)
{
    public string DisplayName => $"{Provider.Name} · {Model}";
    public bool SupportsVision => Provider.SupportsVisionModel(Model);
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
    public List<string> VisionModels { get; set; } = [];
    public bool VisionModelsConfigured { get; set; }
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
            VisionModels = [.. VisionModels],
            VisionModelsConfigured = VisionModelsConfigured,
            SupportsReasoningControl = SupportsReasoningControl,
            ReasoningEffort = ReasoningEffort
        };
    }

    public bool SupportsVisionModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || VisionModels is null || VisionModels.Count == 0)
        {
            return false;
        }

        return VisionModels.Any(pattern =>
            pattern == "*" ||
            pattern.Equals(model, StringComparison.OrdinalIgnoreCase) ||
            (pattern.EndsWith('*') &&
             model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)));
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
    public bool ScreenshotHotkeyEnabled { get; set; } = true;

    // Legacy fields are retained so existing installations migrate cleanly.
    public string OpenAiBaseUri { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;

    // 设置窗口编辑副本用：让「取消」不污染正在使用的配置对象。
    public AppConfig Clone()
    {
        return new AppConfig
        {
            Providers = Providers.Select(provider => provider.Clone()).ToList(),
            Functions = Functions.Select(function => function.Clone()).ToList(),
            SelectedProviderId = SelectedProviderId,
            TavilyApiKeyProtected = TavilyApiKeyProtected,
            KeepWindowOnTop = KeepWindowOnTop,
            HideWhenDeactivated = HideWhenDeactivated,
            AutoFillSelectedText = AutoFillSelectedText,
            StartWithWindows = StartWithWindows,
            ScreenshotHotkeyEnabled = ScreenshotHotkeyEnabled,
            OpenAiBaseUri = OpenAiBaseUri,
            Model = Model,
            OpenAiApiKeyProtected = OpenAiApiKeyProtected
        };
    }
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
    public int ImageCount { get; set; }
    public int SourceCount { get; set; }

    [JsonIgnore]
    public string DisplayMeta =>
        $"{Timestamp:MM-dd HH:mm} · {ModeName} · {ProviderName} / {Model}" +
        (ImageCount > 0 ? $" · {ImageCount} 张图片" : string.Empty);
}
