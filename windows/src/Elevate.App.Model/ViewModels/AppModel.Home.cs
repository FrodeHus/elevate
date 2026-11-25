using Elevate.App.Services;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

public enum TenantMarkKind { None, Active, Error }

/// <summary>What the sidebar shows beside a tenant: its active count, else an error dot.</summary>
public readonly record struct TenantMark(TenantMarkKind Kind, int Count);

/// <summary>Data behind Home and the sidebar. Port of <c>AppModel+Home.swift</c>.</summary>
public sealed partial class AppModel
{
    /// <summary>Entra refuses to deactivate a role for this long after it started.</summary>
    public static readonly TimeSpan DeactivationLock = TimeSpan.FromMinutes(5);

    /// <summary>
    /// "Active now": every kind whatever the tab, soonest first, narrowed by the panel search and
    /// optionally to one tenant; null is every tenant.
    /// </summary>
    public IReadOnlyList<ActiveAssignment> ActiveAssignments(TenantKey? tenant)
    {
        var ordered = ActiveSummary.Order(Active.Values.Where(a => tenant is not { } t || a.RoleKey.TenantKey == t));
        if (!IsFiltering)
        {
            return ordered;
        }

        return
        [
            .. ordered.Where(a => Role(a.RoleKey) is { } r
                ? MatchesFilter(r)
                : PanelFilter.Matches(SearchQuery, SummaryName(a.RoleKey))),
        ];
    }

    /// <summary>The condition under which a row offers Extend: past the deactivation lock, inside the extend window, no approval needed.</summary>
    public bool CanExtendNow(RoleKey key, DateTimeOffset now) =>
        Assignment(key) is { } a
        && DeactivationLock - (now - a.StartDateTime) <= TimeSpan.Zero
        && ExtendWindow.CanExtend(a, Role(key)?.Policy ?? RolePolicy.ManualDefault, now);

    /// <summary>The soonest-expiring active row, when it is the one Extend applies to.</summary>
    public ActiveAssignment? HeroAssignment(DateTimeOffset now) =>
        ActiveAssignments(null).FirstOrDefault() is { } first && CanExtendNow(first.RoleKey, now) ? first : null;

    public bool HomeIsEmpty => ApprovalsIn(null).Count == 0 && ActiveAssignments(null).Count == 0;

    /// <summary>Every entry Active now can show, whatever the filter, so it matches the per-tenant marks.</summary>
    public int TotalActiveCount => ActiveSummary.Order(Active.Values).Count;

    public bool ProfilesCollapsed => Settings.CollapsedProfiles;

    public bool TenantsCollapsed => Settings.CollapsedTenants;

    public void ToggleProfilesSection()
    {
        Settings.CollapsedProfiles = !Settings.CollapsedProfiles;
        Touch();
    }

    public void ToggleTenantsSection()
    {
        Settings.CollapsedTenants = !Settings.CollapsedTenants;
        Touch();
    }

    /// <summary>The warning glyph on a collapsed Tenants header: an account must sign in again or a tenant failed.</summary>
    public bool TenantsNeedAttention =>
        Identities.Any(i => NeedsSignIn(i.Id)) || TenantErrors.Count > 0 || Identities.SelectMany(i => TenantsFor(i.Id)).Any(t => t.LastDiscoveryError is not null);

    public TenantMark SidebarMark(TenantKey key)
    {
        var n = ActiveSummary.Order(Active.Values.Where(a => a.RoleKey.TenantKey == key)).Count;
        return n > 0 ? new TenantMark(TenantMarkKind.Active, n)
            : (TenantErrors.GetValueOrDefault(key) ?? Tenant(key)?.LastDiscoveryError) is not null ? new TenantMark(TenantMarkKind.Error, 0)
            : new TenantMark(TenantMarkKind.None, 0);
    }
}
