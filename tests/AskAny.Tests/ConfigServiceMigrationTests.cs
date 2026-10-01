using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 配置迁移会改写用户的配置文件，是「一错就静默毁数据」的那类逻辑，因此覆盖得最细。
// 这里的用例都对应真实踩过的场景，见 ConfigService 里的三步迁移。
public class ConfigServiceMigrationTests
{
    private static string Prompt(WorkflowMode mode)
    {
        return FunctionCatalog.GetDefaultSystemPrompt(mode);
    }

    // 六个默认项（三个检索项提示词都未改动）应收敛到三项：
    // 检索三项并成「联网检索」，回答与解释并成一项。
    [Fact]
    public async Task DefaultSixItemConfigCollapsesToThree()
    {
        var json = ConfigFixture.BuildConfig(new object[]
        {
            ConfigFixture.Fn("explain", "Explain", "解释说明", Prompt(WorkflowMode.Explain)),
            ConfigFixture.Fn("answer", "Answer", "回答此问题", Prompt(WorkflowMode.Answer)),
            ConfigFixture.Fn("news", "TrackNews", "新闻追踪", Prompt(WorkflowMode.TrackNews)),
            ConfigFixture.Fn("think", "Think", "深度思考", Prompt(WorkflowMode.Think)),
            ConfigFixture.Fn("explain-online", "ExplainOnline", "联网解释", Prompt(WorkflowMode.ExplainOnline)),
            ConfigFixture.Fn("zhihu-search", "ZhihuSearch", "知乎搜索", Prompt(WorkflowMode.ZhihuSearch))
        });

        var config = await ConfigFixture.NormalizeAsync(json);

        Assert.Equal(3, config.Functions.Count);
        Assert.Equal(
            new[] { "回答此问题", "联网检索", "深度思考" },
            config.Functions.Select(function => function.Name).ToArray());

        var network = config.Functions[1];
        Assert.Equal(WorkflowMode.SearchNetwork, network.Mode);
        Assert.Equal(SearchBackend.TavilyGeneral, network.SearchBackend);

        Assert.True(config.FunctionSetConsolidated);
        Assert.True(config.AnswerExplainMerged);
    }

    // 三个检索项里恰好一项提示词被改过时，两项都不能丢：
    // 合并后的「联网检索」要带上那段提示词，以及它原本对应的来源。
    [Fact]
    public async Task SingleCustomizedSearchPromptIsCarriedToNetworkFunction()
    {
        const string customNews = "只保留最近发生的要点。";

        var json = ConfigFixture.BuildConfig(new object[]
        {
            ConfigFixture.Fn("answer", "Answer", "回答此问题", Prompt(WorkflowMode.Answer)),
            ConfigFixture.Fn("news", "TrackNews", "新闻追踪", customNews),
            ConfigFixture.Fn("think", "Think", "深度思考", Prompt(WorkflowMode.Think)),
            ConfigFixture.Fn("explain-online", "ExplainOnline", "联网解释", Prompt(WorkflowMode.ExplainOnline)),
            ConfigFixture.Fn("zhihu-search", "ZhihuSearch", "知乎搜索", Prompt(WorkflowMode.ZhihuSearch))
        });

        var config = await ConfigFixture.NormalizeAsync(json);

        var network = Assert.Single(
            config.Functions,
            function => function.Mode == WorkflowMode.SearchNetwork);

        Assert.Equal(customNews, network.SystemPrompt);
        Assert.Equal(SearchBackend.TavilyNews, network.SearchBackend);
    }

    // 两项以上被改过时，宁可不到目标项数，也不能删用户内容：
    // 只并未改动的，被改过的原样留下。
    [Fact]
    public async Task TwoOrMoreCustomizedSearchPromptsKeepThoseItems()
    {
        const string customNews = "自定义新闻提示词。";
        const string customOnline = "自定义联网提示词。";

        var json = ConfigFixture.BuildConfig(new object[]
        {
            ConfigFixture.Fn("explain", "Explain", "解释说明", Prompt(WorkflowMode.Explain)),
            ConfigFixture.Fn("answer", "Answer", "回答此问题", Prompt(WorkflowMode.Answer)),
            ConfigFixture.Fn("news", "TrackNews", "新闻追踪", customNews),
            ConfigFixture.Fn("think", "Think", "深度思考", Prompt(WorkflowMode.Think)),
            ConfigFixture.Fn("explain-online", "ExplainOnline", "联网解释", customOnline),
            ConfigFixture.Fn("zhihu-search", "ZhihuSearch", "知乎搜索", Prompt(WorkflowMode.ZhihuSearch))
        });

        var config = await ConfigFixture.NormalizeAsync(json);

        Assert.Equal(5, config.Functions.Count);
        Assert.Contains(config.Functions, function => function.SystemPrompt == customNews);
        Assert.Contains(config.Functions, function => function.SystemPrompt == customOnline);

        // 未被改动的知乎项被并入「联网检索」，来源可再切回来。
        var network = Assert.Single(
            config.Functions,
            function => function.Mode == WorkflowMode.SearchNetwork);
        Assert.Equal(SearchBackend.TavilyGeneral, network.SearchBackend);
    }

    // 合并「回答 + 解释」：保留 Answer 那一项的名称与位置，提示词换成合并后的默认值。
    [Fact]
    public async Task AnswerAndExplainMergeKeepsAnswerItem()
    {
        var json = ConfigFixture.BuildConfig(
            new object[]
            {
                ConfigFixture.Fn("explain", "Explain", "解释说明", "自定义解释提示词"),
                ConfigFixture.Fn("answer", "Answer", "我的回答", "自定义回答提示词")
            },
            consolidated: true);

        var config = await ConfigFixture.NormalizeAsync(json);

        var only = Assert.Single(config.Functions);
        Assert.Equal("我的回答", only.Name);
        Assert.Equal(WorkflowMode.Answer, only.Mode);
        Assert.Equal(Prompt(WorkflowMode.Answer), only.SystemPrompt);
    }

