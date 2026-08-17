using OpenQA.Selenium;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Android;

namespace FigureDrawing.UITests;

// Thin helpers over the driver, keyed by the app's local resource ids. Shared by every UI-test
// class: they run against one device in one session (see AppiumCollection), so each test inherits
// whatever screen the previous one left behind and they all need the same way back to a known state.
internal sealed class AppiumGuard(AndroidDriver driver)
{
    public AndroidDriver Driver { get; } = driver;

    public IWebElement FindById(string localId) =>
        Driver.FindElement(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

    // Elements matching an id, or none. Used to assert a view is *absent* (a View.Gone view drops
    // out of the hierarchy entirely).
    public IReadOnlyCollection<IWebElement> FindAllById(string localId) =>
        Driver.FindElements(MobileBy.Id(UiTestEnvironment.ViewId(localId)));

    // Returns the element once present, or null if it never appears within the timeout.
    public IWebElement? WaitForId(string localId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return FindById(localId);
            }
            catch (WebDriverException)
            {
                Thread.Sleep(250);
            }
        }

        return null;
    }

    // Clicks a fully-qualified id (a system dialog button) if it happens to be on screen. The
    // picker's confirm steps differ by Android version, so a missing one is not a failure.
    public void ClickIdIfPresent(string fullId)
    {
        try { Driver.FindElement(MobileBy.Id(fullId)).Click(); }
        catch (Exception e) { Console.WriteLine($"no '{fullId}': {e.Message.Split('\n')[0]}"); }
    }

    // Session / Images / Settings are panes of one Activity since the Claude Design import, so a
    // test needing a control from another tab has to switch to it first.
    public void OpenTab(string tabId)
    {
        ReturnToMainScreen();
        FindById(tabId).Click();
        Thread.Sleep(400);
    }

    // Gets back to MainActivity, which is what owns the tab bar. Two different things can be in the
    // way and neither is handled by pressing Back blindly:
    //
    //   * the system folder picker — a different package, left open by a picker test;
    //   * the player screen — OUR package but a different Activity, with no tab bar at all, left
    //     open by any session test.
    //
    // Waiting for the package to match is not enough for the second case (it already matches), which
    // is why this keys off the Activity and re-activates the app after each Back.
    public void ReturnToMainScreen()
    {
        Driver.ActivateApp(UiTestEnvironment.AppPackage);

        for (var i = 0; i < 6 && !IsOnMainActivity; i++)
        {
            Driver.Navigate().Back();
            Thread.Sleep(700);
            Driver.ActivateApp(UiTestEnvironment.AppPackage);
        }
    }

    // Presses Back until the app is foregrounded again (e.g. to dismiss an open system picker left
    // by a prior test). Use ReturnToMainScreen when a MainActivity view is what's wanted.
    public void ReturnToApp()
    {
        for (var i = 0; i < 5 && Driver.CurrentPackage != UiTestEnvironment.AppPackage; i++)
        {
            Driver.Navigate().Back();
            Thread.Sleep(700);
        }
    }

    bool IsOnMainActivity =>
        (Driver.CurrentActivity ?? string.Empty).Contains("MainActivity", StringComparison.Ordinal);

    public bool WaitUntil(Func<AndroidDriver, bool> condition, TimeSpan timeout)
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
                // transient during an app/picker transition; retry until the deadline
            }

            Thread.Sleep(250);
        }

        return false;
    }

    // Taps a folder in the system picker by its displayed name, scrolling to it if the list is long.
    public void TapPickerFolder(string name)
    {
        var byText = MobileBy.AndroidUIAutomator($"new UiSelector().text(\"{name}\")");

        try
        {
            Driver.FindElement(byText).Click();
        }
        catch (WebDriverException)
        {
            Driver.FindElement(MobileBy.AndroidUIAutomator(
                "new UiScrollable(new UiSelector().scrollable(true))" +
                $".scrollIntoView(new UiSelector().text(\"{name}\"))"));
            Driver.FindElement(byText).Click();
        }
    }

    // Completes a real folder selection: open the picker from the Images tab, walk into the seeded
    // folder, "USE THIS FOLDER", then the Allow-access confirm dialog (both confirms are
    // android:id/button1 at different steps), then wait to land back in the app. Shared because all
    // three suites need a granted library.
    //
    // The walk-into step is not optional. The picker opens at the root of shared storage when
    // DocumentsUI has no history, and Android will not grant the root — it shows "Can't use this
    // folder" with no confirm button, so every downstream assertion fails with the app still in the
    // picker. Navigating by name also makes this independent of wherever the picker was last used.
    public void SelectDefaultFolder()
    {
        OpenTab("tab_images");
        FindById("pick_button").Click();
        Thread.Sleep(2500);

        TapPickerFolder(UiTestEnvironment.DefaultPickerFolderName);
        Thread.Sleep(1500);

        ClickIdIfPresent("android:id/button1");   // USE THIS FOLDER
        Thread.Sleep(1500);
        ClickIdIfPresent("android:id/button1");   // Allow access? -> ALLOW
        WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15));
        Thread.Sleep(1500);                        // let OnActivityResult/LoadFolder settle
    }
}
