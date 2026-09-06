using Elevate.App.Shell;
using Elevate.App.ViewModels;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Elevate.App.Views;

/// <summary>Names the current selection and saves it as a profile. Port of the macOS <c>SaveProfileView</c>.</summary>
public sealed partial class SaveProfileWindow : Window
{
    private readonly AppModel _model;
    private readonly IReadOnlyList<RoleKey> _keys;

    public SaveProfileWindow(AppModel model, IReadOnlyList<RoleKey> keys)
    {
        InitializeComponent();
        _model = model;
        _keys = keys;
        DialogWindows.Configure(this, "Save as profile", 440, 400, Root, autoHeight: true);
        DialogWindows.DefaultButton(Root, SaveButton);
        Fill();
        SaveButton.IsEnabled = false;
        NameBox.Focus(FocusState.Programmatic);
    }

    public IReadOnlyList<RoleKey> Keys => _keys;

    /// <summary>
    /// What running the profile would ask for today: the remembered duration, else the policy
    /// default, never above the maximum. Shown for every row so an empty one is not a surprise.
    /// </summary>
    private TimeSpan ResolvedDuration(RoleKey key)
    {
        var policy = _model.Role(key)?.Policy ?? RolePolicy.ManualDefault;
        var wanted = _model.Remembered(key)?.LastDuration ?? policy.DefaultDuration;
        return wanted < policy.MaximumDuration ? wanted : policy.MaximumDuration;
    }

    private void Fill()
    {
        var resources = Application.Current.Resources;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        foreach (var group in _keys.GroupBy(k => k.TenantKey))
        {
            Entries.Children.Add(new TextBlock
            {
                Text = $"{_model.Identity(group.Key.IdentityId)?.Upn ?? group.Key.IdentityId} · {_model.Tenant(group.Key)?.DisplayName ?? group.Key.TenantId}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = secondary,
                Margin = new Thickness(0, 6, 0, 2),
            });
            foreach (var key in group)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = _model.SummaryName(key), TextTrimming = TextTrimming.CharacterEllipsis });
                var duration = new TextBlock { Text = Countdown.Label(ResolvedDuration(key)), FontSize = 12, Foreground = secondary, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                Grid.SetColumn(duration, 1);
                row.Children.Add(duration);
                Entries.Children.Add(row);
            }
        }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            return;
        }

        _model.SaveProfile(NameBox.Text, _keys);
        _model.SelectMode = false;
        Close();
    }
}
