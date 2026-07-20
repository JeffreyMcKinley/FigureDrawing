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

        // Whether to shuffle the selected images rather than show them in order.
        public bool ShuffleImages { get; set; } = true;

        // Render reference images in grayscale (useful for value studies).
        public bool GrayscaleMode { get; set; } = false;

        // Name of the collection/album last opened, if any.
        public string? LastCollection { get; set; }
    }
}
