using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// FD-002 session-setup: the pure parsing, validity and pacing helpers behind the setup screen. The
// evaluated state of that screen is a draft session and is tested in DrawingSessionSetupTests.
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
    public void Defaults_ArePositive()
    {
        Assert.True(SessionSetup.IsValidSeconds(SessionSetup.DefaultSecondsPerImage));
        Assert.True(SessionSetup.IsValidCount(SessionSetup.DefaultImageCount));
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
