using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AskAny.Services;

// 一个模型的「能力」——目前是 AskAny 会真正用到的几项。
// CanDisableReasoning 为 null 表示未知（此时按「可以关闭」处理，保持原有行为）。
// ContextTokens 为 null 表示未知（此时按默认上限处理）。
public readonly record struct ModelCapability(
    bool SupportsVision,
    bool SupportsReasoning,
    bool? CanDisableReasoning,
    int? ContextTokens)
{
    public static readonly ModelCapability Unknown = new(false, false, null, null);

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

        // 注意：由「支持推理」不能推出「支持图片」—— o1-mini / o3-mini 都是纯文本。
        // 所以图片能力只看命名标记，宁可漏判。
        // 关闭推理的能力在快照外无从判断，返回 null（按可关闭处理，不改变既有行为）。
        return new ModelCapability(
            MatchesAny(lower, VisionMarkers),
            MatchesAny(lower, ReasoningMarkers) || IsReasoningFamily(lower),
            null,
            null);
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

    private static bool IsTrue(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
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
                var vision = IsTrue(property.Value, "v");
                var reasoning = IsTrue(property.Value, "r");

                // d: true=可以关闭推理，false=始终思考，null/缺失=未知
                bool? canDisable = null;
                if (property.Value.TryGetProperty("d", out var disableElement))
                {
                    canDisable = disableElement.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }

                // c: 上下文 token 上限，缺失表示未知
                int? contextTokens = null;
                if (property.Value.TryGetProperty("c", out var contextElement) &&
                    contextElement.ValueKind == JsonValueKind.Number &&
                    contextElement.TryGetInt32(out var tokens) &&
                    tokens > 0)
                {
                    contextTokens = tokens;
                }

                result[property.Name] = new ModelCapability(vision, reasoning, canDisable, contextTokens);
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
