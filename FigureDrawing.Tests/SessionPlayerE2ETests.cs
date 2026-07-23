using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-004 end-to-end: a full player-driven session wired through the REAL components it depends on —
// FD-001 enumeration feeds the pool, FD-002 setup produces the config, FD-003 DrawingSession runs
// it, and the FD-004 SessionPlayer resolves each image through a loader (skipping a broken one). No
// Android/Appium here (the SessionActivity is exercised by the opt-in UITests); this drives the same
// public surface the screen uses.
public class SessionPlayerE2ETests
{
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId) =>
            children.TryGetValue(parentDocumentId, out var kids) ? kids : Array.Empty<DocumentEntry>();
    }

    const string Dir = FolderImageEnumerator.DirectoryMimeType;

    [Fact]
    public void FullSession_FromPickedFolder_SkipsABrokenImage_AndCompletes()
    {
        // 1) FD-001: enumerate a picked folder.
        var tree = new FakeTree(new()
        {
            ["root"] = new[]
            {
                new DocumentEntry("root/pose1.jpg", "image/jpeg"),
                new DocumentEntry("root/pose2.png", "image/png"),
                new DocumentEntry("root/notes.txt", "text/plain"),   // ignored
                new DocumentEntry("root/pose3.webp", "image/webp"),
            },
        });
        var pool = FolderImageEnumerator.EnumerateImages(tree, "root");
        Assert.Equal(3, pool.Count);

        // 2) FD-002: validate setup into a config (3 images, matches the pool exactly).
        var config = SessionSetup.Evaluate("30", "3", folderSelected: true).Config!.Value;

        // 3) FD-003 + FD-004: run the session through the player. pose2 is "broken" (loader returns
        // null); the player must skip past it without counting it and still reach the target of 3.
        var session = new DrawingSession(pool, config, shuffle: false, new Random(7), () => TimeSpan.Zero);
        var skipped = new List<string>();
        var player = new SessionPlayer<string>(
            session,
            id => id.Contains("pose2") ? null : id,
            onUnreadable: skipped.Add);

        var displayed = new List<string>();
        while (!player.IsComplete)
        {
            displayed.Add(player.CurrentImage!);
            player.Next();
        }

        // The broken image was skipped and logged; the session still completed its 3 images.
        Assert.Contains("root/pose2.png", skipped);
        Assert.DoesNotContain(displayed, d => d.Contains("pose2"));
        Assert.True(player.IsComplete);
        Assert.False(player.CouldNotDisplayImage);
        Assert.Equal(3, player.Summary.ImagesDisplayed);
    }

    [Fact]
    public void FullSession_AllImagesBroken_SurfacesCouldNotDisplay()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = new[]
            {
                new DocumentEntry("root/a.jpg", "image/jpeg"),
                new DocumentEntry("root/b.jpg", "image/jpeg"),
            },
        });
        var pool = FolderImageEnumerator.EnumerateImages(tree, "root");
        var config = SessionSetup.Evaluate("30", "5", folderSelected: true).Config!.Value;

        var session = new DrawingSession(pool, config, shuffle: false, new Random(1), () => TimeSpan.Zero);
        var player = new SessionPlayer<string>(session, _ => null, maxConsecutiveFailures: 8);

        Assert.True(player.IsComplete);
        Assert.True(player.CouldNotDisplayImage);
        Assert.Null(player.CurrentImage);
        Assert.Equal(0, player.Summary.ImagesDisplayed);
    }
}
