namespace FigureDrawing.Core;

// One entry from the platform's list of permissions the app still holds, reduced to the two fields
// the decision below needs. The Android layer maps a persisted URI permission to this; tests supply
// it directly.
public readonly record struct PersistedGrant(string? Reference, bool IsRead);

// The rules about the *remembered* reference library — the string `Settings.LastCollection` carries
// between launches (docs/DOMAIN-MODEL.md §5.1, INV-SET-P5).
//
// Two questions, both pure, both previously answered inside MainActivity where only a text-matching
// contract test could reach them: is this stored value still worth acting on at all, and is the
// permission that made it usable still held. What the answers are *used for* stays in the Android
// layer — restoring the library on launch, and telling the system picker where to open.
//
// A stored value is expected to go bad (INV-GRP-5): the folder may be gone, the grant revoked, the
// document deleted, or the value itself written by a version of this app that stored something
// else. Every one of those is an ordinary "no", never an exception.
public static class LibraryReference
{
    // The Storage Access Framework's tree form. Anything else — a bare document URI, a file path, a
    // value from another app — cannot be turned into a tree document id, and asking the platform to
    // do it anyway throws rather than returning null.
    const string TreeSegment = "/tree/";

    // SAF hands out content:// URIs and nothing else. Checked because the value is read back from
    // storage rather than from the picker that produced it.
    const string ContentScheme = "content://";

    // Whether `stored` is a reference this app could still act on, and the trimmed form to act on.
    // Null, blank, non-content, and non-tree values are all a plain "no" (INV-GRP-5). Whether the
    // folder still EXISTS is not knowable here and is not asked: that shows up as an empty
    // enumeration, which is a normal outcome (INV-GRP-4).
    public static bool TryParse(string? stored, out string reference)
    {
        reference = string.Empty;

        if (string.IsNullOrWhiteSpace(stored))
            return false;

        var trimmed = stored.Trim();

        // Ordinal-ignore-case on the scheme only: RFC 3986 makes the scheme case-insensitive, while
        // the document id after it is opaque and must be compared exactly.
        if (!trimmed.StartsWith(ContentScheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var tree = trimmed.IndexOf(TreeSegment, ContentScheme.Length - 1, StringComparison.Ordinal);
        if (tree < 0 || tree + TreeSegment.Length >= trimmed.Length)
            return false;

        reference = trimmed;
        return true;
    }

    // Whether the app still holds a read grant for this reference. A grant that was taken as
    // persistable can still be gone — permissions cleared, volume unmounted, provider uninstalled —
    // and acting on the reference without one fails at the provider instead (INV-GRP-5).
    //
    // Write grants are not accepted as read grants: this app only ever takes the read flag, so a
    // write-only entry means someone else's grant, not ours.
    public static bool HasReadGrant(string? reference, IEnumerable<PersistedGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (string.IsNullOrWhiteSpace(reference))
            return false;

        foreach (var grant in grants)
        {
            if (grant.IsRead && string.Equals(grant.Reference, reference, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
