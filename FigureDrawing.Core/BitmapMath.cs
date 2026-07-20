namespace FigureDrawing.Core;

// Pure, testable image-scaling math used when decoding reference images. Loading full-resolution
// photos into a scrolling list is what makes the app run out of memory; picking a power-of-two
// sub-sample factor bounds each decoded bitmap to roughly the requested size.
public static class BitmapMath
{
    // Largest power-of-two sample size that keeps the down-scaled image at or above reqWidth x
    // reqHeight (BitmapFactory only honours power-of-two inSampleSize values). Returns 1 for any
    // non-positive input, i.e. "decode at full size".
    public static int CalculateInSampleSize(int srcWidth, int srcHeight, int reqWidth, int reqHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || reqWidth <= 0 || reqHeight <= 0)
            return 1;

        var sampleSize = 1;
        while (srcHeight / (sampleSize * 2) >= reqHeight && srcWidth / (sampleSize * 2) >= reqWidth)
            sampleSize *= 2;

        return sampleSize;
    }
}
