namespace FigureDrawing.Core;

// The validated configuration a session runs with: how long each reference image is shown, how many
// images to show in total, and how long to rest between poses. Produced by SessionSetup once both
// inputs are valid; consumed by the session engine (FD-003).
//
// BreakSeconds is 0 for "no break" — the design's default chip row offers None/5s/15s/1m. It is a
// pace setting rather than a validated input: any non-negative value is legal, so it never gates
// Start.
public readonly record struct SessionConfig(int SecondsPerImage, int ImageCount, int BreakSeconds = 0);

// FD-002 session-setup logic, pure so it is unit-testable without Android. The setup screen feeds
// it the raw text from the two EditText inputs plus whether a folder is currently selected, and it
// answers: are the inputs valid, may the session start, and (if so) what config to hand off.
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

    // Evaluates the current setup state from the raw inputs. The Android layer calls this on every
    // keystroke to drive the Start button's enabled state, and again on Start to read Config.
    public static SessionSetupState Evaluate(
        string? secondsText, string? countText, bool folderSelected, int breakSeconds = DefaultBreakSeconds) =>
        new(ParsePositive(secondsText), ParsePositive(countText), folderSelected, Math.Max(0, breakSeconds));

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

// The outcome of evaluating the setup inputs. SecondsPerImage/ImageCount are null when their input
// is missing or invalid.
public sealed record SessionSetupState(
    int? SecondsPerImage, int? ImageCount, bool FolderSelected,
    int BreakSeconds = SessionSetup.DefaultBreakSeconds)
{
    public bool SecondsValid => SecondsPerImage is int s && SessionSetup.IsValidSeconds(s);
    public bool CountValid => ImageCount is int c && SessionSetup.IsValidCount(c);

    // Start is enabled only once a folder is selected AND both inputs are valid (acceptance
    // criteria: "Start is disabled until a folder is selected and inputs are valid").
    public bool CanStart => FolderSelected && SecondsValid && CountValid;

    // The config to hand to the session engine, or null while the setup is not startable.
    public SessionConfig? Config =>
        CanStart ? new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds) : null;

    // Estimated length of the session described by these inputs, in seconds. Unlike Config this is
    // available before the inputs are startable (a missing folder still lets the pace be estimated);
    // it reads 0 while either number is invalid.
    public int EstimateSeconds =>
        SecondsValid && CountValid
            ? SessionSetup.EstimateSeconds(
                new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value, BreakSeconds))
            : 0;
}
