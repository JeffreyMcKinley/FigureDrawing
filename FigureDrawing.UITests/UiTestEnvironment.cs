using System.Diagnostics;

namespace FigureDrawing.UITests;

// Shared config for the Appium end-to-end tests: repo/APK paths and the run gate.
internal static class UiTestEnvironment
{
    public const string AppPackage = "com.companyname.FigureDrawing";
    public const string SignedApkRelative = @"bin\Debug\net9.0-android\com.companyname.FigureDrawing-Signed.apk";

    // The folder ACTION_OPEN_DOCUMENT_TREE opens by default on the FigureDrawing_Pixel emulator
    // (empty on a fresh image). The tests set its contents via adb so "select the default folder"
    // deterministically exercises the empty vs populated cases without fragile picker navigation.
    public const string DefaultPickerDir = "/sdcard/Audiobooks";

    // Appium is heavy (needs an emulator + a running Appium server) and flaky, so it is opt-in:
    // the whole suite only runs when RUN_APPIUM=1. Otherwise every test skips-as-pass, keeping
    // `nx test` fast and deterministic.
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("RUN_APPIUM") == "1";

    public static Uri ServerUri =>
        new(Environment.GetEnvironmentVariable("APPIUM_URL") ?? "http://127.0.0.1:4723");

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SignedApkPath => Path.Combine(RepoRoot, SignedApkRelative);

    // Fully-qualified Android resource id, e.g. com.companyname.FigureDrawing:id/pick_button.
    public static string ViewId(string localId) => $"{AppPackage}:id/{localId}";

    static string Adb => Path.Combine(
        Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        "platform-tools", "adb.exe");

    // Runs an adb command and returns stdout; throws if adb exits non-zero.
    public static string RunAdb(params string[] args)
    {
        var psi = new ProcessStartInfo(Adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"adb {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    // A minimal valid 1x1 PNG (red pixel). Decoded on the host and pushed as a real file so shell
    // escaping can't corrupt it — enough to exercise the enumerate -> decode -> display path.
    const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // Replaces the default picker folder's contents: empties it, then pushes `imageCount` valid PNGs.
    public static void SeedDefaultFolder(int imageCount)
    {
        RunAdb("shell", "rm", "-rf", DefaultPickerDir);
        RunAdb("shell", "mkdir", "-p", DefaultPickerDir);

        if (imageCount > 0)
        {
            var local = Path.Combine(Path.GetTempPath(), "fd-seed.png");
            File.WriteAllBytes(local, Convert.FromBase64String(OnePixelPngBase64));
            for (var i = 0; i < imageCount; i++)
                RunAdb("push", local, $"{DefaultPickerDir}/img{i}.png");
        }

        RunAdb("shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE",
            "-d", $"file://{DefaultPickerDir}");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FigureDrawing.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate FigureDrawing.sln above {AppContext.BaseDirectory}");
    }
}
