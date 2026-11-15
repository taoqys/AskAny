using System;
using System.Linq;
using System.Threading.Tasks;
using AskAny.Models;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

// 「设置里勾选启用、主界面只出现已启用」这套语义要稳：
// 空集合代表全部启用（老配置），子集代表只启用这些，越界项必须被丢掉。
public class ModelEnablementTests
{
    private static ProviderConfig Provider(params string[] models)
    {
        var provider = new ProviderConfig
        {
            Id = "p",
            Name = "P",
            BaseUri = "https://api.example.com/v1"
        };
        provider.Models = models.ToList();
        provider.SelectedModel = models.FirstOrDefault() ?? string.Empty;
        return provider;
    }

    // 构造一个最小但合法的配置：这些用例只关心 providers 与快捷键字段。
    private static AppConfig NewConfig(ProviderConfig provider)
    {
        return new AppConfig
        {
            Providers = [provider],
            SelectedProviderId = provider.Id,
            FunctionSetConsolidated = true,
            AnswerExplainMerged = true
        };
    }

    [Fact]
    public void EmptyEnabledMeansAllModels()
    {
        var provider = Provider("a", "b", "c");

        Assert.Equal(new[] { "a", "b", "c" }, provider.EffectiveModels().ToArray());
    }

    [Fact]
    public void SubsetIsRespected()
    {
        var provider = Provider("a", "b", "c");
        provider.EnabledModels = ["c", "a"];

        Assert.Equal(new[] { "c", "a" }, provider.EffectiveModels().ToArray());
    }

    [Fact]
    public void DuplicatesAndBlanksAreRemoved()
    {
        var provider = Provider("a", "b");
        provider.EnabledModels = ["a", " ", "A"];

        Assert.Equal(new[] { "a" }, provider.EffectiveModels().ToArray());
    }

    [Fact]
    public async Task ConfigNormalizeDropsEnabledModelsThatNoLongerExist()
    {
        var provider = Provider("a", "b");
        provider.EnabledModels = ["a", "ghost-model"];

        var normalized = await ConfigFixture.NormalizeAsync(NewConfig(provider));

        Assert.Equal(new[] { "a" }, normalized.Providers[0].EnabledModels);
    }

    [Fact]
    public async Task ConfigNormalizeCollapsesSelectAllToEmpty()
    {
        var provider = Provider("a", "b");
        provider.EnabledModels = ["a", "b"];

        var normalized = await ConfigFixture.NormalizeAsync(NewConfig(provider));

        // 全选等价于「全部启用」，统一存成空集合，语义只有一种。
        Assert.Empty(normalized.Providers[0].EnabledModels);
    }

    [Fact]
    public async Task SelectedModelFallsBackIntoEnabledSet()
    {
        var provider = Provider("a", "b");
        provider.SelectedModel = "a";
        provider.EnabledModels = ["b"];

        var normalized = await ConfigFixture.NormalizeAsync(NewConfig(provider));

        // 选中的模型没启用时回落到第一个启用的，避免主界面指向一个不存在的 tab。
        Assert.Equal("b", normalized.Providers[0].SelectedModel);
    }

    // 手改配置把间隔写成 0 或 10 分钟，都不该让手势失灵或过于迟缓。
    [Theory]
    [InlineData(0, 120)]
    [InlineData(50, 120)]
    [InlineData(400, 400)]
    [InlineData(99999, 550)]
    public async Task HotkeyIntervalIsClamped(int configured, int expected)
    {
        var config = NewConfig(Provider("a"));
        config.HotkeyDoubleTapIntervalMs = configured;

        var normalized = await ConfigFixture.NormalizeAsync(config);

        Assert.Equal(expected, normalized.HotkeyDoubleTapIntervalMs);
    }

    [Fact]
    public void CloneCopiesEnabledModelsIndependently()
    {
        var provider = Provider("a", "b");
        provider.EnabledModels = ["b"];

        var clone = provider.Clone();
        clone.EnabledModels.Add("a");

        Assert.Equal(new[] { "b" }, provider.EnabledModels);
        Assert.Equal(2, clone.EnabledModels.Count);
    }

    // 上下文长度来自能力快照，用来给多轮历史定预算。
    [Fact]
    public void ContextTokensAreKnownForSnapshotModels()
    {
        var capability = ModelCapabilities.Infer("glm-5.3-flashx");

        Assert.NotNull(capability.ContextTokens);
        Assert.True(capability.ContextTokens > 0);
    }

    [Fact]
    public void UnknownModelHasNoContextTokens()
    {
        Assert.Null(ModelCapabilities.Infer("totally-unknown-model-9000").ContextTokens);
    }

    [Fact]
    public async Task HotkeyModifiersSurviveRoundTrip()
    {
        var config = NewConfig(Provider("a"));
        config.SelectionHotkeyModifier = HotkeyModifier.Alt;
        config.ScreenshotHotkeyModifier = HotkeyModifier.Shift;
        config.ScreenshotHotkeyEnabled = false;

        var normalized = await ConfigFixture.NormalizeAsync(config);

        Assert.Equal(HotkeyModifier.Alt, normalized.SelectionHotkeyModifier);
        Assert.Equal(HotkeyModifier.Shift, normalized.ScreenshotHotkeyModifier);
        Assert.False(normalized.ScreenshotHotkeyEnabled);
    }
}
