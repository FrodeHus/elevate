using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.App.Auth;

/// <summary>
/// Routes every token operation to the provider that owns the identity's sign-in method:
/// <c>ownApp</c> to the own-app (MSAL + broker) provider, <c>ownApp:&lt;client id&gt;</c> to that
/// client id's provider from the pinned registry, every other method to its first-party or
/// custom-client provider. Port of the macOS <c>CompositeTokenProvider</c>.
/// </summary>
public sealed class CompositeTokenProvider : ITokenProvider
{
    private readonly IOwnAppTokenProvider? _ownApp;
    private readonly IFirstPartyProviders _firstParty;
    private readonly IPinnedProviders? _pinned;

    public CompositeTokenProvider(IOwnAppTokenProvider? ownApp, IFirstPartyProviders firstParty, IPinnedProviders? pinned = null)
    {
        ArgumentNullException.ThrowIfNull(firstParty);
        _ownApp = ownApp;
        _firstParty = firstParty;
        _pinned = pinned;
    }

    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct) =>
        Provider(method).SignInAsync(method, ct);

    public Task SignOutAsync(Identity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).SignOutAsync(identity, ct);
    }

    /// <summary>
    /// Every account any provider knows, distinct by id. Unlike macOS, the first-party providers
    /// keep MSAL caches of their own here, so their accounts are enumerable too.
    /// </summary>
    public async Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct)
    {
        var all = new List<Identity>();
        if (_ownApp is not null)
        {
            all.AddRange(await _ownApp.IdentitiesAsync(ct).ConfigureAwait(false));
        }

        foreach (var provider in _firstParty.Known)
        {
            all.AddRange(await provider.IdentitiesAsync(ct).ConfigureAwait(false));
        }

        foreach (var provider in _pinned?.Known ?? [])
        {
            all.AddRange(await provider.IdentitiesAsync(ct).ConfigureAwait(false));
        }

        return [.. all.DistinctBy(i => i.Id)];
    }

    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct)
        => AccessTokenAsync(identity, tenantId, scopes, forceRefresh: false, ct);

    /// <summary>Every provider behind this one is MSAL-backed, so a forced refresh always reaches one.</summary>
    public bool CanForceRefresh => true;

    public Task<string> AccessTokenAsync(
        Identity identity, string tenantId, IReadOnlyList<string> scopes, bool forceRefresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).AccessTokenAsync(identity, tenantId, scopes, forceRefresh, ct);
    }

    public Task<string> AcquireInteractivelyAsync(
        Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Provider(identity.SignInMethod).AcquireInteractivelyAsync(identity, tenantId, scopes, claims, ct);
    }

    private ITokenProvider Provider(SignInMethod method)
    {
        if (method.PinnedClientId is { } pinnedClientId)
        {
            return (_pinned ?? throw new PimException(PimErrorKind.Unexpected, "Sign-in is unavailable in this build"))
                .Provider(pinnedClientId)
                ?? throw new PimException(PimErrorKind.Unexpected, "Enter the application (client) ID as a GUID");
        }

        if (method.UsesMsal)
        {
            return _ownApp ?? throw new PimException(PimErrorKind.Unexpected, "Configure a client id in Settings");
        }

        return _firstParty.Provider(method)
            ?? throw new PimException(PimErrorKind.Unexpected, "Unsupported sign-in method");
    }
}
