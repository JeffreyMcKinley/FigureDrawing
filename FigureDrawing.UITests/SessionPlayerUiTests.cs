using Xunit.Abstractions;

namespace FigureDrawing.UITests;

// FD-004 session player end-to-end over Appium/UiAutomator2. Opt-in via RUN_APPIUM=1 (see
// UiTestEnvironment) — otherwise every test skips-as-pass. Drives setup -> Start -> the full-screen
// player on a real emulator; the pure player logic is covered by SessionPlayerTests and
// PoseSessionTests.
//
// Every test here leaves the app on the player, which is a different Activity from the tabbed setup
// screen. Getting back is AppiumGuard.ReturnToMainScreen's job (OpenTab calls it).
[Collection(AppiumCollection.Name)]
public class SessionPlayerUiTests(AppiumAppFixture app, ITestOutputHelper output)
{
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

    // Selecting a folder with images and tapping Start opens the player showing one full-screen image.
    [Fact]
    public void StartingSession_ShowsFullScreenImage()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3);

        var image = g.WaitForId("session_image", TimeSpan.FromSeconds(10));
        Assert.NotNull(image);
        Assert.True(image!.Displayed, "Session image should be visible after Start.");
        // The setup inputs belong to the previous screen — they must be gone on the player.
        Assert.Empty(g.FindAllById("start_button"));
    }

    // FD-005: the countdown is visible on the player and actually counts down each second.
    [Fact]
    public void SessionPlayer_ShowsACountdown_ThatTicksDown()
    {
        if (Ready() is not { } g) return;

        // A long pose, so the tick being observed can't be confused with the pose rolling over.
        StartSession(g, imageCount: 3, seconds: 60);

        var timer = g.WaitForId("session_timer", TimeSpan.FromSeconds(10));
        Assert.NotNull(timer);
        Assert.True(timer!.Displayed, "Countdown should be visible during a pose.");

        var first = timer.Text;
        Assert.Matches(@"^\d+:\d{2}$", first);

        // Within a few seconds the displayed value must have changed (and gone DOWN).
        var changed = g.WaitUntil(
            _ => g.FindById("session_timer").Text != first,
            TimeSpan.FromSeconds(5));

        Assert.True(changed, $"Countdown stayed at {first}; it should update each second.");
        Assert.True(Seconds(g.FindById("session_timer").Text) < Seconds(first),
            "Countdown should decrease, not increase.");
    }

    // FD-005: at zero the next pose loads automatically, with no user input at all. Uses the
    // shortest usable pose length so the test doesn't wait out a default 30s pose, and no break, so
    // what is being timed is the pose rolling over rather than a rest in between.
    [Fact]
    public void CountdownExpiry_AdvancesToTheNextPose_WithoutInput()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3, seconds: 3);

        var timer = g.WaitForId("session_timer", TimeSpan.FromSeconds(10));
        Assert.NotNull(timer);

        // Wait for the timer to wrap back up to a fresh pose (it can only go up by restarting).
        var restarted = g.WaitUntil(_ => Seconds(g.FindById("session_timer").Text) >= 3,
            TimeSpan.FromSeconds(12));

        Assert.True(restarted, "Countdown should reset for the next pose once it reaches zero.");
        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.WaitForId("session_image", TimeSpan.FromSeconds(5))?.Displayed == true,
            "The next image should be showing after auto-advance.");
    }

    // FD-005: backgrounding the app must freeze the countdown — it may not drain (or fire) while
    // the app is hidden.
    [Fact]
    public void Backgrounding_PausesTheCountdown()
    {
        if (Ready() is not { } g) return;

        StartSession(g, imageCount: 3, seconds: 60);

        Assert.NotNull(g.WaitForId("session_timer", TimeSpan.FromSeconds(10)));
        var before = Seconds(g.FindById("session_timer").Text);

        g.Driver.BackgroundApp(TimeSpan.FromSeconds(6));
        g.WaitUntil(d => d.CurrentPackage == UiTestEnvironment.AppPackage, TimeSpan.FromSeconds(15));

        var after = Seconds(g.FindById("session_timer").Text);

        // A running timer would have lost ~6s; a paused one loses at most the second or two spent
        // in the foreground on either side of the backgrounding.
        Assert.True(before - after <= 3,
            $"Countdown drained from {before}s to {after}s while backgrounded; it should pause.");
    }

    // Tapping the image advances to the next pose early (manual "done" gesture). The app
    // must stay alive and keep showing an image.
    [Fact]
    public void TappingImage_AdvancesToNextPose_WithoutCrashing()
    {
        if (Ready() is not { } g) return;

        // A long pose so the tap, not an expiry, is what advances the session.
        StartSession(g, imageCount: 3, seconds: 60);
        Assert.NotNull(g.WaitForId("session_image", TimeSpan.FromSeconds(10)));

        // Re-find rather than reusing the handle from the wait: the player repaints continuously, so
        // an element resolved even a moment earlier can already be stale by the time it is clicked.
        g.FindById("session_image").Click();

        Assert.Equal(UiTestEnvironment.AppPackage, g.Driver.CurrentPackage);
        Assert.True(g.WaitForId("session_image", TimeSpan.FromSeconds(5))?.Displayed == true,
            "An image should still be shown after advancing.");
    }

    // "m:ss" / "h:mm:ss" -> total seconds.
    static int Seconds(string display)
    {
        var parts = display.Split(':').Select(int.Parse).ToArray();
        return parts.Length == 3
            ? parts[0] * 3600 + parts[1] * 60 + parts[2]
            : parts[0] * 60 + parts[1];
    }

    // Seed a folder with images, select it, set the pace, and tap Start. The break is pinned to
    // "None" because these tests time the pose itself — a rest between poses would show the break
    // overlay's clock instead and make the timings mean something else.
    static void StartSession(AppiumGuard g, int imageCount, int? seconds = null)
    {
        UiTestEnvironment.SeedDefaultFolder(imageCount);
        g.SelectDefaultFolder();
        g.OpenTab("tab_session");

        g.FindById("chip_break_0").Click();

        if (seconds is int s)
        {
            var input = g.FindById("seconds_input");
            input.Clear();
            input.SendKeys(s.ToString());
        }

        Assert.True(g.FindById("start_button").Enabled, "Start should be enabled after selecting images.");
        g.FindById("start_button").Click();
    }
}
