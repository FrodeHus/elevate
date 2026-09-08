using Elevate.App.Notifications;
using Elevate.Core.Auth;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;

namespace Elevate.App.ViewModels;

/// <summary>
/// Access packages (entitlement management) per tenant: polling with a diff against the stored
/// snapshot, notifications, the new-role tracker, and the on-demand reads the window makes. Port
/// of <c>AppModel+AccessPackages.swift</c>.
/// </summary>
public sealed partial class AppModel
{
    /// <summary>A panel open polls a tenant at most this often; the window's refresh button forces it.</summary>
    public static readonly TimeSpan AccessPackagePanelThrottle = TimeSpan.FromMinutes(15);

    /// <summary>The background tick. Never the one-minute active-assignment timer: this data changes slowly.</summary>
    public static readonly TimeSpan AccessPackageBackgroundInterval = TimeSpan.FromHours(8);

    /// <summary>The provider, rebuilt with the coordinator when the client id changes.</summary>
    internal IAccessPackageProvider Packages { get; private set; } = null!;

    /// <summary>The last poll failure per tenant; cleared by the next successful poll.</summary>
    public Dictionary<TenantKey, string> AccessPackageErrors { get; } = [];

    /// <summary>Tenants with a poll in flight; the window shows a spinner for them.</summary>
    public HashSet<TenantKey> AccessPackagesPolling { get; } = [];

    private static IAccessPackageProvider MakeAccessPackageProvider(IHttpClient http, ITokenProvider tokens) => new AccessPackageProvider(http, tokens);

    // MARK: Reads

    public AccessPackageSnapshot? AccessPackageSnapshot(TenantKey key) => State.AccessPackagesFor(key)?.Snapshot;

    public DateTimeOffset? AccessPackagesPolledAt(TenantKey key) => State.AccessPackagesFor(key)?.PolledAt;

    /// <summary>Whether the tenant's Graph token carries the entitlement scope, so the entry point shows.</summary>
    public bool AccessPackagesAvailable(TenantKey key) => Tenant(key)?.AccessPackagesAvailable == true;

    /// <summary>The refused-permission message, when the last poll of this tenant was refused; else null.</summary>
    public string? AccessPackageConsentError(TenantKey key) =>
        AccessPackageErrors.GetValueOrDefault(key) is { } message && message == ConsentMessage ? message : null;

    /// <summary>What a consent refusal reads as, so the window can tell it from other poll failures.</summary>
    public static string ConsentMessage { get; } = new PimException(PimErrorKind.ConsentRequired).UserMessage;

    /// <summary>Tenants whose token carries the entitlement scope.</summary>
    private IEnumerable<TenantContext> AccessPackageTenants => State.Tenants.Where(t => t.AccessPackagesAvailable == true);

