using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Elevate.App.Auth;

/// <summary>
/// The MSAL token cache on disk: one DPAPI-protected file under <c>%LOCALAPPDATA%\Elevate</c>
/// shared by every public client (MSAL keys entries by client id, so the own-app, first-party and
/// custom registrations never see each other's tokens). Registered lazily on first use.
/// </summary>
public sealed class TokenCache
{
    public const string FileName = "msal.cache";

    private readonly Lazy<Task<MsalCacheHelper>> _helper;

    public TokenCache(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
        _helper = new Lazy<Task<MsalCacheHelper>>(() =>
        {
            System.IO.Directory.CreateDirectory(directory);
            // No WithUnprotectedFile: the file is always DPAPI-protected on Windows.
            var properties = new StorageCreationPropertiesBuilder(FileName, directory).Build();
            return MsalCacheHelper.CreateAsync(properties);
        });
    }

    public string Directory { get; }

    /// <summary>Attaches the on-disk cache to <paramref name="app"/>'s user token cache.</summary>
    public async Task RegisterAsync(IPublicClientApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var helper = await _helper.Value.ConfigureAwait(false);
        helper.RegisterCache(app.UserTokenCache);
    }
}

/// <summary>
/// Serialises interactive sign-ins across every provider, so a broker dialog and a browser flow
/// queue instead of racing each other. Port of the macOS <c>InteractiveGate</c>.
/// </summary>
public sealed class InteractiveGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
