using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-005 unit tests for the per-pose countdown. Every test drives a fake monotonic clock, so the
// suite is deterministic and instant — no Thread.Sleep, no real seconds.
public class PoseCountdownTests
{
    // A hand-cranked monotonic clock: tests move time forward explicitly.
    sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }

        public Func<TimeSpan> Read => () => Now;

        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static (PoseCountdown Countdown, FakeClock Clock) Start(int seconds = 30)
    {
        var clock = new FakeClock();
        return (new PoseCountdown(seconds, clock.Read), clock);
    }

    [Fact]
    public void NewCountdown_IsRunning_WithTheFullDurationRemaining()
    {
        var (countdown, _) = Start(30);

        Assert.True(countdown.IsRunning);
        Assert.False(countdown.IsPaused);
        Assert.False(countdown.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(30), countdown.Remaining);
        Assert.Equal(30, countdown.RemainingSeconds);
    }

    [Fact]
    public void Remaining_DrainsWithTheClock()
    {
        var (countdown, clock) = Start(30);

        clock.Advance(10);
        Assert.Equal(TimeSpan.FromSeconds(20), countdown.Remaining);

        clock.Advance(19.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), countdown.Remaining);
    }

    // The countdown is clock-driven, not tick-driven: reading it rarely (a dropped frame, a busy
    // main thread) must not make the pose last longer than configured.
    [Fact]
    public void Remaining_DoesNotDrift_WhenPolledIrregularly()
    {
        var (countdown, clock) = Start(60);

        for (var i = 0; i < 7; i++)
            clock.Advance(0.137);          // ragged, non-second-aligned polling

        clock.Advance(59.041);
        Assert.True(countdown.IsExpired);
        Assert.Equal(TimeSpan.Zero, countdown.Remaining);
    }

    // Displayed seconds round UP so a fresh pose reads its full length and "0" appears only at zero.
    [Theory]
    [InlineData(0.0, 30)]
    [InlineData(0.4, 30)]
    [InlineData(1.0, 29)]
    [InlineData(29.2, 1)]
    [InlineData(30.0, 0)]
    [InlineData(45.0, 0)]
    public void RemainingSeconds_RoundsUp(double elapsed, int expected)
    {
        var (countdown, clock) = Start(30);

        clock.Advance(elapsed);

        Assert.Equal(expected, countdown.RemainingSeconds);
    }

    [Fact]
    public void Remaining_NeverGoesNegative()
    {
        var (countdown, clock) = Start(5);

        clock.Advance(500);

        Assert.Equal(TimeSpan.Zero, countdown.Remaining);
        Assert.Equal(0, countdown.RemainingSeconds);
        Assert.True(countdown.IsExpired);
        Assert.False(countdown.IsRunning);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(90, "1:30")]
    [InlineData(600, "10:00")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    public void Format_RendersMinutesAndSeconds(int seconds, string expected) =>
        Assert.Equal(expected, PoseCountdown.Format(seconds));

    [Fact]
    public void Display_UsesTheRoundedUpRemainingTime()
    {
        var (countdown, clock) = Start(90);

        Assert.Equal("1:30", countdown.Display);

        clock.Advance(30.5);
        Assert.Equal("1:00", countdown.Display);
    }

    // OnPause: time must stop moving while the app is backgrounded.
    [Fact]
    public void Pause_FreezesRemaining()
    {
        var (countdown, clock) = Start(30);

        clock.Advance(10);
        countdown.Pause();
        clock.Advance(120);          // a long spell in the background

        Assert.True(countdown.IsPaused);
        Assert.False(countdown.IsRunning);
        Assert.False(countdown.IsExpired);
        Assert.Equal(TimeSpan.FromSeconds(20), countdown.Remaining);
    }

    // OnResume: the pose picks up exactly where it left off, not from the top and not from a
    // background-shifted clock.
    [Fact]
    public void Resume_ContinuesFromWhereItPaused()
    {
        var (countdown, clock) = Start(30);

        clock.Advance(10);
        countdown.Pause();
        clock.Advance(120);
        countdown.Resume();

        Assert.True(countdown.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(20), countdown.Remaining);

        clock.Advance(5);
        Assert.Equal(TimeSpan.FromSeconds(15), countdown.Remaining);
    }

    // Lifecycle callbacks can arrive doubled up; neither call may lose or gain time.
    [Fact]
    public void PauseAndResume_AreIdempotent()
    {
        var (countdown, clock) = Start(30);

        clock.Advance(4);
        countdown.Pause();
        countdown.Pause();
        clock.Advance(50);
        countdown.Resume();
        countdown.Resume();
        clock.Advance(6);

        Assert.Equal(TimeSpan.FromSeconds(20), countdown.Remaining);
    }

    [Fact]
    public void Resume_DoesNotReviveAnExpiredPose()
    {
        var (countdown, clock) = Start(10);

        clock.Advance(10);
        countdown.Pause();
        countdown.Resume();

        Assert.True(countdown.IsExpired);
        Assert.Equal(TimeSpan.Zero, countdown.Remaining);
    }

    // Every new image gets the full time back (FD-005 acceptance: "timer resets for each image").
    [Fact]
    public void Restart_GivesTheNextPoseTheFullDuration()
    {
        var (countdown, clock) = Start(30);

        clock.Advance(30);
        Assert.True(countdown.IsExpired);

        countdown.Restart();

        Assert.False(countdown.IsExpired);
        Assert.True(countdown.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(30), countdown.Remaining);

        clock.Advance(7);
        Assert.Equal(TimeSpan.FromSeconds(23), countdown.Remaining);
    }

    [Fact]
    public void Restart_AlsoUnpauses()
    {
        var (countdown, clock) = Start(30);

        countdown.Pause();
        clock.Advance(3);
        countdown.Restart();
        clock.Advance(2);

        Assert.False(countdown.IsPaused);
        Assert.Equal(TimeSpan.FromSeconds(28), countdown.Remaining);
    }

    [Fact]
    public void Restart_CanChangeTheDuration()
    {
        var (countdown, _) = Start(30);

        countdown.Restart(TimeSpan.FromSeconds(120));

        Assert.Equal(TimeSpan.FromSeconds(120), countdown.Duration);
        Assert.Equal("2:00", countdown.Display);
    }

    // A zero/negative configured duration must not produce a countdown that runs backwards forever.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveDuration_StartsExpired(int seconds)
    {
        var clock = new FakeClock();
        var countdown = new PoseCountdown(seconds, clock.Read);

        Assert.True(countdown.IsExpired);
        Assert.False(countdown.IsRunning);
        Assert.Equal("0:00", countdown.Display);
    }
}
