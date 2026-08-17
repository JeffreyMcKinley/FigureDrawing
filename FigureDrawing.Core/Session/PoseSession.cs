namespace FigureDrawing.Core;

// Which half of the pace the player screen is in right now.
public enum PosePhase
{
    // A reference image is on screen and its countdown is draining.
    Pose,

    // The configured rest between two poses. The next image is already loaded underneath; the screen
    // covers it with the break overlay until the rest is over.
    Break,

    // The session is over — completed, ended early, or the pool proved undisplayable.
    Complete
}

// The player-screen aggregate: one object that owns "which image is on screen, how long it has
// left, and what happens when that reaches zero". It composes the three pieces that used to be
// sequenced by SessionActivity — DrawingSession (counts and time), SessionPlayer (id -> image,
// unreadable-skip) and PoseCountdown (remaining time, pause/resume) — and adds the break phase.
//
// This exists because the pairing "advance the pose AND restart the clock" is a domain rule, and it
// was previously written inside the Activity (docs/ARCHITECTURE.md §17/§20.2). With the break phase
// it became a three-state machine, which is exactly the point at which it has to move into Core.
// The screen is left with a repaint loop, lifecycle calls, and rendering.
//
// Generic over the image type for the same reason SessionPlayer is: it keeps Bitmap out of Core.
public sealed class PoseSession<TImage> where TImage : class
{
    readonly DrawingSession _session;
    readonly SessionPlayer<TImage> _player;
    readonly PoseCountdown _countdown;
    readonly TimeSpan _poseDuration;
    readonly TimeSpan _breakDuration;

    // session      : the FD-003 engine, already positioned on its first image.
    // load         : resolve an image id to a displayable image, or null when it can't be decoded.
    // onUnreadable : optional hook to log an image skipped because it would not decode.
    // breakSeconds : rest between poses; 0 runs one pose straight into the next.
    // clock        : injectable monotonic time source for deterministic tests (§4).
    public PoseSession(
        DrawingSession session,
        Func<string, TImage?> load,
        Action<string>? onUnreadable = null,
        int breakSeconds = 0,
        Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(load);

        _session = session;
        _player = new SessionPlayer<TImage>(session, load, onUnreadable);
        _poseDuration = TimeSpan.FromSeconds(Math.Max(0, session.SecondsPerImage));
        _breakDuration = TimeSpan.FromSeconds(Math.Max(0, breakSeconds));
        _countdown = new PoseCountdown(_poseDuration, clock);

        // An empty pool, a zero count, or an entirely undisplayable pool finishes before the first
        // repaint ever runs.
        if (_player.IsComplete)
            Phase = PosePhase.Complete;
    }

    public PosePhase Phase { get; private set; } = PosePhase.Pose;

    // The image to draw right now, or null once the session is over. During a break this is already
    // the *next* pose's image — the screen covers it with the break overlay.
    public TImage? CurrentImage => _player.CurrentImage;

    public bool IsComplete => Phase == PosePhase.Complete;

    // True on a break, so the screen knows to show the rest overlay instead of the pose.
    public bool OnBreak => Phase == PosePhase.Break;

    // The session ended because nothing in the pool could be decoded — an error state, not a normal
    // completion.
    public bool CouldNotDisplayImage => _player.CouldNotDisplayImage;

    public bool IsPaused => _countdown.IsPaused;

    // Time left in the current phase and how it reads on screen.
    public TimeSpan Remaining => _countdown.Remaining;
    public string Display => _countdown.Display;

    // How much of the current phase is still to run, 0-100. The progress ring is drawn from this, so
    // it starts full and empties as the pose runs out.
    public int RemainingPercent
    {
        get
        {
            var duration = _countdown.Duration;
            if (duration <= TimeSpan.Zero)
                return 0;

            var fraction = Remaining.TotalSeconds / duration.TotalSeconds;
            return (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        }
    }

    public int CompletedCount => _session.CompletedCount;
    public int SkippedCount => _session.SkippedCount;
    public int TargetCount => _session.TargetCount;

    // Which pose the drawer is on, 1-based, for "Image 3 of 12". Stays at the target once the last
    // pose is running rather than reading one past it.
    public int CurrentPoseNumber => Math.Min(CompletedCount + 1, Math.Max(1, TargetCount));

    public SessionSummary Summary => _session.Summary;

    // Repaint-loop hook: hand the clock a chance to expire the current phase. Safe to call at any
    // cadence — the countdown reads a monotonic clock, so a slow or dropped tick cannot change how
    // much time a pose gets. Returns true when the phase changed, which is the screen's cue to
    // repaint the image rather than just the timer.
    public bool Tick()
    {
        if (Phase == PosePhase.Complete || _countdown.IsPaused || !_countdown.IsExpired)
            return false;

        if (Phase == PosePhase.Break)
        {
            StartPose();
            return true;
        }

        CompletePose();
        return true;
    }

    // Timer expiry or a manual "done" tap: count this pose and move on (through the break, if one is
    // configured). No-op once complete.
    public void Next()
    {
        if (Phase == PosePhase.Complete)
            return;

        CompletePose();
    }

    // Leave this image without counting it. A skip always lands straight on the next pose — resting
    // after an image the drawer did not want is not the point of the break.
    public void Skip()
    {
        if (Phase == PosePhase.Complete)
            return;

        _player.Skip();

        if (_player.IsComplete)
            Phase = PosePhase.Complete;
        else
            StartPose();
    }

    // End early: bank the current pose's partial time and stop. The summary is readable afterwards.
    public void End()
    {
        if (Phase == PosePhase.Complete)
            return;

        _player.End();
        Phase = PosePhase.Complete;
    }

    // Lifecycle: freeze both clocks while the screen is hidden or the pause overlay is up, so a
    // backgrounded app burns no pose time, cannot fire a timer while it is not on screen, and does
    // not bank the time it spent away as drawing time.
    public void Pause()
    {
        _countdown.Pause();
        _session.Pause();
    }

    public void Resume()
    {
        if (Phase == PosePhase.Complete)
            return;

        _countdown.Resume();

        // A break runs its own clock; the session's stays stopped until the next pose starts.
        if (Phase != PosePhase.Break)
            _session.Resume();
    }

    void CompletePose()
    {
        _player.Next();

        if (_player.IsComplete)
        {
            Phase = PosePhase.Complete;
            return;
        }

        if (_breakDuration > TimeSpan.Zero)
        {
            Phase = PosePhase.Break;

            // The engine has already moved to the next image, so its clock is running against a pose
            // nobody is drawing yet. Rest is not drawing time — stop it until the break is over.
            _session.Pause();
            _countdown.Restart(_breakDuration);
            return;
        }

        StartPose();
    }

    void StartPose()
    {
        Phase = PosePhase.Pose;
        _session.Resume();
        _countdown.Restart(_poseDuration);
    }
}
