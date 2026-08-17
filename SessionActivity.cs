using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Media;
using Android.Util;
using Android.Views;
using Android.Widget;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // The player screen from the Claude Design mock: the pose on a lightbox stage, a rail with the
    // countdown ring and the viewing tools, a break overlay between poses, a pause sheet, and the
    // end-of-session summary.
    //
    // All of the session's behaviour is in Core. DrawingSession owns which image is up, how long it has
    // left, and the pose/break/complete state machine; ViewerTools owns the grayscale/flip/grid/blur
    // flags and the zoom range. This class does what an Activity is allowed to do: find views, run a
    // repaint loop, render Core's state, forward taps, and manage the lifecycle.
    //
    // NoActionBar theme: the stage is full-bleed, and the default theme's opaque title bar would eat
    // the top of every pose. session_root uses fitsSystemWindows so the content still clears the
    // system bars.
    //
    // ConfigurationChanges: a fold opening or closing is handled in place rather than by recreating
    // the Activity. Recreation would restart the current pose (session state is not persisted -
    // docs/ARCHITECTURE.md §5), which is exactly what a drawer must not lose mid-session.
    [Activity(
        Label = "@string/app_name",
        Theme = "@style/AppTheme.NoActionBar",
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
            | ConfigChanges.ScreenLayout | ConfigChanges.Orientation | ConfigChanges.KeyboardHidden)]
    public class SessionActivity : Activity
    {
        // Intent extras handed over by MainActivity.StartSession.
        public const string ExtraPool = "pool";            // string[] of content:// uri strings
        public const string ExtraSeconds = "seconds";      // int, seconds per image
        public const string ExtraCount = "count";          // int, images this session shows
        public const string ExtraBreak = "break";          // int, seconds of rest between poses
        public const string ExtraShuffle = "shuffle";      // bool, Settings.ShuffleImages
        public const string ExtraGrayscale = "grayscale";  // bool, Settings.GrayscaleMode
        public const string ExtraKeepAwake = "keepawake";  // bool, Settings.KeepScreenAwake
        public const string ExtraChime = "chime";          // bool, Settings.ChimeOnChange

        const string LogTag = "FigureDrawing";

        // Decode bound for the pose itself: real photos are far larger, and decoding one at full
        // resolution would exhaust memory within a few images.
        const int MaxImageDimension = 1080;

        // Saturation-0 filter applied to the ImageView for grayscale value studies.
        static readonly ColorMatrixColorFilter GrayscaleFilter = MakeGrayscaleFilter();

        // Blur radius for the block-in tool, in px. Only reachable on API 31+ (RenderEffect).
        const float BlurRadius = 24f;

        // Repaint cadence for the countdown. Well under a second so the displayed value flips within
        // a fraction of a second of each boundary; the value itself comes from the clock, so this
        // interval never affects accuracy (a missed tick cannot slow the countdown down).
        const int TickIntervalMs = 200;

        // Width at which the rail moves from under the pose to beside it (fold open / tablet).
        const int WideScreenWidthDp = 600;

        // Above this many poses the progress strip is dropped rather than drawn as slivers.
        const int MaxPips = 40;

        // --- Player views ---
        LinearLayout body = null!;
        View stage = null!;
        LinearLayout rail = null!;
        ImageView image = null!;
        View grid = null!;
        TextView status = null!;
        TextView timer = null!;
        TextView progressLabel = null!;
        ProgressBar ring = null!;
        View breakOverlay = null!;
        TextView breakTimer = null!;
        View pauseOverlay = null!;
        TextView pausedTimer = null!;
        TextView pausedStats = null!;
        View progressGroup = null!;
        LinearLayout pips = null!;
        TextView stats = null!;

        // --- Tool chips ---
        Button grayscaleChip = null!;
        Button flipChip = null!;
        Button gridChip = null!;
        Button blurChip = null!;
        Button zoomInChip = null!;
        Button zoomOutChip = null!;

        // --- Summary views ---
        View summary = null!;
        TextView summaryImages = null!;
        TextView summaryTime = null!;
        TextView summaryAverage = null!;
        TextView summarySkipped = null!;

        DrawingSession<Bitmap> session = null!;
        ViewerTools tools = null!;
        Android.OS.Handler ticker = null!;

        // The repaint callback, held as ONE Runnable instance so it can actually be removed from the
        // Handler queue. Handler.PostDelayed(Action) wraps the delegate in a fresh Java Runnable each
        // call, so RemoveCallbacks(Action) would never match — posting and removing the same stored
        // IRunnable is what guarantees no Tick survives teardown.
        Java.Lang.IRunnable tickRunnable = null!;
        bool ticking;

        // Session inputs, kept so "Run it again" can rebuild an identical session.
        string[] pool = Array.Empty<string>();
        int secondsPerImage;
        int imageCount;
        int breakSeconds;
        bool shuffle;
        bool startGrayscale;
        bool keepAwake;
        bool chime;

        ToneGenerator? tone;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_session);

            BindViews();

            pool = Intent?.GetStringArrayExtra(ExtraPool) ?? Array.Empty<string>();
            secondsPerImage = Intent?.GetIntExtra(ExtraSeconds, SessionSetup.DefaultSecondsPerImage)
                              ?? SessionSetup.DefaultSecondsPerImage;
            imageCount = Intent?.GetIntExtra(ExtraCount, SessionSetup.DefaultImageCount)
                         ?? SessionSetup.DefaultImageCount;
            breakSeconds = Intent?.GetIntExtra(ExtraBreak, SessionSetup.DefaultBreakSeconds)
                           ?? SessionSetup.DefaultBreakSeconds;
            shuffle = Intent?.GetBooleanExtra(ExtraShuffle, true) ?? true;
            startGrayscale = Intent?.GetBooleanExtra(ExtraGrayscale, false) ?? false;
            keepAwake = Intent?.GetBooleanExtra(ExtraKeepAwake, true) ?? true;
            chime = Intent?.GetBooleanExtra(ExtraChime, false) ?? false;

            // Keep the screen awake for the whole session unless the drawer turned that off — no
            // sleeping mid-pose (FD-004 acceptance).
            if (keepAwake)
                Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

            if (chime)
                tone = new ToneGenerator(Android.Media.Stream.Notification, 70);

            Log.Info(LogTag,
                $"Session player start: {pool.Length} images in pool, {imageCount} to draw, " +
                $"{secondsPerImage}s/image, {breakSeconds}s break, shuffle={shuffle}, " +
                $"grayscale={startGrayscale}.");

            ticker = new Android.OS.Handler(Android.OS.Looper.MainLooper!);
            tickRunnable = new Java.Lang.Runnable(Tick);

            ApplyRailLayout();
            StartSession();
        }

        // A fold opening mid-session moves the rail beside the pose without losing the pose.
        public override void OnConfigurationChanged(Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            ApplyRailLayout();
        }

        // Backgrounded: freeze the pose clock and stop repainting, so no time is burned and the
        // timer cannot fire while hidden (FD-005 acceptance).
        protected override void OnPause()
        {
            session.Pause();
            StopTicking();
            base.OnPause();
        }

        // Foregrounded again: pick the pose up exactly where it was left. A session that was already
        // paused by the drawer, or already over, stays that way.
        protected override void OnResume()
        {
            base.OnResume();

            if (session.IsComplete || pauseOverlay.Visibility == ViewStates.Visible)
                return;

            session.Resume();
            RenderClock();
            StartTicking();
        }

        protected override void OnDestroy()
        {
            StopTicking();
            // Drop the keep-awake flag so it can't leak past this screen.
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            tone?.Release();
            tone?.Dispose();
            tone = null;
            base.OnDestroy();
        }

        // --- Wiring ----------------------------------------------------------

        void BindViews()
        {
            body = FindViewById<LinearLayout>(Resource.Id.session_body)!;
            stage = FindViewById<View>(Resource.Id.session_stage)!;
            rail = FindViewById<LinearLayout>(Resource.Id.session_rail)!;
            image = FindViewById<ImageView>(Resource.Id.session_image)!;
            grid = FindViewById<View>(Resource.Id.session_grid)!;
            status = FindViewById<TextView>(Resource.Id.session_status)!;
            timer = FindViewById<TextView>(Resource.Id.session_timer)!;
            progressLabel = FindViewById<TextView>(Resource.Id.session_progress)!;
            ring = FindViewById<ProgressBar>(Resource.Id.session_ring)!;
            breakOverlay = FindViewById<View>(Resource.Id.session_break_overlay)!;
            breakTimer = FindViewById<TextView>(Resource.Id.break_timer)!;
            pauseOverlay = FindViewById<View>(Resource.Id.session_pause_overlay)!;
            pausedTimer = FindViewById<TextView>(Resource.Id.paused_timer)!;
            pausedStats = FindViewById<TextView>(Resource.Id.paused_stats)!;
            progressGroup = FindViewById<View>(Resource.Id.session_progress_group)!;
            pips = FindViewById<LinearLayout>(Resource.Id.session_pips)!;
            stats = FindViewById<TextView>(Resource.Id.session_stats)!;

            grayscaleChip = FindViewById<Button>(Resource.Id.chip_grayscale)!;
            flipChip = FindViewById<Button>(Resource.Id.chip_flip)!;
            gridChip = FindViewById<Button>(Resource.Id.chip_grid)!;
            blurChip = FindViewById<Button>(Resource.Id.chip_blur)!;
            zoomInChip = FindViewById<Button>(Resource.Id.chip_zoom_in)!;
            zoomOutChip = FindViewById<Button>(Resource.Id.chip_zoom_out)!;

            summary = FindViewById<View>(Resource.Id.session_summary)!;
            summaryImages = FindViewById<TextView>(Resource.Id.summary_images)!;
            summaryTime = FindViewById<TextView>(Resource.Id.summary_time)!;
            summaryAverage = FindViewById<TextView>(Resource.Id.summary_average)!;
            summarySkipped = FindViewById<TextView>(Resource.Id.summary_skipped)!;

            // Manual "done" gesture: finish the pose early instead of waiting out the countdown.
            image.Click += (_, _) => Command(() => session.Next());

            FindViewById<Button>(Resource.Id.session_next)!.Click += (_, _) => Command(() => session.Next());
            FindViewById<Button>(Resource.Id.session_skip)!.Click += (_, _) => Command(() => session.Skip());
            FindViewById<Button>(Resource.Id.session_end)!.Click += (_, _) => Command(() => session.End());
            FindViewById<Button>(Resource.Id.session_pause)!.Click += (_, _) => PauseSession();

            FindViewById<Button>(Resource.Id.paused_resume)!.Click += (_, _) => ResumeSession();
            FindViewById<Button>(Resource.Id.paused_skip)!.Click += (_, _) =>
            {
                ResumeSession();
                Command(() => session.Skip());
            };
            FindViewById<Button>(Resource.Id.paused_end)!.Click += (_, _) => Command(() => session.End());

            grayscaleChip.Click += (_, _) => ApplyTool(() => tools.ToggleGrayscale());
            flipChip.Click += (_, _) => ApplyTool(() => tools.ToggleFlip());
            gridChip.Click += (_, _) => ApplyTool(() => tools.ToggleGrid());
            blurChip.Click += (_, _) => ApplyTool(() => tools.ToggleBlur());
            zoomInChip.Click += (_, _) => ApplyTool(() => tools.ZoomIn());
            zoomOutChip.Click += (_, _) => ApplyTool(() => tools.ZoomOut());

            // Blur is a RenderEffect, which only exists from API 31. Below that the chip would be a
            // control that does nothing, so it is not offered at all.
            if (!OperatingSystem.IsAndroidVersionAtLeast(31))
                blurChip.Visibility = ViewStates.Gone;

            FindViewById<Button>(Resource.Id.summary_again)!.Click += (_, _) => StartSession();
            FindViewById<Button>(Resource.Id.summary_settings)!.Click += (_, _) => Finish();
        }

        // Builds (or rebuilds, for "Run it again") a session from the extras this screen was started
        // with, and paints the first pose.
        void StartSession()
        {
            session = new DrawingSession<Bitmap>(
                pool,
                new SessionConfig(secondsPerImage, imageCount, breakSeconds),
                LoadBitmap,
                shuffle,
                onUnreadable: id => Log.Warn(LogTag, $"Skipping unreadable image {id}"));

            tools = new ViewerTools(startGrayscale);

            BuildPips();
            ApplyTools();
            Render();
        }

        // --- Commands --------------------------------------------------------

        // Every pose command goes through here: run it on the Core aggregate, then repaint. The
        // aggregate is what decides whether the command counted, started a break, or ended the run.
        void Command(Action command)
        {
            command();
            Render();
        }

        void PauseSession()
        {
            if (session.IsComplete)
                return;

            session.Pause();
            StopTicking();
            Render();
        }

        void ResumeSession()
        {
            if (session.IsComplete)
                return;

            session.Resume();
            Render();
        }

        void ApplyTool(Action change)
        {
            change();
            ApplyTools();
        }

        // --- Repaint loop ----------------------------------------------------

        // Refresh the displayed time and let the aggregate expire the current phase. Bails the
        // instant the activity is tearing down so a queued Tick can never touch a dead view.
        void Tick()
        {
            if (!ticking || IsFinishing || IsDestroyed)
                return;

            if (session.Tick())
            {
                // The phase changed: a new pose, a break, or the end of the session.
                Chime();
                Render();
            }
            else
            {
                RenderClock();
            }

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

        // A short tone when the pose changes on its own. Only the automatic change chimes: a drawer
        // who tapped Next or Skip is already looking at the screen.
        void Chime()
        {
            if (session.IsComplete)
                return;

            tone?.StartTone(Tone.PropBeep, 120);
        }

        // --- Rendering -------------------------------------------------------

        // Full repaint: which of the three states the screen is in (player, error, summary) and
        // everything inside the current one.
        void Render()
        {
            if (session.IsComplete)
            {
                StopTicking();
                RenderTerminalState();
                return;
            }

            body.Visibility = ViewStates.Visible;
            summary.Visibility = ViewStates.Gone;
            status.Visibility = ViewStates.Gone;

            if (session.CurrentImage is { } bitmap)
                image.SetImageBitmap(bitmap);

            breakOverlay.Visibility = session.OnBreak ? ViewStates.Visible : ViewStates.Gone;

            var paused = session.IsPaused;
            pauseOverlay.Visibility = paused ? ViewStates.Visible : ViewStates.Gone;

            progressLabel.Text = string.Format(
                GetString(Resource.String.session_progress_format),
                session.CurrentPoseNumber, session.TargetCount);

            stats.Text = string.Format(
                GetString(Resource.String.session_stats_format),
                FormatDuration(session.TotalDrawingTime), session.SkippedCount);

            RenderPips();
            RenderClock();

            if (!paused)
                StartTicking();
        }

        // The cheap per-tick repaint: only the things that change every 200ms.
        void RenderClock()
        {
            var display = session.Display;
            timer.Text = display;
            breakTimer.Text = display;
            pausedTimer.Text = display;
            ring.Progress = session.RemainingPercent;

            pausedStats.Text = string.Format(
                GetString(Resource.String.paused_stats_format),
                string.Format(GetString(Resource.String.session_progress_format),
                    session.CurrentPoseNumber, session.TargetCount),
                FormatDuration(session.TotalDrawingTime));
        }

        // The session is over: either nothing in the pool could be decoded (an error), or it ran to
        // its end / was ended early (the summary).
        void RenderTerminalState()
        {
            body.Visibility = ViewStates.Gone;

            if (session.CouldNotDisplayImage)
            {
                // Every reachable image failed to decode — show the error rather than a blank screen.
                summary.Visibility = ViewStates.Gone;
                status.Text = GetString(Resource.String.session_error_text);
                status.Visibility = ViewStates.Visible;
                return;
            }

            status.Visibility = ViewStates.Gone;
            summary.Visibility = ViewStates.Visible;

            summaryImages.Text = session.ImagesDisplayed.ToString();
            summaryTime.Text = FormatDuration(session.TotalDrawingTime);
            summaryAverage.Text = FormatDuration(session.AveragePoseTime);
            summarySkipped.Text = session.SkippedCount.ToString();
        }

        // One segment per pose in the session-progress strip, filled as poses are completed. Weighted
        // rather than fixed-width so a long session still fits the rail; past MaxPips the segments
        // would be sub-pixel, so the strip is dropped and the stats line below carries the progress.
        void BuildPips()
        {
            pips.RemoveAllViews();

            if (session.TargetCount > MaxPips)
                return;

            var gap = Resources!.GetDimensionPixelSize(Resource.Dimension.pip_gap);
            var height = Resources.GetDimensionPixelSize(Resource.Dimension.pip_size);

            for (var i = 0; i < session.TargetCount; i++)
            {
                var segment = new View(this);
                var layoutParams = new LinearLayout.LayoutParams(0, height, 1f);
                layoutParams.SetMargins(i == 0 ? 0 : gap, 0, 0, 0);
                segment.LayoutParameters = layoutParams;
                segment.SetBackgroundResource(Resource.Drawable.bg_pip);
                pips.AddView(segment);
            }
        }

        void RenderPips()
        {
            for (var i = 0; i < pips.ChildCount; i++)
                pips.GetChildAt(i)!.Selected = i < session.CompletedCount;
        }

        // Reflect ViewerTools onto the stage: the chips' selected faces and the effects themselves.
        void ApplyTools()
        {
            grayscaleChip.Selected = tools.Grayscale;
            flipChip.Selected = tools.Flip;
            gridChip.Selected = tools.Grid;
            blurChip.Selected = tools.Blur;
            zoomInChip.Enabled = tools.CanZoomIn;
            zoomOutChip.Enabled = tools.CanZoomOut;

            if (tools.Grayscale)
                image.SetColorFilter(GrayscaleFilter);
            else
                image.ClearColorFilter();

            grid.Visibility = tools.Grid ? ViewStates.Visible : ViewStates.Gone;

            // Flip is a negative horizontal scale, so it composes with zoom in one transform.
            var zoom = (float)tools.Zoom;
            image.ScaleX = tools.Flip ? -zoom : zoom;
            image.ScaleY = zoom;

            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                image.SetRenderEffect(tools.Blur
                    ? RenderEffect.CreateBlurEffect(BlurRadius, BlurRadius, Shader.TileMode.Clamp!)
                    : null);
            }
        }

        // Phone: the rail sits under the pose. Fold open / tablet: it becomes a column beside it,
        // which is also where the session-progress strip earns its space.
        void ApplyRailLayout()
        {
            var wide = Resources!.Configuration!.ScreenWidthDp >= WideScreenWidthDp;

            // Fully qualified: Android.Media and Android.Content.Res both also define an Orientation.
            body.Orientation = wide
                ? Android.Widget.Orientation.Horizontal
                : Android.Widget.Orientation.Vertical;

            var stageParams = (LinearLayout.LayoutParams)stage.LayoutParameters!;
            stageParams.Width = wide ? 0 : ViewGroup.LayoutParams.MatchParent;
            stageParams.Height = wide ? ViewGroup.LayoutParams.MatchParent : 0;
            stageParams.Weight = 1f;
            stage.LayoutParameters = stageParams;

            var railParams = (LinearLayout.LayoutParams)rail.LayoutParameters!;
            railParams.Width = wide
                ? Resources.GetDimensionPixelSize(Resource.Dimension.rail_width)
                : ViewGroup.LayoutParams.MatchParent;
            railParams.Height = wide
                ? ViewGroup.LayoutParams.MatchParent
                : ViewGroup.LayoutParams.WrapContent;
            railParams.Weight = 0f;
            rail.LayoutParameters = railParams;

            progressGroup.Visibility = wide ? ViewStates.Visible : ViewStates.Gone;
        }

        // --- Helpers ---------------------------------------------------------

        // Durations read the same everywhere on this screen (m:ss), using the session's own
        // formatter so the summary and the timer can never disagree about how time is written.
        static string FormatDuration(TimeSpan value) =>
            DrawingSession.Format((int)Math.Round(value.TotalSeconds));

        // Decode a content-uri string to a bitmap, or null if it is unreadable/broken — the
        // session treats null as "skip this image". Never throws out to the session.
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
