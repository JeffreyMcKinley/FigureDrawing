using Android.Content;
using Android.Graphics;
using FigureDrawing.Core;

namespace FigureDrawing
{
    // Shared reference-image decoding used by both the folder preview (MainActivity) and the session
    // player (SessionActivity). Real photos are far larger than the screen, so it down-samples: the
    // first pass reads only the bounds, the second decodes at the computed sample size, keeping a
    // folder of full-resolution images within memory. Returns null when the uri can't be decoded.
    internal static class ImageDecoding
    {
        public static Bitmap? DecodeSampledBitmap(
            ContentResolver resolver, Android.Net.Uri uri, int reqWidth, int reqHeight)
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            using (var stream = resolver.OpenInputStream(uri))
                BitmapFactory.DecodeStream(stream, null, bounds);

            var options = new BitmapFactory.Options
            {
                InSampleSize = BitmapMath.CalculateInSampleSize(
                    bounds.OutWidth, bounds.OutHeight, reqWidth, reqHeight),
            };

            using var decodeStream = resolver.OpenInputStream(uri);
            return BitmapFactory.DecodeStream(decodeStream, null, options);
        }
    }
}
