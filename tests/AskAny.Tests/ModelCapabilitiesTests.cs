using System.Linq;
using AskAny.Models;
using AskAny.Services;
using Xunit;

namespace AskAny.Tests;

// 能力表主数据是内置的 models.dev 快照；快照里没有的新模型走命名启发式。
public class ModelCapabilitiesTests
{
    [Fact]
    public void SnapshotIsLoaded()
    {
        // 快照应覆盖数千个模型；如果资源没被打进程序集，这里会退化到 0。
        Assert.True(ModelCapabilities.SnapshotSize > 1000, $"快照只有 {ModelCapabilities.SnapshotSize} 条");
    }

    [Theory]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("gpt-5.1", true)]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("deepseek-chat", false)]
    public void VisionCapabilityMatchesSnapshot(string modelId, bool expected)
    {
        Assert.Equal(expected, ModelCapabilities.Infer(modelId).SupportsVision);
    }

    [Theory]
    [InlineData("gpt-5.1", true)]
    [InlineData("claude-sonnet-4-5", true)]
    [InlineData("deepseek-chat", false)]
    public void ReasoningCapabilityMatchesSnapshot(string modelId, bool expected)
    {
        Assert.Equal(expected, ModelCapabilities.Infer(modelId).SupportsReasoning);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        Assert.Equal(
            ModelCapabilities.Infer("gpt-4.1-mini"),
            ModelCapabilities.Infer("GPT-4.1-MINI"));
    }

    [Fact]
    public void EmptyIdIsUnknown()
    {
        Assert.Equal(ModelCapability.Unknown, ModelCapabilities.Infer("  "));
    }

    // 快照之外（更新）的模型按命名规则兜底。
    [Fact]
    public void UnknownModelFallsBackToNamingRules()
    {
        var visionModel = ModelCapabilities.Infer("some-vendor-vl-2027");
        Assert.True(visionModel.SupportsVision);

        var reasoningModel = ModelCapabilities.Infer("acme-reasoner-2");
        Assert.True(reasoningModel.SupportsReasoning);

        var plainModel = ModelCapabilities.Infer("acme-basic-1");
        Assert.False(plainModel.SupportsVision);
        Assert.False(plainModel.SupportsReasoning);
    }

    [Fact]
    public void ReasoningFamilyIsTreatedAsVisionCapableToo()
    {
        var o1 = ModelCapabilities.Infer("o1-mini");
        Assert.True(o1.SupportsReasoning);
        Assert.True(o1.SupportsVision);
    }
}
