using LiteDB;

namespace FigureDrawing.Data
{
    // A single persisted settings/config document. Stored as one row (Id == SettingsStore.DocumentId)
    // in the LiteDB "settings" collection. Add new preferences here as the app grows.
    public class AppSettings
    {
        [BsonId]
        public int Id { get; set; }

        // How long each pose/reference image is shown, in seconds.
        public int PoseDurationSeconds { get; set; } = 30;

        // How many images a session shows in total (FD-002 session setup). Persisted so the count
        // chosen last time seeds the setup screen on the next launch.
        public int SessionImageCount { get; set; } = 20;

        // Rest between poses, in seconds. 0 runs one pose straight into the next.
        public int BreakSeconds { get; set; } = 0;

        // Whether to shuffle the selected images rather than show them in order.
        public bool ShuffleImages { get; set; } = true;

        // Render reference images in grayscale (useful for value studies).
        public bool GrayscaleMode { get; set; } = false;

        // Hold the screen on for the whole session so a long pose can't put the device to sleep.
        public bool KeepScreenAwake { get; set; } = true;

        // Play a short tone when the pose changes, for drawing away from the screen.
        public bool ChimeOnChange { get; set; } = false;

        // Name of the collection/album last opened, if any.
        public string? LastCollection { get; set; }
    }
}
