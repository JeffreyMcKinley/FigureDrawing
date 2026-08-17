using System.Globalization;
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

    // FD-005: the countdown must not start life invisible — it is the one control the drawer looks
    // at all session long. Since the Claude Design import it sits in the rail beside the pose rather
    // than overlaying it, so what has to hold is that the rail is painted, not that it is on top.
    [Fact]
    public void SessionTimer_IsVisibleByDefault()
    {
        var timer = Element("session_timer");

        Assert.NotEqual("gone", timer.Attribute(Android + "visibility")?.Value);
        Assert.NotEqual("invisible", timer.Attribute(Android + "visibility")?.Value);
        Assert.Equal("session_rail", AncestorIds(timer).First(id => id is not null));
    }

    // The three things that cover the pose (the rule-of-thirds grid, the between-poses break and the
    // pause sheet) must be declared after the image inside the stage, or they would be painted
    // underneath it and never seen.
    [Theory]
    [InlineData("session_grid")]
    [InlineData("session_break_overlay")]
    [InlineData("session_pause_overlay")]
    public void StageOverlays_AreDeclaredAfterTheImage(string id)
    {
        var stage = Element("session_stage").Elements().ToList();

        var imageIndex = stage.FindIndex(e => LocalId(e) == "session_image");
        var overlayIndex = stage.FindIndex(e => LocalId(e) == id);

        Assert.True(imageIndex >= 0, "session_image must be a direct child of session_stage.");
        Assert.True(overlayIndex > imageIndex,
            $"{id} must be declared after session_image so it draws on top of the pose.");
    }

    // The pause sheet must consume the touches that miss its buttons. Without this a stray tap on
    // the dimmed sheet falls through to session_image, whose click counts the pose and cancels the
    // pause. Not focusable: a focusable container takes D-pad/TalkBack focus ahead of Resume.
    [Fact]
    public void PauseOverlay_ConsumesTouchesThatMissItsButtons()
    {
        var overlay = Element("session_pause_overlay");

        Assert.Equal("true", overlay.Attribute(Android + "clickable")?.Value);
        Assert.NotEqual("true", overlay.Attribute(Android + "focusable")?.Value);
    }

    // The break overlay must NOT consume them: tapping through it is the documented way to end a
    // rest early (INV-SES-10).
    [Fact]
    public void BreakOverlay_LetsATapThrough() =>
        Assert.NotEqual("true", Element("session_break_overlay").Attribute(Android + "clickable")?.Value);

    // Every overlay starts hidden: the pose is what the screen opens on.
    [Theory]
    [InlineData("session_grid")]
    [InlineData("session_break_overlay")]
    [InlineData("session_pause_overlay")]
    [InlineData("session_summary")]
    [InlineData("session_status")]
    public void OverlaysAndSummary_StartHidden(string id) =>
        Assert.Equal("gone", Element(id).Attribute(Android + "visibility")?.Value);

    // The imported player: the rail's readouts and controls, the break/pause overlays' own clocks,
    // the viewing-tool chips, and the summary's four stats.
    [Theory]
    [InlineData("session_body")]
    [InlineData("session_stage")]
    [InlineData("session_rail")]
    [InlineData("session_ring")]
    [InlineData("session_progress")]
    [InlineData("session_pause")]
    [InlineData("session_skip")]
    [InlineData("session_next")]
    [InlineData("session_end")]
    [InlineData("session_progress_group")]
    [InlineData("session_pips")]
    [InlineData("session_stats")]
    [InlineData("break_timer")]
    [InlineData("paused_timer")]
    [InlineData("paused_stats")]
    [InlineData("paused_resume")]
    [InlineData("paused_skip")]
    [InlineData("paused_end")]
    [InlineData("chip_grayscale")]
    [InlineData("chip_flip")]
    [InlineData("chip_grid")]
    [InlineData("chip_blur")]
    [InlineData("chip_zoom_in")]
    [InlineData("chip_zoom_out")]
    [InlineData("summary_images")]
    [InlineData("summary_time")]
    [InlineData("summary_average")]
    [InlineData("summary_skipped")]
    [InlineData("summary_again")]
    [InlineData("summary_settings")]
    [InlineData("session_grid_v1")]
    [InlineData("session_grid_v2")]
    [InlineData("session_grid_h1")]
    [InlineData("session_grid_h2")]
    public void ActivitySession_DeclaresTheImportedPlayerViews(string id) =>
        Assert.Contains(id, LayoutIds("activity_session"));

    [Theory]
    [InlineData("session_progress_format")]
    [InlineData("session_stats_format")]
    [InlineData("session_ring_desc")]
    [InlineData("break_kicker_text")]
    [InlineData("break_help_text")]
    [InlineData("paused_kicker_text")]
    [InlineData("paused_stats_format")]
    [InlineData("tool_grayscale_text")]
    [InlineData("tool_flip_text")]
    [InlineData("tool_grid_text")]
    [InlineData("tool_blur_text")]
    [InlineData("summary_kicker_text")]
    [InlineData("summary_again_text")]
    [InlineData("summary_settings_text")]
    public void Strings_DeclaresTheImportedPlayerStrings(string name) =>
        Assert.Contains(name, StringResourceNames());

    // Galaxy Z Fold7 regression: with the rail beside the pose it is a fixed-width column, and the
    // four tool chips share one row inside it. Chip (wrap_content, full padding) asks for more room
    // than that row has, so the labels wrapped mid-word - "Grayscal / e". Chip.Tool is the style
    // that makes a chip fit its share of the row instead of wrapping.
    [Theory]
    [InlineData("chip_grayscale")]
    [InlineData("chip_flip")]
    [InlineData("chip_grid")]
    [InlineData("chip_blur")]
    [InlineData("chip_zoom_in")]
    [InlineData("chip_zoom_out")]
    public void ToolChips_UseTheNonWrappingChipStyle(string id) =>
        Assert.Equal("@style/Chip.Tool", Element(id).Attribute("style")?.Value);

    // What makes the label fit: one line only, and a 0dp/weight share of the row rather than the
    // chip's own idea of how wide it should be. The fixed height is what keeps the row even — a
    // chip that measures itself would come out a different height from its neighbours.
    [Fact]
    public void ChipToolStyle_KeepsLabelsOnOneLineInAWeightedShareOfTheRow()
    {
        var items = Style("Chip.Tool");

        Assert.Equal("1", items["android:maxLines"]);
        Assert.Equal("0dp", items["android:layout_width"]);
        Assert.Equal("1", items["android:layout_weight"]);
        Assert.Equal("@dimen/chip_min_height", items["android:layout_height"]);
    }

    // "Grayscale" is the label that ran out of room; it needs more of the row than the short ones.
    [Fact]
    public void GrayscaleChip_TakesAWiderShareOfTheRow_ThanTheShortLabels()
    {
        // Invariant: these are Android resource values, not culture-formatted numbers.
        var grayscale = double.Parse(
            Element("chip_grayscale").Attribute(Android + "layout_weight")!.Value, CultureInfo.InvariantCulture);

        foreach (var id in new[] { "chip_flip", "chip_grid", "chip_blur" })
        {
            // No override means the style's weight of 1.
            var weight = Element(id).Attribute(Android + "layout_weight")?.Value ?? "1";
            Assert.True(grayscale > double.Parse(weight, CultureInfo.InvariantCulture),
                $"chip_grayscale (weight {grayscale}) must be wider than {id} (weight {weight}).");
        }
    }

    static IReadOnlyDictionary<string, string> Style(string name)
    {
        var style = XDocument.Load(TestPaths.Path("Resources", "values", "styles.xml"))
            .Root!.Elements("style")
            .First(e => e.Attribute("name")?.Value == name);

        return style.Elements("item")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
    }

    static XElement Element(string id) =>
        XDocument.Load(TestPaths.Path("Resources", "layout", "activity_session.xml"))
            .Descendants().First(e => LocalId(e) == id);

    static string? LocalId(XElement element) =>
        element.Attribute(Android + "id")?.Value?.Replace("@+id/", "").Replace("@id/", "");

    static IEnumerable<string?> AncestorIds(XElement element) =>
        element.Ancestors().Select(LocalId);

    // FD-005 regression: the timer overlays the very top of the pose, so an opaque ActionBar (the
    // default theme's title bar) would draw straight over it and hide it. The session screen is a
    // full-bleed lightbox and must run under a NoActionBar theme.
    [Fact]
    public void SessionActivity_UsesANoActionBarTheme_SoTheTimerIsNotHidden()
    {
        var activityAttribute = Regex.Match(SessionActivitySource, @"\[Activity\((?<args>[^\]]*)\)\]",
            RegexOptions.Singleline);

        Assert.True(activityAttribute.Success, "SessionActivity is missing its [Activity(...)] attribute.");
        Assert.Contains("NoActionBar", activityAttribute.Groups["args"].Value);
    }

    // With no ActionBar reserving the top inset, the timer would otherwise sit under the status bar.
    // fitsSystemWindows on the root keeps the whole pose (and its timer) clear of the system bars.
    [Fact]
    public void SessionRoot_FitsSystemWindows_SoTheTimerClearsTheStatusBar()
    {
        var doc = XDocument.Load(TestPaths.Path("Resources", "layout", "activity_session.xml"));
        var root = doc.Descendants()
            .First(e => e.Attribute(Android + "id")?.Value == "@+id/session_root");

        Assert.Equal("true", root.Attribute(Android + "fitsSystemWindows")?.Value);
    }

    // The screen must drive the session from the Core aggregate and pause/resume it with the
    // lifecycle (FD-005 acceptance). Source-level guard: these are Android-only code paths the unit
    // tests can't execute.
    [Theory]
    [InlineData("new DrawingSession<")]
    [InlineData("session.Tick()")]
    [InlineData("session.Pause()")]
    [InlineData("session.Resume()")]
    [InlineData("protected override void OnPause()")]
    [InlineData("protected override void OnResume()")]
    public void SessionActivity_WiresTheSessionToTheLifecycle(string snippet) =>
        Assert.Contains(snippet, SessionActivitySource);

    // "Restart the pose clock whenever the image changes" is a domain rule, and it used to be
    // written here as `player.Next(); countdown.Restart();` (docs/ARCHITECTURE.md §17). It now lives
    // inside the session aggregate, where it is unit-tested. A clock of the screen's own would be
    // that rule leaking back out of Core — and unlike the old type names, these snippets name things
    // that still exist, so the guard can actually fail against compiling code.
    [Theory]
    [InlineData("Stopwatch")]
    [InlineData("DateTime.Now")]
    [InlineData("SystemClock.")]
    public void SessionActivity_DoesNotDriveTheCountdownItself(string snippet) =>
        Assert.DoesNotContain(snippet, SessionActivitySource);

    // One session object per screen. Two would mean two clocks and two counts, which is the shape
    // the consolidation removed (docs/DOMAIN-MODEL.md §9).
    [Fact]
    public void SessionActivity_ConstructsExactlyOneSession() =>
        Assert.Single(Regex.Matches(SessionActivitySource, @"new DrawingSession<"));

    // INV-PLY-5: decode failures are caught at the adapter and returned as null, so the loader never
    // throws through the session. The catch lives in LoadBitmap, which no unit test can execute.
    [Fact]
    public void SessionActivity_CatchesDecodeFailuresInTheLoader()
    {
        var loader = SessionActivitySource[SessionActivitySource.IndexOf("Bitmap? LoadBitmap(")..];

        Assert.Contains("catch", loader);
        Assert.Contains("return null;", loader);
    }

    // --- Rule-of-thirds guides -----------------------------------------------

    // The four guides must be inside session_grid, or toggling the grid chip would leave some of
    // them painted over every pose.
    [Theory]
    [InlineData("session_grid_v1")]
    [InlineData("session_grid_v2")]
    [InlineData("session_grid_h1")]
    [InlineData("session_grid_h2")]
    public void GridGuides_LiveInsideTheGridOverlay(string id) =>
        Assert.Contains("session_grid", AncestorIds(Element(id))!);

    // The reported bug was that a 1dp hairline is too thin to read against a photograph. The width
    // is now one token, and a literal creeping back into the layout is the regression.
    [Theory]
    [InlineData("session_grid_v1", "layout_width")]
    [InlineData("session_grid_v2", "layout_width")]
    [InlineData("session_grid_h1", "layout_height")]
    [InlineData("session_grid_h2", "layout_height")]
    public void GridGuides_TakeTheirThicknessFromTheBandDimen(string id, string attribute) =>
        Assert.Equal("@dimen/grid_line_band", Element(id).Attribute(Android + attribute)?.Value);

    // SessionActivity resolves these by name at runtime (GetColor / GetDimensionPixelSize), so a
    // rename compiles cleanly and crashes on device — the failure this tier exists to catch (§10).
    [Theory]
    [InlineData("colors.xml", "color", "grid_line")]
    [InlineData("colors.xml", "color", "grid_line_light")]
    [InlineData("colors.xml", "color", "grid_line_dark")]
    [InlineData("colors.xml", "color", "grid_casing_light")]
    [InlineData("colors.xml", "color", "grid_casing_dark")]
    [InlineData("dimens.xml", "dimen", "grid_line_core")]
    [InlineData("dimens.xml", "dimen", "grid_line_casing")]
    [InlineData("dimens.xml", "dimen", "grid_line_band")]
    public void Values_DeclareTheGridTokens(string file, string element, string name) =>
        Assert.Contains(name, ValueResourceNames(file, element));

    // band = core + a casing down each side. If they drift apart the core is either clipped or
    // floats inside a band wider than it, and the guide stops looking like one line.
    [Fact]
    public void GridBand_IsTheCorePlusACasingEachSide()
    {
        var core = Dip("grid_line_core");
        var casing = Dip("grid_line_casing");
        var band = Dip("grid_line_band");

        Assert.Equal(core + (2 * casing), band);
    }

    static double Dip(string name)
    {
        var value = XDocument.Load(TestPaths.Path("Resources", "values", "dimens.xml"))
            .Root!.Elements("dimen")
            .First(e => e.Attribute("name")?.Value == name).Value;

        Assert.EndsWith("dp", value);

        // Invariant: an Android resource value, not a culture-formatted number.
        return double.Parse(value[..^2], CultureInfo.InvariantCulture);
    }

    static IEnumerable<string> ValueResourceNames(string file, string element) =>
        XDocument.Load(TestPaths.Path("Resources", "values", file))
            .Root!.Elements(element)
            .Select(e => e.Attribute("name")!.Value);

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
