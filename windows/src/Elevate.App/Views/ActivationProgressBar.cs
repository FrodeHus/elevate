using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Elevate.App.Views;

/// <summary>
/// One segment per requested role, beside the count: colour alone must not carry the result.
/// Port of the macOS <c>ActivationProgressBar</c>, drawn into a grid and a text block the host owns.
/// </summary>
internal static class ActivationProgressBar
{
    /// <param name="results">A null entry is a request with no result yet.</param>
    public static void Update(Grid segments, TextBlock count, IReadOnlyList<ActivationResult?> results, bool running)
    {
        var text = ActivationSummary.Progress(results);
        if (count.Text != text)
        {
            count.Text = text;
            ToolTipService.SetToolTip(count, text);
            // The count is a polite live region; a TextBlock does not announce its own changes.
            FrameworkElementAutomationPeer.FromElement(count)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        var resources = Application.Current.Resources;
        segments.Children.Clear();
        segments.ColumnDefinitions.Clear();
        foreach (var result in results)
        {
            // Caution already means "awaiting approval" on the rows, so a request in flight is the accent.
            var segment = new Rectangle
            {
                Height = 4,
                RadiusX = 2,
                RadiusY = 2,
                Opacity = result is null ? (running ? 0.4 : 0.25) : 1,
                Fill = (Brush)resources[result switch
                {
                    ActivationResult.Activated => "SystemFillColorSuccessBrush",
                    ActivationResult.PendingApproval => "SystemFillColorCautionBrush",
                    ActivationResult.Failed => "SystemFillColorCriticalBrush",
                    null when !running => "TextFillColorSecondaryBrush",
                    _ => "AccentFillColorDefaultBrush",
                }],
            };
            Grid.SetColumn(segment, segments.ColumnDefinitions.Count);
            segments.ColumnDefinitions.Add(new ColumnDefinition());
            segments.Children.Add(segment);
        }
    }
}
