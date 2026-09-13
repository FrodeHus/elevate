using Elevate.Core.Networking;

namespace Elevate.Audit.Networking;

/// <summary>`--verbose`: one line per request on stderr, after the reply.</summary>
public sealed class LoggingHttpClient(IHttpClient inner, Action<string> log) : IHttpClient
{
    public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        var response = await inner.SendAsync(request, ct).ConfigureAwait(false);
        log($"{request.Method} {request.Url} → {response.Status}");
        return response;
    }
}
