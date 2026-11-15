using System.Windows.Media.Imaging;

namespace AskAny.Models;

public enum WorkflowMode
{
    Answer,
    Explain,
    TrackNews,
    Think,
    ExplainOnline,
    // 追加在末尾：枚举按名称序列化，新值放最后不会影响已有配置。
    ZhihuSearch,
    // 合并后的「联网检索」：具体检索哪个来源由 FunctionOption.SearchBackend 决定。
    // TrackNews / ExplainOnline / ZhihuSearch 作为历史值保留，仅供老配置迁移与默认提示词回退。
    SearchNetwork
}

// 检索来源是独立于「提示词 / 推理」的一条真实行为轴：
// 它决定走哪个检索接口、以及 Tavily 的 topic 取值。
public enum SearchBackend
{
    None,
    TavilyGeneral,
    TavilyNews,
    Zhihu
}

public enum ApiProtocol
{
    ChatCompletions,
    Responses
}

// 全局手势用的修饰键。双击同一个修饰键触发对应动作。
// 只支持修饰键：它们不会被别的程序占用，钩子只旁听不拦截。
public enum HotkeyModifier
{
    Shift,
    Control,
    Alt
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
    public SearchBackend SearchBackend { get; set; } = SearchBackend.None;

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
            IconOffsetY = IconOffsetY,
            SearchBackend = SearchBackend
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

    // 用户从获取到的模型列表里勾选出来的「启用」集合 —— 主界面模型 tab 只显示这些。
    // 为空表示「全部启用」（老配置的语义），避免升级后模型消失。
    public List<string> EnabledModels { get; set; } = [];
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
            EnabledModels = [.. EnabledModels],
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

    // 主界面真正可选的模型：优先「启用」集合，没勾选过就退回完整列表。
    public IReadOnlyList<string> EffectiveModels()
    {
        var enabled = (EnabledModels ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (enabled.Count > 0)
        {
            return enabled;
        }

        return (Models ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AppConfig
{
    public List<ProviderConfig> Providers { get; set; } = [];
    public List<FunctionOption> Functions { get; set; } = [];
    public string SelectedProviderId { get; set; } = string.Empty;
    public string TavilyApiKeyProtected { get; set; } = string.Empty;
    public string ZhihuAccessSecretProtected { get; set; } = string.Empty;

    // 把默认的三个检索项并成「联网检索」只做一次：做过之后用户自行增删都不再被改动。
    public bool FunctionSetConsolidated { get; set; }

    // 「回答」与「解释」合并成一项，同样只做一次。
    public bool AnswerExplainMerged { get; set; }

    // 「深度思考」并进「回答」：思考变成面板上的 OFF/ON 开关，同样只迁移一次。
    public bool ThinkingMergedIntoAnswer { get; set; }

    // 思考开关（OFF/ON）。与功能项解耦：同一个问题偶尔想深思，不该提前选一条命令。
    public bool ThinkingEnabled { get; set; }
    public bool KeepWindowOnTop { get; set; } = true;
    public bool HideWhenDeactivated { get; set; } = true;
    public bool AutoFillSelectedText { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ScreenshotHotkeyEnabled { get; set; } = true;

    // 全局手势可配置：用哪个修饰键双击、间隔多长。
    // 默认仍是「双击 Shift 取词 + 双击 Ctrl 截图」，与老版本一致。
    public HotkeyModifier SelectionHotkeyModifier { get; set; } = HotkeyModifier.Shift;
    public HotkeyModifier ScreenshotHotkeyModifier { get; set; } = HotkeyModifier.Control;
    public int HotkeyDoubleTapIntervalMs { get; set; } = 400;

    // 双模型对比：同一个问题同时问两个模型，左右分屏。
    public bool CompareMode { get; set; }
    public string CompareProviderId { get; set; } = string.Empty;
    public string CompareModel { get; set; } = string.Empty;

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
            ZhihuAccessSecretProtected = ZhihuAccessSecretProtected,
            FunctionSetConsolidated = FunctionSetConsolidated,
            AnswerExplainMerged = AnswerExplainMerged,
            ThinkingMergedIntoAnswer = ThinkingMergedIntoAnswer,
            ThinkingEnabled = ThinkingEnabled,
            KeepWindowOnTop = KeepWindowOnTop,
            HideWhenDeactivated = HideWhenDeactivated,
            AutoFillSelectedText = AutoFillSelectedText,
            StartWithWindows = StartWithWindows,
            ScreenshotHotkeyEnabled = ScreenshotHotkeyEnabled,
            SelectionHotkeyModifier = SelectionHotkeyModifier,
            ScreenshotHotkeyModifier = ScreenshotHotkeyModifier,
            HotkeyDoubleTapIntervalMs = HotkeyDoubleTapIntervalMs,
            CompareMode = CompareMode,
            CompareProviderId = CompareProviderId,
            CompareModel = CompareModel,
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

    // 话题：一次连续追问算一个话题。历史按话题分组，并可从历史「继续该话题」恢复上下文。
    // 空 TopicId 表示老记录（升级前写入的历史）。
    public string TopicId { get; set; } = string.Empty;
    public string TopicTitle { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayMeta =>
        $"{Timestamp:MM-dd HH:mm} · {ModeName} · {ProviderName} / {Model}" +
        (ImageCount > 0 ? $" · {ImageCount} 张图片" : string.Empty) +
        (string.IsNullOrWhiteSpace(TopicTitle) ? string.Empty : $" · 话题：{TopicTitle}");
}
