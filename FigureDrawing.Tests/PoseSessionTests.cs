using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The player-screen aggregate: image + countdown + break phase behind one object. It exists to take
// the "advance the pose AND restart the clock" rule out of SessionActivity (docs/ARCHITECTURE.md
// §17/§20.2), so the pairing is asserted here rather than left to a screen.
public class PoseSessionTests
{
    // A hand-cranked monotonic clock so every timing assertion is deterministic (no real waiting).
    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = { "a", "b", "c", "d" };

    // Loader that resolves every id to itself, so "the image on screen" is directly assertable.
    static string? Echo(string id) => id;

    static PoseSession<string> Make(
        FakeClock clock,
        int seconds = 30,
        int count = 3,
        int breakSeconds = 0,
        IReadOnlyList<string>? pool = null,
        Func<string, string?>? load = null,
        Action<string>? onUnreadable = null)
    {
        var session = new DrawingSession(
            pool ?? Pool, new SessionConfig(seconds, count, breakSeconds),
            shuffle: false, random: new Random(1), clock: clock.Read);

        return new PoseSession<string>(
            session, load ?? Echo, onUnreadable, breakSeconds, clock.Read);
    }

    // --- Starting state ------------------------------------------------------

    [Fact]
    public void StartsOnTheFirstPose_WithAFullClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        Assert.Equal(PosePhase.Pose, s.Phase);
        Assert.Equal("a", s.CurrentImage);
        Assert.False(s.IsComplete);
        Assert.False(s.OnBreak);
        Assert.Equal("0:30", s.Display);
        Assert.Equal(100, s.RemainingPercent);
        Assert.Equal(1, s.CurrentPoseNumber);
        Assert.Equal(3, s.TargetCount);
    }

    [Fact]
    public void EmptyPool_IsCompleteBeforeTheFirstTick()
    {
        var clock = new FakeClock();
        var s = Make(clock, pool: Array.Empty<string>());

        Assert.Equal(PosePhase.Complete, s.Phase);
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
    }

    [Fact]
    public void ZeroCount_IsCompleteBeforeTheFirstTick()
    {
        var clock = new FakeClock();
        var s = Make(clock, count: 0);

        Assert.True(s.IsComplete);
    }

    // --- The rule this type exists for: expiry advances AND restarts the clock ----

    [Fact]
    public void PoseExpiry_CountsThePose_AndRestartsTheClockForTheNextOne()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3);

        clock.Advance(30);
        Assert.True(s.Tick());

        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal(PosePhase.Pose, s.Phase);
        // The clock is back at full for the new pose — the bug this aggregate prevents.
        Assert.Equal("0:30", s.Display);
        Assert.Equal(2, s.CurrentPoseNumber);
    }

    [Fact]
    public void Tick_BeforeExpiry_DoesNothing()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(10);

        Assert.False(s.Tick());
        Assert.Equal("a", s.CurrentImage);
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal("0:20", s.Display);
    }

    [Fact]
    public void Tick_WhilePaused_NeverExpiresThePose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        s.Pause();
        clock.Advance(120);

        Assert.False(s.Tick());
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void PauseThenResume_ResumesWhereThePoseLeftOff()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(10);
        s.Pause();
        clock.Advance(300);        // backgrounded for five minutes
        s.Resume();

        Assert.Equal("0:20", s.Display);
        Assert.False(s.IsComplete);
    }

    // --- Break phase ---------------------------------------------------------

    [Fact]
    public void WithABreak_PoseExpiryEntersTheBreak_OnItsOwnClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();

        Assert.Equal(PosePhase.Break, s.Phase);
        Assert.True(s.OnBreak);
        Assert.Equal("0:15", s.Display);
        // The pose already counted; the next image is loaded underneath the overlay.
        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
    }

    [Fact]
    public void BreakExpiry_ReturnsToThePose_WithAFullPoseClock()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // into the break
        clock.Advance(15);
        Assert.True(s.Tick());     // out of it

        Assert.Equal(PosePhase.Pose, s.Phase);
        Assert.False(s.OnBreak);
        Assert.Equal("0:30", s.Display);
        // A break is rest, not drawing: it does not count a second pose.
        Assert.Equal(1, s.CompletedCount);
    }

    [Fact]
    public void WithoutABreak_PoseRunsStraightIntoTheNext()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 0);

        clock.Advance(30);
        s.Tick();

        Assert.Equal(PosePhase.Pose, s.Phase);
        Assert.False(s.OnBreak);
    }

    [Fact]
    public void LastPose_CompletesTheSession_WithoutATrailingBreak()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2, breakSeconds: 15);

        clock.Advance(30);
        s.Tick();                  // pose 1 done -> break
        clock.Advance(15);
        s.Tick();                  // -> pose 2
        clock.Advance(30);
        s.Tick();                  // pose 2 done -> session over, no break after it

        Assert.Equal(PosePhase.Complete, s.Phase);
        Assert.True(s.IsComplete);
        Assert.False(s.OnBreak);
        Assert.Null(s.CurrentImage);
    }

    // --- Commands ------------------------------------------------------------

    [Fact]
    public void Next_IsTheManualDoneTap_CountingThePose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3);

        clock.Advance(10);
        s.Next();

        Assert.Equal(1, s.CompletedCount);
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void Skip_DoesNotCountThePose_AndSkipsThePendingBreak()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 15);

        clock.Advance(10);
        s.Skip();

        Assert.Equal(0, s.CompletedCount);
        Assert.Equal(1, s.SkippedCount);
        Assert.Equal(PosePhase.Pose, s.Phase);      // straight to the next image, no rest
        Assert.Equal("b", s.CurrentImage);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void Skip_DuringABreak_StartsTheNextPoseImmediately()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 3, breakSeconds: 60);

        clock.Advance(30);
        s.Tick();                  // into a one-minute break
        s.Skip();

        Assert.Equal(PosePhase.Pose, s.Phase);
        Assert.Equal("0:30", s.Display);
    }

    [Fact]
    public void End_StopsTheSession_AndBanksThePartialPose()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 5);

        clock.Advance(30);
        s.Tick();                  // one full pose banked
        clock.Advance(12);
        s.End();

        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(1, s.Summary.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(42), s.Summary.TotalDrawingTime);
    }

    [Fact]
    public void CommandsAfterCompletion_AreNoOps()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 1);

        clock.Advance(30);
        s.Tick();
        Assert.True(s.IsComplete);

        var summary = s.Summary;
        s.Next();
        s.Skip();
        s.End();
        s.Resume();

        Assert.False(s.Tick());
        Assert.Equal(summary, s.Summary);
        Assert.True(s.IsComplete);
    }

    // --- Ring / progress readouts --------------------------------------------

    [Theory]
    [InlineData(0, 100)]
    [InlineData(15, 50)]
    [InlineData(30, 0)]
    [InlineData(45, 0)]        // clamped: an overrun never reads negative
    public void RemainingPercent_TracksTheClockDown(double elapsed, int expected)
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30);

        clock.Advance(elapsed);

        Assert.Equal(expected, s.RemainingPercent);
    }

    [Fact]
    public void CurrentPoseNumber_StopsAtTheTarget()
    {
        var clock = new FakeClock();
        var s = Make(clock, seconds: 30, count: 2);

        Assert.Equal(1, s.CurrentPoseNumber);

        clock.Advance(30);
        s.Tick();
        Assert.Equal(2, s.CurrentPoseNumber);

        clock.Advance(30);
        s.Tick();                  // session over — the label must not read "Image 3 of 2"
        Assert.Equal(2, s.CurrentPoseNumber);
    }

    // --- Unreadable images ---------------------------------------------------

    [Fact]
    public void UnreadableImages_AreSkippedPast_AndReported()
    {
        var clock = new FakeClock();
        var reported = new List<string>();
        var s = Make(clock, count: 3,
            load: id => id == "a" ? null : id,
            onUnreadable: reported.Add);

        Assert.Equal("b", s.CurrentImage);
        Assert.Equal(new[] { "a" }, reported);
        Assert.False(s.CouldNotDisplayImage);
    }

    [Fact]
    public void AnEntirelyUndisplayablePool_CompletesInTheErrorState()
    {
        var clock = new FakeClock();
        var s = Make(clock, count: 3, load: _ => null);

        Assert.True(s.IsComplete);
        Assert.True(s.CouldNotDisplayImage);
        Assert.Null(s.CurrentImage);
    }
}
