using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;

namespace AskAny.Tests.Support;

// ConfigService 的默认构造函数写死 %APPDATA%，因此测试走临时文件 + ConfigService(path)。
internal static class ConfigFixture
{
    public static async Task<AppConfig> NormalizeAsync(string json)
    {
        var path = WriteTemp(json);

        try
        {
            return await new ConfigService(path).LoadAsync();
        }
        finally
        {
            TryDelete(path);
        }
    }

    // 把第一次的结果序列化再归一化一次，用来验证迁移是幂等的。
    public static async Task<AppConfig> NormalizeTwiceAsync(string json)
    {
        var first = await NormalizeAsync(json);
        var path = WriteTemp(JsonSerializer.Serialize(first, JsonDefaults.Options));

        try
        {
            return await new ConfigService(path).LoadAsync();
        }
        finally
        {
            TryDelete(path);
        }
    }

    public static string BuildConfig(
        object[] functions,
        bool consolidated = false,
        bool answerExplainMerged = false)
    {
        return JsonSerializer.Serialize(new
        {
            providers = new object[]
            {
                new
                {
                    id = "openai",
                    name = "OpenAI",
                    protocol = "ChatCompletions",
                    baseUri = "https://api.openai.com/v1",
                    apiKeyProtected = string.Empty,
                    models = new[] { "gpt-4.1-mini" },
                    selectedModel = "gpt-4.1-mini",
                    visionModels = new[] { "gpt-4.1-mini" },
                    visionModelsConfigured = true,
                    supportsReasoningControl = true,
                    reasoningEffort = "high"
                }
            },
            functions,
            selectedProviderId = "openai",
            functionSetConsolidated = consolidated,
            answerExplainMerged = answerExplainMerged
        });
    }

    public static object Fn(string id, string mode, string name, string prompt)
    {
        return new
        {
            id,
            mode,
            name,
            description = string.Empty,
            glyph = "\uE8BD",
            systemPrompt = prompt
        };
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "askany-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
