using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AskAny.Tests.Support;

// 用假 handler 驱动真实的请求构造与响应解析，不需要网络也不需要真实密钥。
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _payload;
    private readonly string _contentType;

    public StubHttpMessageHandler(
        HttpStatusCode status,
        string payload,
        string contentType = "application/json")
    {
        _status = status;
        _payload = payload;
        _contentType = contentType;
    }

    public string? LastRequestBody { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestHeaders = request.Headers;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_payload, Encoding.UTF8, _contentType)
        };
    }
}
