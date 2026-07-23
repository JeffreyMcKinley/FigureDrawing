using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-005 end-to-end: a whole timed session run through the REAL components the screen uses —
// FD-001 enumeration -> FD-002 config -> FD-003 DrawingSession -> FD-004 SessionPlayer -> FD-005
// PoseCountdown — with a fake clock and a fake repaint loop standing in for the Android Handler.
//
// The harness below mirrors SessionActivity's loop exactly (repaint, advance at zero, restart the
// countdown per pose, pause/resume on lifecycle), so a break in that wiring shows up here instead of
// only on a device.
public class CountdownSessionE2ETests
{
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId) =>
            children.TryGetValue(parentDocumentId, out var kids) ? kids : Array.Empty<DocumentEntry>();
    }

    // Stand-in for SessionActivity: same state machine, no Android.
    sealed class Screen
    {
        readonly TimeSpan _tickInterval = TimeSpan.FromMilliseconds(200);

        public Screen(IReadOnlyList<string> pool, SessionConfig config, Func<TimeSpan> clock,
                      Func<string, string?>? load = null)
        {
            Clock = clock;
            var session = new DrawingSession(pool, config, shuffle: false, new Random(3), clock);
            Player = new SessionPlayer<string>(session, load ?? (id => id));
            Countdown = new PoseCountdown(config.SecondsPerImage, clock);
            Ticking = Player.CurrentImage is not null;
            Render();
        }

        public Func<TimeSpan> Clock { get; }
        public SessionPlayer<string> Player { get; }
        public PoseCountdown Countdown { get; }
        public bool Ticking { get; private set; }

        // What the timer view currently reads, recorded on every repaint.
        public List<string> DisplayedTimes { get; } = new();

        // Each image as it appeared on screen, in order.
        public List<string> DisplayedImages { get; } = new();

        // One pass of the Handler loop: repaint, then advance if the pose ran out.
        public void Tick()
        {
            if (!Ticking)
                return;

            DisplayedTimes.Add(Countdown.Display);

            if (Countdown.IsExpired)
                Advance();
        }

        // Timer expiry or a manual "done" tap: count the pose, reset the clock for the next one.
        public void Advance()
        {
            if (Player.IsComplete)
            {
                Ticking = false;
                return;
            }

            Player.Next();
            Countdown.Restart();
            Render();
        }

        public void OnPause()
        {
            Countdown.Pause();
            Ticking = false;
        }

        public void OnResume()
        {
            if (Player.CurrentImage is null)
                return;

            Countdown.Resume();
            Ticking = true;
        }

        void Render()
        {
            if (Player.CurrentImage is { } id)
            {
                DisplayedImages.Add(id);
                Ticking = true;
                return;
            }

            Ticking = false;
        }

        public TimeSpan TickInterval => _tickInterval;
    }

    sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }

        public Func<TimeSpan> Read => () => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    static IReadOnlyList<string> Pool(params string[] names)
    {
        var tree = new FakeTree(new()
        {
            ["root"] = names.Select(n => new DocumentEntry($"root/{n}", "image/jpeg")).ToArray(),
        });
        return FolderImageEnumerator.EnumerateImages(tree, "root");
    }

    // Run the screen's loop for a wall-clock span, ticking at the screen's repaint interval.
    static void Run(Screen screen, FakeClock clock, TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += screen.TickInterval)
        {
            clock.Advance(screen.TickInterval);
            screen.Tick();
        }
    }

    // Acceptance: at zero the next image loads automatically and the count advances; the timer
    // resets for each new image; the whole session runs itself to completion.
    [Fact]
    public void TimedSession_AutoAdvancesEveryPose_AndCompletesOnItsOwn()
    {
        var clock = new FakeClock();
        var pool = Pool("a.jpg", "b.jpg", "c.jpg");
        var config = SessionSetup.Evaluate("30", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(pool, config, clock.Read);

        // One pose in: still on the first image, nothing counted yet.
        Run(screen, clock, TimeSpan.FromSeconds(29));
        Assert.Single(screen.DisplayedImages);
        Assert.Equal(0, screen.Player.Summary.ImagesDisplayed);

        // Cross the first expiry: second image, timer back to full.
        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Assert.Equal(1, screen.Player.Summary.ImagesDisplayed);
        Assert.Equal("0:30", screen.Countdown.Display);

        // Let the rest of the session run itself out (3 x 30s plus slack).
        Run(screen, clock, TimeSpan.FromSeconds(70));

        Assert.True(screen.Player.IsComplete);
        Assert.False(screen.Ticking);
        Assert.Equal(3, screen.DisplayedImages.Count);
        Assert.Equal(3, screen.Player.Summary.ImagesDisplayed);
        Assert.Equal(new[] { "root/a.jpg", "root/b.jpg", "root/c.jpg" }, screen.DisplayedImages);
    }

    // Acceptance: the countdown is visible and updates each second — every second from 30 down to 0
    // must actually appear on screen, in descending order.
    [Fact]
    public void Countdown_ShowsEverySecond_InDescendingOrder()
    {
        var clock = new FakeClock();
        var config = SessionSetup.Evaluate("30", "1", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(30));

        var seen = screen.DisplayedTimes.Distinct().ToList();
        Assert.Equal(
            Enumerable.Range(0, 31).Reverse().Select(s => PoseCountdown.Format(s)).ToList(),
            seen);
    }

    // Acceptance: backgrounding pauses the timer — it must not drain or fire while hidden.
    [Fact]
    public void Backgrounding_PausesTheTimer_AndResumesWhereItLeftOff()
    {
        var clock = new FakeClock();
        var config = SessionSetup.Evaluate("60", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(20));
        Assert.Equal("0:40", screen.Countdown.Display);

        // Home button: five minutes in the background, far longer than the pose.
        screen.OnPause();
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(screen.Ticking);
        Assert.Equal("0:40", screen.Countdown.Display);
        Assert.Single(screen.DisplayedImages);                       // did NOT advance while hidden
        Assert.Equal(0, screen.Player.Summary.ImagesDisplayed);

        // Back to the app: the pose resumes with its remaining 40s intact.
        screen.OnResume();
        Run(screen, clock, TimeSpan.FromSeconds(39));
        Assert.Single(screen.DisplayedImages);

        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Assert.Equal(1, screen.Player.Summary.ImagesDisplayed);
    }

    // A manual "done" tap mid-pose counts the image AND hands the next pose a full timer.
    [Fact]
    public void ManualAdvance_ResetsTheTimerForTheNextPose()
    {
        var clock = new FakeClock();
        var config = SessionSetup.Evaluate("45", "3", folderSelected: true).Config!.Value;
        var screen = new Screen(Pool("a.jpg", "b.jpg", "c.jpg"), config, clock.Read);

        Run(screen, clock, TimeSpan.FromSeconds(5));
        screen.Advance();

        Assert.Equal(1, screen.Player.Summary.ImagesDisplayed);
        Assert.Equal("0:45", screen.Countdown.Display);

        // And the fresh timer still expires a full 45s later, not 40s.
        Run(screen, clock, TimeSpan.FromSeconds(44));
        Assert.Equal(2, screen.DisplayedImages.Count);
        Run(screen, clock, TimeSpan.FromSeconds(1.4));
        Assert.Equal(3, screen.DisplayedImages.Count);
    }

    // Unreadable images are skipped by the player without the countdown handing them pose time:
    // the broken image never shows, and the poses that do show each get their full duration.
    [Fact]
    public void BrokenImages_DoNotConsumePoseTime()
    {
        var clock = new FakeClock();
        var config = SessionSetup.Evaluate("30", "2", folderSelected: true).Config!.Value;
        var screen = new Screen(
            Pool("a.jpg", "broken.jpg", "c.jpg"),
            config,
            clock.Read,
            load: id => id.Contains("broken") ? null : id);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));

        Assert.DoesNotContain(screen.DisplayedImages, d => d.Contains("broken"));
        Assert.Equal(new[] { "root/a.jpg", "root/c.jpg" }, screen.DisplayedImages);
        Assert.Equal("0:30", screen.Countdown.Display);

        Run(screen, clock, TimeSpan.FromSeconds(30.4));
        Assert.True(screen.Player.IsComplete);
        Assert.Equal(2, screen.Player.Summary.ImagesDisplayed);
    }
}
