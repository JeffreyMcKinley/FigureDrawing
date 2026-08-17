using LiteDB;

namespace FigureDrawing.Data
{
    // The persisted preferences, and the single-document store that reads and writes them
    // (docs/DOMAIN-MODEL.md §5.1). One object: there is exactly one document, in one collection,
    // with one store — splitting the two bought a second name and no second implementation.
    //
    // Settings seed, they do not control (INV-SET-P3): values are copied into the setup screen on
    // launch and into intent extras on Start. Neither a session nor the setup logic reads them at
    // runtime. Losing the file costs preferences and nothing else (INV-SET-P6).
    //
    // Storage vocabulary stops here (INV-STO-4): nothing above this type speaks BSON, collections,
    // or LiteDB. The database handle is a private field, so LiteDB's mapper — which maps public
    // properties — never sees it.
    public sealed class Settings : IDisposable
    {
        const int DocumentId = 1;
        const string CollectionName = "settings";

        LiteDatabase? database;
        ILiteCollection<Settings>? documents;

        // Public and parameterless for LiteDB's mapper. Application code calls Open.
        public Settings()
        {
        }

        // Opens the database (creating it on first launch) and returns the persisted settings,
        // inserting a default document the first time. The caller owns the returned instance and
        // disposes it with the screen that opened it (INV-STO-3).
        public static Settings Open(string databasePath)
        {
            ArgumentNullException.ThrowIfNull(databasePath);

            try
            {
                return Read(databasePath);
            }
            catch (Exception) when (File.Exists(databasePath))
            {
                // Losing the database costs preferences and nothing else (INV-SET-P6). A file left
                // corrupt by a kill mid-write would otherwise fail every launch from here on, so it
                // is discarded and reopened from defaults rather than thrown at the screen.
                File.Delete(databasePath);
                return Read(databasePath);
            }
        }

        static Settings Read(string databasePath)
        {
            // Sole owner of the database (INV-STO-1) — no other type opens a LiteDatabase.
            var database = new LiteDatabase(databasePath);

            try
            {
                var documents = database.GetCollection<Settings>(CollectionName);

                var current = documents.FindById(DocumentId);
                if (current is null)
                {
                    current = new Settings { Id = DocumentId };
                    documents.Insert(current);
                }

                current.database = database;
                current.documents = documents;
                return current;
            }
            catch
            {
                // Nothing owns the handle yet, so a failure here would leave the file locked for the
                // life of the process — including for the retry above.
                database.Dispose();
                throw;
            }
        }

        // Persists the current values (insert or update). Called at named moments only — a folder
        // was picked, Start was pressed — never on every keystroke (INV-SET-P4).
        public void Save()
        {
            // A silently dropped save would cost the user their preferences, so a save against a
            // disposed (or mapper-materialized) instance is an error rather than a no-op.
            ObjectDisposedException.ThrowIf(documents is null, this);

            // The store owns the document identity; callers never set Id (INV-STO-2).
            Id = DocumentId;
            documents.Upsert(this);
        }

        public void Dispose()
        {
            database?.Dispose();
            database = null;
            documents = null;
        }

        // Exactly one document, forever (INV-SET-P1).
        [BsonId]
        public int Id { get; set; }

        // Every property below has a default, so a first run and a missing field behave identically
        // and a new preference is a new defaulted property rather than a migration (INV-SET-P2).

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

        // The reference library last opened, held by reference and never by its contents
        // (INV-SET-P5, INV-GRP-1). A stale one is expected: the permission may since have been
        // revoked (INV-GRP-5).
        public string? LastCollection { get; set; }
    }
}
