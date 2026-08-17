using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-002 session-setup: the pure validation/parsing that backs the setup screen (seconds-per-image,
// image-count, and the Start gate). The Android EditText/Button wiring is exercised e2e; this is the
// testable core.
public class SessionSetupTests
{
    [Theory]
    [InlineData("30", 30)]
    [InlineData("1", 1)]
    [InlineData("  45  ", 45)]     // surrounding whitespace tolerated
    [InlineData("0", null)]        // not > 0
    [InlineData("-5", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("3.5", null)]      // not an integer
    public void ParsePositive_ParsesOnlyPositiveIntegers(string? raw, int? expected) =>
        Assert.Equal(expected, SessionSetup.ParsePositive(raw));

    [Theory]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidSeconds_RequiresPositive(int seconds, bool expected) =>
        Assert.Equal(expected, SessionSetup.IsValidSeconds(seconds));

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void IsValidCount_RequiresPositive(int count, bool expected) =>
        Assert.Equal(expected, SessionSetup.IsValidCount(count));

    [Fact]
    public void Evaluate_AllValidAndFolderSelected_CanStartWithConfig()
    {
        var state = SessionSetup.Evaluate("45", "12", folderSelected: true);

        Assert.True(state.SecondsValid);
        Assert.True(state.CountValid);
        Assert.True(state.CanStart);
        Assert.Equal(new SessionConfig(45, 12), state.Config);
    }

    [Fact]
    public void Evaluate_NoFolder_CannotStartEvenWhenInputsValid()
    {
        var state = SessionSetup.Evaluate("30", "20", folderSelected: false);

        Assert.True(state.SecondsValid);
        Assert.True(state.CountValid);
        Assert.False(state.CanStart);
        Assert.Null(state.Config);
    }

    [Theory]
    [InlineData("0", "20")]    // seconds invalid
    [InlineData("30", "0")]    // count invalid
    [InlineData("", "20")]     // seconds blank
    [InlineData("30", "")]     // count blank
    [InlineData("x", "y")]     // both non-numeric
    public void Evaluate_InvalidInput_CannotStart(string secondsText, string countText)
    {
        var state = SessionSetup.Evaluate(secondsText, countText, folderSelected: true);

        Assert.False(state.CanStart);
        Assert.Null(state.Config);
    }

    [Fact]
    public void Evaluate_ReportsPerFieldValidityIndependently()
    {
        var state = SessionSetup.Evaluate("30", "0", folderSelected: true);

        Assert.True(state.SecondsValid);
        Assert.False(state.CountValid);
    }

    [Fact]
    public void Defaults_ArePositive()
    {
        Assert.True(SessionSetup.IsValidSeconds(SessionSetup.DefaultSecondsPerImage));
        Assert.True(SessionSetup.IsValidCount(SessionSetup.DefaultImageCount));
    }

    // --- Break between poses -------------------------------------------------

    [Fact]
    public void Evaluate_CarriesTheBreakIntoTheConfig()
    {
        var state = SessionSetup.Evaluate("45", "12", folderSelected: true, breakSeconds: 15);

        Assert.Equal(15, state.BreakSeconds);
        Assert.Equal(new SessionConfig(45, 12, 15), state.Config);
    }

    // The break is a pace setting, not a validated input — "no break" is a legitimate choice, so it
    // must never be able to close the Start gate.
    [Fact]
    public void Evaluate_NoBreak_StillStarts()
    {
        var state = SessionSetup.Evaluate("45", "12", folderSelected: true, breakSeconds: 0);

        Assert.True(state.CanStart);
        Assert.Equal(0, state.Config!.Value.BreakSeconds);
    }

    [Fact]
    public void Evaluate_NegativeBreak_IsFlooredAtZero()
    {
        var state = SessionSetup.Evaluate("45", "12", folderSelected: true, breakSeconds: -30);

        Assert.Equal(0, state.BreakSeconds);
    }

    [Fact]
    public void Evaluate_OmittedBreak_DefaultsToNone()
    {
        var state = SessionSetup.Evaluate("45", "12", folderSelected: true);

        Assert.Equal(SessionSetup.DefaultBreakSeconds, state.BreakSeconds);
    }

    // --- Session length estimate ---------------------------------------------

    [Theory]
    [InlineData(30, 12, 0, 360)]        // 12 * 30s, no breaks
    [InlineData(60, 10, 15, 735)]       // 10 * 60s + 9 breaks of 15s
    [InlineData(30, 1, 60, 30)]         // a single pose has no break after it
    [InlineData(30, 0, 15, 0)]          // nothing to draw, nothing to estimate
    public void EstimateSeconds_CountsBreaksBetweenPosesOnly(
        int seconds, int count, int breakSeconds, int expected) =>
        Assert.Equal(expected, SessionSetup.EstimateSeconds(new SessionConfig(seconds, count, breakSeconds)));

    // The estimate is shown under the Start button, which is visible before a folder is picked.
    [Fact]
    public void StateEstimate_IsAvailableBeforeAFolderIsPicked()
    {
        var state = SessionSetup.Evaluate("60", "10", folderSelected: false, breakSeconds: 15);

        Assert.False(state.CanStart);
        Assert.Equal(735, state.EstimateSeconds);
    }

    [Fact]
    public void StateEstimate_IsZeroWhileAnInputIsInvalid()
    {
        Assert.Equal(0, SessionSetup.Evaluate("", "10", folderSelected: true).EstimateSeconds);
        Assert.Equal(0, SessionSetup.Evaluate("60", "x", folderSelected: true).EstimateSeconds);
    }

    // --- Quick-pick chips ----------------------------------------------------

    // The setup screen renders one chip per preset, so the presets must stay usable inputs.
    [Fact]
    public void SecondsPresets_AreAllValidDurations()
    {
        Assert.NotEmpty(SessionSetup.SecondsPresets);
        Assert.All(SessionSetup.SecondsPresets, s => Assert.True(SessionSetup.IsValidSeconds(s)));
        Assert.Contains(SessionSetup.DefaultSecondsPerImage, SessionSetup.SecondsPresets);
    }

    [Fact]
    public void BreakPresets_StartAtNone_AndAreNeverNegative()
    {
        Assert.Equal(0, SessionSetup.BreakPresets[0]);
        Assert.All(SessionSetup.BreakPresets, b => Assert.True(b >= 0));
    }
}
