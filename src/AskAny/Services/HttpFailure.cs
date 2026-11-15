using System.Net;

namespace AskAny.Services;

// 把 HTTP 失败翻译成「下一步该做什么」，而不是把原始 payload 甩给用户。
// AiService 与 ModelCatalogService 共用同一套措辞，避免同一个错误在两处说法不一致。
public static class HttpFailure
{
    public static string Describe(HttpStatusCode status, string payload)
    {
        var code = (int)status;
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "认证失败：请到「设置 → 模型服务」检查 API Key。",
            HttpStatusCode.NotFound =>
                "接口地址或模型不存在：请检查「接口地址」与模型名。",
            HttpStatusCode.TooManyRequests =>
                "触发限流或额度不足：稍后重试，或检查账户额度。",
            HttpStatusCode.RequestEntityTooLarge =>
                "请求内容过大：减少图片或开启新对话后重试。",
            HttpStatusCode.BadRequest =>
                "请求被拒绝：模型名或参数可能不被该提供商支持。",
            _ when code >= 500 =>
                "服务端错误：稍后重试。",
            _ => null
        };

        var detail = Shorten(payload);
        return hint is null
            ? $"模型请求失败（{code}）：{detail}"
            : $"模型请求失败（{code}）：{hint}　原始响应：{detail}";
    }

    public static string Shorten(string value, int maximum = 320)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }
}
