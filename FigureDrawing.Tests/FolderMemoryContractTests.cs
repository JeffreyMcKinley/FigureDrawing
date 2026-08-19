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
    // trusted: MainActivity's own comments name most of the APIs asserted for.
    [Fact]
    public void TheStripper_RemovesCommentsAndLiterals()
    {
        Assert.Equal(Source.Length, Code.Length);
        Assert.DoesNotContain("Storage Access Framework", Code, StringComparison.Ordinal);
        Assert.DoesNotContain("figuredrawing.db", Code, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", Code, StringComparison.Ordinal);
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
        var body = MethodBody("RememberedTree");

        Assert.Contains("LibraryReference.TryParse", body, StringComparison.Ordinal);
        Assert.Contains("LibraryReference.HasReadGrant", body, StringComparison.Ordinal);
        Assert.Contains("PersistedUriPermissions", MethodBody("PersistedGrants"), StringComparison.Ordinal);
    }

    // A remembered folder whose grant has since been revoked (permission cleared, volume unmounted,
    // provider uninstalled) must leave the empty state showing, not throw on every launch from then
    // on — the stale uri is persisted, so an escape here reproduces forever (INV-GRP-5).
    [Fact]
    public void Restoring_ChecksTheGrantAndSurvivesAFailure()
    {
        var body = MethodBody("RestoreLastFolder");

        Assert.Contains("RememberedTree()", body, StringComparison.Ordinal);

        var caught = body.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "RestoreLastFolder no longer catches a failed load.");
        Assert.Contains("ResetLibrary();", body[caught..], StringComparison.Ordinal);
    }

    // The remembered folder is where the picker opens, so reopening the same library is one tap.
    [Fact]
    public void PickingAgain_StartsThePickerInTheRememberedFolder()
    {
        var body = MethodBody("PickFolder");

        Assert.Contains("Intent.ActionOpenDocumentTree", body, StringComparison.Ordinal);
        Assert.Contains("DocumentsContract.ExtraInitialUri", body, StringComparison.Ordinal);
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

        var before = body[..attached];
        Assert.Contains("LastPickedDocumentUri()", before, StringComparison.Ordinal);
        Assert.Matches(@"\bif\b", before);
    }
}
