using System.IO;
using Android.Content;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Data;

namespace FigureDrawing
{
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int PickImagesRequestCode = 1000;
        const string LogTag = "FigureDrawing";
        const string DatabaseFileName = "figuredrawing.db";

        LinearLayout imageContainer = null!;
        TextView emptyLabel = null!;

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
            pickButton.Click += (_, _) => PickImages();
        }

        protected override void OnDestroy()
        {
            settingsStore?.Dispose();
            base.OnDestroy();
        }

        // Opens the system file picker filtered to images. ACTION_GET_CONTENT does not
        // require any runtime storage permission and returns a readable content:// Uri.
        void PickImages()
        {
            var intent = new Intent(Intent.ActionGetContent);
            intent.SetType("image/*");
            intent.AddCategory(Intent.CategoryOpenable);
            intent.PutExtra(Intent.ExtraAllowMultiple, true);

            StartActivityForResult(
                Intent.CreateChooser(intent, GetString(Resource.String.pick_button_text)),
                PickImagesRequestCode);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != PickImagesRequestCode || resultCode != Result.Ok || data is null)
                return;

            imageContainer.RemoveAllViews();

            if (data.ClipData is { } clip)
            {
                for (int i = 0; i < clip.ItemCount; i++)
                {
                    var uri = clip.GetItemAt(i)?.Uri;
                    if (uri is not null)
                        AddImage(uri);
                }
            }
            else if (data.Data is { } single)
            {
                AddImage(single);
            }

            emptyLabel.Visibility = imageContainer.ChildCount > 0 ? ViewStates.Gone : ViewStates.Visible;
        }

        void AddImage(Android.Net.Uri uri)
        {
            var imageView = new ImageView(this);

            var layoutParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent);
            layoutParams.SetMargins(0, 0, 0, 24);

            imageView.LayoutParameters = layoutParams;
            imageView.SetAdjustViewBounds(true);
            imageView.SetImageURI(uri);

            imageContainer.AddView(imageView);
        }
    }
}
