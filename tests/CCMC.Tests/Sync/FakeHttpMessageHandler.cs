using System.Net;

namespace CCMC.Tests.Sync;

/// <summary>
/// A deterministic, in-process fake for HttpClient's transport - no live
/// cloud server is required for any of these tests. Either returns a
/// scripted HttpResponseMessage, or invokes a scripted exception factory to
/// simulate a network failure/timeout without any real socket.
/// </summary>
internal sealed class FakeHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
    Func<HttpRequestMessage, Exception>? throwing = null) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;

        if (throwing is not null)
        {
            throw throwing(request);
        }

        return Task.FromResult(respond!(request));
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}
