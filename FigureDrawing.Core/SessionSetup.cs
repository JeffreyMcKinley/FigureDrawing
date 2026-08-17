namespace FigureDrawing.Core;

// The validated configuration a session runs with: how long each reference image is shown, how many
// images to show in total, and how long to rest between poses. Produced by SessionSetup once both
// inputs are valid; consumed by the session engine (FD-003).
//
// BreakSeconds is 0 for "no break" — the design's default chip row offers None/5s/15s/1m. It is a
// pace setting rather than a validated input: any non-negative value is legal, so it never gates
// Start.
public readonly record struct SessionConfig(int SecondsPerImage, int ImageCount, int BreakSeconds = 0);

// FD-002 session-setup logic, pure so it is unit-testable without Android: parsing, validity, the
// presets the chips render, and how long a configured session runs.
//
// The evaluated *state* of the setup screen is not here — it is a draft DrawingSession
// (DrawingSession<TImage>.Evaluate), because a session that has not started yet is what the setup
// screen is showing. See docs/DOMAIN-MODEL.md §3.1 and §9.
public static class SessionSetup
{
    // Seeded into the inputs on first run, before any settings have been persisted.
    public const int DefaultSecondsPerImage = 30;
    public const int DefaultImageCount = 20;
    public const int DefaultBreakSeconds = 0;

    // The quick-pick chip rows on the setup screen. The order here is the order the chips render in,
    // so the screen never hardcodes the values it binds — it walks these.
    public static readonly IReadOnlyList<int> SecondsPresets = new[] { 30, 60, 120, 300 };
    public static readonly IReadOnlyList<int> BreakPresets = new[] { 0, 5, 15, 60 };

    // Both values must be strictly positive (acceptance criteria: "validated > 0").
    public static bool IsValidSeconds(int seconds) => seconds > 0;
    public static bool IsValidCount(int count) => count > 0;

    // Parses an EditText's text into a positive integer, or null when it is blank, non-numeric, or
    // not > 0. Leading/trailing whitespace is tolerated so a stray space doesn't disable Start.
    public static int? ParsePositive(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.Trim(), out var value) && value > 0 ? value : null;
    }

    // How long the whole session takes end to end: every pose plus a break between each adjacent
    // pair (there is no break after the last pose). Drives the "About 12:30 including breaks" line
    // under the Start button.
    public static int EstimateSeconds(SessionConfig config)
    {
        var seconds = Math.Max(0, config.SecondsPerImage);
        var count = Math.Max(0, config.ImageCount);
        var breaks = Math.Max(0, config.BreakSeconds);

        return seconds * count + breaks * Math.Max(0, count - 1);
    }
}
