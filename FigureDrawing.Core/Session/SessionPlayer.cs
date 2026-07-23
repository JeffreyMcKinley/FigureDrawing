namespace FigureDrawing.Core;

// FD-004 session player: the UI-independent brain of the drawing screen. FD-003's DrawingSession
// owns the sequence/count/time; the screen still needs the one concern the engine deliberately does
// not have — turning the session's current image id into something displayable, and coping when an
// image is unreadable/broken (acceptance criteria: "handles unreadable/broken image URIs gracefully
// — skip + log").
//
// This class owns exactly that: it resolves the session's current image id through an injected
// loader, and on failure skips past the broken image (which, per FD-006 semantics, does NOT count
// toward the total) until one loads, the session completes, or a bounded number of consecutive
// failures proves the pool undisplayable. It is generic over the image type and Android-free, so it
// is fully unit-testable with a fake loader; the Android screen supplies a Bitmap loader.
public sealed class SessionPlayer<TImage> where TImage : class
{
    readonly DrawingSession _session;
    readonly Func<string, TImage?> _load;
    readonly Action<string>? _onUnreadable;
    readonly int _maxConsecutiveFailures;

    // session   : the FD-003 engine, already positioned on its first image.
    // load      : resolve an image id to a displayable image, or null if it can't be read/decoded.
    // onUnreadable : optional hook to log a skipped-because-broken image id (Android Log.Warn).
    // maxConsecutiveFailures : upper bound on consecutive unreadable images before giving up, so a
    //             folder of all-broken images can't loop forever (the pool repeats when the
    //             configured count exceeds the pool size).
    public SessionPlayer(
        DrawingSession session,
        Func<string, TImage?> load,
        Action<string>? onUnreadable = null,
        int maxConsecutiveFailures = 100)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(load);

        _session = session;
        _load = load;
        _onUnreadable = onUnreadable;
        _maxConsecutiveFailures = Math.Max(1, maxConsecutiveFailures);

        Resolve();
    }

    // The image to show right now, or null when the session is complete or nothing could be displayed.
    public TImage? CurrentImage { get; private set; }

    // True once the underlying session has ended: the configured count was reached, End() was called,
    // or the pool proved undisplayable (see CouldNotDisplayImage).
    public bool IsComplete => _session.IsComplete;

    // True when the session ended because it could not find another displayable image (the loader
    // failed maxConsecutiveFailures times in a row). Distinct from a normal completion so the screen
    // can show an error instead of a blank/summary state.
    public bool CouldNotDisplayImage { get; private set; }

    // Snapshot for the summary screen (FD-007): images completed + time drawn.
    public SessionSummary Summary => _session.Summary;

    // Advance on timer expiry / a "done" tap: count the current image toward the total, then resolve
    // the next displayable image. No-op once complete.
    public void Next()
    {
        _session.Next();
        Resolve();
    }

    // Skip the current image (does not count toward the total), then resolve the next displayable one.
    public void Skip()
    {
        _session.Skip();
        Resolve();
    }

    // End the session early (FD-007): stop and clear the current image.
    public void End()
    {
        _session.End();
        CurrentImage = null;
    }

    // Resolve the session's current image id to a displayable image, skipping past unreadable ones
    // until one loads, the session completes on its own, or the failure budget is exhausted.
    void Resolve()
    {
        var failures = 0;
        while (!_session.IsComplete && _session.CurrentImage is { } id)
        {
            var image = _load(id);
            if (image is not null)
            {
                CurrentImage = image;
                return;
            }

            _onUnreadable?.Invoke(id);

            if (++failures >= _maxConsecutiveFailures)
            {
                CouldNotDisplayImage = true;
                _session.End();
                break;
            }

            _session.Skip();
        }

        CurrentImage = null;
    }
}
