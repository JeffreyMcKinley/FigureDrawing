using System.Diagnostics;

namespace FigureDrawing.Core;

// A finished-session snapshot for the summary screen (FD-007): how many images counted toward the
// total and how long was spent drawing them. TotalDrawingTime excludes time spent on skipped images.
public readonly record struct SessionSummary(int ImagesDisplayed, TimeSpan TotalDrawingTime);

// FD-003 session engine. A UI-independent model that owns the state of one drawing session: the
// random image sequence, the remaining-count, skip semantics, and elapsed-time accounting. The
// Android screens (FD-004..FD-007) drive it (Next/Skip/End) and observe it (CurrentImage/Remaining/
// IsComplete/Summary); it has no Android dependency so it is fully unit-testable.
//
// Open questions resolved:
//  - Pool smaller than the requested count: images REPEAT (the configured count is honored). Each
//    pass through the pool is a fresh shuffle, so every image is shown once before any repeats.
//  - "Time spent drawing" EXCLUDES skipped images' partial time (Skip does not accumulate).
public sealed class DrawingSession
{
    readonly List<string> _pool;
    readonly bool _shuffle;
    readonly Random _random;
    readonly Func<TimeSpan> _now;
    readonly int _targetCount;

    // Upcoming images for the current pass through the pool; refilled (reshuffled) when drained.
    readonly Queue<string> _upcoming = new();

    TimeSpan _accumulatedDrawingTime;
    TimeSpan _currentImageStart;

    // pool     : image URIs/document IDs to draw from (FD-001 output).
    // config   : validated seconds-per-image + total image count (FD-002 output).
    // shuffle  : AppSettings.ShuffleImages — random order when true, pool order when false.
    // random   : injectable for deterministic tests; defaults to a shared Random.
    // clock    : injectable monotonic time source for deterministic tests; defaults to a Stopwatch.
    public DrawingSession(
        IReadOnlyList<string> pool,
        SessionConfig config,
        bool shuffle = true,
        Random? random = null,
        Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        _pool = new List<string>(pool);
        _shuffle = shuffle;
        _random = random ?? Random.Shared;
        _targetCount = Math.Max(0, config.ImageCount);
        SecondsPerImage = config.SecondsPerImage;

        var stopwatch = Stopwatch.StartNew();
        _now = clock ?? (() => stopwatch.Elapsed);

        // Load the first image (or complete immediately for an empty pool / zero count).
        Advance();
    }

    // Configured display time per image; the timer that drives Next() runs for this long (FD-004).
    public int SecondsPerImage { get; }

    // Total number of images this session will count toward completion.
    public int TargetCount => _targetCount;

    // The image currently displayed, or null once the session is complete.
    public string? CurrentImage { get; private set; }

    // Images completed (counted toward the total). Skips do not increment this.
    public int CompletedCount { get; private set; }

    // Images still to complete before the session ends. Never negative.
    public int Remaining => Math.Max(0, _targetCount - CompletedCount);

    // True once the configured count is reached or End() was called; further ops are no-ops.
    public bool IsComplete { get; private set; }

    // Advance on timer expiry OR a manual "done" tap: counts the current image toward the total,
    // banks its drawing time, and moves to the next image (completing the session at the target).
    // No-op once complete.
    public void Next()
    {
        if (IsComplete || CurrentImage is null)
            return;

        _accumulatedDrawingTime += CurrentElapsed();
        CompletedCount++;

        if (CompletedCount >= _targetCount)
        {
            Finish();
            return;
        }

        Advance();
    }

    // Skip the current image (FD-006): move to a different image WITHOUT counting it toward the
    // total and WITHOUT banking its partial drawing time. No-op once complete.
    public void Skip()
    {
        if (IsComplete || CurrentImage is null)
            return;

        Advance();
    }

    // End the session early (FD-007): bank the current image's partial drawing time and stop. The
    // current image is NOT counted toward the total (it was not completed). No-op once complete.
    public void End()
    {
        if (IsComplete)
            return;

        if (CurrentImage is not null)
            _accumulatedDrawingTime += CurrentElapsed();

        Finish();
    }

    // Snapshot for the summary screen. TotalDrawingTime reflects completed (and, after End(), the
    // final partial) images only — never skipped time.
    public SessionSummary Summary => new(CompletedCount, _accumulatedDrawingTime);

    // Time spent on the image currently displayed, from the monotonic clock.
    TimeSpan CurrentElapsed() => _now() - _currentImageStart;

    // Move to the next image and (re)start its timer, or finish if the pool is empty.
    void Advance()
    {
        if (_targetCount <= 0 || _pool.Count == 0)
        {
            Finish();
            return;
        }

        if (_upcoming.Count == 0)
            Refill();

        CurrentImage = _upcoming.Dequeue();
        _currentImageStart = _now();
    }

    // Rebuild the upcoming queue for a fresh pass through the pool, shuffled when configured.
    void Refill()
    {
        var pass = new List<string>(_pool);

        if (_shuffle)
        {
            // Fisher-Yates with the injected Random for deterministic, unbiased shuffling.
            for (var i = pass.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (pass[i], pass[j]) = (pass[j], pass[i]);
            }
        }

        foreach (var image in pass)
            _upcoming.Enqueue(image);
    }

    void Finish()
    {
        IsComplete = true;
        CurrentImage = null;
    }
}
