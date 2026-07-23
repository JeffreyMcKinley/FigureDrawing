using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FigureDrawing.Tests;

// FD-004 UI-contract tests: SessionActivity resolves views/strings/layouts by name at runtime, so a
// rename/deletion in the resource XML would compile fine but crash on the device. Mirrors
// UiResourceContractTests (which covers MainActivity) for the session player screen — read the
// resource XML directly (no device) and assert every name SessionActivity references still exists.
public class SessionScreenContractTests
{
    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    static string SessionActivitySource => File.ReadAllText(TestPaths.Path("SessionActivity.cs"));

    static IReadOnlySet<string> ReferencedResources(string kind)
    {
        var matches = Regex.Matches(SessionActivitySource, $@"Resource\.{kind}\.(\w+)");
        return matches.Select(m => m.Groups[1].Value).ToHashSet();
    }

    static IReadOnlySet<string> LayoutIds(string layoutName)
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", $"{layoutName}.xml"));
        return doc.Descendants()
            .Select(e => e.Attribute(Android + "id")?.Value)
            .Where(v => v is not null)
            .Select(v => v!.Replace("@+id/", "").Replace("@id/", ""))
            .ToHashSet();
    }

    static IReadOnlySet<string> StringResourceNames()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "values", "strings.xml"));
        return doc.Root!.Elements("string")
            .Select(e => e.Attribute("name")?.Value)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet();
    }

    [Fact]
    public void EveryReferencedViewId_ExistsInActivitySessionLayout()
    {
        var ids = LayoutIds("activity_session");
        var missing = ReferencedResources("Id").Where(id => !ids.Contains(id)).ToList();

        Assert.True(missing.Count == 0,
            $"SessionActivity references view id(s) not declared in activity_session.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedString_ExistsInStringsXml()
    {
        var names = StringResourceNames();
        var missing = ReferencedResources("String").Where(n => !names.Contains(n)).ToList();

        Assert.True(missing.Count == 0,
            $"SessionActivity references string(s) not declared in strings.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedLayout_HasAFile()
    {
        var missing = ReferencedResources("Layout")
            .Where(name => !File.Exists(TestPaths.Path("Resources", "layout", $"{name}.xml")))
            .ToList();

        Assert.True(missing.Count == 0,
            $"SessionActivity references layout(s) with no file: {string.Join(", ", missing)}");
    }

    // FD-004 explicit view contract: the full-screen image and the status/error text.
    // FD-005 adds the countdown view.
    [Theory]
    [InlineData("session_image")]
    [InlineData("session_status")]
    [InlineData("session_timer")]
    public void ActivitySession_DeclaresFdViews(string id) =>
        Assert.Contains(id, LayoutIds("activity_session"));

    // FD-004 string contract: the image content-description and the undisplayable-pool error.
    // FD-005 adds the timer's content-description and its pre-first-tick placeholder.
    [Theory]
    [InlineData("session_image_desc")]
    [InlineData("session_error_text")]
    [InlineData("session_timer_desc")]
    [InlineData("session_timer_placeholder")]
    public void Strings_DeclaresSessionStrings(string name) =>
        Assert.Contains(name, StringResourceNames());

    // FD-005: the countdown must not be hidden behind the pose image, and must not start life
    // invisible — it is the one control the drawer looks at all session long.
    [Fact]
    public void SessionTimer_OverlaysTheImage_AndIsVisibleByDefault()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", "activity_session.xml"));
        var root = doc.Root!;
        var children = root.Elements().ToList();

        var imageIndex = children.FindIndex(e => e.Attribute(Android + "id")?.Value == "@+id/session_image");
        var timerIndex = children.FindIndex(e => e.Attribute(Android + "id")?.Value == "@+id/session_timer");

        Assert.True(imageIndex >= 0 && timerIndex > imageIndex,
            "session_timer must be declared after session_image so it draws on top of the pose.");

        var timer = children[timerIndex];
        Assert.NotEqual("gone", timer.Attribute(Android + "visibility")?.Value);
        Assert.NotEqual("invisible", timer.Attribute(Android + "visibility")?.Value);
    }

    // The screen must drive the countdown from the Core PoseCountdown and reset it per pose, and it
    // must pause/resume with the lifecycle (FD-005 acceptance). Source-level guard: these are
    // Android-only code paths the unit tests can't execute.
    [Theory]
    [InlineData("new PoseCountdown(")]
    [InlineData("countdown.Restart()")]
    [InlineData("countdown.Pause()")]
    [InlineData("countdown.Resume()")]
    [InlineData("protected override void OnPause()")]
    [InlineData("protected override void OnResume()")]
    public void SessionActivity_WiresTheCountdownToTheLifecycle(string snippet) =>
        Assert.Contains(snippet, SessionActivitySource);

    // The fitCenter scale type is what keeps the image filling the screen WITHOUT distortion
    // (FD-004 acceptance). Guard it against an accidental edit to a stretching scale type.
    [Fact]
    public void SessionImage_UsesFitCenter_SoImageIsNotDistorted()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", "activity_session.xml"));
        var image = doc.Descendants()
            .First(e => e.Attribute(Android + "id")?.Value == "@+id/session_image");

        Assert.Equal("fitCenter", image.Attribute(Android + "scaleType")?.Value);
    }
}
