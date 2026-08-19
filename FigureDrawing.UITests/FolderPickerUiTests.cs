using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-001 end-to-end UI tests driven through Appium/UiAutomator2 on a real emulator. Opt-in via
// RUN_APPIUM=1 (see UiTestEnvironment) — otherwise every test skips-as-pass. Run the suite with
// scripts\run-appium-tests.ps1, which boots the emulator, installs the APK, clears app state, and
// starts Appium.
//
// Since the Claude Design import the folder picker lives on the Images tab, so these tests switch
// tabs before touching it (AppiumGuard.OpenTab).
[Collection(AppiumCollection.Name)]
public class FolderPickerUiTests(AppiumAppFixture app, ITestOutputHelper output)
{
    // Returns the live driver, or null when the test should skip (suite disabled). Fails loudly
    // if the suite is enabled but the session never started.
    AppiumGuard? Ready()
    {
        if (!UiTestEnvironment.Enabled)
        {
            output.WriteLine("Skipped: set RUN_APPIUM=1 (and boot emulator + Appium) to run.");
            return null;
        }

        Assert.True(app.StartupError is null, $"Appium session failed to start: {app.StartupError}");
        Assert.NotNull(app.Driver);
        return new AppiumGuard(app.Driver!);
    }

    [Fact]
    public void AppLaunches_ShowsPickFolderButton()
    {
        if (Ready() is not { } g) return;
        g.OpenTab("tab_images");

        var button = g.FindById("pick_button");

        Assert.True(button.Displayed);
        Assert.Equal("Pick folder", button.Text, ignoreCase: true);
    }

    // The empty state is a first-run assertion, so it seeds its own precondition rather than relying
    // on the order tests happen to run in: a sibling test that picks a folder persists that choice.
    [Fact]
    public void InitialState_ShowsEmptyFolderPrompt()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.ResetAppState();
        g.Driver.ActivateApp(UiTestEnvironment.AppPackage);
        g.OpenTab("tab_images");

        var label = g.FindById("empty_label");

        Assert.True(label.Displayed);
        Assert.Contains("No folder selected", label.Text, StringComparison.OrdinalIgnoreCase);
    }

    // Tapping Pick folder opens the system picker. Asserted on a FIRST RUN, with nothing
    // remembered: that is the state where MainActivity has no starting point to hand the picker, and
    // a hint it cannot build must not cost the artist the picker itself.
    [Fact]
    public void TappingPickFolder_OpensSystemDocumentPicker()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.ResetAppState();
        g.Driver.ActivateApp(UiTestEnvironment.AppPackage);
        g.OpenTab("tab_images");

        g.FindById("pick_button").Click();

        // ACTION_OPEN_DOCUMENT_TREE launches the platform DocumentsUI (a different package). Assert
        // focus left our app rather than matching version-specific picker labels, which drift.
        var moved = g.WaitUntil(
            d => !d.CurrentPackage.Equals(UiTestEnvironment.AppPackage, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        Assert.True(moved, $"Expected the system folder picker; still in {g.Driver.CurrentPackage}.");

        // Leave the shared session on the app screen for whatever test runs next.
        g.ReturnToMainScreen();
    }

    // The app must survive selecting an EMPTY folder. Seeds the default picker folder empty,
    // completes a real selection (incl. the Allow-access dialog), and asserts the app did not
    // crash and shows the empty-state message.
    [Fact]
    public void SelectingEmptyFolder_DoesNotCrash()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 0);
        g.SelectDefaultFolder(expectImages: 0);

        AssertAppAlive(g);
        Assert.Contains("No images found", g.FindById("empty_label").Text, StringComparison.OrdinalIgnoreCase);
    }

    // The app must survive selecting a folder that CONTAINS images (the case that used to OOM /
    // crash). Seeds the default folder with images, selects it, asserts no crash and that the
    // empty-state message is gone (images were loaded).
    [Fact]
    public void SelectingFolderWithImages_DoesNotCrash()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 5);
        g.SelectDefaultFolder(expectImages: 5);

        AssertAppAlive(g);
        // empty_label is set to View.Gone when images load, so it drops out of the tree.
        Assert.Empty(g.FindAllById("empty_label"));
    }

    // The picked folder is remembered across launches (Settings.LastCollection + the persisted uri
    // grant): after a restart the library is already loaded, with no second trip to the picker.
    [Fact]
    public void PickedFolder_IsRestoredOnRelaunch()
    {
        if (Ready() is not { } g) return;

        // Seven, not three: a count that cannot be a substring of a neighbouring one, so "restored
        // the right folder" and "restored something" are distinguishable.
        UiTestEnvironment.SeedDefaultFolder(imageCount: 7);
        g.SelectDefaultFolder(expectImages: 7);

        // A full stop, not just a background: restoring is OnCreate work, and a resumed process
        // would pass this test without ever running it.
        g.Driver.TerminateApp(UiTestEnvironment.AppPackage);
        g.Driver.ActivateApp(UiTestEnvironment.AppPackage);

        // The relaunched app comes up on the Session pane, so wait for the tab bar — the Images
        // pane's own views are View.Gone until it is opened and cannot be waited on here.
        Assert.NotNull(g.WaitForId("tab_images", TimeSpan.FromSeconds(15)));

        g.OpenTab("tab_images");

        AssertAppAlive(g);
        Assert.Empty(g.FindAllById("empty_label"));
        Assert.Equal(7, g.LibraryCount());
    }

    // Picking again starts where the artist left off: MainActivity passes the remembered folder as
    // EXTRA_INITIAL_URI, so the picker opens inside it rather than wherever it was last used.
    [Fact]
    public void ReopeningPicker_StartsInTheLastFolder()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 2);
        g.SelectDefaultFolder(expectImages: 2);

        // Without this the test cannot fail: DocumentsUI reopens where it was last left, which is
        // the folder the line above just browsed into, so the picker would land there with or
        // without the app's hint. Clearing the picker leaves the app's uri grant untouched — grants
        // live in the system, not in DocumentsUI's data.
        UiTestEnvironment.ResetPickerState();

        g.OpenTab("tab_images");
        g.FindById("pick_button").Click();
        Assert.True(
            g.WaitUntil(d => d.CurrentPackage != UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15)),
            "Picker never opened.");

        var folder = UiTestEnvironment.DefaultPickerFolderName;
        var inside = g.PickerIsInside(folder, TimeSpan.FromSeconds(10))
                     || g.PickerShowsRow(UiTestEnvironment.SeededImageName(0), TimeSpan.FromSeconds(5));

        g.ReturnToMainScreen();
        Assert.True(inside, $"Picker did not open inside '{folder}' — the remembered folder was not passed as the starting point.");
    }

    // Proves the app did not crash: it is foregrounded and its main view still resolves.
    static void AssertAppAlive(AppiumGuard g)
    {
        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.FindById("pick_button").Displayed, "Pick button gone — app likely crashed/restarted mid-flow.");
    }
}
