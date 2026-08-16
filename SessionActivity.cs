using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // FD-004 session player screen: shows ONE reference image at a time, full-screen, driving the
    // FD-003 DrawingSession through the FD-004 SessionPlayer. It keeps the screen awake, honors
    // grayscale mode, and skips unreadable images (handled in SessionPlayer).
    //
    // FD-005 adds the per-pose countdown: a PoseCountdown (Core, clock-driven, unit-tested) holds the
    // remaining time; this screen only repaints it on a Handler loop and calls Advance() at zero. The
    // countdown pauses in OnPause and resumes in OnResume so a backgrounded app does not burn pose
    // time or fire while hidden. Tapping the image still advances early (a manual "done" gesture).
    //
    // Skip/end controls (FD-006/007) land in later tickets. When the session completes it returns to
    // the setup screen (the summary screen is FD-007).
    //
    // NoActionBar theme: the countdown (FD-005) overlays the very top of the pose, but the default
    // theme's ActionBar (the "FigureDrawing" title bar) is opaque and draws over that same top strip,
    // hiding the timer entirely. A full-bleed lightbox has no need for a title bar anyway; dropping it
    // lets the timer sit at the true top. session_root uses fitsSystemWindows so the timer still clears
    // the status bar.
    [Activity(Label = "@string/app_name", Theme = "@android:style/Theme.Material.NoActionBar")]
    public class SessionActivity : Activity
    {
        // Intent extras handed over by MainActivity.StartSession.
        public const string ExtraPool = "pool";            // string[] of content:// uri strings
        public const string ExtraSeconds = "seconds";      // int, seconds per image
        public const string ExtraCount = "count";          // int, images this session shows
        public const string ExtraShuffle = "shuffle";      // bool, AppSettings.ShuffleImages
        public const string ExtraGrayscale = "grayscale";  // bool, AppSettings.GrayscaleMode

        const string LogTag = "FigureDrawing";

        // Matches MainActivity's preview bound: decode down to ~this many px so a folder of
        // full-resolution photos does not exhaust memory.
        const int MaxImageDimension = 1080;

        // Saturation-0 filter applied to the ImageView when grayscale mode is on (value studies).
        static readonly ColorMatrixColorFilter GrayscaleFilter = MakeGrayscaleFilter();

        // Repaint cadence for the countdown. Well under a second so the displayed value flips within
        // a fraction of a second of each boundary; the value itself comes from the clock, so this
        // interval never affects accuracy (a missed tick cannot slow the countdown down).
        const int TickIntervalMs = 200;

        ImageView image = null!;
        TextView status = null!;
        TextView timer = null!;
        SessionPlayer<Bitmap> player = null!;
        PoseCountdown countdown = null!;
        Android.OS.Handler ticker = null!;

        // The repaint callback, held as ONE Runnable instance so it can actually be removed from the
        // Handler queue. Handler.PostDelayed(Action) wraps the delegate in a fresh Java Runnable each
        // call, so RemoveCallbacks(Action) would never match — posting and removing the same stored
        // IRunnable is what guarantees no Tick survives teardown.
        Java.Lang.IRunnable tickRunnable = null!;
        bool ticking;
        bool grayscale;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_session);

            // Keep the screen awake for the whole session — no sleeping mid-pose (FD-004 acceptance).
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            image = FindViewById<ImageView>(Resource.Id.session_image)!;
            status = FindViewById<TextView>(Resource.Id.session_status)!;
            timer = FindViewById<TextView>(Resource.Id.session_timer)!;

            var pool = Intent?.GetStringArrayExtra(ExtraPool) ?? Array.Empty<string>();
            var seconds = Intent?.GetIntExtra(ExtraSeconds, SessionSetup.DefaultSecondsPerImage)
                          ?? SessionSetup.DefaultSecondsPerImage;
            var count = Intent?.GetIntExtra(ExtraCount, SessionSetup.DefaultImageCount)
                        ?? SessionSetup.DefaultImageCount;
            var shuffle = Intent?.GetBooleanExtra(ExtraShuffle, true) ?? true;
            grayscale = Intent?.GetBooleanExtra(ExtraGrayscale, false) ?? false;

            Log.Info(LogTag,
                $"Session player start: {pool.Length} images in pool, {count} to draw, " +
                $"{seconds}s/image, shuffle={shuffle}, grayscale={grayscale}.");

            var session = new DrawingSession(pool, new SessionConfig(seconds, count), shuffle);
            player = new SessionPlayer<Bitmap>(
                session,
                LoadBitmap,
                onUnreadable: id => Log.Warn(LogTag, $"Skipping unreadable image {id}"));

            countdown = new PoseCountdown(seconds);
            ticker = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
            tickRunnable = new Java.Lang.Runnable(Tick);

            // Manual "done" gesture: finish the pose early instead of waiting out the countdown.
            image.Click += (_, _) => Advance();

            Render();
        }

        // Backgrounded: freeze the pose clock and stop repainting, so no time is burned and the
        // timer cannot fire while hidden (FD-005 acceptance).
        protected override void OnPause()
        {
            countdown.Pause();
            StopTicking();
            base.OnPause();
        }

        // Foregrounded again: pick the pose up exactly where it was left.
        protected override void OnResume()
        {
            base.OnResume();

            if (player.CurrentImage is null)
                return;

            countdown.Resume();
            UpdateTimer();
            StartTicking();
        }

        protected override void OnDestroy()
        {
            StopTicking();
            // Drop the keep-awake flag so it can't leak past this screen.
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            base.OnDestroy();
        }

        // Count the current image and move to the next pose; finish back to setup once complete.
        // Restarts the countdown so every pose gets the full configured time.
        void Advance()
        {
            if (player.IsComplete)
            {
                StopTicking();
                Finish();
                return;
            }

            player.Next();
            countdown.Restart();
            Render();
        }

        // Repaint loop: refresh the displayed time and advance when the pose runs out. Bails the
        // instant the activity is tearing down so a queued Tick can never touch a dead view.
        void Tick()
        {
            if (!ticking || IsFinishing || IsDestroyed)
                return;

            UpdateTimer();

            if (countdown.IsExpired)
                Advance();      // may stop the loop (session complete) or restart the countdown

            if (ticking)
                ticker.PostDelayed(tickRunnable, TickIntervalMs);
        }

        void StartTicking()
        {
            if (ticking)
                return;

            ticking = true;
            ticker.PostDelayed(tickRunnable, TickIntervalMs);
        }

        // Stop AND drop any already-queued repaint, so nothing fires after we've torn down.
        void StopTicking()
        {
            ticking = false;
            ticker.RemoveCallbacks(tickRunnable);
        }

        void UpdateTimer() => timer.Text = countdown.Display;

        // Reflect the player's current state on screen: the image (grayscale if configured), the
        // "couldn't display" error, or finish when the session completed normally.
        void Render()
        {
            if (player.CurrentImage is { } bitmap)
            {
                image.SetImageBitmap(bitmap);
                if (grayscale)
                    image.SetColorFilter(GrayscaleFilter);
                else
                    image.ClearColorFilter();

                image.Visibility = ViewStates.Visible;
                status.Visibility = ViewStates.Gone;

                // A pose is on screen: show its countdown and keep it repainting.
                UpdateTimer();
                timer.Visibility = ViewStates.Visible;
                StartTicking();
                return;
            }

            // Nothing to draw any more — stop the clock before showing the error / leaving.
            StopTicking();
            image.Visibility = ViewStates.Gone;
            timer.Visibility = ViewStates.Gone;

            if (player.CouldNotDisplayImage)
            {
                // Every reachable image failed to decode — show the error rather than a blank screen.
                status.Text = GetString(Resource.String.session_error_text);
                status.Visibility = ViewStates.Visible;
            }
            else
            {
                // Session finished normally; return to the setup screen (FD-007 summary lands later).
                Finish();
            }
        }

        // Decode a content-uri string to a bitmap, or null if it is unreadable/broken — the
        // SessionPlayer treats null as "skip this image". Never throws out to the player.
        Bitmap? LoadBitmap(string id)
        {
            try
            {
                var uri = Android.Net.Uri.Parse(id);
                if (uri is null)
                    return null;

                return ImageDecoding.DecodeSampledBitmap(
                    ContentResolver!, uri, MaxImageDimension, MaxImageDimension);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Failed to decode {id}: {ex.Message}");
                return null;
            }
        }

        static ColorMatrixColorFilter MakeGrayscaleFilter()
        {
            var matrix = new ColorMatrix();
            matrix.SetSaturation(0f);
            return new ColorMatrixColorFilter(matrix);
        }
    }
}
