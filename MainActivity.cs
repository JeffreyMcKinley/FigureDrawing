using System.IO;
using Android.Content;
using Android.Database;
using Android.Provider;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Core;
using FigureDrawing.Data;

namespace FigureDrawing
{
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int PickFolderRequestCode = 1000;
        const string LogTag = "FigureDrawing";
        const string DatabaseFileName = "figuredrawing.db";

        LinearLayout imageContainer = null!;
        TextView emptyLabel = null!;

        // Content-uri strings for every image the picked folder yielded, in enumeration order. This
        // is the pool handed to the session engine (FD-003) when Start is tapped (FD-004).
        readonly List<string> imageUris = new();

        EditText secondsInput = null!;
        EditText countInput = null!;
        Button startButton = null!;

        // A folder with at least one image is loaded — the gate for enabling Start (FD-002).
        bool folderSelected;

        SettingsStore settingsStore = null!;
        AppSettings settings = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_main);

            // Open the local settings/config database (created on first launch) from the app's
            // private files directory, then load the persisted settings.
            var databasePath = Path.Combine(FilesDir!.AbsolutePath, DatabaseFileName);
            settingsStore = new SettingsStore(databasePath);
            settings = settingsStore.GetSettings();
            Log.Info(LogTag,
                $"Settings loaded from {databasePath}: " +
                $"poseDuration={settings.PoseDurationSeconds}s, shuffle={settings.ShuffleImages}, " +
                $"grayscale={settings.GrayscaleMode}");

            imageContainer = FindViewById<LinearLayout>(Resource.Id.image_container)!;
            emptyLabel = FindViewById<TextView>(Resource.Id.empty_label)!;

            var pickButton = FindViewById<Button>(Resource.Id.pick_button)!;
            pickButton.Click += (_, _) => PickFolder();

            // FD-002 session setup: seed the inputs from the last-used settings, recompute the
            // Start gate on every edit, and hand the config off on Start.
            secondsInput = FindViewById<EditText>(Resource.Id.seconds_input)!;
            countInput = FindViewById<EditText>(Resource.Id.count_input)!;
            startButton = FindViewById<Button>(Resource.Id.start_button)!;

            secondsInput.Text = settings.PoseDurationSeconds.ToString();
            countInput.Text = settings.SessionImageCount.ToString();

            secondsInput.TextChanged += (_, _) => UpdateStartState();
            countInput.TextChanged += (_, _) => UpdateStartState();
            startButton.Click += (_, _) => StartSession();
            UpdateStartState();

            // Restore the folder chosen on a previous launch, if the persisted URI permission
            // is still granted. A revoked permission (folder deleted, permission cleared) is
            // expected and simply leaves the empty state showing.
            RestoreLastFolder();
        }

        protected override void OnDestroy()
        {
            settingsStore?.Dispose();
            base.OnDestroy();
        }

        // Opens the system folder picker (Storage Access Framework). ACTION_OPEN_DOCUMENT_TREE
        // returns a tree content:// Uri granting access to the folder and everything under it.
        void PickFolder()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            StartActivityForResult(intent, PickFolderRequestCode);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickFolderRequestCode || resultCode != Result.Ok)
                return;

            var treeUri = data?.Data;
            if (treeUri is null)
                return;

            // Handling a folder result must never crash the app. Persisting the grant, writing
            // settings, or enumerating the tree can each throw (SecurityException, provider quirks,
            // out-of-memory on large images); a failure here shows a message instead of dying.
            try
            {
                // Persist the read grant so the folder can be reused on the next launch. Pass the
                // read flag as a constant — deriving it from data.Flags is fragile: on some devices
                // the result intent reports no flags, yielding 0 and a SecurityException.
                ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);

                settings.LastCollection = treeUri.ToString();
                settingsStore.SaveSettings(settings);

                Log.Info(LogTag, $"Folder selected: {treeUri}");
                LoadFolder(treeUri);
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, $"Failed to open folder {treeUri}: {ex}");
                imageContainer.RemoveAllViews();
                emptyLabel.Text = GetString(Resource.String.folder_error_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }
        }

        void RestoreLastFolder()
        {
            if (string.IsNullOrEmpty(settings.LastCollection))
                return;

            var treeUri = Android.Net.Uri.Parse(settings.LastCollection);
            if (treeUri is null)
                return;

            // Only reload if we still hold a persisted read permission for the tree.
            foreach (var permission in ContentResolver!.PersistedUriPermissions)
            {
                if (permission.IsReadPermission && permission.Uri?.Equals(treeUri) == true)
                {
                    LoadFolder(treeUri);
                    return;
                }
            }

            Log.Info(LogTag, $"Persisted permission for {treeUri} no longer held; skipping restore.");
        }

        // Enumerates image files under the picked tree (recursively, via the testable
        // FolderImageEnumerator) and shows them. Any entry whose MIME type starts with "image/"
        // is accepted (jpg/png/webp/gif/heic/...).
        void LoadFolder(Android.Net.Uri treeUri)
        {
            imageContainer.RemoveAllViews();
            imageUris.Clear();

            var rootDocumentId = DocumentsContract.GetTreeDocumentId(treeUri);
            if (rootDocumentId is not null)
            {
                var tree = new ContentResolverDocumentTree(ContentResolver!, treeUri);
                foreach (var documentId in FolderImageEnumerator.EnumerateImages(tree, rootDocumentId))
                {
                    var fileUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
                    if (fileUri is null)
                        continue;

                    // Record the uri for the session pool regardless of whether the preview decodes;
                    // the session player (FD-004) handles any that turn out unreadable.
                    imageUris.Add(fileUri.ToString()!);
                    AddImage(fileUri);
                }
            }

            if (imageUris.Count > 0)
            {
                emptyLabel.Visibility = ViewStates.Gone;
            }
            else
            {
                emptyLabel.Text = GetString(Resource.String.empty_folder_text);
                emptyLabel.Visibility = ViewStates.Visible;
            }

            // A session needs images to run, so the Start gate opens only when the folder yielded at
            // least one image (FD-002).
            folderSelected = imageUris.Count > 0;
            UpdateStartState();
        }

        // Recomputes whether the session may start and reflects it on the Start button. Pure logic
        // (parsing + validation) lives in the testable SessionSetup.Evaluate.
        void UpdateStartState()
        {
            var state = SessionSetup.Evaluate(secondsInput.Text, countInput.Text, folderSelected);
            startButton.Enabled = state.CanStart;
        }

        // FD-002 Start: persist the chosen values so they seed the next session, then hand the config
        // to the session engine (FD-003). Guarded on the same validation the button state uses.
        void StartSession()
        {
            var state = SessionSetup.Evaluate(secondsInput.Text, countInput.Text, folderSelected);
            if (state.Config is not { } config)
                return;

            settings.PoseDurationSeconds = config.SecondsPerImage;
            settings.SessionImageCount = config.ImageCount;
            settingsStore.SaveSettings(settings);

            Log.Info(LogTag,
                $"Session start: {config.SecondsPerImage}s/image, {config.ImageCount} images, " +
                $"{imageUris.Count} in pool.");

            // FD-004: hand the pool + config to the session player screen. Shuffle/grayscale come
            // from the persisted settings; the player reads them from the intent extras.
            var intent = new Intent(this, typeof(SessionActivity));
            intent.PutExtra(SessionActivity.ExtraPool, imageUris.ToArray());
            intent.PutExtra(SessionActivity.ExtraSeconds, config.SecondsPerImage);
            intent.PutExtra(SessionActivity.ExtraCount, config.ImageCount);
            intent.PutExtra(SessionActivity.ExtraShuffle, settings.ShuffleImages);
            intent.PutExtra(SessionActivity.ExtraGrayscale, settings.GrayscaleMode);
            StartActivity(intent);
        }

        // Adapts a Storage Access Framework tree (DocumentsContract + ContentResolver) to the
        // IDocumentTree abstraction the pure enumerator walks.
        sealed class ContentResolverDocumentTree(ContentResolver resolver, Android.Net.Uri treeUri)
            : IDocumentTree
        {
            public IEnumerable<DocumentEntry> GetChildren(string parentDocumentId)
            {
                var childrenUri =
                    DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentDocumentId);

                ICursor? cursor = resolver.Query(
                    childrenUri!,
                    new[]
                    {
                        DocumentsContract.Document.ColumnDocumentId,
                        DocumentsContract.Document.ColumnMimeType,
                    },
                    null, null, null);

                if (cursor is null)
                    yield break;

                try
                {
                    while (cursor.MoveToNext())
                    {
                        var documentId = cursor.GetString(0);
                        if (documentId is null)
                            continue;

                        yield return new DocumentEntry(documentId, cursor.GetString(1));
                    }
                }
                finally
                {
                    cursor.Close();
                }
            }
        }

        // Bound on the decoded size of each reference image (px). Real photos are far larger; using
        // SetImageURI would decode them at full resolution and exhaust memory for a folder of them.
        const int MaxImageDimension = 1080;

        void AddImage(Android.Net.Uri uri)
        {
            Android.Graphics.Bitmap? bitmap;
            try
            {
                bitmap = ImageDecoding.DecodeSampledBitmap(
                    ContentResolver!, uri, MaxImageDimension, MaxImageDimension);
            }
            catch (Exception ex)
            {
                // A single unreadable/oversized image must not sink the whole folder.
                Log.Warn(LogTag, $"Skipping image {uri}: {ex.Message}");
                return;
            }

            if (bitmap is null)
                return;

            var imageView = new ImageView(this);

            var layoutParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent);
            layoutParams.SetMargins(0, 0, 0, 24);

            imageView.LayoutParameters = layoutParams;
            imageView.SetAdjustViewBounds(true);
            imageView.SetImageBitmap(bitmap);

            imageContainer.AddView(imageView);
        }
    }
}
