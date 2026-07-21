namespace FigureDrawing.Core;

// The validated configuration a session runs with: how long each reference image is shown and how
// many images to show in total. Produced by SessionSetup once both inputs are valid; consumed by
// the session engine (FD-003).
public readonly record struct SessionConfig(int SecondsPerImage, int ImageCount);

// FD-002 session-setup logic, pure so it is unit-testable without Android. The setup screen feeds
// it the raw text from the two EditText inputs plus whether a folder is currently selected, and it
// answers: are the inputs valid, may the session start, and (if so) what config to hand off.
public static class SessionSetup
{
    // Seeded into the inputs on first run, before any settings have been persisted.
    public const int DefaultSecondsPerImage = 30;
    public const int DefaultImageCount = 20;

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
    public static SessionSetupState Evaluate(string? secondsText, string? countText, bool folderSelected) =>
        new(ParsePositive(secondsText), ParsePositive(countText), folderSelected);
}

// The outcome of evaluating the setup inputs. SecondsPerImage/ImageCount are null when their input
// is missing or invalid.
public sealed record SessionSetupState(int? SecondsPerImage, int? ImageCount, bool FolderSelected)
{
    public bool SecondsValid => SecondsPerImage is int s && SessionSetup.IsValidSeconds(s);
    public bool CountValid => ImageCount is int c && SessionSetup.IsValidCount(c);

    // Start is enabled only once a folder is selected AND both inputs are valid (acceptance
    // criteria: "Start is disabled until a folder is selected and inputs are valid").
    public bool CanStart => FolderSelected && SecondsValid && CountValid;

    // The config to hand to the session engine, or null while the setup is not startable.
    public SessionConfig? Config =>
        CanStart ? new SessionConfig(SecondsPerImage!.Value, ImageCount!.Value) : null;
}
