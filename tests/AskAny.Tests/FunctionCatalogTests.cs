using System;
using System.Linq;
using AskAny.Models;
using AskAny.Services;
using Xunit;

namespace AskAny.Tests;

public class FunctionCatalogTests
{
    [Fact]
    public void DefaultsAreThreeItems()
    {
        var functions = FunctionCatalog.CreateDefaultFunctions();

        Assert.Equal(3, functions.Count);
        Assert.Equal(
            new[] { WorkflowMode.Answer, WorkflowMode.Think, WorkflowMode.SearchNetwork },
            functions.Select(function => function.Mode).ToArray());
    }

    [Fact]
    public void NetworkDefaultSearchesTheWholeWeb()
    {
        var network = FunctionCatalog.CreateNetwork();

        Assert.Equal(WorkflowMode.SearchNetwork, network.Mode);
        Assert.Equal(SearchBackend.TavilyGeneral, network.SearchBackend);
    }

    [Fact]
    public void NonSearchDefaultsDoNotSearch()
    {
        var functions = FunctionCatalog.CreateDefaultFunctions();

        foreach (var function in functions.Where(item => item.Mode != WorkflowMode.SearchNetwork))
        {
            Assert.Equal(SearchBackend.None, function.SearchBackend);
        }
    }

    [Theory]
    [InlineData(WorkflowMode.TrackNews, SearchBackend.TavilyNews)]
    [InlineData(WorkflowMode.ExplainOnline, SearchBackend.TavilyGeneral)]
    [InlineData(WorkflowMode.ZhihuSearch, SearchBackend.Zhihu)]
    [InlineData(WorkflowMode.Answer, SearchBackend.None)]
    [InlineData(WorkflowMode.SearchNetwork, SearchBackend.None)]
    public void LegacyModesMapToSearchBackends(WorkflowMode mode, SearchBackend expected)
    {
        Assert.Equal(expected, FunctionCatalog.SearchSourceForLegacyMode(mode));
    }

    [Theory]
    [InlineData(WorkflowMode.TrackNews, true)]
    [InlineData(WorkflowMode.ExplainOnline, true)]
    [InlineData(WorkflowMode.ZhihuSearch, true)]
    [InlineData(WorkflowMode.SearchNetwork, false)]
    [InlineData(WorkflowMode.Answer, false)]
    [InlineData(WorkflowMode.Think, false)]
    public void LegacySearchModeDetection(WorkflowMode mode, bool expected)
    {
        Assert.Equal(expected, FunctionCatalog.IsLegacySearchMode(mode));
    }

    // 合并后「回答」要同时覆盖回答与解释，兜底提示词必须能表达这两件事。
    [Fact]
    public void MergedAnswerPromptCoversAnsweringAndExplaining()
    {
        var prompt = FunctionCatalog.GetDefaultSystemPrompt(WorkflowMode.Answer);

        Assert.Contains("回答", prompt);
        Assert.Contains("解释", prompt);
    }

    [Fact]
    public void SearchBackendNamesAreLocalised()
    {
        Assert.Equal("全网", FunctionCatalog.GetSearchBackendName(SearchBackend.TavilyGeneral));
        Assert.Equal("新闻", FunctionCatalog.GetSearchBackendName(SearchBackend.TavilyNews));
        Assert.Equal("知乎", FunctionCatalog.GetSearchBackendName(SearchBackend.Zhihu));
        Assert.Equal("不检索", FunctionCatalog.GetSearchBackendName(SearchBackend.None));
    }
}
