using System.Collections.Generic;
using AskAny.Models;
using Xunit;

namespace AskAny.Tests;

// 设置窗口编辑的是配置副本，保存成功后才由磁盘回读；Clone 漏字段就会静默丢配置。
public class AppConfigCloneTests
{
    private static AppConfig BuildPopulated()
    {
        return new AppConfig
        {
            Providers = new List<ProviderConfig>
            {
                new()
                {
                    Id = "p1",
                    Name = "P1",
                    Protocol = ApiProtocol.Responses,
                    BaseUri = "https://example.com/v1",
                    ApiKeyProtected = "CIPHER",
                    Models = new List<string> { "m1", "m2" },
                    SelectedModel = "m2",
                    VisionModels = new List<string> { "m1" },
                    VisionModelsConfigured = true,
                    SupportsReasoningControl = false,
                    ReasoningEffort = "low"
                }
            },
            Functions = new List<FunctionOption>
            {
                new()
                {
                    Id = "f1",
                    Mode = WorkflowMode.SearchNetwork,
                    Name = "联网检索",
                    Description = "描述",
                    Glyph = "\uE774",
                    SystemPrompt = "提示词",
                    IconOffsetX = 1.5,
                    IconOffsetY = -2.5,
                    SearchBackend = SearchBackend.Zhihu
                }
            },
            SelectedProviderId = "p1",
            TavilyApiKeyProtected = "TAVILY",
            ZhihuAccessSecretProtected = "ZHIHU",
            KeepWindowOnTop = false,
            HideWhenDeactivated = false,
            AutoFillSelectedText = false,
            StartWithWindows = true,
            ScreenshotHotkeyEnabled = false,
            FunctionSetConsolidated = true,
            AnswerExplainMerged = true,
            OpenAiBaseUri = "https://legacy.example.com/v1",
            Model = "legacy-model",
            OpenAiApiKeyProtected = "LEGACY"
        };
    }

    [Fact]
    public void CloneCopiesEveryScalarField()
    {
        var original = BuildPopulated();
        var clone = original.Clone();

        Assert.Equal(original.SelectedProviderId, clone.SelectedProviderId);
        Assert.Equal(original.TavilyApiKeyProtected, clone.TavilyApiKeyProtected);
        Assert.Equal(original.ZhihuAccessSecretProtected, clone.ZhihuAccessSecretProtected);
        Assert.Equal(original.KeepWindowOnTop, clone.KeepWindowOnTop);
        Assert.Equal(original.HideWhenDeactivated, clone.HideWhenDeactivated);
        Assert.Equal(original.AutoFillSelectedText, clone.AutoFillSelectedText);
        Assert.Equal(original.StartWithWindows, clone.StartWithWindows);
        Assert.Equal(original.ScreenshotHotkeyEnabled, clone.ScreenshotHotkeyEnabled);
        Assert.Equal(original.FunctionSetConsolidated, clone.FunctionSetConsolidated);
        Assert.Equal(original.AnswerExplainMerged, clone.AnswerExplainMerged);
        Assert.Equal(original.OpenAiBaseUri, clone.OpenAiBaseUri);
        Assert.Equal(original.Model, clone.Model);
        Assert.Equal(original.OpenAiApiKeyProtected, clone.OpenAiApiKeyProtected);
    }

    [Fact]
    public void CloneCopiesNestedFunctionFields()
    {
        var original = BuildPopulated();
        var clone = original.Clone();
        var function = Assert.Single(clone.Functions);

        Assert.Equal("f1", function.Id);
        Assert.Equal(WorkflowMode.SearchNetwork, function.Mode);
        Assert.Equal("联网检索", function.Name);
        Assert.Equal("描述", function.Description);
        Assert.Equal("\uE774", function.Glyph);
        Assert.Equal("提示词", function.SystemPrompt);
        Assert.Equal(1.5, function.IconOffsetX);
        Assert.Equal(-2.5, function.IconOffsetY);
        Assert.Equal(SearchBackend.Zhihu, function.SearchBackend);
    }

    [Fact]
    public void CloneIsDeepEnoughThatEditingItLeavesTheOriginalAlone()
    {
        var original = BuildPopulated();
        var clone = original.Clone();

        clone.Providers[0].Name = "改过的名字";
        clone.Providers[0].Models.Add("m3");
        clone.Functions[0].Name = "改过的功能";
        clone.Functions[0].SearchBackend = SearchBackend.TavilyNews;
        clone.TavilyApiKeyProtected = "改过的密文";

        Assert.Equal("P1", original.Providers[0].Name);
        Assert.Equal(2, original.Providers[0].Models.Count);
        Assert.Equal("联网检索", original.Functions[0].Name);
        Assert.Equal(SearchBackend.Zhihu, original.Functions[0].SearchBackend);
        Assert.Equal("TAVILY", original.TavilyApiKeyProtected);
    }
}
