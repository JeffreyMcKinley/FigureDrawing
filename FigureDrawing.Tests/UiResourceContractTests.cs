using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FigureDrawing.Tests;

// UI-contract tests: MainActivity resolves views/strings/layouts by name at runtime
// (FindViewById(Resource.Id.x), GetString(Resource.String.y), SetContentView(Resource.Layout.z)).
// A rename or deletion in the Android resource XML would compile fine but crash at runtime with a
// null view / missing resource. These tests read the resource XML directly (no device) and assert
// every name MainActivity references still exists.
public class UiResourceContractTests
{
    static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    static string MainActivitySource => File.ReadAllText(TestPaths.Path("MainActivity.cs"));

    // Names referenced from code as Resource.<kind>.<name>.
    static IReadOnlySet<string> ReferencedResources(string kind)
    {
        var matches = Regex.Matches(MainActivitySource, $@"Resource\.{kind}\.(\w+)");
        return matches.Select(m => m.Groups[1].Value).ToHashSet();
    }

    // android:id="@+id/foo" / "@id/foo" declared anywhere in a layout file.
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
    public void EveryReferencedViewId_ExistsInLayout()
    {
        var ids = LayoutIds("activity_main");
        var missing = ReferencedResources("Id").Where(id => !ids.Contains(id)).ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references view id(s) not declared in activity_main.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedString_ExistsInStringsXml()
    {
        var names = StringResourceNames();
        var missing = ReferencedResources("String").Where(n => !names.Contains(n)).ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references string(s) not declared in strings.xml: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryReferencedLayout_HasAFile()
    {
        var missing = ReferencedResources("Layout")
            .Where(name => !File.Exists(TestPaths.Path("Resources", "layout", $"{name}.xml")))
            .ToList();

        Assert.True(missing.Count == 0,
            $"MainActivity references layout(s) with no file: {string.Join(", ", missing)}");
    }

    // FD-001 explicit view contract: the folder-picker screen must keep these three views.
    [Theory]
    [InlineData("pick_button")]
    [InlineData("image_container")]
    [InlineData("empty_label")]
    public void ActivityMain_DeclaresFdViews(string id) =>
        Assert.Contains(id, LayoutIds("activity_main"));

    // FD-001 explicit string contract: picker label + both empty-state messages.
    [Theory]
    [InlineData("app_name")]
    [InlineData("pick_button_text")]
    [InlineData("empty_label_text")]
    [InlineData("empty_folder_text")]
    public void Strings_DeclaresFdStrings(string name) =>
        Assert.Contains(name, StringResourceNames());
}
