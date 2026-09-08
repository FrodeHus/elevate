using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// Shared entry points for running, pinning and deleting a profile, so the chips in the flyout,
/// the rows of the All list and their menus behave identically. Port of the macOS <c>ProfileActions</c>.
/// </summary>
internal static class ProfileActions
{
    /// <summary>
    /// Runs a profile: silently with the remembered reason and durations when asked and possible,
    /// otherwise through the run window.
    /// </summary>
    public static async Task RunAsync(AppModel model, Guid id, bool silentlyIfPossible)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (silentlyIfPossible && await model.QuickRunAsync(id))
        {
            return;
        }

        model.RequestRun(id);
        App.Current.OpenRunProfile(id);
    }

    /// <summary>The menu behind a chip, an All-list row and its ⋯ button: run, edit, pin, manage, delete.</summary>
    public static MenuFlyout Menu(AppModel model, Guid id, XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(model);
        var menu = new MenuFlyout();
        var profile = model.Profile(id);
        if (profile is null)
        {
            return menu;
        }

        menu.Items.Add(Item("Run…", () => _ = RunAsync(model, id, silentlyIfPossible: false)));
        var last = Item("Run with last reason", () => _ = RunAsync(model, id, silentlyIfPossible: true));
        last.IsEnabled = profile.LastJustification is not null;
        menu.Items.Add(last);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Edit…", () => App.Current.OpenManageProfiles(id)));
        if (profile.Pinned)
        {
            menu.Items.Add(Item("Unpin from flyout", () => model.SetPinned(id, false)));
        }
        else
        {
            var pin = Item("Pin to flyout", () => model.SetPinned(id, true));
            pin.IsEnabled = model.PinnedProfiles.Count < ProfilePins.Limit;
            menu.Items.Add(pin);
        }

        menu.Items.Add(Item("Manage profiles…", () => App.Current.OpenManageProfiles()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Delete…", () => _ = ConfirmDeleteAsync(model, id, root)));
        return menu;
    }

    /// <summary>Deleting has no undo: the dialog names the profile and what it holds, and says the assignments stay.</summary>
    public static async Task ConfirmDeleteAsync(AppModel model, Guid id, XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Profile(id) is not { } profile)
        {
            return;
        }

        var ok = await DialogWindows.ConfirmAsync(
            root,
            $"Delete \"{profile.Name}\"?",
            $"The profile and its {ProfileSummary.Caption(profile.Entries)} are removed. Active assignments are not changed.",
            "Delete");
        if (ok && model.Profile(id) is not null)
        {
            model.DeleteProfile(id);
        }
    }

    /// <summary>"Unpin one first: 4 is the most the row holds", for a refused pin.</summary>
    public static string PinRefusedHint => $"Unpin one first: {ProfilePins.Limit} is the most the row holds";

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
