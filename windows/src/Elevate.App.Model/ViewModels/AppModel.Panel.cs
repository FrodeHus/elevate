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

    // MARK: The Azure pivot's scope tree

    /// <summary>
    /// The Azure pivot's eligibilities for one tenant, as the management group / subscription /
    /// resource group / resource tree the scope strings already describe. Built from the filtered
    /// rows, so a search narrows the tree rather than sitting beside it.
    /// </summary>
    public IReadOnlyList<ScopeNode> AzureTree(TenantKey tenantKey) =>
        ScopeTree.Build(RolesFor(tenantKey, PanelTab.Azure));

    /// <summary>A scope node's collapsed state is per tenant: the same scope can be reached by two accounts.</summary>
    public static string ScopeNodeKey(TenantKey tenantKey, ScopeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return $"{tenantKey.IdentityId}|{tenantKey.TenantId}|{node.Scope}";
    }

    /// <summary>
    /// While a search is running every match stays visible in its place in the tree, so a closed
    /// node does not hide a row the user is looking at.
    /// </summary>
    public bool IsScopeCollapsed(TenantKey tenantKey, ScopeNode node) =>
        !IsFiltering && CollapsedScopes.Contains(ScopeNodeKey(tenantKey, node));

    public void ToggleScope(TenantKey tenantKey, ScopeNode node)
    {
        var key = ScopeNodeKey(tenantKey, node);
        if (!CollapsedScopes.Remove(key))
        {
            CollapsedScopes.Add(key);
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

    /// <summary>Where a scope node's checkbox stands: nothing under it chosen, some of it, or all of it.</summary>
    public enum SubtreeSelection { None, Some, All }

    /// <summary>
    /// The roles a scope node's checkbox acts on: everything at or below it that can actually be
    /// activated. A role already active, or view-only, is not something the checkbox can take, so
    /// it is left out of both the count and the toggle.
    /// </summary>
    public IReadOnlyList<RoleKey> SubtreeKeys(ScopeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return [.. node.AllRoles.Select(r => r.Key).Where(CanSelect)];
    }

    /// <summary>
    /// Whether a role's own checkbox would be enabled: activatable, and not already active,
    /// scheduled or awaiting approval — there is nothing for a bulk activation to do with those.
    /// A failed one can be chosen again.
    /// </summary>
    public bool CanSelect(RoleKey key)
    {
        if (!CanActivate(key))
        {
            return false;
        }

        return Assignment(key) is not { } assignment || assignment.Status.Kind == AssignmentStatusKind.Failed;
    }

    public SubtreeSelection SubtreeState(ScopeNode node)
    {
        var keys = SubtreeKeys(node);
        if (keys.Count == 0)
        {
            return SubtreeSelection.None;
        }

        var chosen = keys.Count(Selection.Contains);
        if (chosen == 0)
        {
            return SubtreeSelection.None;
        }

        return chosen == keys.Count ? SubtreeSelection.All : SubtreeSelection.Some;
    }

    /// <summary>
    /// "Select every eligibility under this scope", and pressing it again lets them all go. A
    /// partly chosen subtree fills up rather than emptying: the checkbox is offering the rest.
    /// Returns how many keys the press added or removed, for the confirmation the row shows.
    /// </summary>
    public int ToggleSubtree(ScopeNode node)
    {
        var keys = SubtreeKeys(node);
        if (keys.Count == 0)
        {
            return 0;
        }

        var changed = 0;
        if (SubtreeState(node) == SubtreeSelection.All)
        {
            changed = keys.Count(Selection.Remove);
        }
        else
        {
            foreach (var key in keys)
            {
                if (Selection.Add(key))
                {
                    changed++;
                }
            }
        }

        if (changed > 0)
        {
            Touch();
        }

        return changed;
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
