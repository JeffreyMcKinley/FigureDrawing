using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-002 session-setup end-to-end tests over Appium/UiAutomator2. Opt-in via RUN_APPIUM=1 (see
// UiTestEnvironment) — otherwise every test skips-as-pass. Exercises the setup inputs and the Start
// gate on a real emulator; the pure validation is covered by SessionSetupTests.
public class SessionSetupUiTests(AppiumAppFixture app, ITestOutputHelper output)
    : IClassFixture<AppiumAppFixture>
{
    Guard? Ready()
    {
        if (!UiTestEnvironment.Enabled)
        {
            output.WriteLine("Skipped: set RUN_APPIUM=1 (and boot emulator + Appium) to run.");
            return null;
        }

        Assert.True(app.StartupError is null, $"Appium session failed to start: {app.StartupError}");
        Assert.NotNull(app.Driver);
        return new Guard(app.Driver!);
    }

    [Fact]
    public void SetupScreen_ShowsInputsAndStartButton()
    {
        if (Ready() is not { } g) return;
        g.ReturnToApp();

        Assert.True(g.FindById("seconds_input").Displayed);
        Assert.True(g.FindById("count_input").Displayed);
        Assert.True(g.FindById("start_button").Displayed);
    }

    [Fact]
    public void Inputs_SeededFromSettings_WithPositiveDefaults()
    {
        if (Ready() is not { } g) return;
        g.ReturnToApp();

        Assert.True(int.TryParse(g.FindById("seconds_input").Text, out var seconds) && seconds > 0,
            $"seconds_input not seeded with a positive value: '{g.FindById("seconds_input").Text}'");
        Assert.True(int.TryParse(g.FindById("count_input").Text, out var count) && count > 0,
            $"count_input not seeded with a positive value: '{g.FindById("count_input").Text}'");
    }

    // Start is gated on a folder being selected. Selecting a folder that contains images, with the
    // (valid) default inputs, must enable Start.
    [Fact]
    public void SelectingFolderWithImages_EnablesStart()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 3);
        SelectDefaultFolder(g);

        Assert.True(g.FindById("start_button").Enabled,
            "Start should be enabled once a folder with images is selected and inputs are valid.");
    }

    // With a folder selected, clearing an input (invalid) disables Start; restoring it re-enables.
    [Fact]
    public void InvalidInput_DisablesStart()
    {
        if (Ready() is not { } g) return;

        UiTestEnvironment.SeedDefaultFolder(imageCount: 3);
        SelectDefaultFolder(g);
        Assert.True(g.FindById("start_button").Enabled);

        var seconds = g.FindById("seconds_input");
        seconds.Clear();
        Assert.False(g.FindById("start_button").Enabled,
            "Start should be disabled while seconds-per-image is empty.");

        seconds.SendKeys("30");
        Assert.True(g.FindById("start_button").Enabled,
            "Start should re-enable once a valid seconds value is entered.");
    }

    // Mirrors FolderPickerUiTests.SelectDefaultFolder: open picker, USE THIS FOLDER, ALLOW, return.
    static void SelectDefaultFolder(Guard g)
    {
        g.ReturnToApp();
        g.FindById("pick_button").Click();
        Thread.Sleep(2500);
        g.ClickIdIfPresent("android:id/button1");   // USE THIS FOLDER
        Thread.Sleep(1500);
        g.ClickIdIfPresent("android:id/button1");   // Allow access? -> ALLOW
        g.WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15));
        Thread.Sleep(1500);                          // let OnActivityResult/LoadFolder settle
    }

    sealed class Guard(OpenQA.Selenium.Appium.Android.AndroidDriver driver)
    {
        public OpenQA.Selenium.Appium.Android.AndroidDriver Driver { get; } = driver;

        public IWebElement FindById(string localId) =>
            Driver.FindElement(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

        public void ClickIdIfPresent(string fullId)
        {
            try { Driver.FindElement(MobileBy.Id(fullId)).Click(); }
            catch (Exception e) { System.Console.WriteLine($"no '{fullId}': {e.Message.Split('\n')[0]}"); }
        }

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
                try { if (condition(Driver)) return true; }
                catch (WebDriverException) { /* transient during transitions */ }
                Thread.Sleep(250);
            }

            return false;
        }
    }
}