    // 只剩回答或只剩解释时不该动手：合并本身没有意义，不能去改用户留下的那一个。
    [Fact]
    public async Task MergeDoesNothingWhenOnlyOneOfThePairExists()
    {
        var json = ConfigFixture.BuildConfig(
            new object[] { ConfigFixture.Fn("explain", "Explain", "解释说明", "自定义解释提示词") },
            consolidated: true);

        var config = await ConfigFixture.NormalizeAsync(json);

        var only = Assert.Single(config.Functions);
        Assert.Equal(WorkflowMode.Explain, only.Mode);
        Assert.Equal("自定义解释提示词", only.SystemPrompt);
    }

    // 老配置没有 SearchBackend 字段，必须由历史 Mode 推导，否则检索能力会静默消失。
    [Fact]
    public async Task LegacySearchModeDerivesSearchBackend()
    {
        var json = ConfigFixture.BuildConfig(
            new object[] { ConfigFixture.Fn("news", "TrackNews", "新闻追踪", "自定义新闻提示词") },
            consolidated: true,
            answerExplainMerged: true);

        var config = await ConfigFixture.NormalizeAsync(json);

        var only = Assert.Single(config.Functions);
        Assert.Equal(SearchBackend.TavilyNews, only.SearchBackend);
    }

    [Fact]
    public async Task EmptyFunctionsGetTheThreeDefaults()
    {
        var config = await ConfigFixture.NormalizeAsync(
            ConfigFixture.BuildConfig(Array.Empty<object>()));

        Assert.Equal(3, config.Functions.Count);
        Assert.Contains(config.Functions, function => function.Mode == WorkflowMode.Answer);
        Assert.Contains(config.Functions, function => function.Mode == WorkflowMode.Think);
        Assert.Contains(config.Functions, function => function.Mode == WorkflowMode.SearchNetwork);
    }

    // providers 为空时用老字段迁移出提供商，且地址、模型、密文都要保住。
    [Fact]
    public async Task ProvidersEmptyMigratesFromLegacyFields()
    {
        var json = JsonSerializer.Serialize(new
        {
            providers = Array.Empty<object>(),
            functions = new object[]
            {
                ConfigFixture.Fn("answer", "Answer", "回答", "提示词")
            },
            openAiBaseUri = "https://api.deepseek.com",
            model = "deepseek-chat",
            openAiApiKeyProtected = "CIPHERTEXT"
        });

        var config = await ConfigFixture.NormalizeAsync(json);

        var provider = Assert.Single(config.Providers);
        Assert.Equal("https://api.deepseek.com", provider.BaseUri);
        Assert.Equal("deepseek-chat", provider.SelectedModel);
        Assert.Contains("deepseek-chat", provider.Models);
        Assert.Equal("CIPHERTEXT", provider.ApiKeyProtected);
        Assert.Equal(provider.Id, config.SelectedProviderId);
    }

    // 迁移必须幂等：第二次加载不能再改一次，否则用户每次启动配置都在变。
    [Fact]
    public async Task MigrationIsIdempotent()
    {
        var json = ConfigFixture.BuildConfig(new object[]
        {
            ConfigFixture.Fn("explain", "Explain", "解释说明", Prompt(WorkflowMode.Explain)),
            ConfigFixture.Fn("answer", "Answer", "回答此问题", Prompt(WorkflowMode.Answer)),
            ConfigFixture.Fn("news", "TrackNews", "新闻追踪", Prompt(WorkflowMode.TrackNews)),
            ConfigFixture.Fn("think", "Think", "深度思考", Prompt(WorkflowMode.Think)),
            ConfigFixture.Fn("explain-online", "ExplainOnline", "联网解释", Prompt(WorkflowMode.ExplainOnline)),
            ConfigFixture.Fn("zhihu-search", "ZhihuSearch", "知乎搜索", Prompt(WorkflowMode.ZhihuSearch))
        });

        var once = await ConfigFixture.NormalizeAsync(json);
        var twice = await ConfigFixture.NormalizeTwiceAsync(json);

        Assert.Equal(once.Functions.Count, twice.Functions.Count);
        Assert.Equal(
            once.Functions.Select(function => function.Name).ToArray(),
            twice.Functions.Select(function => function.Name).ToArray());
        Assert.Equal(
            once.Functions.Select(function => function.SearchBackend).ToArray(),
            twice.Functions.Select(function => function.SearchBackend).ToArray());
    }

    // 空名称/空模型不再被静默替换成占位符（这正是之前让校验变成死代码的原因）。
    [Fact]
    public async Task EmptyModelListFallsBackOnLoadButIsNotFabricatedAsPlaceholder()
    {
        var json = JsonSerializer.Serialize(new
        {
            providers = new object[]
            {
                new
                {
                    id = "p1",
                    name = "P1",
                    protocol = "ChatCompletions",
                    baseUri = "https://example.com/v1",
                    apiKeyProtected = string.Empty,
                    models = Array.Empty<string>(),
                    selectedModel = string.Empty
                }
            },
            functions = new object[] { ConfigFixture.Fn("answer", "Answer", "回答", "提示词") },
            selectedProviderId = "p1"
        });

        var config = await ConfigFixture.NormalizeAsync(json);

        var provider = Assert.Single(config.Providers);
        Assert.NotEmpty(provider.Models);
        Assert.Contains(provider.SelectedModel, provider.Models);
    }
}
