using Elevate.Core.Models;

namespace Elevate.App.ViewModels;

/// <summary>Which tenant or profile the panel is pointed at, and how Escape steps back. Port of <c>AppModel+Selection.swift</c>.</summary>
public sealed partial class AppModel
{
    /// <summary>The profile open in the panel. Never persisted; change it only through <see cref="SetSelectedProfile"/>.</summary>
    public Guid? SelectedProfile { get; private set; }

    private bool _browsing;

    /// <summary>True once the user chose to browse the role list instead of Home. Never persisted.</summary>
    public bool Browsing
    {
        get => _browsing;
        set
        {
            if (SetProperty(ref _browsing, value))
            {
                Touch();
            }
        }
    }

    /// <summary>
    /// The tenant the panel is scoped to. Only a tracked tenant whose account does not need to sign
    /// in again counts; anything else reads as null (all tenants) without touching the stored choice.
    /// </summary>
    public TenantKey? ScopeTenant
    {
        get => Settings.PanelTenant is { } key && Tenant(key) is not null && !NeedsSignIn(key.IdentityId) ? key : null;
        set
        {
            Settings.PanelTenant = value;
            // Any change of tenant, however it comes about, leaves that tenant's access packages.
            CloseAccessPackages();
            Touch();
        }
    }

    public PanelSelection PanelSelection =>
        SelectedProfile is { } id && Profile(id) is not null ? new PanelSelection.Profile(id)
        : ScopeTenant is { } key ? new PanelSelection.Tenant(key)
        : new PanelSelection.AllTenants();

    private void SetSelectedProfile(Guid? id)
    {
        var previous = SelectedProfile;
        SelectedProfile = id;
        // Leaving a profile drops its draft or results; a run still going is kept.
        if (previous is { } left && left != id && _runSessions.TryGetValue(left, out var session) && !session.Running && !RunWindowsOpen.Contains(left))
        {
            _runSessions.Remove(left);
        }

        Touch();
    }

    /// <summary>Switching tenant or going back to all tenants leaves the query and bulk selection alone.</summary>
    public void Select(PanelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        CloseAccessPackages();
        switch (selection)
        {
            case PanelSelection.AllTenants:
                Settings.PanelTenant = null;
                SetSelectedProfile(null);
                break;
            case PanelSelection.Tenant t:
                Settings.PanelTenant = t.Key;
                SetSelectedProfile(null);
                break;
            case PanelSelection.Profile p:
                SetSelectedProfile(p.Id);
                break;
        }
    }

    /// <summary>The tenant whose access packages the main view shows; null when it shows something else. Never persisted.</summary>
    public TenantKey? AccessPackagesTenant { get; private set; }

    /// <summary>The package whose request form replaces the list; null when the list shows.</summary>
    public AccessPackage? RequestingPackage { get; private set; }

    private AccessPackageTab _accessPackagesTab;

    /// <summary>Lives here, not in the view, so it survives the trip through the request form.</summary>
    public AccessPackageTab AccessPackagesTab
    {
        get => _accessPackagesTab;
        set
        {
            if (SetProperty(ref _accessPackagesTab, value))
            {
                Touch();
            }
        }
    }

    /// <summary>Shows the tenant's access packages in the main view; from the all-tenants list this selects the tenant too.</summary>
    public void OpenAccessPackages(TenantKey key)
    {
        Select(new PanelSelection.Tenant(key));
        AccessPackagesTenant = key;
        _accessPackagesTab = AccessPackageTab.Available;
        Touch();
    }

    public void BeginPackageRequest(AccessPackage package)
    {
        RequestingPackage = package;
        Touch();
    }

    public void EndPackageRequest()
    {
        RequestingPackage = null;
        Touch();
    }

    /// <summary>A submitted request: back to the list, on the Requested tab where it now shows.</summary>
    public void FinishPackageRequest()
    {
        _accessPackagesTab = AccessPackageTab.Requested;
        EndPackageRequest();
    }

    public void CloseAccessPackages()
    {
        if (AccessPackagesTenant is null && RequestingPackage is null)
        {
            return;
        }

        AccessPackagesTenant = null;
        RequestingPackage = null;
        Touch();
    }

    public PanelContent PanelContent => AccessPackagesTenant is { } packages && PanelSelection is PanelSelection.Tenant tenant && tenant.Key == packages
        ? RequestingPackage is { } package ? new PanelContent.RequestPackage(packages, package) : new PanelContent.AccessPackages(packages)
        : PanelSelection switch
    {
        PanelSelection.Profile p => new PanelContent.Profile(p.Id),
        PanelSelection.Tenant t => new PanelContent.Tenant(t.Key),
        _ => Browsing || IsFiltering || SelectMode || HomeIsEmpty ? new PanelContent.RoleList() : new PanelContent.Home(),
    };

    /// <summary>Roles matching the filter in every tracked tenant but the scoped one, so the list can offer to widen the search.</summary>
    public int OtherTenantMatchCount => ScopeTenant is { } scope && IsFiltering
        ? State.Tenants.Where(t => t.Key != scope).Sum(t => RolesFor(t.Key, PanelTab).Count)
        : 0;

    public void SearchAllTenants() => ScopeTenant = null;

    /// <summary>
    /// Steps back one level per press; false when there was nothing to step back from, so the flyout can
    /// close. Never changes the selected tenant. Port of <c>handleEscape</c>.
    /// </summary>
    public bool HandleEscape()
    {
        // Judged by what is on screen, so packages left behind by a vanished tenant cannot swallow a press.
        var content = PanelContent;
        if (content is PanelContent.RequestPackage)
        {
            EndPackageRequest();
        }
        else if (content is PanelContent.AccessPackages)
        {
            CloseAccessPackages();
        }
        else if (SearchQuery.Length > 0)
        {
            SearchQuery = string.Empty;
        }
        else if (PanelSelection is PanelSelection.Profile)
        {
            SetSelectedProfile(null);
        }
        else if (SelectMode)
        {
            SelectMode = false;
        }
        else if (Browsing && PanelSelection is PanelSelection.AllTenants)
        {
            Browsing = false;
        }
        else
        {
            return false;
        }

        return true;
    }
}
