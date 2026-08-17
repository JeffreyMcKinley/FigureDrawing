using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// Resolving the current image id to something displayable, and the unreadable-image policy
// (INV-PLY-*): a broken file skips its pose without counting it, and a wholly undisplayable pool
// ends the session with a distinguishable error rather than looping. Uses string as the image type
// so a loader is just "id -> id, or null".
public class DrawingSessionImageTests
{
    static readonly string[] Pool = ["a", "b", "c", "d", "e"];

    // A session over Pool with a deterministic seed/clock so sequences are reproducible.
    static DrawingSession<string> Session(
        int count,
        Func<string, string?> load,
        IReadOnlyList<string>? pool = null,
        bool shuffle = false,
        Action<string>? onUnreadable = null,
        int maxConsecutiveFailures = 100) =>
        new(pool ?? Pool, new SessionConfig(30, count), load, shuffle, new Random(1234),
            () => TimeSpan.Zero, onUnreadable, maxConsecutiveFailures);

    // A loader where the given ids fail to load (return null); everything else loads to itself.
    static Func<string, string?> LoaderFailing(params string[] broken)
    {
        var set = broken.ToHashSet();
        return id => set.Contains(id) ? null : id;
    }

    [Fact]
    public void ShowsFirstImage_WhenItLoads()
    {
        var session = Session(count: 3, LoaderFailing());

        Assert.Equal("a", session.CurrentImage);   // pool order, first image loads
        Assert.Equal("a", session.CurrentImageId);
        Assert.False(session.IsComplete);
        Assert.False(session.CouldNotDisplayImage);
    }

    [Fact]
    public void SkipsUnreadableImages_UntilOneLoads_AndLogsEach()
    {
        var skipped = new List<string>();
        var session = Session(
            count: 3,                          // order a, b, c, ...
            LoaderFailing("a", "b"),           // a and b are broken
            onUnreadable: skipped.Add);

        Assert.Equal("c", session.CurrentImage);         // skipped past a and b
        Assert.Equal(new[] { "a", "b" }, skipped);       // both logged, in order
        Assert.False(session.CouldNotDisplayImage);
    }

    [Fact]
    public void SkippingBrokenImages_DoesNotCountThemTowardTheTotal()
    {
        // a broken, b good: the session lands on b having skipped a. b has not been completed yet.
        var session = Session(count: 3, LoaderFailing("a"));

        Assert.Equal("b", session.CurrentImage);
        Assert.Equal(0, session.ImagesDisplayed);   // an unreadable image never counts (INV-PLY-2)
        Assert.Equal(1, session.SkippedCount);
    }

    [Fact]
    public void AllImagesUnreadable_EndsSessionAndFlagsIt_WithinFailureBudget()
    {
        var skipped = new List<string>();
        var session = Session(
            count: 5,
            LoaderFailing("x", "y"),                    // every image broken
            pool: ["x", "y"],                           // pool < count -> repeats
            onUnreadable: skipped.Add,
            maxConsecutiveFailures: 10);

        Assert.True(session.IsComplete);
        Assert.True(session.CouldNotDisplayImage);
        Assert.Null(session.CurrentImage);
        Assert.Equal(10, skipped.Count);                 // gave up after the budget, no infinite loop
    }

    // Time spent failing to decode is not drawing time. Each attempt is a real decode on a device,
    // so an exhausted budget could otherwise bank seconds against a pose nobody ever saw
    // (INV-PLY-2, INV-SUM-3).
    [Fact]
    public void ExhaustingTheFailureBudget_BanksNoDrawingTime()
    {
        var now = TimeSpan.Zero;
        var session = new DrawingSession<string>(
            ["x", "y"],
            new SessionConfig(30, 5),
            _ =>
            {
                now += TimeSpan.FromSeconds(1);   // every attempt costs a second
                return null;
            },
            shuffle: false,
            random: new Random(1),
            clock: () => now,
            maxConsecutiveFailures: 10);

        Assert.True(session.IsComplete);
        Assert.True(session.CouldNotDisplayImage);
        Assert.Equal(0, session.ImagesDisplayed);
        Assert.Equal(TimeSpan.Zero, session.TotalDrawingTime);
        Assert.Equal(TimeSpan.Zero, session.AveragePoseTime);
    }

    [Fact]
    public void Next_CountsCurrentImage_AndAdvancesToNextDisplayable()
    {
        var session = Session(count: 3, LoaderFailing());
        Assert.Equal("a", session.CurrentImage);

        session.Next();
        Assert.Equal("b", session.CurrentImage);
        Assert.Equal(1, session.ImagesDisplayed);
    }

    [Fact]
    public void Next_SkipsBrokenImagesWhenAdvancing()
    {
        // a good, b broken, c good: completing a should land on c (b skipped).
        var session = Session(count: 3, LoaderFailing("b"));
        Assert.Equal("a", session.CurrentImage);

        session.Next();
        Assert.Equal("c", session.CurrentImage);
        Assert.Equal(1, session.ImagesDisplayed);   // only a counted; b was skipped
    }

    [Fact]
    public void CompletingConfiguredCount_EndsSession_WithoutErrorFlag()
    {
        var session = Session(count: 2, LoaderFailing());

        session.Next();
        session.Next();

        Assert.True(session.IsComplete);
        Assert.False(session.CouldNotDisplayImage);   // normal completion, not an image failure
        Assert.Null(session.CurrentImage);
        Assert.Equal(2, session.ImagesDisplayed);
    }

    [Fact]
    public void End_StopsImmediately_AndClearsCurrentImage()
    {
        var session = Session(count: 5, LoaderFailing());
        session.Next();                               // 1 completed

        session.End();

        Assert.True(session.IsComplete);
        Assert.Null(session.CurrentImage);
        Assert.Null(session.CurrentImageId);
        Assert.Equal(1, session.ImagesDisplayed);     // ended image not counted
    }

    [Fact]
    public void EmptyPool_CompletesImmediately_WithoutErrorFlag()
    {
        var session = Session(count: 3, LoaderFailing(), pool: []);

        Assert.True(session.IsComplete);
        Assert.Null(session.CurrentImage);
        Assert.False(session.CouldNotDisplayImage);   // nothing to display != failed to display
    }

    // INV-PLY-5 ("the loader never throws through the session") is a contract on the *adapter*: the
    // Android loader catches decode failures and returns null. The session does not paper over a
    // loader that throws, and `SessionScreenContractTests` is what guards the adapter's side.
    [Fact]
    public void NullPoolOrLoader_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DrawingSession<string>(null!, new SessionConfig(30, 1), LoaderFailing()));
        Assert.Throws<ArgumentNullException>(() =>
            new DrawingSession<string>(Pool, new SessionConfig(30, 1), null!));
    }
}
