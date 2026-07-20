namespace FigureDrawing.Core;

// One entry (file or subfolder) inside a picked folder tree, reduced to the two fields the
// enumeration cares about. The Android layer maps a Storage Access Framework document row to
// this; tests supply it directly.
public readonly record struct DocumentEntry(string DocumentId, string? MimeType);

// Abstraction over "list the direct children of a folder document". The Android adapter backs
// this with DocumentsContract + ContentResolver; tests back it with an in-memory tree.
public interface IDocumentTree
{
    IEnumerable<DocumentEntry> GetChildren(string parentDocumentId);
}

// Pure FD-001 folder-selection logic: given a document tree, walk it recursively and yield the
// document IDs of every image file. No Android dependency, so it is unit-testable.
public static class FolderImageEnumerator
{
    // MIME type the Storage Access Framework reports for a subdirectory in a tree.
    public const string DirectoryMimeType = "vnd.android.document/directory";

    public static bool IsDirectory(string? mimeType) => mimeType == DirectoryMimeType;

    // Any tree entry whose MIME type starts with "image/" (jpg/png/webp/gif/heic/...). A
    // directory is never an image even though nothing else claims that MIME prefix.
    public static bool IsImage(string? mimeType) =>
        mimeType is not null && !IsDirectory(mimeType) && mimeType.StartsWith("image/");

    // Depth-first walk from rootDocumentId, descending into every subdirectory and returning the
    // document ID of every image file found, in encounter order. Guards against cycles (a tree
    // that reports a document as its own descendant) so a malformed provider can't loop forever.
    public static IReadOnlyList<string> EnumerateImages(IDocumentTree tree, string rootDocumentId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(rootDocumentId);

        var images = new List<string>();
        var visited = new HashSet<string>();
        Walk(tree, rootDocumentId, images, visited);
        return images;
    }

    static void Walk(IDocumentTree tree, string documentId, List<string> images, HashSet<string> visited)
    {
        if (!visited.Add(documentId))
            return;

        foreach (var entry in tree.GetChildren(documentId))
        {
            if (entry.DocumentId is null)
                continue;

            if (IsDirectory(entry.MimeType))
                Walk(tree, entry.DocumentId, images, visited);
            else if (IsImage(entry.MimeType))
                images.Add(entry.DocumentId);
        }
    }
}
