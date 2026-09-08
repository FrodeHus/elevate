using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Cli.Session;

/// <summary>What one tenant reported for the signed-in user: the packages they may request, their requests and assignments.</summary>
public sealed record TenantPackages(
    TenantKey Key,
    IReadOnlyList<AccessPackage> Packages,
    IReadOnlyList<AccessPackageRequest> Requests,
    IReadOnlyList<AccessPackageAssignment> Assignments);

/// <summary>
/// Access packages (entitlement management) per tenant. A headless port of the macOS
/// <c>AppModel+AccessPackages</c> without the polling, the diff and the notifications: every
/// command reads the service directly and nothing is stored beyond the tenant's availability flag.
/// </summary>
public sealed partial class ElevateSession
{
    /// <summary>Why access packages cannot be read in this tenant, or null when they can be tried.</summary>
    public string? AccessPackagesUnavailableReason(TenantKey key)
    {
        if (Identity(key.IdentityId) is not { } identity)
        {
            return "That account is no longer signed in";
        }

        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return $"The {identity.SignInMethod.DisplayName} cannot read access packages; sign in with your own app registration.";
        }

        if (Tenant(key)?.AccessPackagesAvailable == false)
        {
            return "Access packages are not permitted in this tenant. Sign in again to consent to EntitlementMgmt-SubjectAccess.ReadWrite, or use 'elevate tenants retry'.";
        }

        return null;
    }

    /// <summary>Tracked tenants that may hold access packages, narrowed by the account and tenant filters.</summary>
    public IReadOnlyList<TenantKey> AccessPackageTenants(string? account, string? tenant) =>
        [.. Tenants
            .Where(t => string.IsNullOrWhiteSpace(account) || AccountMatches(t.IdentityId, account))
            .Where(t => string.IsNullOrWhiteSpace(tenant)
                || t.DisplayName.Contains(tenant, StringComparison.OrdinalIgnoreCase)
                || t.TenantId.Equals(tenant, StringComparison.OrdinalIgnoreCase))
            .Where(t => AccessPackagesUnavailableReason(t.Key) is null)
            .Select(t => t.Key)];

    private bool AccountMatches(string identityId, string account) =>
        Identity(identityId) is { } i
        && (i.Upn.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.DisplayName.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.Id.Equals(account, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the user's requests and assignments (and the requestable packages when asked). A
    /// success marks the tenant available; a consent or permission refusal marks it unavailable
    /// and rethrows so the command can say so.
    /// </summary>
    public async Task<TenantPackages> ReadPackagesAsync(TenantKey key, bool includePackages, CancellationToken ct)
    {
        var (identity, tenant) = Require(key);
        try
        {
            var requests = await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.MyRequestsAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false);
            var assignments = await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.MyAssignmentsAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false);
            IReadOnlyList<AccessPackage> packages = includePackages
                ? await Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequestablePackagesAsync(identity, key.TenantId, ct), ct).ConfigureAwait(false)
                : [];
            if (tenant.AccessPackagesAvailable != true)
            {
                Upsert(tenant with { AccessPackagesAvailable = true });
            }

            return new TenantPackages(key, packages, requests, assignments);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.ConsentRequired or PimErrorKind.Forbidden)
        {
            if (tenant.AccessPackagesAvailable != false)
            {
                Upsert(tenant with { AccessPackagesAvailable = false });
            }

            throw;
        }
    }

    public Task<IReadOnlyList<PolicyRequirement>> PackageRequirementsAsync(TenantKey key, string packageId, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequirementsAsync(packageId, identity, key.TenantId, ct), ct);
    }

    public Task<AccessPackageRequest> RequestPackageAsync(TenantKey key, string packageId, string? policyId, string justification, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, () => Packages.RequestAsync(packageId, policyId, justification, identity, key.TenantId, ct), ct);
    }

    public Task CancelPackageRequestAsync(TenantKey key, string requestId, CancellationToken ct)
    {
        var (identity, _) = Require(key);
        return Acquire(identity, key.TenantId, Packages.Scopes, async () =>
        {
            await Packages.CancelAsync(requestId, identity, key.TenantId, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// Whether the cached Graph token for this tenant carries the entitlement scope. False for the
    /// first-party apps, which never do; null when nothing can be told without a prompt.
    /// Mirror of the macOS <c>probeAccessPackages</c>.
    /// </summary>
    public async Task<bool?> ProbeAccessPackagesAsync(Identity identity, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return false;
        }

        string token;
        try
        {
            token = await Tokens.AccessTokenAsync(identity, tenantId, Scopes.EntitlementAll, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        return AccessTokenClaims.PermitsEntitlementSelfService(token);
    }

    private (Identity Identity, TenantContext Tenant) Require(TenantKey key)
    {
        var identity = Identity(key.IdentityId) ?? throw new PimException(PimErrorKind.Unexpected, "That account is no longer signed in");
        var tenant = Tenant(key) ?? throw new PimException(PimErrorKind.Unexpected, "That tenant is not tracked");
        return (identity, tenant);
    }
}
