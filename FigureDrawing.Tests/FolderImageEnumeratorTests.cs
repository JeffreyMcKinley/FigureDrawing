using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-001 folder-selection: the recursive image-enumeration logic that backs the folder picker.
// The Android UI/SAF plumbing is not exercised here; FolderImageEnumerator is the pure core.
public class FolderImageEnumeratorTests
{
    // In-memory IDocumentTree: maps a folder document ID to its direct children.
    sealed class FakeTree(Dictionary<string, DocumentEntry[]> children) : IDocumentTree
    {
        public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId) =>
            children.TryGetValue(parentDocumentId, out var kids) ? kids : Array.Empty<DocumentEntry>();
    }

    const string Dir = FolderImageEnumerator.DirectoryMimeType;

    static DocumentEntry Img(string id, string mime = "image/jpeg") => new(id, mime);
    static DocumentEntry Folder(string id) => new(id, Dir);
    static DocumentEntry File(string id, string mime) => new(id, mime);

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
        Assert.Equal(expected, FolderImageEnumerator.IsImage(mime));

    [Fact]
    public void EnumerateImages_ReturnsTopLevelImages()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("a"), Img("b"), Img("c")],
        });

        var result = FolderImageEnumerator.EnumerateImages(tree, "root");

        Assert.Equal(new[] { "a", "b", "c" }, result);
    }

    [Fact]
    public void EnumerateImages_ExcludesNonImageFiles()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("pic"), File("notes", "text/plain"), File("doc", "application/pdf")],
        });

        var result = FolderImageEnumerator.EnumerateImages(tree, "root");

        Assert.Equal(new[] { "pic" }, result);
    }

    [Fact]
    public void EnumerateImages_RecursesIntoSubfolders()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Img("top"), Folder("sub")],
            ["sub"] = [Img("nested"), Folder("deep")],
            ["deep"] = [Img("deepest")],
        });

        var result = FolderImageEnumerator.EnumerateImages(tree, "root");

        Assert.Equal(new[] { "top", "nested", "deepest" }, result);
    }

    [Fact]
    public void EnumerateImages_EmptyFolder_ReturnsEmpty()
    {
        var tree = new FakeTree(new() { ["root"] = Array.Empty<DocumentEntry>() });

        Assert.Empty(FolderImageEnumerator.EnumerateImages(tree, "root"));
    }

    [Fact]
    public void EnumerateImages_FolderWithOnlyNonImages_ReturnsEmpty()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [File("a", "text/plain"), Folder("sub")],
            ["sub"] = [File("b", "application/zip")],
        });

        Assert.Empty(FolderImageEnumerator.EnumerateImages(tree, "root"));
    }

    [Fact]
    public void EnumerateImages_SkipsEmptySubfolderWithoutCrashing()
    {
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("empty"), Img("keep")],
            // "empty" intentionally has no entry in the map.
        });

        Assert.Equal(new[] { "keep" }, FolderImageEnumerator.EnumerateImages(tree, "root"));
    }

    [Fact]
    public void EnumerateImages_CyclicTree_DoesNotLoopForever()
    {
        // Malformed provider: "a" reports "b" as a child folder and "b" reports "a" back.
        var tree = new FakeTree(new()
        {
            ["root"] = [Folder("a")],
            ["a"] = [Img("x"), Folder("b")],
            ["b"] = [Img("y"), Folder("a")],
        });

        var result = FolderImageEnumerator.EnumerateImages(tree, "root");

        Assert.Equal(new[] { "x", "y" }, result);
    }

    [Fact]
    public void EnumerateImages_NullArguments_Throw()
    {
        var tree = new FakeTree(new());

        Assert.Throws<ArgumentNullException>(() => FolderImageEnumerator.EnumerateImages(null!, "root"));
        Assert.Throws<ArgumentNullException>(() => FolderImageEnumerator.EnumerateImages(tree, null!));
    }
}
