using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.App.Auth;

/// <summary>
/// The provider behind the own-app registration. Besides the token operations it can drop accounts
/// from its local cache without a browser round trip, which a client-id change needs.
/// </summary>
public interface IOwnAppTokenProvider : ITokenProvider
{
    /// <summary>Removes the given accounts from this client's local token cache silently.</summary>
    Task RemoveCachedAccountsAsync(IEnumerable<Identity> identities, CancellationToken ct);
}

/// <summary>
/// The providers for the first-party (Azure CLI, Azure PowerShell) and custom client ids, created
/// on demand per client id and kept for the life of the process.
/// </summary>
public interface IFirstPartyProviders
{
    /// <summary>The provider for a non-own-app method, or null for <see cref="SignInMethodKind.OwnApp"/>.</summary>
    ITokenProvider? Provider(SignInMethod method);

    /// <summary>Every provider created so far, for account enumeration.</summary>
    IReadOnlyCollection<ITokenProvider> Known { get; }
}
