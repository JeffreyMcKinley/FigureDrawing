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
}
