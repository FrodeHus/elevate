using System.Diagnostics;
using System.Text.Json;
using Elevate.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Elevate.App.Views;

/// <summary>Native per-row animation using the same traced poses as the macOS app.</summary>
public sealed class ActivationStatusIcon : Grid
{
    private sealed record Pose(double[][] Upper, double[][] Lower);
    private sealed record Motion(double Interval, Pose[] Frames);
    private static readonly Lazy<Motion?> Data = new(LoadMotion);
    private readonly ActivationIconPlayback _playback = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
    private readonly UISettings _settings = new();
    private readonly ShapePath _upper = new();
    private readonly ShapePath _lower = new();
    private readonly FontIcon _fallback = new() { FontSize = 16 };
    private ActivationIconPhase _phase;

    public ActivationStatusIcon()
    {
        Width = Height = 20;
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_lower);
        Children.Add(_upper);
        Children.Add(_fallback);
        _timer.Tick += (_, _) => DrawFrame();
        Loaded += (_, _) =>
        {
            _settings.AnimationsEnabledChanged += OnAnimationsChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _settings.AnimationsEnabledChanged -= OnAnimationsChanged;
        };
        ActualThemeChanged += (_, _) => DrawFrame();
    }

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(ActivationIconPhase), typeof(ActivationStatusIcon),
        new PropertyMetadata(ActivationIconPhase.Hidden, (sender, args) =>
            ((ActivationStatusIcon)sender).SetPhase((ActivationIconPhase)args.NewValue)));

    public ActivationIconPhase Phase
    {
        get => (ActivationIconPhase)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public bool Downward { get; set; }

    public void SetPhase(ActivationIconPhase phase)
    {
        _phase = phase;
        _playback.Update(phase, Now);
        Visibility = phase == ActivationIconPhase.Hidden ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private void OnAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        _timer.Stop();
        DrawFrame();
        if (IsLoaded && _playback.Sample(Now, !_settings.AnimationsEnabled).Animating)
            _timer.Start();
    }

    private void DrawFrame()
    {
        if (_phase == ActivationIconPhase.Hidden) { _timer.Stop(); return; }
        var sample = _playback.Sample(Now, !_settings.AnimationsEnabled);
        if (!sample.Animating) _timer.Stop();
        var resources = Application.Current.Resources;
        var accent = ((SolidColorBrush)resources["AccentFillColorDefaultBrush"]).Color;
        var success = ((SolidColorBrush)resources["SystemFillColorSuccessBrush"]).Color;
        if (Data.Value is not { } motion)
        {
            _fallback.Glyph = _phase == ActivationIconPhase.Success ? "\uE73E" : (Downward ? "\uE96E" : "\uE96D");
            _fallback.Foreground = new SolidColorBrush(_phase == ActivationIconPhase.Success ? success : accent);
            return;
        }

        _fallback.Visibility = Visibility.Collapsed;
        var position = Math.Min((sample.MorphTime ?? 0) / motion.Interval, motion.Frames.Length - 1);
        var index = (int)position;
        var a = motion.Frames[index];
        var b = motion.Frames[Math.Min(index + 1, motion.Frames.Length - 1)];
        var green = Smooth(((sample.MorphTime ?? 0) - 0.6) / 0.8);
        var color = Color.FromArgb(255, Mix(accent.R, success.R, green), Mix(accent.G, success.G, green), Mix(accent.B, success.B, green));
        Paint(_upper, a.Upper, b.Upper, true);
        Paint(_lower, a.Lower, b.Lower, false);

        void Paint(ShapePath path, double[][] from, double[][] to, bool upper)
        {
            var wave = Math.Max(0, sample.PulseTime - (upper ? .12 : 0));
            var pulse = sample.MorphTime is null ? Math.Pow(Math.Sin(Math.PI * wave / .74), 2) : 0;
            var figure = new PathFigure { IsClosed = true, IsFilled = true };
            var segment = new PolyLineSegment();
            for (var i = 0; i < from.Length; i++)
            {
                var fraction = position - index;
                var point = new Point((from[i][0] + (to[i][0] - from[i][0]) * fraction - 32) / 192 * 20,
                    (from[i][1] + (to[i][1] - from[i][1]) * fraction - 9 * pulse - 32) / 192 * 20);
                point.Y = ActivationIconPlayback.VerticalPosition(point.Y, sample.MorphTime, Downward);
                if (i == 0) figure.StartPoint = point;
                else segment.Points.Add(point);
            }

            figure.Segments.Add(segment);
            var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
            geometry.Figures.Add(figure);
            path.Data = geometry;
            path.Fill = new SolidColorBrush(color);
            path.Opacity = upper ? 1 : .65 + .35 * green;
        }
    }

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
    private static double Smooth(double value) { var t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }

    private static Motion? LoadMotion()
    {
        try
        {
            var motion = JsonSerializer.Deserialize<Motion>(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "elevation-motion.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (motion is null || motion.Interval <= 0 || motion.Frames.Length < 2) return null;
            var first = motion.Frames[0];
            return motion.Frames.All(p => p.Upper.Length == first.Upper.Length && p.Lower.Length == first.Lower.Length
                && p.Upper.Concat(p.Lower).All(x => x.Length == 2 && x.All(double.IsFinite))) ? motion : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
