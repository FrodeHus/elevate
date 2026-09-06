using Microsoft.UI.Xaml.Controls;

namespace Elevate.App.Views;

/// <summary>A ComboBox of 30-minute steps from 30 minutes up to the policy maximum. Port of the macOS <c>DurationPicker</c>.</summary>
public sealed partial class DurationPicker : ComboBox
{
    private TimeSpan _maximum = TimeSpan.FromHours(8);

    public DurationPicker()
    {
        MinWidth = 120;
        Rebuild();
    }

    public TimeSpan Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            var current = Duration;
            Rebuild();
            Duration = current;
        }
    }

    public TimeSpan Duration
    {
        get => SelectedItem is Option o ? o.Value : Options.First().Value;
        set
        {
            var snapped = Nearest(value);
            SelectedItem = Items.OfType<Option>().FirstOrDefault(o => o.Value == snapped) ?? Items.OfType<Option>().First();
        }
    }

    private IEnumerable<Option> Options => Items.OfType<Option>();

    private void Rebuild()
    {
        Items.Clear();
        var maxMinutes = Math.Max(30, (int)(_maximum.TotalMinutes));
        for (var m = 30; m <= maxMinutes; m += 30)
        {
            Items.Add(new Option(TimeSpan.FromMinutes(m)));
        }

        SelectedIndex = 0;
    }

    /// <summary>Snaps to the closest 30-minute step, never past the policy maximum.</summary>
    private TimeSpan Nearest(TimeSpan d)
    {
        var options = Options.ToList();
        var minutes = d.TotalMinutes;
        var snapped = (int)Math.Round(minutes / 30) * 30;
        var first = (int)options.First().Value.TotalMinutes;
        var last = (int)options.Last().Value.TotalMinutes;
        return TimeSpan.FromMinutes(Math.Min(Math.Max(snapped, first), last));
    }

    public static string Label(TimeSpan d)
    {
        var m = (int)d.TotalMinutes;
        return m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
    }

    private sealed record Option(TimeSpan Value)
    {
        public override string ToString() => Label(Value);
    }
}
