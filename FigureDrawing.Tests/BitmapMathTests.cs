using FigureDrawing.Core;

namespace FigureDrawing.Tests;

// Sample-size math that bounds decoded image memory (the fix for the folder-of-photos crash).
public class BitmapMathTests
{
    [Theory]
    // Already small enough -> no downsample.
    [InlineData(800, 600, 1080, 1080, 1)]
    [InlineData(1080, 1080, 1080, 1080, 1)]
    // 4000x4000 photo into a 1080 box -> /2 (2000) still >=1080, /4 (1000) < 1080 -> stop at 2.
    [InlineData(4000, 4000, 1080, 1080, 2)]
    // 8000x6000 -> 2 (4000/3000) ok, 4 (2000/1500) ok, 8 (1000/750) < 1080 -> 4.
    [InlineData(8000, 6000, 1080, 1080, 4)]
    // Very large -> larger factor.
    [InlineData(20000, 20000, 1080, 1080, 16)]
    public void CalculateInSampleSize_PicksBoundedPowerOfTwo(int sw, int sh, int rw, int rh, int expected) =>
        Assert.Equal(expected, BitmapMath.CalculateInSampleSize(sw, sh, rw, rh));

    [Theory]
    [InlineData(0, 100, 1080, 1080)]
    [InlineData(100, 0, 1080, 1080)]
    [InlineData(100, 100, 0, 1080)]
    [InlineData(-1, -1, -1, -1)]
    public void CalculateInSampleSize_NonPositiveInput_ReturnsOne(int sw, int sh, int rw, int rh) =>
        Assert.Equal(1, BitmapMath.CalculateInSampleSize(sw, sh, rw, rh));

    [Fact]
    public void CalculateInSampleSize_ResultIsAlwaysPowerOfTwo()
    {
        for (var dim = 100; dim <= 20000; dim += 137)
        {
            var s = BitmapMath.CalculateInSampleSize(dim, dim, 1080, 1080);
            Assert.True((s & (s - 1)) == 0, $"sample size {s} for {dim} is not a power of two");
        }
    }
}
