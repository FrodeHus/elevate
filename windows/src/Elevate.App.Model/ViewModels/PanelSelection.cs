using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>What the panel is pointed at: every tenant, one tenant, or one profile opened from Home.</summary>
public abstract record PanelSelection
{
    public sealed record AllTenants : PanelSelection;

    public sealed record Tenant(TenantKey Key) : PanelSelection;

    public sealed record Profile(Guid Id) : PanelSelection;
}

/// <summary>What the panel's main view shows for the current <see cref="PanelSelection"/> and browsing state.</summary>
public abstract record PanelContent
{
    public sealed record Home : PanelContent;

    public sealed record RoleList : PanelContent;

    public sealed record Tenant(TenantKey Key) : PanelContent;

    public sealed record Profile(Guid Id) : PanelContent;

    public sealed record AccessPackages(TenantKey Key) : PanelContent;

    public sealed record RequestPackage(TenantKey Key, AccessPackage Package) : PanelContent;
}

/// <summary>The tabs of the access packages view.</summary>
public enum AccessPackageTab
{
    Available,
    Requested,
    Assigned,
    Declined,
}
