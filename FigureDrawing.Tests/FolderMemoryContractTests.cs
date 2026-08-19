using System.Text;
using System.Text.RegularExpressions;

namespace FigureDrawing.Tests;

// Contract tests for "the app remembers the folder you picked" (docs/ARCHITECTURE.md §11).
//
// The rules behind the feature are Core's and are unit tested in LibraryReferenceTests. What is
// left is wiring across a boundary Core cannot see — a persistable URI grant, one persisted string,
// and the extra that tells the system picker where to open — so it is pinned here by reading
// MainActivity as a file, the same tier that already guards the resource lookups. The behaviour is
// covered on a device by the folder-memory tests in FolderPickerUiTests.
//
// Comments and string literals are stripped before anything is asserted: a tier whose assertions a
// comment could satisfy would stay green through the exact deletion it exists to catch. What is
// asserted is which API is reached from which method, never how a statement is spelled — a rename
// or a reformat must not fail a build that still behaves.
public class FolderMemoryContractTests
{
    static readonly string Source = File.ReadAllText(TestPaths.Path("MainActivity.cs"));

    // MainActivity with every comment, string and char literal blanked out, positions preserved.
    static readonly string Code = StripCommentsAndLiterals(Source);

    // Replaces the contents of comments and literals with spaces, keeping the length and the line
    // breaks so offsets still line up with the original file. Blanking rather than deleting also
    // keeps a brace inside a string or an interpolation hole out of the brace matcher below.
    static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;

