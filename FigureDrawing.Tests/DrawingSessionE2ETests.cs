using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-003 end-to-end: a full drawing session wired through the REAL components it depends on —
// FD-001 folder enumeration (FolderImageEnumerator) feeds the pool, FD-002 setup (SessionSetup)
// produces the config, and FD-003 (DrawingSession) runs it start-to-summary. No Android/Appium here
// because the session SCREEN does not exist yet (built in FD-004..FD-007); this drives the engine
// through the same public surface those screens will, simulating timer expiries, a skip, and End.
public class DrawingSessionE2ETests
{
    // In-memory document tree so FolderImageEnumerator runs for real against a picked "folder".
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId) =>
            children.TryGetValue(parentDocumentId, out var kids) ? kids : Array.Empty<DocumentEntry>();
    }

    sealed class FakeClock
    {
        public TimeSpan Now;
        public Func<TimeSpan> Read => () => Now;
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    const string Dir = FolderImageEnumerator.DirectoryMimeType;

    [Fact]
    public void FullSession_FromPickedFolder_ProducesExpectedSummary()
    {
        // 1) FD-001: enumerate a picked folder (root + one subfolder, mixed files).
        var tree = new FakeTree(new()
        {
            ["root"] = new[]
            {
                new DocumentEntry("root/pose1.jpg", "image/jpeg"),
                new DocumentEntry("root/pose2.png", "image/png"),
                new DocumentEntry("root/notes.txt", "text/plain"),   // ignored
                new DocumentEntry("root/more", Dir),
            },
            ["root/more"] = new[]
            {
                new DocumentEntry("root/more/pose3.webp", "image/webp"),
                new DocumentEntry("root/more/pose4.jpg", "image/jpeg"),
            },
        });

        var pool = FolderImageEnumerator.EnumerateImages(tree, "root");
        Assert.Equal(4, pool.Count);   // 4 images, .txt excluded

        // 2) FD-002: validate the user's setup inputs into a config.
        var setup = SessionSetup.Evaluate("30", "6", folderSelected: true);
        Assert.True(setup.CanStart);
        var config = setup.Config!.Value;   // 30s/image, 6 images (> pool -> repeats)

        // 3) FD-003: run the session as the screen would, deterministic clock + seed.
        var clock = new FakeClock();
        var session = new DrawingSession(pool, config, shuffle: true, new Random(2026), clock.Read);

        var displayed = new List<string>();

        // Complete images 1-2 on the 30s timer.
        for (var i = 0; i < 2; i++)
        {
            displayed.Add(session.CurrentImage!);
            clock.Advance(30);
            session.Next();
        }

        // Skip image 3 after 8s (does not count, time excluded).
        var skipped = session.CurrentImage!;
        clock.Advance(8);
        session.Skip();
        Assert.NotEqual(skipped, session.CurrentImage);

        // Complete images 3-6 on the timer to reach the configured count of 6.
        while (!session.IsComplete)
        {
            displayed.Add(session.CurrentImage!);
            clock.Advance(30);
            session.Next();
        }

        // 4) FD-007 summary: 6 images completed, 6*30s drawn, skipped 8s excluded.
        var summary = session.Summary;
        Assert.True(session.IsComplete);
        Assert.Equal(6, summary.ImagesDisplayed);
        Assert.Equal(TimeSpan.FromSeconds(6 * 30), summary.TotalDrawingTime);

        Assert.Equal(6, displayed.Count);
        Assert.All(displayed, img => Assert.Contains(img, pool));
    }

    [Fact]
    public void FullSession_EndedEarly_SummaryReflectsWorkSoFar()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = new[]
            {
                new DocumentEntry("root/a.jpg", "image/jpeg"),
                new DocumentEntry("root/b.jpg", "image/jpeg"),
                new DocumentEntry("root/c.jpg", "image/jpeg"),
            },
        });
        var pool = FolderImageEnumerator.EnumerateImages(tree, "root");

        var config = SessionSetup.Evaluate("20", "10", folderSelected: true).Config!.Value;
        var clock = new FakeClock();
        var session = new DrawingSession(pool, config, shuffle: false, new Random(1), clock.Read);

        clock.Advance(20);
        session.Next();          // 1 completed, 20s
        clock.Advance(20);
        session.Next();          // 2 completed, 40s
        clock.Advance(5);        // mid-third image
        session.End();           // bank partial, stop early

        var summary = session.Summary;
        Assert.True(session.IsComplete);
        Assert.Equal(2, summary.ImagesDisplayed);                         // ended image not counted
        Assert.Equal(TimeSpan.FromSeconds(45), summary.TotalDrawingTime); // 20 + 20 + 5 partial
    }
}
