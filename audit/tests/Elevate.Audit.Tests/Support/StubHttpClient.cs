using System.Text;
using Elevate.Core.Networking;

namespace Elevate.Audit.Tests.Support;

/// <summary>Routes requests by HTTP method plus a URL substring; the last matching registration wins. Records every request.</summary>
public sealed class StubHttpClient : IHttpClient
{
    private sealed record Route(string Method, string UrlContains, Func<HttpRequestData, HttpResponseData> Respond);

    private readonly List<Route> _routes = [];
    private readonly List<HttpRequestData> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<HttpRequestData> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    public IReadOnlyList<HttpRequestData> RequestsMatching(string urlContains) =>
        Requests.Where(r => r.Url.AbsoluteUri.Contains(urlContains, StringComparison.Ordinal)).ToList();

    public void On(string method, string urlContains, string body, int status = 200) =>
        On(method, urlContains, _ => new HttpResponseData(status, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(body)));

    public void On(string method, string urlContains, Func<HttpRequestData, HttpResponseData> respond)
    {
        lock (_gate) { _routes.Add(new Route(method, urlContains, respond)); }
    }

    public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        lock (_gate)
        {
            _requests.Add(request);
            var route = _routes.LastOrDefault(r =>
                string.Equals(r.Method, request.Method, StringComparison.OrdinalIgnoreCase)
                && request.Url.AbsoluteUri.Contains(r.UrlContains, StringComparison.Ordinal));
            return Task.FromResult(route?.Respond(request)
                ?? new HttpResponseData(599, new Dictionary<string, string>(), Encoding.UTF8.GetBytes($"no stub for {request.Method} {request.Url}")));
        }
    }
}
