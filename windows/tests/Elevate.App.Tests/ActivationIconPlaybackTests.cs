using Elevate.App.ViewModels;

namespace Elevate.App.Tests;

public sealed class ActivationIconPlaybackTests
{
    [Fact]
    public void DeactivationPointsDownWhileWorkingAndEndsWithUprightCheck()
    {
        Assert.Equal(16, ActivationIconPlayback.VerticalPosition(4, null, true));
        Assert.Equal(4, ActivationIconPlayback.VerticalPosition(4, ActivationIconPlayback.MorphDuration, true));
        Assert.Equal(4, ActivationIconPlayback.VerticalPosition(4, null, false));
    }

    [Fact]
    public void FailedDeactivationNeverMorphsAndRetryStartsFresh()
    {
        var playback = new ActivationIconPlayback();
        playback.Update(ActivationIconPhase.Working, 0);
        playback.Update(ActivationIconPhase.Hidden, 3);
        Assert.Null(playback.Sample(100, false).MorphTime);
        Assert.False(playback.Sample(100, false).Animating);
        playback.Update(ActivationIconPhase.Working, 101);
        Assert.Equal(0, playback.Sample(101, false).PulseTime);
        playback.Update(ActivationIconPhase.Success, 102);
        Assert.Equal(ActivationIconPlayback.MorphDuration, playback.Sample(102, true).MorphTime);
    }

    [Fact]
    public void LongRequestNeverBecomesSuccessByTimeAlone()
    {
        var playback = new ActivationIconPlayback();
        playback.Update(ActivationIconPhase.Working, 0);
        var sample = playback.Sample(86400, false);
        Assert.Null(sample.MorphTime);
        Assert.True(sample.Animating);
    }

    [Fact]
    public void SuccessMorphsOnceAndRetryResets()
    {
        var playback = new ActivationIconPlayback();
        playback.Update(ActivationIconPhase.Working, 0);
        playback.Update(ActivationIconPhase.Success, 10);
        Assert.Equal(0.7, playback.Sample(10.7, false).MorphTime!.Value, 6);
        playback.Update(ActivationIconPhase.Success, 11);
        Assert.False(playback.Sample(12, false).Animating);
        playback.Update(ActivationIconPhase.Working, 13);
        Assert.Null(playback.Sample(13, false).MorphTime);
    }

    [Fact]
    public void ReducedMotionAndExistingSuccessAreStatic()
    {
        var playback = new ActivationIconPlayback();
        playback.Update(ActivationIconPhase.Success, 0);
        Assert.False(playback.Sample(0, false).Animating);
        playback.Update(ActivationIconPhase.Working, 1);
        Assert.False(playback.Sample(1, true).Animating);
        playback.Update(ActivationIconPhase.Success, 2);
        Assert.Equal(ActivationIconPlayback.MorphDuration, playback.Sample(2, true).MorphTime);
    }
}