    /// <summary>
    /// Whether the cached Graph token for this tenant carries the entitlement scope. False for the
    /// first-party apps, which never do; null when nothing can be told without a prompt, so the
    /// caller keeps the previous answer. Mirror of the macOS <c>probeAccessPackages</c>.
    /// </summary>
    internal async Task<bool?> ProbeAccessPackagesAsync(Identity identity, string tenantId)
    {
        if (!identity.SignInMethod.IsPreauthorisedForEntraActivation)
        {
            return false;
        }

        string token;
        try
        {
            token = await Tokens.AccessTokenAsync(identity, tenantId, Scopes.EntitlementAll, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }

        return AccessTokenClaims.PermitsEntitlementSelfService(token);
    }

    // MARK: Polling

    /// <summary>Polls every eligible tenant that has not been polled within the throttle window.</summary>
    public async Task PollAccessPackagesIfDueAsync(bool force = false)
    {
        if (!Bootstrapped || !IsOnline)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var due = AccessPackageTenants
            .Where(t => force || AccessPackagesPolledAt(t.Key) is not { } at || now - at > AccessPackagePanelThrottle)
            .Select(t => t.Key)
            .ToList();
        await Task.WhenAll(due.Select(PollAccessPackagesAsync));
    }

    /// <summary>Reads requests and assignments for one tenant, notifies about what changed, persists.</summary>
    public async Task PollAccessPackagesAsync(TenantKey key)
    {
        if (Identity(key.IdentityId) is not { } identity || Tenant(key) is not { } tenant || DeclinedTenants.Contains(key))
        {
            return;
        }

        if (!AccessPackagesPolling.Add(key))
        {
            return;
        }

        var generation = ConfigGeneration;
        Touch();
        try
        {
            var provider = Packages;
            var requests = await AcquireAsync(key, identity, provider.Scopes, () => provider.MyRequestsAsync(identity, key.TenantId));
            var assignments = await AcquireAsync(key, identity, provider.Scopes, () => provider.MyAssignmentsAsync(identity, key.TenantId));
            if (generation != ConfigGeneration || Tenant(key) is null)
            {
                return;
            }

            var current = new AccessPackageSnapshot(requests, assignments);
            var previous = AccessPackageSnapshot(key);
            var events = AccessPackageDiff.Events(previous, current, DateTimeOffset.UtcNow);
            State.SetAccessPackages(key, current, DateTimeOffset.UtcNow);
            AccessPackageErrors.Remove(key);
            Persist();
            foreach (var e in events)
            {
                await NotifyAsync(e, tenant);
            }

            await ReschedulePackageExpiriesAsync();
        }
        catch (OperationCanceledException)
        {
            // The tenant was removed while it was being read.
        }
        catch (Exception e)
        {
            if (generation != ConfigGeneration)
            {
                return;
            }

            var message = Describe(e);
            AccessPackageErrors[key] = message;
            LogError($"Access packages in {tenant.DisplayName}: {message}");
        }
        finally
        {
            AccessPackagesPolling.Remove(key);
            Touch();
        }
    }

    private async Task NotifyAsync(AccessPackageEvent e, TenantContext tenant)
    {
        // An assignment with a known end already has a timed toast from the notifier; a second one
        // from the poll would repeat it. Only an end the service never told us about needs this.
        if (e is AccessPackageEvent.Expired { Assignment.ExpiresAt: not null })
        {
            return;
        }

        var title = e switch
        {
            AccessPackageEvent.Approved => "Access package approved",
            AccessPackageEvent.Denied => "Access package denied",
            AccessPackageEvent.DeliveryFailed => "Access package delivery failed",
            AccessPackageEvent.Revoked => "Access package revoked",
            _ => "Access package expired",
        };
        try
        {
            await Notifier.NotifyAsync(title, $"{e.PackageName} in {tenant.DisplayName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError($"Notifications: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits out a poll for this tenant already in flight, then starts a fresh one. A request or
    /// cancel made while a poll is running would otherwise never land in the snapshot, because
    /// <see cref="PollAccessPackagesAsync"/> returns at once when one is already running.
    /// </summary>
    private async Task WaitForPollThenPollAsync(TenantKey key)
    {
        for (var i = 0; i < 50 && AccessPackagesPolling.Contains(key); i++)
        {
            await Task.Delay(100);
        }

        await PollAccessPackagesAsync(key);
    }

    /// <summary>
    /// Every delivered assignment with an end date, across tenants, handed to the notifier so the
    /// expiry notification fires on time even between polls.
    /// </summary>
    internal async Task ReschedulePackageExpiriesAsync()
    {
        var expiries = new List<PackageExpiry>();
        foreach (var record in State.AccessPackages)
        {
            var tenantName = Tenant(record.TenantKey)?.DisplayName ?? record.TenantKey.TenantId;
            foreach (var a in record.Snapshot.Assignments)
            {
                if (a.State == AccessPackageAssignmentState.Delivered && a.ExpiresAt is { } end)
                {
                    expiries.Add(new PackageExpiry(a.Id, a.PackageName, tenantName, end));
                }
            }
        }

        try
        {
            await Notifier.SetPackageExpiriesAsync(expiries);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Notifications: {e.Message}");
        }
    }

    // MARK: Window data

    public Task<IReadOnlyList<AccessPackage>> RequestablePackagesAsync(TenantKey key)
    {
        var identity = Require(key);
        var provider = Packages;
        return AcquireAsync(key, identity, provider.Scopes, () => provider.RequestablePackagesAsync(identity, key.TenantId));
    }

    public Task<IReadOnlyList<PolicyRequirement>> PackageRequirementsAsync(TenantKey key, string packageId)
    {
        var identity = Require(key);
        var provider = Packages;
        return AcquireAsync(key, identity, provider.Scopes, () => provider.RequirementsAsync(packageId, identity, key.TenantId));
    }

    /// <summary>Submits the request, then re-polls so the Requested tab shows it.</summary>
    public async Task RequestPackageAsync(TenantKey key, string packageId, string? policyId, string justification)
    {
        var identity = Require(key);
        var provider = Packages;
        await AcquireAsync(key, identity, provider.Scopes, () => provider.RequestAsync(packageId, policyId, justification, identity, key.TenantId));
        await WaitForPollThenPollAsync(key);
    }

    public async Task CancelPackageRequestAsync(TenantKey key, string requestId)
    {
        var identity = Require(key);
        var provider = Packages;
        await AcquireAsync(key, identity, provider.Scopes, async () =>
        {
            await provider.CancelAsync(requestId, identity, key.TenantId);
            return true;
        });
        await WaitForPollThenPollAsync(key);
    }

    private Identity Require(TenantKey key) =>
        Identity(key.IdentityId) ?? throw new PimException(PimErrorKind.Unexpected, "That account is no longer signed in.");

    // MARK: New roles

    /// <summary>Whether the panel marks this role as new since the last discovery.</summary>
    public bool IsRoleNew(RoleKey key) => State.RoleTrackerFor(key.TenantKey).IsNew(key);

    /// <summary>
    /// Feeds one tenant's discovered roles to its tracker; additions are announced once, named
    /// alphabetically, and marked in the panel until the second open.
    /// </summary>
    internal async Task ObserveDiscoveredRolesAsync(TenantKey key, IReadOnlyList<EligibleRole> discovered)
    {
        if (Tenant(key) is null)
        {
            return;
        }

        var (tracker, added) = State.RoleTrackerFor(key).Observe(discovered.Select(r => r.Key).ToHashSet());
        State.SetRoleTracker(key, tracker);
        Persist();
        if (added.Count == 0)
        {
            return;
        }

        var names = discovered.Where(r => added.Contains(r.Key)).Select(r => r.DisplayName).OrderBy(n => n, StringComparer.Ordinal);
        var tenantName = Tenant(key)?.DisplayName ?? key.TenantId;
        try
        {
            await Notifier.NotifyAsync($"New roles available in {tenantName}", string.Join(", ", names));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogError($"Notifications: {e.Message}");
        }
    }

    /// <summary>Counts a panel open on every tenant's tracker; the second open clears the markers.</summary>
    private void CountPanelOpenForTrackers()
    {
        var changed = false;
        foreach (var tenant in State.Tenants)
        {
            var before = State.RoleTrackerFor(tenant.Key);
            var tracker = before.PanelOpened();
            if (!tracker.Equals(before))
            {
                State.SetRoleTracker(tenant.Key, tracker);
                changed = true;
            }
        }

        // A panel open before bootstrap finishes must not write a default state over the saved file.
        if (changed && Bootstrapped)
        {
            Persist();
        }
    }

    // MARK: Timer

    private async Task RunAccessPackageTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(AccessPackageBackgroundInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (IsOnline)
                {
                    await PollAccessPackagesIfDueAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
