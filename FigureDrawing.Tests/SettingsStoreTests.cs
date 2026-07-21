using FigureDrawing.Data;

namespace FigureDrawing.Tests;

// Persistence layer (Data/SettingsStore over LiteDB). Runs against a real on-disk LiteDB file in
// a temp directory, deleted on dispose — LiteDB is embedded and runs fine on net9.0 desktop.
public sealed class SettingsStoreTests : IDisposable
{
    readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"figuredrawing-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(databasePath))
            File.Delete(databasePath);
    }

    [Fact]
    public void GetSettings_FirstRun_ReturnsDefaults()
    {
        using var store = new SettingsStore(databasePath);

        var settings = store.GetSettings();

        Assert.Equal(30, settings.PoseDurationSeconds);
        Assert.Equal(20, settings.SessionImageCount);
        Assert.True(settings.ShuffleImages);
        Assert.False(settings.GrayscaleMode);
        Assert.Null(settings.LastCollection);
    }

    [Fact]
    public void SaveSettings_PersistsAcrossStoreInstances()
    {
        using (var store = new SettingsStore(databasePath))
        {
            var settings = store.GetSettings();
            settings.PoseDurationSeconds = 60;
            settings.SessionImageCount = 40;
            settings.ShuffleImages = false;
            settings.GrayscaleMode = true;
            // FD-001: the picked folder tree URI is persisted here.
            settings.LastCollection = "content://com.android.externalstorage.documents/tree/primary%3APics";
            store.SaveSettings(settings);
        }

        // Reopen from disk — simulates a fresh app launch.
        using var reopened = new SettingsStore(databasePath);
        var restored = reopened.GetSettings();

        Assert.Equal(60, restored.PoseDurationSeconds);
        Assert.Equal(40, restored.SessionImageCount);
        Assert.False(restored.ShuffleImages);
        Assert.True(restored.GrayscaleMode);
        Assert.Equal(
            "content://com.android.externalstorage.documents/tree/primary%3APics",
            restored.LastCollection);
    }

    [Fact]
    public void SaveSettings_IsUpsert_KeepsSingleDocument()
    {
        using var store = new SettingsStore(databasePath);

        var first = store.GetSettings();
        first.LastCollection = "content://tree/one";
        store.SaveSettings(first);

        var second = store.GetSettings();
        second.LastCollection = "content://tree/two";
        store.SaveSettings(second);

        // Latest write wins; there is exactly one settings document (id is fixed).
        Assert.Equal("content://tree/two", store.GetSettings().LastCollection);
    }
}
