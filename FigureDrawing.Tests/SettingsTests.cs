using FigureDrawing.Data;

namespace FigureDrawing.Tests;

// Preferences (Data/Settings over LiteDB): the single document and its own persistence. Runs
// against a real on-disk LiteDB file in a temp directory, deleted on dispose — LiteDB is embedded
// and runs fine on net9.0 desktop.
public sealed class SettingsTests : IDisposable
{
    readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"figuredrawing-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(databasePath))
            File.Delete(databasePath);
    }

    [Fact]
    public void Open_FirstRun_ReturnsDefaults()
    {
        using var settings = Settings.Open(databasePath);

        Assert.Equal(30, settings.PoseDurationSeconds);
        Assert.Equal(20, settings.SessionImageCount);
        Assert.Equal(0, settings.BreakSeconds);
        Assert.True(settings.ShuffleImages);
        Assert.False(settings.GrayscaleMode);
        Assert.True(settings.KeepScreenAwake);
        Assert.False(settings.ChimeOnChange);
        Assert.Null(settings.LastCollection);
    }

    [Fact]
    public void Save_PersistsAcrossInstances()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.PoseDurationSeconds = 60;
            settings.SessionImageCount = 40;
            settings.BreakSeconds = 15;
            settings.ShuffleImages = false;
            settings.GrayscaleMode = true;
            settings.KeepScreenAwake = false;
            settings.ChimeOnChange = true;
            // The picked folder tree URI is persisted here, by reference (INV-SET-P5).
            settings.LastCollection = "content://com.android.externalstorage.documents/tree/primary%3APics";
            settings.Save();
        }

        // Reopen from disk — simulates a fresh app launch.
        using var restored = Settings.Open(databasePath);

        Assert.Equal(60, restored.PoseDurationSeconds);
        Assert.Equal(40, restored.SessionImageCount);
        Assert.Equal(15, restored.BreakSeconds);
        Assert.False(restored.ShuffleImages);
        Assert.True(restored.GrayscaleMode);
        Assert.False(restored.KeepScreenAwake);
        Assert.True(restored.ChimeOnChange);
        Assert.Equal(
            "content://com.android.externalstorage.documents/tree/primary%3APics",
            restored.LastCollection);
    }

    [Fact]
    public void Save_IsUpsert_KeepsSingleDocument()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.LastCollection = "content://tree/one";
            settings.Save();

            settings.LastCollection = "content://tree/two";
            settings.Save();
        }

        // Latest write wins; there is exactly one settings document (INV-SET-P1, INV-STO-2).
        using var reopened = Settings.Open(databasePath);

        Assert.Equal("content://tree/two", reopened.LastCollection);
        Assert.Equal(1, reopened.Id);
    }

    // A silently dropped save would cost the user their preferences, so saving through a disposed
    // instance is an error rather than a no-op.
    [Fact]
    public void Save_AfterDispose_Throws()
    {
        var settings = Settings.Open(databasePath);
        settings.Dispose();

        Assert.Throws<ObjectDisposedException>(settings.Save);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var settings = Settings.Open(databasePath);

        settings.Dispose();
        settings.Dispose();
    }

    // The other half of INV-SET-P6: a file left corrupt by a kill mid-write must cost preferences
    // and nothing else. Throwing here would crash the launch that reads it — every launch, since
    // the bad file stays on disk.
    [Fact]
    public void CorruptDatabase_StartsFromDefaults()
    {
        File.WriteAllBytes(databasePath, "this is not a LiteDB file"u8.ToArray());

        using var settings = Settings.Open(databasePath);

        Assert.Equal(30, settings.PoseDurationSeconds);
        Assert.Null(settings.LastCollection);

        // And it is usable afterwards, not just openable.
        settings.PoseDurationSeconds = 90;
        settings.Save();
    }

    // INV-STO-1: no other type opens a LiteDatabase. Source-level, because a second store would
    // compile and pass every behavioural test while quietly holding a second handle on the file.
    [Fact]
    public void OnlySettings_OpensTheDatabase()
    {
        var owners = Directory
            .EnumerateFiles(TestPaths.RepoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           // This file names the type in the assertion below, not in a call.
                           !path.EndsWith("SettingsTests.cs"))
            .Where(path => File.ReadAllText(path).Contains("new LiteDatabase("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["Settings.cs"], owners);
    }

    // Losing the database costs preferences and nothing else (INV-SET-P6): a fresh open after the
    // file is gone starts from defaults instead of failing.
    [Fact]
    public void DeletedDatabase_ReopensWithDefaults()
    {
        using (var settings = Settings.Open(databasePath))
        {
            settings.PoseDurationSeconds = 120;
            settings.Save();
        }

        File.Delete(databasePath);

        using var reopened = Settings.Open(databasePath);
        Assert.Equal(30, reopened.PoseDurationSeconds);
    }
}
