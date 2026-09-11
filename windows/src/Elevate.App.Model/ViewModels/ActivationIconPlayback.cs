namespace Elevate.App.ViewModels;

public enum ActivationIconPhase { Hidden, Working, Success }

/// <summary>Only a state change to confirmed success can start the completion morph.</summary>
public sealed class ActivationIconPlayback
{
    public const double MorphDuration = 1.4;
    private ActivationIconPhase _phase;
    private double _started;
    private bool _morphs;

    public void Update(ActivationIconPhase next, double now)
    {
        if (next == _phase) return;
        _morphs = _phase == ActivationIconPhase.Working && next == ActivationIconPhase.Success;
        _phase = next;
        _started = now;
    }

    public readonly record struct Frame(double PulseTime, double? MorphTime, bool Animating);

    public Frame Sample(double now, bool reduceMotion)
    {
        var elapsed = Math.Max(0, now - _started);
        if (_phase == ActivationIconPhase.Working)
            return new Frame(reduceMotion ? 0 : elapsed, null, !reduceMotion);
        if (_phase != ActivationIconPhase.Success) return new Frame(0, null, false);
        var time = reduceMotion || !_morphs ? MorphDuration : Math.Min(elapsed, MorphDuration);
        return new Frame(0, time, time < MorphDuration);
    }
}
