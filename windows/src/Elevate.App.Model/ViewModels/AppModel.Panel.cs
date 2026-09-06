using Elevate.App.Services;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.App.ViewModels;

/// <summary>Pivots, collapse state, search, visibility and selection. Port of <c>AppModel+Panel.swift</c>.</summary>
public sealed partial class AppModel
{
    // MARK: Tabs and collapse state

    /// <summary>The list the panel shows; the bulk selection survives switching so a selection can span pivots.</summary>
    public PanelTab PanelTab
    {
        get => Settings.PanelTab;
        set
        {
            if (Settings.PanelTab != value)
            {
                Settings.PanelTab = value;
                OnPropertyChanged();
                Touch();
            }
        }
    }

    public bool CollapsedActive => Settings.CollapsedActive;

    public void ToggleActive()
    {
        Settings.CollapsedActive = !Settings.CollapsedActive;
        Touch();
    }

    public static string Title(PanelTab tab) => tab switch
    {
        PanelTab.Roles => "Entra",
        PanelTab.Azure => "Azure",
        _ => "Groups",
    };

    public static IReadOnlySet<RoleScopeKind> Kinds(PanelTab tab) => tab switch
    {
        PanelTab.Roles => EntraKinds,
        PanelTab.Azure => AzureKinds,
        _ => GroupKinds,
    };

    private static readonly HashSet<RoleScopeKind> EntraKinds = [RoleScopeKind.EntraDirectory];
    private static readonly HashSet<RoleScopeKind> AzureKinds = [RoleScopeKind.AzureResource];
    private static readonly HashSet<RoleScopeKind> GroupKinds = [RoleScopeKind.Group];

    public void ToggleTenant(TenantKey key)
    {
        if (!CollapsedTenants.Remove(key))
        {
            CollapsedTenants.Add(key);
        }

        Touch();
    }

    public void ToggleIdentity(string id)
    {
        if (!CollapsedIdentities.Remove(id))
        {
            CollapsedIdentities.Add(id);
        }

        Touch();
    }

    // MARK: Search

    public bool IsFiltering => PanelFilter.IsActive(SearchQuery);

    private bool MatchesFilter(EligibleRole role)
    {
        if (!IsFiltering)
        {
            return true;
        }

        var tenantName = Tenant(role.Key.TenantKey)?.DisplayName ?? role.Key.TenantId;
        var upn = Identity(role.Key.IdentityId)?.Upn ?? string.Empty;
        return PanelFilter.Matches(SearchQuery, role, tenantName, upn);
    }

    // MARK: Visibility

    public IReadOnlyList<EligibleRole> RolesFor(TenantKey tenantKey, PanelTab tab)
    {
        var kinds = Kinds(tab);
        return [.. RolesFor(tenantKey).Where(r => kinds.Contains(r.Key.Scope.Kind) && MatchesFilter(r))];
    }

    /// <summary>While filtering, only tenants with a matching row in the current pivot; otherwise all of them.</summary>
    public IReadOnlyList<TenantContext> VisibleTenants(string identityId)
    {
        var all = TenantsFor(identityId);
        if (!IsFiltering)
        {
            return all;
        }

        return [.. all.Where(t => RolesFor(t.Key, PanelTab).Count > 0)];
    }

    public IReadOnlyList<Identity> VisibleIdentities
    {
        get
        {
            if (!IsFiltering)
            {
                return Identities;
            }

            return [.. Identities.Where(i => VisibleTenants(i.Id).Count > 0)];
        }
    }

    // MARK: Summary

    /// <summary>Name for the summary row; before the eligible list has loaded only the key is known.</summary>
    public string SummaryName(RoleKey key)
    {
        if (Role(key) is { } r)
        {
            return r.DisplayName;
        }

        return key.Scope switch
        {
            EntraDirectoryScope e => e.RoleDefinitionId,
            AzureResourceScope a => $"{a.RoleDefinitionId.Split('/').LastOrDefault() ?? a.RoleDefinitionId} @ {a.Scope}",
            GroupScope g => $"{g.GroupId} ({(g.AccessId == GroupAccess.Owner ? "owner" : "member")})",
            _ => key.ToString(),
        };
    }

    /// <summary>Active assignments of the pivot's kinds, for the pivot labels' counts.</summary>
    public int ActiveCount(PanelTab tab)
    {
        var kinds = Kinds(tab);
        return Active.Values.Count(a => a.Status.Kind == AssignmentStatusKind.Active && kinds.Contains(a.RoleKey.Scope.Kind));
    }

    /// <summary>Active roles of one tenant in the current pivot, for the header's "N active".</summary>
    public int ActiveCount(TenantKey key) =>
        RolesFor(key, PanelTab).Count(r => Assignment(r.Key)?.Status.Kind == AssignmentStatusKind.Active);

    /// <summary>The "Active now" summary shows only the current pivot's kinds; the pivot labels carry the other counts.</summary>
    public IReadOnlyList<ActiveAssignment> ActiveAssignmentsOrdered
    {
        get
        {
            var kinds = Kinds(PanelTab);
            var ordered = ActiveSummary.Order(Active.Values.Where(a => kinds.Contains(a.RoleKey.Scope.Kind)));
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
    }

    // MARK: Selection

    public int SelectionCount => Selection.Count;

    /// <summary>Per-kind counts of the bulk selection, for the cross-pivot hint in the bulk bar.</summary>
    public (int Entra, int Azure, int Groups) SelectionBreakdown
    {
        get
        {
            int entra = 0, azure = 0, groups = 0;
            foreach (var key in Selection)
            {
                switch (key.Scope.Kind)
                {
                    case RoleScopeKind.EntraDirectory:
                        entra++;
                        break;
                    case RoleScopeKind.AzureResource:
                        azure++;
                        break;
                    default:
                        groups++;
                        break;
                }
            }

            return (entra, azure, groups);
        }
    }

    /// <summary>Noun for the bulk bar: roles and groups can be selected together across pivots.</summary>
    public string SelectionNoun
    {
        get
        {
            var kinds = Selection.Select(k => k.Scope.Kind).ToHashSet();
            if (kinds.Count == 0)
            {
                return "role";
            }

            if (kinds.Count == 1 && kinds.Contains(RoleScopeKind.Group))
            {
                return "group";
            }

            return kinds.Contains(RoleScopeKind.Group) ? "item" : "role";
        }
    }

    public void ToggleSelection(RoleKey key)
    {
        if (!CanActivate(key))
        {
            return;
        }

        if (!Selection.Remove(key))
        {
            Selection.Add(key);
        }

        Touch();
    }
}
