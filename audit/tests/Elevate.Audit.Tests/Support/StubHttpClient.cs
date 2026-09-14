using System.Text;
using Elevate.Core.Networking;

namespace Elevate.Audit.Tests.Support;

/// <summary>Routes requests by HTTP method plus a URL substring; the last matching registration wins. Records every request.</summary>
public sealed class StubHttpClient : IHttpClient
{
    private sealed record Route(string Method, string UrlContains, Func<HttpRequestData, Task<HttpResponseData>> Respond);

    private readonly List<Route> _routes = [];
    private readonly List<HttpRequestData> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<HttpRequestData> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    public IReadOnlyList<HttpRequestData> RequestsMatching(string urlContains) =>
        Requests.Where(r => Unescaped(r.Url).Contains(urlContains, StringComparison.Ordinal)).ToList();

    public void On(string method, string urlContains, string body, int status = 200) =>
        On(method, urlContains, _ => new HttpResponseData(status, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(body)));

    public void On(string method, string urlContains, Func<HttpRequestData, HttpResponseData> respond) =>
        On(method, urlContains, request => Task.FromResult(respond(request)));

    /// <summary>For tests that need a response to complete out of order relative to sibling requests (e.g. bounded-concurrency waves), such as one that awaits a delay before responding.</summary>
    public void On(string method, string urlContains, Func<HttpRequestData, Task<HttpResponseData>> respond)
    {
        lock (_gate) { _routes.Add(new Route(method, urlContains, respond)); }
    }

    public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        Route? route;
        lock (_gate)
        {
            _requests.Add(request);
            route = _routes.LastOrDefault(r =>
                string.Equals(r.Method, request.Method, StringComparison.OrdinalIgnoreCase)
                && Unescaped(request.Url).Contains(r.UrlContains, StringComparison.Ordinal));
        }

        return route is null
            ? Task.FromResult(new HttpResponseData(599, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($"no stub for {request.Method} {request.Url}")))
            : route.Respond(request);
    }

    /// <summary>
    /// Matches against the decoded URL: OData filters such as <c>groupId eq 'g1'</c> are registered with
    /// literal spaces and quotes, while <see cref="Uri.AbsoluteUri"/> percent-encodes them.
    /// </summary>
    private static string Unescaped(Uri url) => Uri.UnescapeDataString(url.AbsoluteUri);
}
