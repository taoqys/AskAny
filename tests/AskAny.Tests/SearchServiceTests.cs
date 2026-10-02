using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AskAny.Services;
using AskAny.Tests.Support;
using Xunit;

namespace AskAny.Tests;

public class SearchServiceTests
{
    private const string TavilyPayload = """
        {"results":[
          {"title":"标题","url":"https://example.com/a","content":"摘要","published_date":"2025-01-01"}
        ]}
        """;

    private const string ZhihuPayload = """
        {"Code":0,"Message":"success","Data":{"HasMore":false,"SearchHashId":"h","Items":[
          {"Title":"知乎标题","ContentType":"Article","ContentID":"1","ContentText":"知乎摘要",
           "Url":"https://zhuanlan.zhihu.com/p/1","CommentCount":3,"VoteUpCount":128,
           "AuthorName":"作者","EditTime":1710000000,"AuthorityLevel":"2","RankingScore":0.9}
        ]}}
        """;

    private static SearchService Build(StubHttpMessageHandler handler)
    {
        return new SearchService(new HttpClient(handler));
    }

    [Fact]
    public async Task TavilyResultsAreMapped()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TavilyPayload);

        var packet = await Build(handler).SearchAsync("查询", news: false, "key");

        var source = Assert.Single(packet.Sources);
        Assert.Equal("标题", source.Title);
        Assert.Equal("https://example.com/a", source.Url);
        Assert.Equal("摘要", source.Content);
        Assert.Equal("2025-01-01", source.PublishedDate);
    }

    [Fact]
    public async Task TavilyGeneralSearchUsesGeneralTopic()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TavilyPayload);

        await Build(handler).SearchAsync("查询", news: false, "key");

        Assert.Contains("\"topic\":\"general\"", handler.LastRequestBody);
        Assert.DoesNotContain("time_range", handler.LastRequestBody);
    }

    // 回归：新闻模式原来发 days=7，但 days 不在 Tavily 官方 API 里，时间窗从未生效。
    [Fact]
    public async Task TavilyNewsSearchUsesDocumentedTimeRangeInsteadOfDays()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TavilyPayload);

        await Build(handler).SearchAsync("查询", news: true, "key");

        Assert.Contains("\"topic\":\"news\"", handler.LastRequestBody);
        Assert.Contains("\"time_range\":\"week\"", handler.LastRequestBody);
        Assert.Contains("\"filter_by_published_date\":true", handler.LastRequestBody);
        Assert.DoesNotContain("\"days\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task TavilyMissingKeyIsRejected()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, TavilyPayload);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).SearchAsync("查询", news: false, "  "));
    }

    [Fact]
    public async Task ZhihuEnvelopeIsMapped()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ZhihuPayload);

        var packet = await Build(handler).SearchZhihuAsync("查询", "secret");

        var source = Assert.Single(packet.Sources);
        Assert.Equal("知乎标题", source.Title);
        Assert.Equal("https://zhuanlan.zhihu.com/p/1", source.Url);
        Assert.Equal("知乎摘要", source.Content);
        Assert.NotNull(source.PublishedDate);
        Assert.Equal(10, source.PublishedDate!.Length);
    }

    [Fact]
    public async Task ZhihuRequestSendsTimestampHeaderAndCappedCount()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ZhihuPayload);

        await Build(handler).SearchZhihuAsync("需要 编码的查询", "secret");

        Assert.True(handler.LastRequestHeaders!.Contains("X-Request-Timestamp"));
        Assert.Equal("Bearer", handler.LastRequestHeaders.Authorization!.Scheme);
        Assert.Contains("Count=10", handler.LastRequestUri!.Query);
        Assert.DoesNotContain(" ", handler.LastRequestUri.Query);
    }

    // 20001 既可能是密钥错，也可能是本机时钟偏差超过 10 分钟，提示必须同时点出两种成因。
    [Fact]
    public async Task ZhihuAuthErrorMentionsClockSkew()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":20001,"Message":"鉴权失败","Data":null}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).SearchZhihuAsync("查询", "secret"));

        Assert.Contains("20001", exception.Message);
        Assert.Contains("10 分钟", exception.Message);
    }

    [Fact]
    public async Task ZhihuRateLimitIsReportedSeparately()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":30001,"Message":"too many requests","Data":null}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).SearchZhihuAsync("查询", "secret"));

        Assert.Contains("30001", exception.Message);
    }

    [Fact]
    public async Task ZhihuMissingSecretIsRejected()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, ZhihuPayload);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).SearchZhihuAsync("查询", string.Empty));
    }

    // 没有链接的结果无法作为来源标注，直接丢弃。
    [Fact]
    public async Task ZhihuItemsWithoutUrlAreSkipped()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":0,"Message":"success","Data":{"Items":[{"Title":"没有链接","ContentText":"x","Url":"","EditTime":1710000000},{"Title":"有链接","ContentText":"y","Url":"https://zhihu.com/p/2","EditTime":1710000000}]}}""");

        var packet = await Build(handler).SearchZhihuAsync("查询", "secret");

        var source = Assert.Single(packet.Sources);
        Assert.Equal("有链接", source.Title);
    }

    [Fact]
    public async Task ZhihuEmptyResultSetIsNotAnError()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":0,"Message":"success","Data":{"Items":[],"EmptyReason":"no match"}}""");

        var packet = await Build(handler).SearchZhihuAsync("查询", "secret");

        Assert.Empty(packet.Sources);
    }

    [Fact]
    public async Task HttpFailureSurfacesStatusCode()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.ServiceUnavailable,
            "upstream down",
            "text/plain");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).SearchZhihuAsync("查询", "secret"));

        Assert.Contains("503", exception.Message);
    }

    // 知乎按成功调用计费，设置界面用这个接口显示剩余额度；它本身不消耗业务额度。
    [Fact]
    public async Task ZhihuQuotaIsParsed()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":0,"Message":"success","Data":[{"APIID":"zhihu_search","APIName":"知乎搜索","TotalQuota":500,"TotalUsed":12,"RemainingQuota":488}]}""");

        var items = await Build(handler).GetZhihuQuotaAsync("secret");

        var item = Assert.Single(items);
        Assert.Equal("zhihu_search", item.ApiId);
        Assert.Equal("知乎搜索", item.ApiName);
        Assert.Equal(500, item.TotalQuota);
        Assert.Equal(12, item.TotalUsed);
        Assert.Equal(488, item.RemainingQuota);
    }

    [Fact]
    public async Task ZhihuQuotaRequestCarriesAuthAndTimestamp()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":0,"Message":"success","Data":[]}""");

        await Build(handler).GetZhihuQuotaAsync("secret");

        Assert.Equal("Bearer", handler.LastRequestHeaders!.Authorization!.Scheme);
        Assert.True(handler.LastRequestHeaders.Contains("X-Request-Timestamp"));
        Assert.Contains("APIIDs=", handler.LastRequestUri!.Query);
    }

    [Fact]
    public async Task ZhihuQuotaEmptyDataIsNotAnError()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":0,"Message":"success","Data":[]}""");

        Assert.Empty(await Build(handler).GetZhihuQuotaAsync("secret"));
    }

    [Fact]
    public async Task ZhihuQuotaAuthErrorIsClassified()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"Code":20001,"Message":"鉴权失败","Data":null}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).GetZhihuQuotaAsync("secret"));

        Assert.Contains("10 分钟", exception.Message);
    }

    [Fact]
    public async Task ZhihuQuotaNeedsSecret()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(handler).GetZhihuQuotaAsync(string.Empty));
    }
}
