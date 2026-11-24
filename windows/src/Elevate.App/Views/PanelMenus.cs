using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// The account and tenant menus, shared by the list's group headers and the sidebar so both offer
/// the same items. The confirmations they open are shown on <c>root</c>, the flyout window's root.
/// </summary>
internal static class PanelMenus
{
    /// <summary>
    /// The account menu. <paramref name="sole"/> is the account's one tenant when it has exactly
    /// one: that tenant's items are folded in, as its header is.
    /// </summary>
    public static MenuFlyout IdentityMenu(AppModel model, Identity identity, TenantContext? sole, XamlRoot root)
    {
        var id = identity.Id;
        var menu = new MenuFlyout();
        // The sign-in method heads the menu instead of crowding the row.
        menu.Items.Add(new MenuFlyoutItem { Text = $"{identity.Upn}, signed in with {identity.SignInMethod.DetailedName}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (model.NeedsSignIn(id))
        {
            var again = Item("Sign in again", () => RetrySignIn(model, id));
            again.IsEnabled = model.IsMethodAllowed(identity.SignInMethod) && !model.SignInInFlight.Contains(id);
            menu.Items.Add(again);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Item("Discover tenants…", () => App.Current.OpenDiscoverTenants(id)));
        menu.Items.Add(Item("Add tenant…", () => App.Current.OpenAddTenant(id)));
        if (sole is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddTenantItems(menu, model, sole, root);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (model.CanChangeRegistration(identity))
        {
            // An account not on an Entra app registration yet gains Entra roles and PIM for
            // Groups by moving to one, so the item says so instead of "change".
            menu.Items.Add(Item(
                identity.SignInMethod.IsOwnApp ? "Change app registration…" : "Upgrade to Entra app registration…",
                () => App.Current.OpenChangeRegistration(id)));
        }

        menu.Items.Add(Item("Sign out…", () => _ = ConfirmSignOutAsync(model, id, root)));
        return menu;
    }

    public static MenuFlyout TenantMenu(AppModel model, TenantContext tenant, XamlRoot root)
    {
        var menu = new MenuFlyout();
        AddTenantItems(menu, model, tenant, root);
        return menu;
    }

    private static void AddTenantItems(MenuFlyout menu, AppModel model, TenantContext tenant, XamlRoot root)
    {
        if (tenant.AccessPackagesAvailable == true)
        {
            // First, as on macOS: the one item that opens a window of its own.
            menu.Items.Add(Item("Access packages…", () => App.Current.OpenAccessPackages(tenant.Key)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Item("Configure known PIM roles…", () => App.Current.OpenConfigureRoles(tenant.Key)));
        menu.Items.Add(Item("Retry discovery", () => _ = model.RetryDiscoveryAsync(tenant.Key)));
        // Each portal opens in this tenant; the browser session picks the account.
        var open = new MenuFlyoutSubItem { Text = "Open…" };
        foreach (var portal in AdminPortal.All)
        {
            var portalUri = portal.Uri(tenant.TenantId);
            open.Items.Add(Item(portal.Title, () => _ = Windows.System.Launcher.LaunchUriAsync(portalUri)));
        }

        menu.Items.Add(open);
        // Offered for every Entra-app-registration account, not only after discovery fell back:
        // an administrator may need to re-consent after a scope is added, before anything fails.
        // AdminConsentUrl is null for the first-party (Azure CLI / PowerShell) and custom methods.
        if (model.AdminConsentUrl(tenant.IdentityId, tenant.TenantId) is { } url)
        {
            menu.Items.Add(Item("Open admin consent link…", () => _ = Windows.System.Launcher.LaunchUriAsync(url)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        if (model.IsPinnedTenant(tenant.Key))
        {
            // A pinned tenant would only come back on the next launch; the menu says so rather
            // than offering a removal that does nothing.
            menu.Items.Add(new MenuFlyoutItem { Text = "Pinned by your organization", IsEnabled = false });
            return;
        }

        var remove = Item("Remove tenant…", () => _ = ConfirmRemoveTenantAsync(model, tenant, root));
        remove.IsEnabled = tenant.Source != TenantSource.Home;
        menu.Items.Add(remove);
    }

    /// <summary>Re-runs the account's own sign-in method and clears the flag; the model reports the outcome through Notice.</summary>
    public static void RetrySignIn(AppModel model, string identityId)
    {
        if (model.Identity(identityId) is { } identity)
        {
            _ = model.RetrySignInAsync(identity);
        }
    }

    // Signing out and removing a tenant run at once and have no undo: each says what is forgotten
    // and that the assignments themselves are untouched before it goes ahead.

    private static async Task ConfirmSignOutAsync(AppModel model, string identityId, XamlRoot root)
    {
        if (model.Identity(identityId) is not { } identity)
        {
            return;
        }

        var n = model.TenantsFor(identityId).Count;
        var tenants = n == 1 ? "its tenant" : $"its {n} tenants";
        var ok = await DialogWindows.ConfirmAsync(
            root,
            $"Sign out {identity.Upn}?",
            $"Elevate forgets {tenants}, configured roles and profile entries for this account. Active assignments in Entra are not changed. You can add the account again later.",
            "Sign out");
        if (ok && model.Identity(identityId) is { } still)
        {
            model.SignOut(still);
        }
    }

    private static async Task ConfirmRemoveTenantAsync(AppModel model, TenantContext tenant, XamlRoot root)
    {
        var ok = await DialogWindows.ConfirmAsync(
            root,
            $"Remove {tenant.DisplayName}?",
            "Its roles, configured PIM roles and profile entries are removed from Elevate. Active assignments in Entra are not changed. You can add the tenant again later.",
            "Remove tenant");
        if (ok && model.Tenant(tenant.Key) is not null)
        {
            model.RemoveTenant(tenant.Key);
        }
    }

    internal static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
