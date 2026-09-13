using System.Globalization;
using Elevate.Core.Networking;

namespace Elevate.Audit.Networking;

/// <summary>
/// Retries 429 and 503 replies, waiting for <c>Retry-After</c> (seconds or an HTTP date), capped at
/// 60 s, or 2, 4, 8… seconds when the header is absent. Core's transport maps a 429 straight to an
/// error, and a tenant-wide scan hits the Graph throttle routinely, so this sits under it.
/// </summary>
public sealed class RetryingHttpClient(
    IHttpClient inner,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int maxAttempts = 5) : IHttpClient
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken ct)
    {
        HttpResponseData response;
        var attempt = 0;
        while (true)
        {
            attempt++;
            response = await inner.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Status is not (429 or 503) || attempt >= maxAttempts)
            {
                return response;
            }

            await _delay(WaitFor(response, attempt), ct).ConfigureAwait(false);
        }
    }

    internal static TimeSpan WaitFor(HttpResponseData response, int attempt)
    {
        var wait = response.Header("Retry-After") switch
        {
            { } seconds when double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => TimeSpan.FromSeconds(Math.Max(0, s)),
            { } date when DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) => at - DateTimeOffset.UtcNow,
            _ => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        };
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        return wait > MaxWait ? MaxWait : wait;
    }
}
