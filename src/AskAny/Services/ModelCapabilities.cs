using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AskAny.Services;

// 一个模型的「能力」——目前是 AskAny 会真正用到的两项。
public readonly record struct ModelCapability(bool SupportsVision, bool SupportsReasoning)
{
    public static readonly ModelCapability Unknown = new(false, false);

    public bool IsKnown => SupportsVision || SupportsReasoning;
}

// 模型能力表。
//
// 主数据是内置的快照 Assets/model-capabilities.json：生成时从 https://models.dev/api.json
// 抽取「模型 id → 是否支持图片 / 是否支持推理」，覆盖数千个模型，离线可用、不依赖网络。
// 快照里没有的新模型再按命名规则猜一把。
//
// 之所以内置而不是运行时拉取：models.dev 的 api.json 有 6.6MB，为一个轻量助手每次启动
// 去拉这么一份数据不划算；能力数据变化很慢，快照 + 命名兜底足够。
public static class ModelCapabilities
{
    private const string ResourceName = "AskAny.Assets.model-capabilities.json";

    // 快照里查不到时用的启发式规则（通常意味着这是一个比快照更新的模型）。
    // 只用来兜底，所以宁可保守：宁可漏判成「不支持」，也不要误判成「支持」后
    // 把图片发过去、被服务端拒绝。
    private static readonly string[] VisionMarkers =
    [
        "vision", "-vl", "vl-", "llava", "pixtral", "internvl", "gemini",
        "claude-3", "claude-4", "claude-sonnet", "claude-opus",
        "gpt-4o", "gpt-4.1", "gpt-5", "glm-4v", "glm-4.5v", "qwen-vl", "qwen2-vl", "qwen2.5-vl", "qwen3-vl",
        "mimo-vl", "step-1v", "yi-vl", "doubao-vision"
    ];

    private static readonly string[] ReasoningMarkers =
    [
        "reasoner", "reasoning", "thinking", "-think", "-r1", "qwq", "glm-z1"
    ];

    private static readonly Lazy<Dictionary<string, ModelCapability>> Snapshot =
        new(LoadSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int SnapshotSize => Snapshot.Value.Count;

    public static ModelCapability Infer(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return ModelCapability.Unknown;
        }

        var id = modelId.Trim();
        if (Snapshot.Value.TryGetValue(id, out var known))
        {
            return known;
        }

        var lower = id.ToLowerInvariant();
        return new ModelCapability(
            MatchesAny(lower, VisionMarkers) || IsReasoningFamily(lower),
            MatchesAny(lower, ReasoningMarkers) || IsReasoningFamily(lower));
    }

    // o1 / o3 / o4 系列既是多模态也支持推理；用前缀判断避免 "o1" 误伤其它 id。
    private static bool IsReasoningFamily(string lowerId)
    {
        return lowerId is "o1" or "o3" or "o4"
               || lowerId.StartsWith("o1-", StringComparison.Ordinal)
               || lowerId.StartsWith("o3-", StringComparison.Ordinal)
               || lowerId.StartsWith("o4-", StringComparison.Ordinal);
    }

    private static bool MatchesAny(string value, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (value.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, ModelCapability> LoadSnapshot()
    {
        var result = new Dictionary<string, ModelCapability>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = typeof(ModelCapabilities).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                ErrorLog.Write($"模型能力快照缺失：{ResourceName}");
                return result;
            }

            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var vision = property.Value.TryGetProperty("v", out var visionElement) &&
                             visionElement.ValueKind == JsonValueKind.True;
                var reasoning = property.Value.TryGetProperty("r", out var reasoningElement) &&
                                reasoningElement.ValueKind == JsonValueKind.True;
                result[property.Name] = new ModelCapability(vision, reasoning);
            }
        }
        catch (JsonException exception)
        {
            ErrorLog.Write("解析模型能力快照", exception);
        }
        catch (IOException exception)
        {
            ErrorLog.Write("读取模型能力快照", exception);
        }

        return result;
    }
}
