using LiteDB;

namespace FigureDrawing.Data
{
    public sealed class SettingsStore : IDisposable
    {
        const int DocumentId = 1;
        const string CollectionName = "settings";

        readonly LiteDatabase database;
        readonly ILiteCollection<AppSettings> settings;

        public SettingsStore(string databasePath)
        {
            database = new LiteDatabase(databasePath);
            settings = database.GetCollection<AppSettings>(CollectionName);
        }

        // Returns the persisted settings, creating a default document on first run.
        public AppSettings GetSettings()
        {
            var current = settings.FindById(DocumentId);
            if (current is null)
            {
                current = new AppSettings { Id = DocumentId };
                settings.Insert(current);
            }

            return current;
        }

        // Persists the given settings (insert or update).
        public void SaveSettings(AppSettings value)
        {
            value.Id = DocumentId;
            settings.Upsert(value);
        }

        public void Dispose() => database.Dispose();
    }
}
