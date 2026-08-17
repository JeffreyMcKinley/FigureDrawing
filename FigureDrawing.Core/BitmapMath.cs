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

    // Largest power-of-two sample size that keeps the LONGEST side at or above maxDimension. This is
    // the memory ceiling: unlike CalculateInSampleSize it holds whatever the aspect ratio is, so a
    // panorama cannot decode at full width. Returns 1 for any non-positive input.
    public static int CalculateBoundedSampleSize(int srcWidth, int srcHeight, int maxDimension)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || maxDimension <= 0)
            return 1;

        var sampleSize = 1;
        while (Math.Max(srcWidth, srcHeight) / (sampleSize * 2) >= maxDimension)
            sampleSize *= 2;

        return sampleSize;
    }

    // What the decoders actually use. A center-cropped tile wants its SHORT side at or above the
    // request (no upscaling), but an aspect-extreme source must not blow the heap doing it: take the
    // crop-quality factor, then keep doubling until the long side fits maxDimension. Math.Max of two
    // powers of two is a power of two, so BitmapFactory's inSampleSize contract still holds.
    public static int CalculateCropSampleSize(int srcWidth, int srcHeight, int request, int maxDimension) =>
        Math.Max(CalculateInSampleSize(srcWidth, srcHeight, request, request),
                 CalculateBoundedSampleSize(srcWidth, srcHeight, maxDimension));
}
