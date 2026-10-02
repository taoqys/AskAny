using System.Security.Cryptography;
using AskAny.Models;

namespace AskAny.Services;

public sealed class ConfigService
{
    private static readonly byte[] Entropy = "AskAny.Config.v1"u8.ToArray();
    private readonly string _configPath;

    public ConfigService()
        : this(null)
    {
    }

    // 传入路径时从该文件读写，用于 --dump-config 这类诊断；默认仍是 %APPDATA%\AskAny\config.json。
    public ConfigService(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            _configPath = configPath;
            return;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AskAny");
        Directory.CreateDirectory(directory);
        _configPath = Path.Combine(directory, "config.json");
    }

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return Normalize(new AppConfig());
        }

        try
        {
            await using var stream = File.OpenRead(_configPath);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(
                             stream,
                             JsonDefaults.Options,
                             cancellationToken) ?? new AppConfig();
            return Normalize(config);
        }
        catch (JsonException)
        {
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var temporaryPath = _configPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonDefaults.Options, cancellationToken);
        }

        File.Move(temporaryPath, _configPath, true);
    }

    private static AppConfig Normalize(AppConfig config)
    {
        config.Functions = NormalizeFunctions(config.Functions);
        config.Functions = ConsolidateFunctions(config, config.Functions);
        config.Functions = MergeAnswerAndExplain(config, config.Functions);

        if (config.Providers.Count == 0)
        {
            var isDeepSeek = config.OpenAiBaseUri.Contains(
                "deepseek",
                StringComparison.OrdinalIgnoreCase);
            var migratedProvider = ProviderCatalog.CreatePreset(
                isDeepSeek ? "deepseek-responses" : "openai-chat");
            migratedProvider.BaseUri = string.IsNullOrWhiteSpace(config.OpenAiBaseUri)
                ? migratedProvider.BaseUri
                : config.OpenAiBaseUri;
            migratedProvider.SelectedModel = string.IsNullOrWhiteSpace(config.Model)
                ? migratedProvider.SelectedModel
                : config.Model;
            migratedProvider.Models = migratedProvider.Models
                .Append(migratedProvider.SelectedModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            migratedProvider.ApiKeyProtected = config.OpenAiApiKeyProtected;
            config.Providers = [migratedProvider];
            config.SelectedProviderId = migratedProvider.Id;
        }

        foreach (var provider in config.Providers)
        {
            provider.Models = provider.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (provider.Models.Count == 0)
            {
                provider.Models = ["model-name"];
            }

            if (string.IsNullOrWhiteSpace(provider.SelectedModel) ||
                !provider.Models.Contains(provider.SelectedModel, StringComparer.OrdinalIgnoreCase))
            {
                provider.SelectedModel = provider.Models[0];
            }

            if (string.IsNullOrWhiteSpace(provider.ReasoningEffort))
            {
                provider.ReasoningEffort = "high";
            }

            if (!provider.VisionModelsConfigured)
            {
                provider.VisionModels = provider.BaseUri.Contains(
                    "api.openai.com",
                    StringComparison.OrdinalIgnoreCase)
                    ? provider.Models.ToList()
                    : [];
                provider.VisionModelsConfigured = true;
            }
            else
            {
                provider.VisionModels = (provider.VisionModels ?? [])
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var selected = config.Providers.FirstOrDefault(
            provider => provider.Id.Equals(config.SelectedProviderId, StringComparison.OrdinalIgnoreCase));
        config.SelectedProviderId = selected?.Id ?? config.Providers[0].Id;
        return config;
    }

    // 把默认的三个检索项（联网解释 / 新闻追踪 / 知乎搜索）并成一个「联网检索」，只做一次。
    // 规则刻意保守，避免毁掉用户的自定义提示词：
    //   · 提示词与所属模式的默认值一致 → 视为「未改动」，可以直接并；
    //   · 恰好只有一项被改过 → 三项全并，并把那段提示词（连同它的来源）带到「联网检索」上；
    //   · 两项以上被改过 → 只并未改动的，改过的原样保留。宁可不到 4 项，也不删用户内容。
    private static List<FunctionOption> ConsolidateFunctions(
        AppConfig config,
        List<FunctionOption> functions)
    {
        if (config.FunctionSetConsolidated)
        {
            return functions;
        }

        config.FunctionSetConsolidated = true;

        var searchItems = functions
            .Select((function, index) => (function, index))
            .Where(item => FunctionCatalog.IsLegacySearchMode(item.function.Mode))
            .ToList();

        // 没有历史检索项（新装或已并过）→ 不动。
        if (searchItems.Count == 0)
        {
            return functions;
        }

        var customized = searchItems
            .Where(item => !HasDefaultPrompt(item.function))
            .ToList();

        if (customized.Count >= 2)
        {
            var untouched = searchItems.Where(item => HasDefaultPrompt(item.function)).ToList();
            return untouched.Count == 0
                ? functions
                : ReplaceWithNetwork(functions, untouched, carriedPrompt: null, carriedSource: null);
        }

        var single = customized.Count == 1 ? customized[0].function : null;
        return ReplaceWithNetwork(
            functions,
            searchItems,
            carriedPrompt: single?.SystemPrompt,
            carriedSource: single is null
                ? null
                : FunctionCatalog.SearchBackendForLegacyMode(single.Mode));
    }

    // 「回答」与「解释」只差一段系统提示词，检索 / 推理 / 温度 / 多轮上下文全部相同，
    // 因此合并成一项。只在两者同时存在时动手：只剩一个时「合并」本身没有意义，
    // 不该去改用户留下的那一个。
    private static List<FunctionOption> MergeAnswerAndExplain(
        AppConfig config,
        List<FunctionOption> functions)
    {
        if (config.AnswerExplainMerged)
        {
            return functions;
        }

        config.AnswerExplainMerged = true;

        var answer = functions.FirstOrDefault(function => function.Mode == WorkflowMode.Answer);
        var explain = functions.FirstOrDefault(function => function.Mode == WorkflowMode.Explain);
        if (answer is null || explain is null)
        {
            return functions;
        }

        // 保留 Answer 那一项（名称 / 图标 / 位置都不动），提示词换成合并后的默认值。
        answer.SystemPrompt = FunctionCatalog.GetDefaultSystemPrompt(WorkflowMode.Answer);
        return functions.Where(function => !ReferenceEquals(function, explain)).ToList();
    }

    private static bool HasDefaultPrompt(FunctionOption function)
    {
        return string.Equals(
            function.SystemPrompt,
            FunctionCatalog.GetDefaultSystemPrompt(function.Mode),
            StringComparison.Ordinal);
    }

    private static List<FunctionOption> ReplaceWithNetwork(
        List<FunctionOption> functions,
        List<(FunctionOption function, int index)> merged,
        string? carriedPrompt,
        SearchBackend? carriedSource)
    {
        var insertAt = merged.Min(item => item.index);
        var mergedSet = merged.Select(item => item.function).ToHashSet();
        var network = FunctionCatalog.CreateNetwork();

        if (!string.IsNullOrWhiteSpace(carriedPrompt))
        {
            network.SystemPrompt = carriedPrompt;
        }

        if (carriedSource is { } source)
        {
            network.SearchBackend = source;
        }

        var result = new List<FunctionOption>();
        for (var index = 0; index < functions.Count; index++)
        {
            if (index == insertAt)
            {
                result.Add(network);
            }

            if (!mergedSet.Contains(functions[index]))
            {
                result.Add(functions[index]);
            }
        }

        return result;
    }

    private static List<FunctionOption> NormalizeFunctions(List<FunctionOption>? configuredFunctions)
    {
        if (configuredFunctions is null || configuredFunctions.Count == 0)
        {
            return FunctionCatalog.CreateDefaultFunctions();
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var functions = new List<FunctionOption>();

        foreach (var configuredFunction in configuredFunctions)
        {
            var function = configuredFunction?.Clone() ?? FunctionCatalog.CreateCustom();
            if (string.IsNullOrWhiteSpace(function.Id) || !usedIds.Add(function.Id))
            {
                function.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(function.Id);
            }

            function.Name = string.IsNullOrWhiteSpace(function.Name)
                ? FunctionCatalog.GetModeName(function.Mode)
                : function.Name.Trim();
            function.Description = function.Description?.Trim() ?? string.Empty;
            function.Glyph = string.IsNullOrWhiteSpace(function.Glyph)
                ? "\uE8BD"
                : function.Glyph;
            function.SystemPrompt = string.IsNullOrWhiteSpace(function.SystemPrompt)
                ? FunctionCatalog.GetDefaultSystemPrompt(function.Mode)
                : function.SystemPrompt.Trim();

            // 老配置没有 SearchBackend 字段：由历史检索模式推导，否则这些项的检索能力会静默消失。
            // 「联网检索」模式不推导 —— 它的来源由用户显式选择，推导会把「不检索」覆盖掉。
            if (function.SearchBackend == SearchBackend.None &&
                FunctionCatalog.IsLegacySearchMode(function.Mode))
            {
                function.SearchBackend = FunctionCatalog.SearchBackendForLegacyMode(function.Mode);
            }

            functions.Add(function);
        }

        return functions;
    }

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
