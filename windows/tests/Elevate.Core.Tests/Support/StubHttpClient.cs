using System.Text;
using Elevate.Core.Networking;

namespace Elevate.Core.Tests.Support;

/// <summary>
/// Routes requests by HTTP method plus a substring of the URL; the last matching registration wins.
/// Records every request. Port of the Swift <c>StubHTTPClient</c>.
/// </summary>
public sealed class StubHttpClient : IHttpClient
{
    private sealed record Route(string Method, string UrlContains, Func<HttpRequestData, HttpResponseData> Respond);

    private readonly List<Route> _routes = [];
    private readonly List<HttpRequestData> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<HttpRequestData> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public void On(
        string method,
        string urlContains,
        int status = 200,
        IReadOnlyDictionary<string, string>? headers = null,
        byte[]? body = null)
    {
        var response = new HttpResponseData(status, headers ?? new Dictionary<string, string>(), body ?? []);
        On(method, urlContains, _ => response);
    }

    public void On(string method, string urlContains, string body, int status = 200)
        => On(method, urlContains, status, null, Encoding.UTF8.GetBytes(body));

    public void On(string method, string urlContains, Func<HttpRequestData, HttpResponseData> respond)
    {
        lock (_gate)
        {
            _routes.Add(new Route(method, urlContains, respond));
        }
    }

    public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        Route? route;
        lock (_gate)
        {
            _requests.Add(request);
            route = _routes.FindLast(r =>
                r.Method == request.Method && request.Url.AbsoluteUri.Contains(r.UrlContains, StringComparison.Ordinal));
        }

        if (route is null)
        {
            var body = Encoding.UTF8.GetBytes($"no stub for {request.Method} {request.Url.AbsoluteUri}");
            return Task.FromResult(new HttpResponseData(599, new Dictionary<string, string>(), body));
        }

        return Task.FromResult(route.Respond(request));
    }

    public IReadOnlyList<HttpRequestData> RequestsMatching(string substring)
    {
        lock (_gate)
        {
            return [.. _requests.Where(r => r.Url.AbsoluteUri.Contains(substring, StringComparison.Ordinal))];
        }
    }
}
