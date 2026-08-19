using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// The remembered-library rules (docs/DOMAIN-MODEL.md §5.1). These are the cases a stored value
// actually arrives in — blank, foreign, truncated, or fine — which is what the Android layer used
// to decide inline where nothing but a text-matching contract test could reach it.
public class LibraryReferenceTests
{
    const string TreeUri = "content://com.android.externalstorage.documents/tree/primary%3APictures";

    [Fact]
    public void AFreshInstall_HasNothingToRestore()
    {
        Assert.False(LibraryReference.TryParse(null, out var reference));
        Assert.Equal(string.Empty, reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ABlankValue_IsNotAReference(string stored) =>
        Assert.False(LibraryReference.TryParse(stored, out _));

    // Garbage in the settings document is expected rather than exceptional: the file survives app
    // upgrades, restores from another device, and kills mid-write (INV-SET-P6).
    [Theory]
    [InlineData("not a uri")]
    [InlineData("/sdcard/Pictures")]
    [InlineData("file:///sdcard/Pictures")]
    [InlineData("http://example.com/tree/x")]
    public void AValueThatIsNotAContentUri_IsNotAReference(string stored) =>
        Assert.False(LibraryReference.TryParse(stored, out _));

    // The single-document form the picker returns for ACTION_OPEN_DOCUMENT, not the tree form
    // ACTION_OPEN_DOCUMENT_TREE returns. Asking the platform for a tree document id from one of
    // these throws, which is the failure this rule exists to stop.
    [Fact]
    public void ASingleDocumentUri_IsNotAReference() =>
        Assert.False(LibraryReference.TryParse(
            "content://com.android.externalstorage.documents/document/primary%3APictures", out _));

    // "content://.../tree/" with nothing after it carries no folder identity.
    [Fact]
    public void ATreeUriWithNoDocumentId_IsNotAReference() =>
        Assert.False(LibraryReference.TryParse(
            "content://com.android.externalstorage.documents/tree/", out _));

    [Fact]
    public void ATreeUri_IsAReference()
    {
        Assert.True(LibraryReference.TryParse(TreeUri, out var reference));
        Assert.Equal(TreeUri, reference);
    }

    // The scheme is case-insensitive per RFC 3986; the opaque document id after it is not, so the
    // value comes back exactly as stored apart from surrounding whitespace.
    [Fact]
    public void TheSchemeIsCaseInsensitive_AndTheRestIsUntouched()
    {
        const string mixedCase = "CONTENT://com.android.externalstorage.documents/tree/primary%3APictures";

        Assert.True(LibraryReference.TryParse($"  {mixedCase}  ", out var reference));
        Assert.Equal(mixedCase, reference);
    }

    [Fact]
    public void WithNoGrantsAtAll_NothingIsHeld() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, []));

    [Fact]
    public void AGrantForAnotherFolder_IsNotThisOne() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, [
            new PersistedGrant("content://com.android.externalstorage.documents/tree/primary%3ADownload", true),
        ]));

    // The app only ever takes the read flag, so a write-only entry belongs to something else.
    [Fact]
    public void AWriteOnlyGrant_DoesNotRestoreTheLibrary() =>
        Assert.False(LibraryReference.HasReadGrant(TreeUri, [new PersistedGrant(TreeUri, false)]));

    [Fact]
    public void AReadGrantForThisFolder_IsHeld() =>
        Assert.True(LibraryReference.HasReadGrant(TreeUri, [
            new PersistedGrant("content://com.android.externalstorage.documents/tree/primary%3ADownload", true),
            new PersistedGrant(TreeUri, true),
        ]));

    // A revoked grant leaves the reference behind, which is exactly the state a launch has to
    // survive (INV-GRP-5).
    [Fact]
    public void AReferenceWithoutItsGrant_ParsesButIsNotHeld()
    {
        Assert.True(LibraryReference.TryParse(TreeUri, out var reference));
        Assert.False(LibraryReference.HasReadGrant(reference, [new PersistedGrant(null, true)]));
    }

    [Fact]
    public void AGrantIsNeverHeldForNothing() =>
        Assert.False(LibraryReference.HasReadGrant(null, [new PersistedGrant(null, true)]));
}