        void Blank(char c) => output.Append(c == '\n' ? '\n' : ' ');

        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    Blank(source[i++]);
                continue;
            }

            if (c == '/' && next == '*')
            {
                Blank(source[i++]);
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                    Blank(source[i++]);

                for (var end = 0; end < 2 && i < source.Length; end++)
                    Blank(source[i++]);

                continue;
            }

            if (c is '"' or '\'')
            {
                // A verbatim string ends on a quote that is not doubled; every other literal ends on
                // the first unescaped closing quote.
                var verbatim = c == '"' && output.Length > 0 && Verbatim(output);
                var quote = c;

                Blank(source[i++]);

                while (i < source.Length)
                {
                    if (verbatim)
                    {
                        if (source[i] == quote && i + 1 < source.Length && source[i + 1] == quote)
                        {
                            Blank(source[i++]);
                            Blank(source[i++]);
                            continue;
                        }

                        if (source[i] == quote)
                            break;
                    }
                    else
                    {
                        if (source[i] == '\\' && i + 1 < source.Length)
                        {
                            Blank(source[i++]);
                            Blank(source[i++]);
                            continue;
                        }

                        if (source[i] == quote || source[i] == '\n')
                            break;
                    }

                    Blank(source[i++]);
                }

                if (i < source.Length)
                    Blank(source[i++]);

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    // Whether the quote just consumed was preceded by @ (possibly after $), i.e. opens a verbatim
    // string. The prefix is still in the output because it is ordinary code.
    static bool Verbatim(StringBuilder emitted)
    {
        for (var i = emitted.Length - 1; i >= 0 && emitted.Length - i <= 2; i--)
        {
            if (emitted[i] == '@')
                return true;

            if (emitted[i] != '$')
                return false;
        }

        return false;
    }

    // The body of a method declared in MainActivity, brace-matched from its declaration. The
    // declaration is located by name at the start of a line and must be followed by nothing but
    // whitespace and its opening brace, so a mention of the method elsewhere — including a call —
    // cannot retarget the search.
    static string MethodBody(string methodName)
    {
        var declaration = Regex.Match(
            Code,
            $@"(?m)^[ \t]*(?:[\w.<>?\[\],]+[ \t]+)+{Regex.Escape(methodName)}[ \t]*\([^)\n]*\)[ \t]*$");

        Assert.True(declaration.Success, $"MainActivity no longer declares a method named '{methodName}'.");

        var open = Code.IndexOf('{', declaration.Index + declaration.Length);
        Assert.True(open >= 0, $"No body found for '{methodName}'.");
        Assert.True(
            Code[(declaration.Index + declaration.Length)..open].Trim().Length == 0,
            $"'{methodName}' is not followed by a block body; this test cannot read it.");

        var depth = 0;
        for (var i = open; i < Code.Length; i++)
        {
            if (Code[i] == '{') depth++;
            else if (Code[i] == '}' && --depth == 0)
                return Code[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{methodName}'.");
    }

    // The stripper is what makes every assertion below mean something, so it is checked rather than
    // trusted — against a fixture rather than against MainActivity, so refactoring the app cannot
    // fail the test that validates the tool.
    [Fact]
    public void TheStripper_RemovesCommentsAndLiterals()
    {
        const string fixture =
            "var a = Keep(); // Dropped()\\n" +
            "/* Dropped() */ var b = \"Dropped()\";\\n" +
            "var c = @\"Dropped() \"\" still dropped\";\\n" +
            "var d = $\"{Kept()} Dropped()\";\\n" +
            "var e = '}';\\n";

        var stripped = StripCommentsAndLiterals(fixture);

        Assert.Equal(fixture.Length, stripped.Length);
        Assert.Equal(fixture.Count(c => c == '\n'), stripped.Count(c => c == '\n'));
        Assert.DoesNotContain("Dropped()", stripped, StringComparison.Ordinal);
        Assert.Contains("Keep()", stripped, StringComparison.Ordinal);
        // Code inside an interpolation hole goes with the literal, deliberately: a method named
        // inside a log message is not wiring, and treating it as code is how an assertion ends up
        // satisfied by a diagnostic string.
        Assert.DoesNotContain("Kept()", stripped, StringComparison.Ordinal);

        // A brace inside a literal must never reach the brace matcher.
        Assert.DoesNotContain("}'", stripped, StringComparison.Ordinal);

        // And it is the real MainActivity the other tests read, so it must survive the same pass.
        Assert.Equal(Source.Length, Code.Length);
        Assert.DoesNotContain("figuredrawing.db", Code, StringComparison.Ordinal);
    }

    // Without a persisted grant the folder is remembered and unreadable: the uri survives, the
    // permission does not, and every relaunch shows the empty state.
    [Fact]
    public void PickingAFolder_TakesAPersistableReadGrant()
    {
        var body = MethodBody("OnActivityResult");

        Assert.Contains("TakePersistableUriPermission", body, StringComparison.Ordinal);
        Assert.Contains("GrantReadUriPermission", body, StringComparison.Ordinal);

        // Read-only against the artist's own storage: the app never writes into the library, so a
        // write flag on the grant would be privilege it has no use for.
        Assert.DoesNotContain("GrantWriteUriPermission", Code, StringComparison.Ordinal);
    }

    // The folder's identity is persisted, and persisted at the moment it is picked — Settings only
    // reaches disk on Save (docs/ARCHITECTURE.md §6), and the pick is one of the named write
    // moments (INV-SET-P4), so an assignment without one is forgotten on exit.
    [Fact]
    public void PickingAFolder_PersistsItAsLastCollection()
    {
        var body = MethodBody("OnActivityResult");

        var assignment = Regex.Match(body, @"settings\.LastCollection\s*=");
        Assert.True(assignment.Success, "The picked folder is no longer written to Settings.LastCollection.");
        Assert.Contains("settings.Save();", body[assignment.Index..], StringComparison.Ordinal);
    }

    // Only the root's identity is persisted, never its contents (INV-GRP-1): the pool is re-walked
    // on restore, so a folder edited between launches needs no migration.
    [Fact]
    public void NothingButTheFolderIdentity_IsPersisted()
    {
        var assigned = Regex.Matches(Code, @"settings\.LastCollection\s*=\s*(?<value>[^;]+);")
            .Select(m => m.Groups["value"].Value)
            .ToList();

        Assert.NotEmpty(assigned);
        Assert.All(assigned, value =>
        {
            Assert.DoesNotContain("Pool", value, StringComparison.Ordinal);
            Assert.DoesNotContain("library", value, StringComparison.Ordinal);
            Assert.DoesNotContain("Sample", value, StringComparison.Ordinal);
        });
    }

    // Restoring is OnCreate work: a launch has to come up with the library already loaded rather
    // than waiting for the artist to visit the Images tab.
    [Fact]
    public void Launching_RestoresTheRememberedFolder()
    {
        Assert.Contains("RestoreLastFolder();", MethodBody("OnCreate"), StringComparison.Ordinal);
    }

    // Whether a remembered folder is worth acting on is Core's rule, not the screen's — the Activity
    // may parse the uri and enumerate the platform's grants, and nothing else (§14).
    [Fact]
    public void WhetherTheRememberedFolderIsUsable_IsCoreRule()
    {
        Assert.Contains("LibraryReference.TryParse", MethodBody("RememberedTree"), StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", MethodBody("RestoreLastFolder"), StringComparison.Ordinal);
        Assert.Contains("PersistedUriPermissions", MethodBody("PersistedGrants"), StringComparison.Ordinal);
    }

    // A remembered folder whose grant has since been revoked (permission cleared, volume unmounted,
    // provider uninstalled) must leave the empty state showing, not throw on every launch from then
    // on — the stale uri is persisted, so an escape here reproduces forever (INV-GRP-5).
    [Fact]
    public void Restoring_ChecksTheGrantAndSurvivesAFailure()
    {
        var body = MethodBody("RestoreLastFolder");

        Assert.Contains("LibraryReference.TryParse", body, StringComparison.Ordinal);

        var caught = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "RestoreLastFolder no longer catches a failed load.");
        Assert.Contains("ShowRememberedFolderUnavailable();", body[caught..], StringComparison.Ordinal);

        // Whatever the screen says, the library it is showing has to be emptied: a half-restored
        // pool would leave Start open on images the session cannot read.
        Assert.Contains("ResetLibrary();", MethodBody("ShowRememberedFolderUnavailable"), StringComparison.Ordinal);

        // The grant refresh is best effort and must never fail a restore that otherwise worked.
        Assert.Contains("catch (Exception", MethodBody("RefreshGrant"), StringComparison.Ordinal);
    }

    // The remembered folder is where the picker opens, so reopening the same library is one tap.
    [Fact]
    public void PickingAgain_StartsThePickerInTheRememberedFolder()
    {
        var body = MethodBody("PickFolder");

        Assert.Contains("Intent.ActionOpenDocumentTree", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", body, StringComparison.Ordinal);

        // Launching it crosses the system boundary: an image with no documents provider must show a
        // message, not take the process down on a tap (INV-X-11).
        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);
    }

    // Handed the bare tree uri, DocumentsUI lands at the root of the provider instead of inside the
    // folder — the hint has to be the tree's *document* uri.
    [Fact]
    public void ThePickerHint_IsTheTreesDocumentUri()
    {
        var body = MethodBody("LastPickedDocumentUri");

        Assert.Contains("RememberedTree()", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.GetTreeDocumentId", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.BuildDocumentUriUsingTree", body, StringComparison.Ordinal);
    }

    // Settings are written at named moments (INV-SET-P4), and leaving the screen is one of them: a
    // process swiped off the recents list never runs OnDestroy, so a value still sitting only in
    // memory when the artist closes the app is a value they lose.
    [Fact]
    public void LeavingTheScreen_WritesSettingsToTheDatabase()
    {
        var body = MethodBody("OnPause");

        // The typed inputs live nowhere else, so without capturing them the write has nothing new to
        // persist and the backstop is decorative.
        Assert.Contains("CaptureTypedInputs();", body, StringComparison.Ordinal);
        Assert.Contains("SaveSettings();", body, StringComparison.Ordinal);

        // A failed write on the way out must not take the screen down with it — and must be visible,
        // because a preference set that silently stops persisting is this whole feature failing.
        var save = MethodBody("SaveSettings");
        Assert.Contains("catch (Exception", save, StringComparison.Ordinal);
        Assert.Contains("Log.Error", save, StringComparison.Ordinal);
        Assert.DoesNotContain("throw", save, StringComparison.Ordinal);
    }

    // A remembered folder that cannot be reopened is not the same state as never having picked one,
    // and must not read like it on screen: the artist needs to know their choice is still known and
    // what went wrong with it, or "it forgot my folder" is the only available reading.
    [Fact]
    public void ARememberedFolderThatCannotBeReopened_SaysSo()
    {
        var body = MethodBody("RenderLibrary");

        // Which of the three empty states the artist is in is Core's classification; the screen only
        // maps it to a resource, and each state maps to a different one.
        Assert.Contains("LibraryReference.Classify", body, StringComparison.Ordinal);
        Assert.Contains("LibraryStatus.Unavailable => Resource.String.folder_unavailable_text", body, StringComparison.Ordinal);
        Assert.Contains("LibraryStatus.Empty => Resource.String.empty_folder_text", body, StringComparison.Ordinal);
        Assert.Contains("Resource.String.empty_label_text", body, StringComparison.Ordinal);
    }

    // Restoring needs the grant; pointing the picker does not. Requiring one for the other is what
    // turns a revoked permission into a second problem — the artist re-picking the folder from the
    // provider root instead of from where they left off.
    [Fact]
    public void ThePickerHint_DoesNotRequireAGrant()
    {
        Assert.DoesNotContain("HasReadGrant", MethodBody("LastPickedDocumentUri"), StringComparison.Ordinal);
        Assert.DoesNotContain("HasReadGrant", MethodBody("RememberedTree"), StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", MethodBody("RestoreLastFolder"), StringComparison.Ordinal);
    }

    // A grant survives being taken again, and taking it again on a successful restore is what keeps
    // the folder in daily use from being the oldest entry when the platform trims the list.
    [Fact]
    public void RestoringAFolder_RefreshesItsGrant()
    {
        // Twice in the file: once where a folder is picked, once where a remembered one is restored.
        // Asserted on the API rather than on which private method happens to hold it.
        var taken = Regex.Matches(Code, @"TakePersistableUriPermission").Count;

        Assert.True(taken >= 2, $"Expected the grant taken on pick AND refreshed on restore; found {taken} call(s).");
    }

    // A grant kept for a folder the artist has moved on from can cost them the one they still use:
    // the platform caps a package's persisted grants and drops the oldest past the cap.
    [Fact]
    public void PickingADifferentFolder_ReleasesTheGrantItSupersedes()
    {
        Assert.Contains("LibraryReference.GrantsToRelease", Code, StringComparison.Ordinal);
        Assert.Contains("ReleasePersistableUriPermission", Code, StringComparison.Ordinal);
        Assert.Contains("ReleaseSupersededGrants", MethodBody("OnActivityResult"), StringComparison.Ordinal);
    }

    // A hint is a convenience; failing to build one must never cost the artist the picker itself.
    [Fact]
    public void AnUnusableHint_LeavesThePickerOpening()
    {
        var hint = MethodBody("LastPickedDocumentUri");

        var caught = hint.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "LastPickedDocumentUri no longer catches a failed hint.");
        Assert.Contains("return null;", hint[caught..], StringComparison.Ordinal);

        // Guarded at the call site too, so an absent hint is an absent extra rather than a crash:
        // the hint is fetched and tested before the extra is ever attached.
        var body = MethodBody("PickFolder");
        var attached = body.IndexOf("PutExtra", StringComparison.Ordinal);
        Assert.True(attached >= 0, "PickFolder no longer attaches the picker hint.");

        // The conditional has to test the hint itself: "contains an if" would be satisfied by
        // `if (true) intent.PutExtra(..., LastPickedDocumentUri())`.
        Assert.Matches(@"if\s*\(\s*LastPickedDocumentUri\(\)\s*(is|!=)", body[..attached]);
    }
}
