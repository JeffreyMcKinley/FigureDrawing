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

    [Fact]
    public void TappingPickFolder_OpensSystemDocumentPicker()
    {
        if (Ready() is not { } g) return;
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
        g.SelectDefaultFolder();

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
        g.SelectDefaultFolder();

        AssertAppAlive(g);
        // empty_label is set to View.Gone when images load, so it drops out of the tree.
        Assert.Empty(g.FindAllById("empty_label"));
    }

    // Proves the app did not crash: it is foregrounded and its main view still resolves.
    static void AssertAppAlive(AppiumGuard g)
    {
        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.FindById("pick_button").Displayed, "Pick button gone — app likely crashed/restarted mid-flow.");
    }
}
