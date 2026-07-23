namespace FigureDrawing.Core;

// FD-005 per-pose countdown. The UI-independent half of "show a countdown for each pose and advance
// when it hits zero": it owns how much time is left, whether it is running, and how that time reads
// on screen. The Android screen owns only the repaint loop (a Handler) and calls Pause()/Resume()
// from the lifecycle.
//
// Time comes from a monotonic clock rather than counting ticks, so a slow/skipped repaint cannot make
// the countdown drift: remaining is always (duration - time actually spent running). Paused time does
// not count, which is what makes OnPause/OnResume correct — a backgrounded app resumes exactly where
// the pose left off instead of expiring in the background.
public sealed class PoseCountdown
{
    readonly Func<TimeSpan> _now;

    TimeSpan _duration;

    // Time banked from completed run segments (everything before the latest Resume/Restart).
    TimeSpan _banked;

    // When the current run segment started, or null while paused.
    TimeSpan? _runningSince;

    // duration : how long each pose lasts (SessionConfig.SecondsPerImage).
    // clock    : injectable monotonic time source for deterministic tests; defaults to a Stopwatch
    //            (same convention as DrawingSession).
    public PoseCountdown(TimeSpan duration, Func<TimeSpan>? clock = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _now = clock ?? (() => stopwatch.Elapsed);
        _duration = Clamp(duration);
        _runningSince = _now();
    }

    // Convenience for the configured seconds-per-image.
    public PoseCountdown(int seconds, Func<TimeSpan>? clock = null)
        : this(TimeSpan.FromSeconds(Math.Max(0, seconds)), clock)
    {
    }

    // The full length of the current pose.
    public TimeSpan Duration => _duration;

    // True while time is actually draining (not paused, not expired).
    public bool IsRunning => _runningSince is not null && Remaining > TimeSpan.Zero;

    // True while paused by the lifecycle (or an explicit pause).
    public bool IsPaused => _runningSince is null;

    // Time left on the current pose, never negative.
    public TimeSpan Remaining
    {
        get
        {
            var elapsed = _banked + (_runningSince is { } since ? _now() - since : TimeSpan.Zero);
            var left = _duration - elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    // True once the pose's time is used up — the screen turns this into SessionPlayer.Next().
    public bool IsExpired => Remaining <= TimeSpan.Zero;

    // Whole seconds to display, rounded UP: a fresh 30s pose reads "30" immediately and only reads
    // "0" once it has actually expired (rounding down would show "0" for the whole final second).
    public int RemainingSeconds => (int)Math.Ceiling(Remaining.TotalSeconds - 1e-6);

    // What the timer view shows: m:ss (or h:mm:ss for the rare hour-long pose).
    public string Display => Format(RemainingSeconds);

    // Freeze the countdown (OnPause). Idempotent; banks the elapsed time of the current segment.
    public void Pause()
    {
        if (_runningSince is not { } since)
            return;

        _banked += _now() - since;
        _runningSince = null;
    }

    // Unfreeze (OnResume). Idempotent, and a no-op once expired so a resume can't revive a dead pose.
    public void Resume()
    {
        if (_runningSince is not null || IsExpired)
            return;

        _runningSince = _now();
    }

    // Start the countdown over for a new pose (same duration). Always resumes running.
    public void Restart() => Restart(_duration);

    // Start over with a new duration (e.g. a config change between sessions).
    public void Restart(TimeSpan duration)
    {
        _duration = Clamp(duration);
        _banked = TimeSpan.Zero;
        _runningSince = _now();
    }

    // Seconds -> "m:ss" / "h:mm:ss". Static so the screen and tests can format without an instance.
    public static string Format(int totalSeconds)
    {
        if (totalSeconds < 0)
            totalSeconds = 0;

        var time = TimeSpan.FromSeconds(totalSeconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    static TimeSpan Clamp(TimeSpan duration) => duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
}
