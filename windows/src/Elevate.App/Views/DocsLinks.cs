using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>The user guides on GitHub that the setup panel and Settings point to.</summary>
public static class DocsLinks
{
    public static readonly Uri GettingStarted = new("https://github.com/FrodeHus/elevate/blob/main/docs/getting-started.md#2-choose-how-to-sign-in");
    public static readonly Uri AppRegistration = new("https://github.com/FrodeHus/elevate/blob/main/docs/entra-app-registration.md");
}

/// <summary>
/// A caption-sized row of the two guide links, separated by a middle dot. A footnote, not a
/// call to action: the buttons carry no padding so the row sits flush with the captions around it.
/// </summary>
public sealed partial class DocsLinksRow : ContentControl
{
    public DocsLinksRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(Link("Getting started", DocsLinks.GettingStarted));
        row.Children.Add(new TextBlock
        {
            Text = "·",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        row.Children.Add(Link("App registration guide", DocsLinks.AppRegistration));
        Content = row;
        IsTabStop = false;
    }

    private static HyperlinkButton Link(string text, Uri uri) => new()
    {
        Content = text,
        NavigateUri = uri,
        FontSize = 12,
        Padding = new Thickness(0),
        MinHeight = 0,
    };
}
