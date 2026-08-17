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
    // The three tabbed screens of the Claude Design mock in one Activity: Session (setup), Images
    // (the reference library and the folder picker) and Settings. They share the settings document
    // and the loaded pool, so they are panes rather than separate screens (activity_main.xml).
    //
    // Everything with a rule behind it lives in Core: SessionSetup validates the inputs, gates Start
    // and estimates the session's length; FolderImageEnumerator walks the picked tree; SettingsStore
    // persists the preferences. This class finds views, reflects that state, and forwards taps.
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int PickFolderRequestCode = 1000;
        const string LogTag = "FigureDrawing";
        const string DatabaseFileName = "figuredrawing.db";

        // Bound on each decoded reference thumbnail (px). The grid is a preview, not the pose, so it
        // decodes far smaller than the player's 1080px.
        const int ThumbnailDimension = 360;

        // How many thumbnails the grid renders. A folder can hold thousands of photos; decoding all
        // of them would exhaust memory long before the drawer scrolled to them. The pool itself is
        // never truncated - every image found is still in the session - only the preview is.
        const int MaxThumbnails = 24;

        // --- Panes and tabs ---
        View paneSetup = null!;
        View paneLibrary = null!;
        View paneSettings = null!;
        View tabSession = null!;
        View tabImages = null!;
        View tabSettings = null!;

        // --- Library ---
        GridLayout imageContainer = null!;
        TextView emptyLabel = null!;
        TextView libraryCount = null!;
        TextView libraryMore = null!;

        // Content-uri strings for every image the picked folder yielded, in enumeration order. This
        // is the pool handed to the session engine (FD-003) when Start is tapped (FD-004).
        readonly List<string> imageUris = new();

        // --- Setup ---
        EditText secondsInput = null!;
        EditText countInput = null!;
        Button startButton = null!;
        TextView poolLabel = null!;
        TextView estimateLabel = null!;
        readonly Dictionary<int, Button> secondsChips = new();
        readonly Dictionary<int, Button> breakChips = new();

        // --- Settings ---
        Button shuffleToggle = null!;
        Button awakeToggle = null!;
        Button chimeToggle = null!;
        Button grayscaleToggle = null!;

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
                $"poseDuration={settings.PoseDurationSeconds}s, break={settings.BreakSeconds}s, " +
                $"shuffle={settings.ShuffleImages}, grayscale={settings.GrayscaleMode}");

            BindPanes();
            BindLibrary();
            BindSetup();
            BindSettings();

            ShowPane(paneSetup, tabSession);

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

        // Coming back from a finished session: the pool and inputs are unchanged, but the session may
        // have been started with values the summary screen let the drawer revisit.
        protected override void OnResume()
        {
            base.OnResume();
            UpdateStartState();
        }

        // --- Panes -----------------------------------------------------------

        void BindPanes()
        {
            paneSetup = FindViewById<View>(Resource.Id.pane_setup)!;
            paneLibrary = FindViewById<View>(Resource.Id.pane_library)!;
            paneSettings = FindViewById<View>(Resource.Id.pane_settings)!;

            tabSession = FindViewById<View>(Resource.Id.tab_session)!;
            tabImages = FindViewById<View>(Resource.Id.tab_images)!;
            tabSettings = FindViewById<View>(Resource.Id.tab_settings)!;

            tabSession.Click += (_, _) => ShowPane(paneSetup, tabSession);
            tabImages.Click += (_, _) => ShowPane(paneLibrary, tabImages);
            tabSettings.Click += (_, _) => ShowPane(paneSettings, tabSettings);
        }

        // Exactly one pane is visible; the matching tab carries the accent bar and label colour
        // (both inherit the tab's selected state through duplicateParentState).
        void ShowPane(View pane, View tab)
        {
            paneSetup.Visibility = pane == paneSetup ? ViewStates.Visible : ViewStates.Gone;
            paneLibrary.Visibility = pane == paneLibrary ? ViewStates.Visible : ViewStates.Gone;
            paneSettings.Visibility = pane == paneSettings ? ViewStates.Visible : ViewStates.Gone;

            tabSession.Selected = tab == tabSession;
            tabImages.Selected = tab == tabImages;
            tabSettings.Selected = tab == tabSettings;
        }

        // --- Session setup ---------------------------------------------------

        void BindSetup()
        {
            secondsInput = FindViewById<EditText>(Resource.Id.seconds_input)!;
            countInput = FindViewById<EditText>(Resource.Id.count_input)!;
            startButton = FindViewById<Button>(Resource.Id.start_button)!;
            poolLabel = FindViewById<TextView>(Resource.Id.pool_label)!;
            estimateLabel = FindViewById<TextView>(Resource.Id.estimate_label)!;

            // The chip rows are the presets from Core, in the order Core lists them, so the two can
            // never drift apart.
            BindPresetChips(secondsChips, SessionSetup.SecondsPresets, new[]
            {
                Resource.Id.chip_sec_30, Resource.Id.chip_sec_60,
                Resource.Id.chip_sec_120, Resource.Id.chip_sec_300
            }, OnSecondsPreset);

            BindPresetChips(breakChips, SessionSetup.BreakPresets, new[]
            {
                Resource.Id.chip_break_0, Resource.Id.chip_break_5,
                Resource.Id.chip_break_15, Resource.Id.chip_break_60
            }, OnBreakPreset);

            secondsInput.Text = settings.PoseDurationSeconds.ToString();
            countInput.Text = settings.SessionImageCount.ToString();

            secondsInput.TextChanged += (_, _) => UpdateStartState();
            countInput.TextChanged += (_, _) => UpdateStartState();
            startButton.Click += (_, _) => StartSession();

            // "Change" on the pool card is a shortcut to the folder picker's own screen.
            FindViewById<Button>(Resource.Id.change_button)!.Click +=
                (_, _) => ShowPane(paneLibrary, tabImages);

            UpdateStartState();
        }

        // Wires one chip row to one list of Core presets. The chips carry no value of their own —
        // they render presets[i] and hand it back — so a change to the preset list moves both ends.
        void BindPresetChips(
            Dictionary<int, Button> chips, IReadOnlyList<int> presets, int[] viewIds, Action<int> onPick)
        {
            for (var i = 0; i < presets.Count && i < viewIds.Length; i++)
            {
                var value = presets[i];
                var chip = FindViewById<Button>(viewIds[i])!;
                chip.Click += (_, _) => onPick(value);
                chips[value] = chip;
            }
        }

        void OnSecondsPreset(int seconds)
        {
            secondsInput.Text = seconds.ToString();
            secondsInput.SetSelection(secondsInput.Text!.Length);
        }

        void OnBreakPreset(int breakSeconds)
        {
            settings.BreakSeconds = breakSeconds;
            settingsStore.SaveSettings(settings);
            UpdateStartState();
        }

        // Recomputes whether the session may start and reflects it across the setup pane: the Start
        // gate, which preset chips read as chosen, the pool card and the length estimate. Pure logic
        // (parsing, validation, the estimate) lives in the testable SessionSetup.
        void UpdateStartState()
        {
            var state = SessionSetup.Evaluate(
                secondsInput.Text, countInput.Text, folderSelected, settings.BreakSeconds);

            startButton.Enabled = state.CanStart;

            foreach (var (value, chip) in secondsChips)
                chip.Selected = state.SecondsPerImage == value;

            foreach (var (value, chip) in breakChips)
                chip.Selected = state.BreakSeconds == value;

            poolLabel.Text = imageUris.Count > 0
                ? string.Format(GetString(Resource.String.pool_ready_format), imageUris.Count)
                : GetString(Resource.String.pool_empty_text);

            estimateLabel.Text = string.Format(
                GetString(Resource.String.estimate_format),
                PoseCountdown.Format(state.EstimateSeconds));
        }

        // FD-002 Start: persist the chosen values so they seed the next session, then hand the config
        // to the session engine (FD-003). Guarded on the same validation the button state uses.
        void StartSession()
        {
            var state = SessionSetup.Evaluate(
                secondsInput.Text, countInput.Text, folderSelected, settings.BreakSeconds);

            if (state.Config is not { } config)
                return;

            settings.PoseDurationSeconds = config.SecondsPerImage;
            settings.SessionImageCount = config.ImageCount;
            settingsStore.SaveSettings(settings);

            Log.Info(LogTag,
                $"Session start: {config.SecondsPerImage}s/image, {config.ImageCount} images, " +
                $"{config.BreakSeconds}s break, {imageUris.Count} in pool.");

            // FD-004: hand the pool + config to the session player screen. The preferences the player
            // needs travel as extras too — a screen never reads AppSettings on the far side (§16).
            var intent = new Intent(this, typeof(SessionActivity));
            intent.PutExtra(SessionActivity.ExtraPool, imageUris.ToArray());
            intent.PutExtra(SessionActivity.ExtraSeconds, config.SecondsPerImage);
            intent.PutExtra(SessionActivity.ExtraCount, config.ImageCount);
            intent.PutExtra(SessionActivity.ExtraBreak, config.BreakSeconds);
            intent.PutExtra(SessionActivity.ExtraShuffle, settings.ShuffleImages);
            intent.PutExtra(SessionActivity.ExtraGrayscale, settings.GrayscaleMode);
            intent.PutExtra(SessionActivity.ExtraKeepAwake, settings.KeepScreenAwake);
            intent.PutExtra(SessionActivity.ExtraChime, settings.ChimeOnChange);
            StartActivity(intent);
        }

        // --- Settings --------------------------------------------------------

        void BindSettings()
        {
            shuffleToggle = FindViewById<Button>(Resource.Id.setting_shuffle)!;
            awakeToggle = FindViewById<Button>(Resource.Id.setting_awake)!;
            chimeToggle = FindViewById<Button>(Resource.Id.setting_chime)!;
            grayscaleToggle = FindViewById<Button>(Resource.Id.setting_grayscale)!;

            shuffleToggle.Click += (_, _) => Toggle(() => settings.ShuffleImages = !settings.ShuffleImages);
            awakeToggle.Click += (_, _) => Toggle(() => settings.KeepScreenAwake = !settings.KeepScreenAwake);
            chimeToggle.Click += (_, _) => Toggle(() => settings.ChimeOnChange = !settings.ChimeOnChange);
            grayscaleToggle.Click += (_, _) => Toggle(() => settings.GrayscaleMode = !settings.GrayscaleMode);

            RenderSettings();
        }

        // A preference change is saved the moment it is made — the Settings screen has no Save
        // button, so an unsaved toggle would silently be lost on back.
        void Toggle(Action change)
        {
            change();
            settingsStore.SaveSettings(settings);
            RenderSettings();
        }

        void RenderSettings()
        {
            RenderToggle(shuffleToggle, settings.ShuffleImages);
            RenderToggle(awakeToggle, settings.KeepScreenAwake);
            RenderToggle(chimeToggle, settings.ChimeOnChange);
            RenderToggle(grayscaleToggle, settings.GrayscaleMode);
        }

        void RenderToggle(Button toggle, bool on)
        {
            toggle.Selected = on;
            toggle.Text = GetString(on ? Resource.String.toggle_on_text : Resource.String.toggle_off_text);
        }

        // --- Reference library -----------------------------------------------

        void BindLibrary()
        {
            imageContainer = FindViewById<GridLayout>(Resource.Id.image_container)!;
            emptyLabel = FindViewById<TextView>(Resource.Id.empty_label)!;
            libraryCount = FindViewById<TextView>(Resource.Id.library_count)!;
            libraryMore = FindViewById<TextView>(Resource.Id.library_more)!;

            // Two columns folded, four with the fold open (values[-sw600dp]/integers.xml).
            imageContainer.ColumnCount = Resources!.GetInteger(Resource.Integer.library_columns);

            FindViewById<Button>(Resource.Id.pick_button)!.Click += (_, _) => PickFolder();
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
                ClearThumbnails();
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
            ClearThumbnails();
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

                    if (imageContainer.ChildCount < MaxThumbnails)
                        AddThumbnail(fileUri);
                }
            }

            RenderLibrary();

            // A session needs images to run, so the Start gate opens only when the folder yielded at
            // least one image (FD-002).
            folderSelected = imageUris.Count > 0;
            UpdateStartState();
        }

        void RenderLibrary()
        {
            if (imageUris.Count > 0)
            {
                emptyLabel.Visibility = ViewStates.Gone;
                libraryCount.Text =
                    string.Format(GetString(Resource.String.pool_ready_format), imageUris.Count);
            }
            else
            {
                emptyLabel.Text = GetString(Resource.String.empty_folder_text);
                emptyLabel.Visibility = ViewStates.Visible;
                libraryCount.Text = GetString(Resource.String.pool_empty_text);
            }

            // Be explicit that the grid is a sample of a bigger pool rather than the whole of it.
            var hidden = imageUris.Count - imageContainer.ChildCount;
            if (hidden > 0)
            {
                libraryMore.Text = string.Format(GetString(Resource.String.library_more_format), hidden);
                libraryMore.Visibility = ViewStates.Visible;
            }
            else
            {
                libraryMore.Visibility = ViewStates.Gone;
            }
        }

        void ClearThumbnails()
        {
            imageContainer.RemoveAllViews();
            libraryMore.Visibility = ViewStates.Gone;
        }

        void AddThumbnail(Android.Net.Uri uri)
        {
            Android.Graphics.Bitmap? bitmap;
            try
            {
                bitmap = ImageDecoding.DecodeSampledBitmap(
                    ContentResolver!, uri, ThumbnailDimension, ThumbnailDimension);
            }
            catch (Exception ex)
            {
                // A single unreadable/oversized image must not sink the whole folder.
                Log.Warn(LogTag, $"Skipping image {uri}: {ex.Message}");
                return;
            }

            if (bitmap is null)
                return;

            var margin = Resources!.GetDimensionPixelSize(Resource.Dimension.space_2);
            var layoutParams = new GridLayout.LayoutParams
            {
                Width = 0,
                Height = Resources.GetDimensionPixelSize(Resource.Dimension.thumb_height),
                ColumnSpec = GridLayout.InvokeSpec(GridLayout.Undefined, 1f)
            };
            layoutParams.SetMargins(margin, margin, margin, margin);

            var imageView = new ImageView(this)
            {
                LayoutParameters = layoutParams,
                ContentDescription = GetString(Resource.String.thumbnail_desc)
            };
            imageView.SetScaleType(ImageView.ScaleType.CenterCrop);
            imageView.SetBackgroundResource(Resource.Drawable.bg_thumb);
            imageView.ClipToOutline = true;
            imageView.SetImageBitmap(bitmap);

            imageContainer.AddView(imageView);
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
    }
}
