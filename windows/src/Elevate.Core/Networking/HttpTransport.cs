using System.Net.Http.Headers;
using System.Text;
using Elevate.Core.Models;

namespace Elevate.Core.Networking;

/// <summary>One outgoing HTTP call. Port of the Swift <c>HTTPRequest</c>.</summary>
public sealed record HttpRequestData(
    string Method,
    Uri Url,
    IReadOnlyDictionary<string, string> Headers,
    byte[]? Body)
{
    public HttpRequestData(string method, Uri url)
        : this(method, url, new Dictionary<string, string>(), null)
    {
    }
}

/// <summary>One HTTP reply. Port of the Swift <c>HTTPResponse</c>.</summary>
public sealed record HttpResponseData(
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public HttpResponseData(int status)
        : this(status, new Dictionary<string, string>(), [])
    {
    }

    /// <summary>Case-insensitive header lookup, like the Swift <c>header(_:)</c>.</summary>
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    public string BodyText => Encoding.UTF8.GetString(Body);
}

/// <summary>The transport seam every provider talks through, so tests can stub the network.</summary>
public interface IHttpClient
{
    Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct);
}

/// <summary>
/// The production <see cref="IHttpClient"/>, over <see cref="HttpClient"/>. Failures other than
/// cancellation become <see cref="PimErrorKind.Network"/>, matching the Swift URLSession client.
/// </summary>
public sealed class HttpClientAdapter : IHttpClient
{
    private readonly HttpClient _client;

    public HttpClientAdapter(HttpClient? client = null) => _client = client ?? new HttpClient();

    public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);

        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var (name, value) in request.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Collect(response.Headers, headers);
            Collect(response.Content.Headers, headers);

            var content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            return new HttpResponseData((int)response.StatusCode, headers, content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PimException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new PimException(PimErrorKind.Network, e.Message);
        }
    }

    private static void Collect(HttpHeaders source, Dictionary<string, string> target)
    {
        foreach (var (name, values) in source)
        {
            target[name] = string.Join(", ", values);
        }
    }
}
