using Elevate.Cli.Infrastructure;
using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Cli.Auth;

/// <summary>
/// Routes every token operation to the provider for the identity's sign-in method: the own-app
/// client id from settings, or the Azure CLI, Azure PowerShell or custom client id the account was
/// added with. Providers are created on first use, one per client id, over one shared cache.
/// </summary>
public sealed class CliTokenProvider : ITokenProvider
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, MsalCliProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenCacheStore _cache;
    private readonly InteractiveGate _gate = new();
    private readonly InteractiveFlow _flow;
    private readonly Action<string> _say;
    private readonly Func<string> _ownAppClientId;

    public CliTokenProvider(TokenCacheStore cache, Func<string> ownAppClientId, InteractiveFlow flow, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(ownAppClientId);
        ArgumentNullException.ThrowIfNull(say);
        _cache = cache;
        _ownAppClientId = ownAppClientId;
        _flow = flow;
        _say = say;
    }

    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default) =>
        Provider(method).SignInAsync(method, ct);

    public Task SignOutAsync(Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).SignOutAsync(identity, ct);
    }

    public async Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default)
    {
        List<MsalCliProvider> known;
        lock (_lock)
        {
            known = [.. _providers.Values];
        }

        var all = new List<Identity>();
        foreach (var provider in known)
        {
            all.AddRange(await provider.IdentitiesAsync(ct).ConfigureAwait(false));
        }

        return [.. all.DistinctBy(i => i.Id)];
    }

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).AccessTokenAsync(identity, tenantId, scopes, ct);
    }

    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).AcquireInteractivelyAsync(identity, tenantId, scopes, claims, ct);
    }

    /// <summary>Whether a method can sign in right now; the own app needs a client id in settings.</summary>
    public bool IsAvailable(SignInMethod method) => ClientIdFor(method) is not null;

    /// <summary>Makes sure the provider for an identity exists, so its account can be enumerated.</summary>
    public void Touch(SignInMethod method)
    {
        if (ClientIdFor(method) is not null)
        {
            _ = Provider(method);
        }
    }

    private string? ClientIdFor(SignInMethod method)
    {
        if (method.UsesMsal)
        {
            var id = _ownAppClientId();
            return CliSettings.IsValidClientId(id) ? id.Trim() : null;
        }

        return CliSettings.IsValidClientId(method.ClientId) ? method.ClientId!.Trim() : null;
    }

    private MsalCliProvider Provider(SignInMethod method)
    {
        var clientId = ClientIdFor(method) ?? throw new CliException(
            method.UsesMsal
                ? "No client ID is configured. Run 'elevate config set client-id <application id>' first, or sign in with --method cli."
                : "That sign-in method has no usable client ID.",
            ExitCodes.Usage);
        lock (_lock)
        {
            var key = method.UsesMsal ? "own:" + clientId : clientId;
            if (!_providers.TryGetValue(key, out var provider))
            {
                provider = new MsalCliProvider(method, clientId, _cache, _gate, _flow, _say);
                _providers[key] = provider;
            }

            return provider;
        }
    }
}
