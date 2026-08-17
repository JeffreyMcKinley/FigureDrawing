using System.Diagnostics;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace FigureDrawing.Tests;

// Build-smoke tests for the net9.0-android app. The fast ones assert build *configuration* and the
// produced APK without needing the Android toolchain, so they run in every `nx test`. The full
// end-to-end compile is opt-in (env RUN_ANDROID_BUILD_TEST=1) because it needs JDK 17 + the Android
// SDK and takes ~15s; see repo memory for the required paths.
public class AndroidBuildTests(ITestOutputHelper output)
{
    const string AppCsproj = "FigureDrawing.csproj";
    const string OutputDir = @"bin\Debug\net9.0-android";
    const string SignedApk = "com.companyname.FigureDrawing-Signed.apk";

    static XDocument AppProject => XDocument.Load(TestPaths.Path(AppCsproj));

    static string? Prop(string name) =>
        AppProject.Descendants(name).FirstOrDefault()?.Value;

    [Fact]
    public void App_TargetsAndroid_AsExecutable()
    {
        Assert.Equal("net9.0-android", Prop("TargetFramework"));
        Assert.Equal("Exe", Prop("OutputType"));
        Assert.False(string.IsNullOrWhiteSpace(Prop("ApplicationId")));
    }

    // The app's typeface is a framework font resource (Resources/font/inter.xml), which arrived in
    // API 26. Dropping the floor below that builds and installs fine but renders the whole app in
    // the platform sans-serif, because android:fontFamily pointing at a font resource is ignored.
    [Fact]
    public void App_MinimumApi_SupportsFontResources()
    {
        var supported = Prop("SupportedOSPlatformVersion");

        Assert.True(int.TryParse(supported, out var api),
            $"SupportedOSPlatformVersion is not a number: '{supported}'");
        Assert.True(api >= 26, $"Font resources need API 26; SupportedOSPlatformVersion is {api}.");
    }

    // Regression guard for the root-glob bug: because the app csproj sits at the repo root, its
    // default **/*.cs glob would otherwise pull in the sibling Core/Tests sources and break the
    // Android build. These excludes must stay.
    [Theory]
    [InlineData(@"FigureDrawing.Core\**\*.cs")]
    [InlineData(@"FigureDrawing.Tests\**\*.cs")]
    [InlineData(@"FigureDrawing.UITests\**\*.cs")]
    public void App_ExcludesSiblingProjectSources(string removeGlob)
    {
        var removes = AppProject.Descendants("Compile")
            .Select(c => c.Attribute("Remove")?.Value)
            .Where(v => v is not null);

        Assert.Contains(removeGlob, removes);
    }

    // Asserts the signed APK the build produces. If the app has never been built on this machine
    // the output dir is absent — skip rather than fail (a plain `nx test` doesn't build Android).
    [Fact]
    public void SignedApk_IsProduced()
    {
        var dir = TestPaths.Path(OutputDir);
        if (!Directory.Exists(dir))
        {
            output.WriteLine($"Skipped: {OutputDir} not present. Build the app first (see memory).");
            return;
        }

        var apk = Path.Combine(dir, SignedApk);
        Assert.True(File.Exists(apk), $"Expected signed APK at {apk}");
        Assert.True(new FileInfo(apk).Length > 0, "Signed APK is empty.");
    }

    // Full end-to-end compile. Opt-in via RUN_ANDROID_BUILD_TEST=1; needs JDK 17 + Android SDK.
    // Paths come from env (JavaSdkDirectory / AndroidSdkDirectory) with the known machine defaults
    // as fallback; if the JDK/SDK are absent the toolchain isn't installed, so skip.
    [Fact]
    public void App_Builds_ProducingSignedApk()
    {
        if (Environment.GetEnvironmentVariable("RUN_ANDROID_BUILD_TEST") != "1")
        {
            output.WriteLine("Skipped: set RUN_ANDROID_BUILD_TEST=1 to run the full Android build.");
            return;
        }

        var jdk = Environment.GetEnvironmentVariable("JavaSdkDirectory")
            ?? @"C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot";
        var sdk = Environment.GetEnvironmentVariable("AndroidSdkDirectory")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk");

        if (!Directory.Exists(jdk) || !Directory.Exists(sdk))
        {
            output.WriteLine($"Skipped: JDK ({jdk}) or Android SDK ({sdk}) not found.");
            return;
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "build", AppCsproj, "-c", "Debug", "--nologo",
            $"-p:JavaSdkDirectory={jdk}", $"-p:AndroidSdkDirectory={sdk}",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.True(proc.ExitCode == 0, $"dotnet build failed:\n{stdout}\n{stderr}");
        Assert.True(File.Exists(TestPaths.Path(OutputDir, SignedApk)), "Build did not produce the signed APK.");
    }
}
