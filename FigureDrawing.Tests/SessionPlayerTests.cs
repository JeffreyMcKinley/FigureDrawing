using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-004 session player: the UI-independent brain of the drawing screen. It resolves the FD-003
// session's current image id through a loader and, per the acceptance criteria, skips past
// unreadable/broken images (logging each) until one displays, the session completes, or the pool
// proves undisplayable. Uses string as the image type so a loader is just "id -> id, or null".
public class SessionPlayerTests
{
    static readonly string[] Pool = { "a", "b", "c", "d", "e" };

    // Builds a session over Pool with a deterministic seed/clock so sequences are reproducible.
    static DrawingSession Session(int count, IReadOnlyList<string>? pool = null, bool shuffle = false) =>
        new(pool ?? Pool, new SessionConfig(30, count), shuffle, new Random(1234), () => TimeSpan.Zero);

    // A loader where the given ids fail to load (return null); everything else loads to itself.
    static Func<string, string?> LoaderFailing(params string[] broken)
    {
        var set = broken.ToHashSet();
        return id => set.Contains(id) ? null : id;
    }

    [Fact]
    public void ShowsFirstImage_WhenItLoads()
    {
        var player = new SessionPlayer<string>(Session(count: 3, shuffle: false), LoaderFailing());

        Assert.Equal("a", player.CurrentImage);   // pool order, first image loads
        Assert.False(player.IsComplete);
        Assert.False(player.CouldNotDisplayImage);
    }

    [Fact]
    public void SkipsUnreadableImages_UntilOneLoads_AndLogsEach()
    {
        var skipped = new List<string>();
        var player = new SessionPlayer<string>(
            Session(count: 3, shuffle: false),          // order a, b, c, ...
            LoaderFailing("a", "b"),                     // a and b are broken
            onUnreadable: skipped.Add);

        Assert.Equal("c", player.CurrentImage);          // skipped past a and b
        Assert.Equal(new[] { "a", "b" }, skipped);       // both logged, in order
        Assert.False(player.CouldNotDisplayImage);
    }

    [Fact]
    public void SkippingBrokenImages_DoesNotCountThemTowardTheTotal()
    {
        // a broken, b good: the player lands on b having skipped a. b has not been completed yet.
        var player = new SessionPlayer<string>(Session(count: 3, shuffle: false), LoaderFailing("a"));

        Assert.Equal("b", player.CurrentImage);
        Assert.Equal(0, player.Summary.ImagesDisplayed);  // skip does not count (FD-006 semantics)
    }

    [Fact]
    public void AllImagesUnreadable_EndsSessionAndFlagsIt_WithinFailureBudget()
    {
        var skipped = new List<string>();
        var player = new SessionPlayer<string>(
            Session(count: 5, pool: new[] { "x", "y" }, shuffle: false), // pool < count -> repeats
            LoaderFailing("x", "y"),                                     // every image broken
            onUnreadable: skipped.Add,
            maxConsecutiveFailures: 10);

        Assert.True(player.IsComplete);
        Assert.True(player.CouldNotDisplayImage);
        Assert.Null(player.CurrentImage);
        Assert.Equal(10, skipped.Count);                 // gave up after the budget, no infinite loop
    }

    [Fact]
    public void Next_CountsCurrentImage_AndAdvancesToNextDisplayable()
    {
        var player = new SessionPlayer<string>(Session(count: 3, shuffle: false), LoaderFailing());
        Assert.Equal("a", player.CurrentImage);

        player.Next();
        Assert.Equal("b", player.CurrentImage);
        Assert.Equal(1, player.Summary.ImagesDisplayed);
    }

    [Fact]
    public void Next_SkipsBrokenImagesWhenAdvancing()
    {
        // a good, b broken, c good: completing a should land on c (b skipped).
        var player = new SessionPlayer<string>(Session(count: 3, shuffle: false), LoaderFailing("b"));
        Assert.Equal("a", player.CurrentImage);

        player.Next();
        Assert.Equal("c", player.CurrentImage);
        Assert.Equal(1, player.Summary.ImagesDisplayed);  // only a counted; b was skipped
    }

    [Fact]
    public void CompletingConfiguredCount_EndsSession_WithoutErrorFlag()
    {
        var player = new SessionPlayer<string>(Session(count: 2, shuffle: false), LoaderFailing());

        player.Next();
        player.Next();

        Assert.True(player.IsComplete);
        Assert.False(player.CouldNotDisplayImage);        // normal completion, not an image failure
        Assert.Null(player.CurrentImage);
        Assert.Equal(2, player.Summary.ImagesDisplayed);
    }

    [Fact]
    public void End_StopsImmediately_AndClearsCurrentImage()
    {
        var player = new SessionPlayer<string>(Session(count: 5, shuffle: false), LoaderFailing());
        player.Next();                                    // 1 completed

        player.End();

        Assert.True(player.IsComplete);
        Assert.Null(player.CurrentImage);
        Assert.Equal(1, player.Summary.ImagesDisplayed);  // ended image not counted
    }

    [Fact]
    public void EmptyPool_CompletesImmediately_WithoutErrorFlag()
    {
        var player = new SessionPlayer<string>(
            Session(count: 3, pool: Array.Empty<string>()), LoaderFailing());

        Assert.True(player.IsComplete);
        Assert.Null(player.CurrentImage);
        Assert.False(player.CouldNotDisplayImage);        // nothing to display != failed to display
    }

    [Fact]
    public void NullSessionOrLoader_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SessionPlayer<string>(null!, LoaderFailing()));
        Assert.Throws<ArgumentNullException>(() =>
            new SessionPlayer<string>(Session(count: 1), null!));
    }
}
