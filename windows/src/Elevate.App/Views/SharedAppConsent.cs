using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>
/// The one place the shared-app confirmation text lives, so the setup panel and Settings offer the
/// project-provided registration on identical terms. Wording matches the macOS
/// <c>SharedAppConsentDialog</c>.
/// </summary>
public static class SharedAppConsent
{
    public const string Title = "Use the shared Elevate app?";
    public const string ConfirmLabel = "Use shared app";
    public const string QuickStartLabel = "Quick start with the shared Elevate app…";
    public const string Message =
        "The Elevate project provides an optional multi-tenant app registration for quick starts "
        + "and testing, so you do not have to create your own. It has no client secret and only the "
        + "delegated permissions listed in the app registration guide. It is offered as a convenience "
        + "with no SLA: it may change or be withdrawn at any time. Organizations that need full "
        + "control should register their own app. An administrator must grant consent once per tenant "
        + "before sign-in works.";

    /// <summary>Shows the caveat and returns true when the user chose to use the shared app.</summary>
    public static async Task<bool> ConfirmAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Title,
            Content = Message,
            PrimaryButtonText = ConfirmLabel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
