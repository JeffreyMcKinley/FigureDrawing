using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-003 session engine: the UI-independent model that owns a drawing session's random sequence,
// remaining-count, skip semantics, and time accounting. Driven by FD-004..FD-007 screens; this is
// the testable core. A full start-to-summary lifecycle is exercised in DrawingSessionE2ETests.
public class DrawingSessionTests
{
    // A hand-cranked monotonic clock so time accounting is deterministic (no real waiting).
    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    static readonly string[] Pool = { "a", "b", "c", "d", "e" };

    static DrawingSession Make(
        int count,
        IReadOnlyList<string>? pool = null,
        bool shuffle = true,
        int seed = 1234,
        FakeClock? clock = null,
        int seconds = 30) =>
        new(pool ?? Pool, new SessionConfig(seconds, count), shuffle, new Random(seed),
            (clock ?? new FakeClock()).Read);

    // --- Sequence / selection ------------------------------------------------

    [Fact]
    public void StartsOnAnImageFromThePool()
    {
        var s = Make(count: 3);
        Assert.NotNull(s.CurrentImage);
        Assert.Contains(s.CurrentImage, Pool);
        Assert.False(s.IsComplete);
    }

    [Fact]
    public void ShuffleFalse_ProducesPoolOrder()
    {
        var s = Make(count: 5, shuffle: false);

        var seen = Drain(s);
        Assert.Equal(Pool, seen);
    }

    [Fact]
    public void ShuffleTrue_IsAPermutationOfThePool_NoDropsOrDupes()
    {
        var s = Make(count: 5, shuffle: true, seed: 7);

        var seen = Drain(s);
        Assert.Equal(Pool.OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public void ShuffleTrue_ReordersFromPoolOrder()
    {
        // With this seed the shuffled first pass differs from pool order (guards the shuffle runs).
        var s = Make(count: 5, shuffle: true, seed: 7);
        var seen = Drain(s);
        Assert.NotEqual(Pool, seen);
    }

    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        var a = Drain(Make(count: 5, seed: 99));
        var b = Drain(Make(count: 5, seed: 99));
        Assert.Equal(a, b);
    }

    // --- Count / completion --------------------------------------------------

    [Fact]
    public void NextDecrementsRemainingAndCountsTowardTotal()
    {
        var s = Make(count: 3);
        Assert.Equal(3, s.Remaining);
        Assert.Equal(0, s.CompletedCount);

        s.Next();
        Assert.Equal(1, s.CompletedCount);
        Assert.Equal(2, s.Remaining);
    }

    [Fact]
    public void CompletesWhenConfiguredCountReached()
    {
        var s = Make(count: 3);
        s.Next();
        s.Next();
        Assert.False(s.IsComplete);

        s.Next();
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(0, s.Remaining);
        Assert.Equal(3, s.CompletedCount);
    }

    [Fact]
    public void OperationsAfterCompletionAreNoOps()
    {
        var s = Make(count: 1);
        s.Next();
        Assert.True(s.IsComplete);

        s.Next();
        s.Skip();
        s.End();
        Assert.Equal(1, s.CompletedCount);
        Assert.True(s.IsComplete);
    }

    // --- Skip semantics ------------------------------------------------------

    [Fact]
    public void SkipDoesNotCountTowardTotalButChangesImage()
    {
        var s = Make(count: 3, shuffle: false); // deterministic order a,b,c,d,e
        var first = s.CurrentImage;

        s.Skip();
        Assert.Equal(0, s.CompletedCount);
        Assert.Equal(3, s.Remaining);
        Assert.NotEqual(first, s.CurrentImage);
    }

    [Fact]
    public void SkipDoesNotEndTheSession()
    {
        var s = Make(count: 1);
        s.Skip();
        s.Skip();
        Assert.False(s.IsComplete);
        Assert.Equal(0, s.CompletedCount);
    }

    // --- Time accounting -----------------------------------------------------

    [Fact]
    public void BanksDrawingTimePerCompletedImage()
    {
        var clock = new FakeClock();
        var s = Make(count: 2, clock: clock);

        clock.Advance(30);       // drew first image 30s
        s.Next();
        clock.Advance(25);       // drew second image 25s
        s.Next();

        Assert.True(s.IsComplete);
        Assert.Equal(TimeSpan.FromSeconds(55), s.Summary.TotalDrawingTime);
        Assert.Equal(2, s.Summary.ImagesDisplayed);
    }

    [Fact]
    public void SkippedTimeIsExcludedFromTotal()
    {
        var clock = new FakeClock();
        var s = Make(count: 1, clock: clock);

        clock.Advance(40);       // spent 40s then skipped -> excluded
        s.Skip();
        clock.Advance(30);       // drew the next image 30s -> counted
        s.Next();

        Assert.Equal(TimeSpan.FromSeconds(30), s.Summary.TotalDrawingTime);
        Assert.Equal(1, s.Summary.ImagesDisplayed);
    }

    [Fact]
    public void EndBanksCurrentPartialTimeButDoesNotCountTheImage()
    {
        var clock = new FakeClock();
        var s = Make(count: 5, clock: clock);

        clock.Advance(30);
        s.Next();                // image 1 completed, 30s
        clock.Advance(12);       // partway through image 2
        s.End();

        Assert.True(s.IsComplete);
        Assert.Equal(1, s.Summary.ImagesDisplayed);                    // partial image not counted
        Assert.Equal(TimeSpan.FromSeconds(42), s.Summary.TotalDrawingTime);
    }

    // --- Pool smaller than count (repeat) ------------------------------------

    [Fact]
    public void CountLargerThanPool_RepeatsToHonorCount()
    {
        var pool = new[] { "x", "y" };
        var s = Make(count: 5, pool: pool, shuffle: false);

        var seen = Drain(s);
        Assert.Equal(5, seen.Count);                 // honored the configured count
        Assert.All(seen, img => Assert.Contains(img, pool));
    }

    [Fact]
    public void CountLargerThanPool_ShowsEveryImageBeforeRepeating()
    {
        var pool = new[] { "x", "y", "z" };
        var s = Make(count: 3, pool: pool, shuffle: true, seed: 3);

        var firstPass = Drain(s);
        Assert.Equal(pool.OrderBy(v => v), firstPass.OrderBy(v => v)); // full pass, no repeat yet
    }

    // --- Degenerate pools ----------------------------------------------------

    [Fact]
    public void EmptyPool_CompletesImmediately()
    {
        var s = Make(count: 3, pool: Array.Empty<string>());
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
        Assert.Equal(0, s.Summary.ImagesDisplayed);
    }

    [Fact]
    public void ZeroCount_CompletesImmediately()
    {
        var s = Make(count: 0);
        Assert.True(s.IsComplete);
        Assert.Null(s.CurrentImage);
    }

    // Runs the session to completion via Next(), collecting each displayed image.
    static List<string> Drain(DrawingSession s)
    {
        var seen = new List<string>();
        while (!s.IsComplete)
        {
            seen.Add(s.CurrentImage!);
            s.Next();
        }
        return seen;
    }
}
