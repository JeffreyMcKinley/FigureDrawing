using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The reference library: what counts as a drawable image, the recursive walk under a picked folder,
// and the pool it hands to a session (INV-IMG-3, INV-GRP-*, INV-POOL-*). The Android UI/SAF
// plumbing is not exercised here; the library is the pure core behind it.
public class ReferenceLibraryTests
{
    // In-memory IDocumentTree: maps a folder document ID to its direct children.
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public int Walks { get; private set; }

        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            Walks++;
            return children.TryGetValue(parentDocumentId, out var kids) ? kids : [];
        }

        public void Replace(string parentDocumentId, params DocumentEntry[] kids) =>
            children[parentDocumentId] = kids;
    }

    const string Dir = ReferenceLibrary.DirectoryMimeType;

    static DocumentEntry Img(string id, string mime = "image/jpeg") => new(id, mime);
    static DocumentEntry Folder(string id) => new(id, Dir);
    static DocumentEntry File(string id, string mime) => new(id, mime);

    static IReadOnlyList<string> Pool(FakeTree tree, string root = "root") =>
        new ReferenceLibrary(tree, root).Pool;

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("image/heic", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    [InlineData(Dir, false)]
    [InlineData(null, false)]
    public void IsImage_ClassifiesByMimePrefix(string? mime, bool expected) =>
        Assert.Equal(expected, ReferenceLibrary.IsImage(mime));

    [Fact]
    public void Pool_ReturnsTopLevelImages()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("a"), Img("b"), Img("c")],
        });

        Assert.Equal(new[] { "a", "b", "c" }, Pool(tree));
    }

    [Fact]
    public void Pool_ExcludesNonImageFiles()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("pic"), File("notes", "text/plain"), File("doc", "application/pdf")],
        });

        Assert.Equal(new[] { "pic" }, Pool(tree));
    }

    [Fact]
    public void Pool_RecursesIntoSubfolders_InEncounterOrder()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("top"), Folder("sub")],
            ["sub"] = [Img("nested"), Folder("deep")],
            ["deep"] = [Img("deepest")],
        });

        Assert.Equal(new[] { "top", "nested", "deepest" }, Pool(tree));
    }

    [Fact]
    public void EmptyFolder_IsEmpty_NotAnError()
    {
        var tree = new FakeTree(new() { ["root"] = [] });
        var library = new ReferenceLibrary(tree, "root");

        Assert.Empty(library.Pool);
        Assert.True(library.IsEmpty);
        Assert.Equal(0, library.Count);
    }

    [Fact]
    public void FolderWithOnlyNonImages_IsEmpty()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [File("a", "text/plain"), Folder("sub")],
            ["sub"] = [File("b", "application/zip")],
        });

        Assert.Empty(Pool(tree));
    }

    [Fact]
    public void SkipsEmptySubfolderWithoutCrashing()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("empty"), Img("keep")],
            // "empty" intentionally has no entry in the map.
        });

        Assert.Equal(new[] { "keep" }, Pool(tree));
    }

    [Fact]
    public void CyclicTree_DoesNotLoopForever()
    {
        // Malformed provider: "a" reports "b" as a child folder and "b" reports "a" back.
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("a")],
            ["a"] = [Img("x"), Folder("b")],
            ["b"] = [Img("y"), Folder("a")],
        });

        Assert.Equal(new[] { "x", "y" }, Pool(tree));
    }

    // The same image reached through two subfolders is one entry in the pool, not two — repetition
    // within a session comes from passes, never from a pool that lists an image twice (INV-POOL-2).
    [Fact]
    public void Pool_HasNoDuplicates_WhenAnImageIsReachedTwice()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("one"), Folder("two")],
            ["one"] = [Img("shared"), Img("only-one")],
            ["two"] = [Img("shared"), Img("only-two")],
        });

        Assert.Equal(new[] { "shared", "only-one", "only-two" }, Pool(tree));
    }

    // The visited set stops a reported cycle; a provider that invents a fresh id at every level
    // never repeats one, so depth is what stops it. Unbounded recursion here is a stack overflow,
    // which kills the process rather than raising something catchable (INV-GRP-3).
    [Fact]
    public void UnboundedlyDeepTree_StopsInsteadOfOverflowing()
    {
        // "d0" contains "d1" contains "d2"… forever, each level also holding one image.
        var tree = new EndlessTree();

        var library = new ReferenceLibrary(tree, "d0");

        Assert.NotEmpty(library.Pool);
        Assert.True(library.Count < 200, $"walked {library.Count} levels — the depth cap did not hold");
    }

    sealed class EndlessTree : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
        {
            var level = int.Parse(parentDocumentId[1..]);
            yield return new DocumentEntry($"image-{level}", "image/jpeg");
            yield return new DocumentEntry($"d{level + 1}", Dir);
        }
    }

    // A mapper backed by the Android adapter can fail on a document id the provider reports from
    // outside the tree. One bad row drops that entry; it never aborts the walk (INV-X-11).
    [Fact]
    public void ToImageId_ThatThrows_DropsTheEntryAndKeepsWalking()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("good"), Img("bad"), Img("also-good")] });

        var library = new ReferenceLibrary(
            tree, "root",
            toImageId: id => id == "bad" ? throw new InvalidOperationException("outside the tree") : id);

        Assert.Equal(new[] { "good", "also-good" }, library.Pool);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var tree = new FakeTree(new());

        Assert.Throws<ArgumentNullException>(() => new ReferenceLibrary(null!, "root"));
        Assert.Throws<ArgumentNullException>(() => new ReferenceLibrary(tree, null!));
    }

    // --- Identity, mapping, and re-enumeration --------------------------------

    [Fact]
    public void Library_KeepsItsRootIdentityAndName()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a")] });
        var library = new ReferenceLibrary(tree, "root", "Pics");

        Assert.Equal("root", library.RootDocumentId);
        Assert.Equal("Pics", library.DisplayName);
        Assert.Equal(1, library.Count);
        Assert.False(library.IsEmpty);
    }

    // The Android layer maps each document id to the durable content URI a session draws from, so
    // the pool needs no second pass at the call site.
    [Fact]
    public void ToImageId_MapsEachDocumentIdIntoThePool()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a"), Img("b")] });

        var library = new ReferenceLibrary(tree, "root", toImageId: id => $"content://tree/{id}");

        Assert.Equal(new[] { "content://tree/a", "content://tree/b" }, library.Pool);
    }

    [Fact]
    public void ToImageId_ReturningNull_DropsTheEntry()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("keep"), Img("drop")] });

        var library = new ReferenceLibrary(tree, "root", toImageId: id => id == "drop" ? null : id);

        Assert.Equal(new[] { "keep" }, library.Pool);
    }

    // Membership is derived, never stored (INV-GRP-1): re-enumerating picks up an edited folder.
    [Fact]
    public void Enumerate_RewalksTheTree_PickingUpChanges()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a")] });
        var library = new ReferenceLibrary(tree, "root");
        Assert.Equal(new[] { "a" }, library.Pool);

        tree.Replace("root", Img("a"), Img("b"));
        library.Enumerate();

        Assert.Equal(new[] { "a", "b" }, library.Pool);
    }

    [Fact]
    public void Empty_HasNoRootAndNoImages()
    {
        var empty = ReferenceLibrary.Empty;

        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Pool);
        Assert.Equal(string.Empty, empty.RootDocumentId);

        // Re-enumerating a library with no folder behind it is a no-op, not a crash.
        empty.Enumerate();
        Assert.Empty(empty.Pool);
    }

    // A session copies the pool at construction, so re-walking the folder mid-session cannot
    // reorder or resize the run already under way (INV-POOL-4).
    [Fact]
    public void ReEnumerating_DoesNotDisturbARunningSession()
    {
        var tree = new FakeTree(new() { ["root"] = [Img("a"), Img("b")] });
        var library = new ReferenceLibrary(tree, "root");

        var session = new DrawingSession<string>(
            library.Pool, new SessionConfig(30, 4), id => id,
            shuffle: false, random: new Random(1), clock: () => TimeSpan.Zero);

        tree.Replace("root", Img("z"), Img("y"), Img("x"));
        library.Enumerate();

        var seen = new List<string>();
        while (!session.IsComplete)
        {
            seen.Add(session.CurrentImage!);
            session.Next();
        }

        Assert.Equal(new[] { "a", "b", "a", "b" }, seen);
        Assert.Equal(new[] { "z", "y", "x" }, library.Pool);
    }
}
