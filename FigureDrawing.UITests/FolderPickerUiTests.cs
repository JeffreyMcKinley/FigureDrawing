using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-001 end-to-end UI tests driven through Appium/UiAutomator2 on a real emulator. Opt-in via
// RUN_APPIUM=1 (see UiTestEnvironment) — otherwise every test skips-as-pass. Run the suite with
// scripts\run-appium-tests.ps1, which boots the emulator, installs the APK, and starts Appium.
public class FolderPickerUiTests(AppiumAppFixture app, ITestOutputHelper output)
    : IClassFixture<AppiumAppFixture>
{
    // Returns the live driver, or null when the test should skip (suite disabled). Fails loudly
    // if the suite is enabled but the session never started.
    AndroidGuard? Ready()
    {
        if (!UiTestEnvironment.Enabled)
        {
            output.WriteLine("Skipped: set RUN_APPIUM=1 (and boot emulator + Appium) to run.");
            return null;
        }

        Assert.True(app.StartupError is null, $"Appium session failed to start: {app.StartupError}");
        Assert.NotNull(app.Driver);
        return new AndroidGuard(app.Driver!);
    }

    [Fact]
    public void AppLaunches_ShowsPickFolderButton()
    {
        if (Ready() is not { } g) return;

        var button = g.FindById("pick_button");

        Assert.True(button.Displayed);
        Assert.Equal("Pick folder", button.Text, ignoreCase: true);
    }

    [Fact]
    public void InitialState_ShowsEmptyFolderPrompt()
    {
        if (Ready() is not { } g) return;

        var label = g.FindById("empty_label");

        Assert.True(label.Displayed);
        Assert.Contains("No folder selected", label.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TappingPickFolder_OpensSystemDocumentPicker()
    {
        if (Ready() is not { } g) return;

        g.FindById("pick_button").Click();

        // ACTION_OPEN_DOCUMENT_TREE launches the platform DocumentsUI (a different package). Assert
        // focus left our app rather than matching version-specific picker labels, which drift.
        var moved = g.WaitUntil(
            d => !d.CurrentPackage.Equals(UiTestEnvironment.AppPackage, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));

        Assert.True(moved, $"Expected the system folder picker; still in {g.Driver.CurrentPackage}.");

        // Leave the shared app session on the app screen for whatever test runs next.
        g.ReturnToApp();
    }

    // The app must survive selecting an EMPTY folder. Seeds the default picker folder empty,
    // completes a real selection (incl. the Allow-access dialog), and asserts the app did not
    // crash and shows the empty-state message.
    [Fact]
    public void SelectingEmptyFolder_DoesNotCrash()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 0);
        SelectDefaultFolder(g);

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
        SelectDefaultFolder(g);

        AssertAppAlive(g);
        // empty_label is set to View.Gone when images load, so it drops out of the tree.
        Assert.Empty(g.Driver.FindElements(MobileBy.Id(UiTestEnvironment.ViewId("empty_label"))));
    }

    // Completes a real folder selection: open picker, "USE THIS FOLDER", then the Allow-access
    // confirm dialog (both are android:id/button1 at different steps), then wait to return to app.
    static void SelectDefaultFolder(AndroidGuard g)
    {
        g.ReturnToApp();                             // tests share one session; start from the app
        g.FindById("pick_button").Click();
        Thread.Sleep(2500);
        g.ClickIdIfPresent("android:id/button1");   // USE THIS FOLDER (select current dir)
        Thread.Sleep(1500);
        g.ClickIdIfPresent("android:id/button1");   // Allow access? -> ALLOW
        g.WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15));
        Thread.Sleep(1500);                          // let OnActivityResult/LoadFolder settle
    }

    // Proves the app did not crash: it is foregrounded and its main view still resolves.
    static void AssertAppAlive(AndroidGuard g)
    {
        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.FindById("pick_button").Displayed, "Pick button gone — app likely crashed/restarted mid-flow.");
    }

    // Thin helpers over the driver keyed by the app's local resource ids.
    sealed class AndroidGuard(OpenQA.Selenium.Appium.Android.AndroidDriver driver)
    {
        public OpenQA.Selenium.Appium.Android.AndroidDriver Driver { get; } = driver;

        public IWebElement FindById(string localId) =>
            Driver.FindElement(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

        public void ClickIdIfPresent(string fullId)
        {
            try { Driver.FindElement(MobileBy.Id(fullId)).Click(); }
            catch (Exception e) { System.Console.WriteLine($"no '{fullId}': {e.Message.Split('\n')[0]}"); }
        }

        // Presses Back until the app is foregrounded again (e.g. to dismiss an open system picker
        // left by a prior test). Tests share one app session, so each must start from a known state.
        public void ReturnToApp()
        {
            for (var i = 0; i < 5 && Driver.CurrentPackage != UiTestEnvironment.AppPackage; i++)
            {
                Driver.Navigate().Back();
                Thread.Sleep(700);
            }
        }

        public bool WaitUntil(Func<OpenQA.Selenium.Appium.Android.AndroidDriver, bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition(Driver))
                        return true;
                }
                catch (WebDriverException)
                {
                    // transient during the app/picker transition; retry until deadline
                }

                Thread.Sleep(250);
            }

            return false;
        }
    }
}
