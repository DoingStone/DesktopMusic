using System.Drawing;
using System.Drawing.Imaging;

namespace TaskbarLyrics.IconGen;

/// <summary>
/// Converts flat-background line art into a white alpha mask.
/// <para>
/// The source is teal strokes on pure white. Simply copying it would carry a white
/// rectangle into the icon, which then cannot be placed on a coloured plate, cannot
/// be thickened without the white hiding earlier passes, and shows square corners.
/// Extracting the shape into alpha solves all three: the result is a white image
/// whose alpha is the stroke coverage, so it can be tinted with one colour matrix
/// and composited repeatedly.
/// </para>
/// </summary>
internal static class LineArt
{
    /// <summary>
    /// A pixel this far from the background counts as inked. Also the minimum noise
    /// floor, so an almost-flat background cannot produce a visible wash.
    /// </summary>
    private const int MinInkFloor = 26;

    /// <summary>
    /// Extract the mask. Also reports the artwork's line colour and the tight
    /// bounds of the inked area, so callers can centre and scale it precisely.
    /// </summary>
    public static Bitmap Extract(Bitmap source, out Color lineColor, out RectangleF contentBounds)
    {
        int w = source.Width;
        int h = source.Height;

        // Assume the background is whatever the corners are.
        var background = AverageOfCorners(source);

        // The ink colour is the most saturated pixel: the stroke core.
        lineColor = FindInkColor(source, background);

        // Ink depth = how far the strokes depart from the background.
        int inkDepth = InkDepth(background, lineColor);
        if (inkDepth < MinInkFloor) inkDepth = MinInkFloor;

        // Discard the background's own noise before mapping coverage.
        //
        // The source is JPEG-derived, so "white" varies by a few levels and there is
        // ringing around every stroke. Without a floor those tiny deviations become
        // a faint low-alpha wash across the whole plate. Rescaling from the floor
        // keeps the anti-aliased stroke edges while dropping the haze to nothing.
        int floor = Math.Max(MinInkFloor, inkDepth / 8);
        int span = Math.Max(1, inkDepth - floor);

        var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        int minX = w, minY = h, maxX = -1, maxY = -1;

        var data = mask.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;
            var pixels = new byte[stride * h];

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;

                for (int x = 0; x < w; x++)
                {
                    var c = source.GetPixel(x, y);

                    // Coverage from the channel that departs most from the background.
                    int depth = Math.Max(
                        Math.Abs(c.R - background.R),
                        Math.Max(Math.Abs(c.G - background.G), Math.Abs(c.B - background.B)));

                    int alpha = (depth - floor) * 255 / span;
                    if (alpha < 0) alpha = 0;
                    if (alpha > 255) alpha = 255;

                    int i = row + (x * 4);
                    // B, G, R, A — white with coverage in alpha.
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = (byte)alpha;

                    if (alpha > 24)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            mask.UnlockBits(data);
        }

        contentBounds = maxX < 0
            ? new RectangleF(0, 0, w, h)
            : new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return mask;
    }

    /// <summary>Background colour, taken as the average of the four corners.</summary>
    private static Color AverageOfCorners(Bitmap source)
    {
        int w = source.Width - 1;
        int h = source.Height - 1;

        var samples = new[]
        {
            source.GetPixel(0, 0),
            source.GetPixel(w, 0),
            source.GetPixel(0, h),
            source.GetPixel(w, h),
        };

        return Color.FromArgb(
            samples.Sum(c => c.R) / samples.Length,
            samples.Sum(c => c.G) / samples.Length,
            samples.Sum(c => c.B) / samples.Length);
    }

    /// <summary>
    /// The stroke colour, averaged over the deepest pixels.
    /// <para>
    /// Taking the single darkest pixel is wrong here: the source is
    /// JPEG-derived, so its darkest samples are compression artifacts at stroke
    /// edges and came out noticeably darker and duller than the real ink. Averaging
    /// the solid stroke cores recovers the intended colour.
    /// </para>
    /// </summary>
    private static Color FindInkColor(Bitmap source, Color background)
    {
        // Pass 1: how deep does the ink go?
        int maxDepth = 0;
        for (int y = 0; y < source.Height; y += 2)
        {
            for (int x = 0; x < source.Width; x += 2)
            {
                var c = source.GetPixel(x, y);
                int depth = Depth(c, background);
                if (depth > maxDepth) maxDepth = depth;
            }
        }

        if (maxDepth <= 0) return Color.Black;

        // Pass 2: average everything at least 70% inked, i.e. the stroke body.
        int threshold = maxDepth * 70 / 100;
        long r = 0, g = 0, b = 0, n = 0;

        for (int y = 0; y < source.Height; y += 2)
        {
            for (int x = 0; x < source.Width; x += 2)
            {
                var c = source.GetPixel(x, y);
                if (Depth(c, background) < threshold) continue;

                r += c.R;
                g += c.G;
                b += c.B;
                n++;
            }
        }

        if (n == 0) return Color.Black;

        return Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>Total departure from the background across the three channels.</summary>
    private static int Depth(Color c, Color background) =>
        Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);

    /// <summary>How far the ink departs from the background on its strongest channel.</summary>
    private static int InkDepth(Color background, Color ink) => Math.Max(
        Math.Abs(ink.R - background.R),
        Math.Max(Math.Abs(ink.G - background.G), Math.Abs(ink.B - background.B)));
}
