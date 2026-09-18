using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace TaskbarLyrics.IconGen;

/// <summary>
/// Builds the application icon from the source line art in
/// <c>Assets/IconSource.png</c> (teal gear + note + sound bars on white).
/// <para>
/// The artwork is not pasted in as a white square. It is first converted into an
/// alpha mask, so the lines can sit on a rounded plate, be thickened to survive
/// 16 px rendering, and leave the icon's corners genuinely transparent.
/// </para>
/// <para>
/// Drawn at 4x then downsampled, which is what keeps the many small gear teeth
/// from aliasing into mush.
/// </para>
/// </summary>
internal static class Program
{
    private const int Supersample = 4;

    private static readonly int[] IconSizes = { 256, 128, 64, 48, 40, 32, 24, 20, 16 };

    /// <summary>Fraction of the plate left as padding around the artwork.</summary>
    private const float ArtInset = 0.085f;

    /// <summary>
    /// Below this rendered size the strokes are thickened, because a hairline teal
    /// stroke would otherwise wash out to a pale smudge at 16 px.
    /// </summary>
    private const int BoldBelowSize = 64;

    private static int Main(string[] args)
    {
        var repoRoot = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var assetsDir = Path.Combine(repoRoot, "src", "TaskbarLyrics.App", "Assets");
        var sourcePath = Path.Combine(assetsDir, "IconSource.png");

        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source artwork not found: {sourcePath}");
            return 1;
        }

        using var source = new Bitmap(sourcePath);

        using var art = LineArt.Extract(source, out var lineColor, out var contentBounds);
        Console.WriteLine($"line colour   : {lineColor.R},{lineColor.G},{lineColor.B}");
        Console.WriteLine($"content bounds: {contentBounds}");

        var pngPath = Path.Combine(assetsDir, "AppIcon.png");
        var icoPath = Path.Combine(assetsDir, "TaskbarLyrics.ico");

        using (var master = RenderIcon(art, contentBounds, lineColor, 256))
        {
            master.Save(pngPath, ImageFormat.Png);
            Console.WriteLine($"wrote {pngPath} ({new FileInfo(pngPath).Length} bytes)");
        }

        WriteIco(icoPath, art, contentBounds, lineColor);
        Console.WriteLine($"wrote {icoPath} ({new FileInfo(icoPath).Length} bytes)");

        return 0;
    }

    /// <summary>Render one square icon.</summary>
    private static Bitmap RenderIcon(Bitmap art, RectangleF content, Color lineColor, int size)
    {
        int big = size * Supersample;

        using var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var plate = PlateRect(big);
            using var path = RoundedRect(plate, big * 0.225f);

            // White plate, matching the artwork's own background, with a hairline
            // edge so the icon keeps a silhouette on white page backgrounds too.
            using (var fill = new SolidBrush(Color.White))
            {
                g.FillPath(fill, path);
            }

            using (var edge = new Pen(Color.FromArgb(30, 0, 0, 0), Math.Max(1f, big * 0.005f)))
            {
                g.DrawPath(edge, path);
            }

            var inner = RectangleF.Inflate(plate, -plate.Width * ArtInset, -plate.Height * ArtInset);
            DrawArt(g, art, content, inner, lineColor, size);
        }

        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(canvas, new Rectangle(0, 0, size, size));
        }

        return result;
    }

    /// <summary>
    /// Draw the artwork scaled to fit, preserving aspect ratio, and opt into stroke
    /// thickening at small sizes.
    /// </summary>
    private static void DrawArt(
        Graphics g, Bitmap art, RectangleF content, RectangleF target, Color lineColor, int size)
    {
        float scale = Math.Min(target.Width / content.Width, target.Height / content.Height);
        float drawW = content.Width * scale;
        float drawH = content.Height * scale;
        float drawX = target.X + ((target.Width - drawW) / 2f);
        float drawY = target.Y + ((target.Height - drawH) / 2f);

        var dest = new RectangleF(drawX, drawY, drawW, drawH);

        // The extracted art is a white mask carrying the line shape in its alpha, so
        // tinting it is one colour-matrix multiply.
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix(new[]
        {
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { lineColor.R / 255f, lineColor.G / 255f, lineColor.B / 255f, 0f, 1f },
        }));

        if (size >= BoldBelowSize)
        {
            DrawOnce(g, art, content, dest, attributes);
            return;
        }

        // Thicken by redrawing at sub-pixel offsets: a cheap dilation that keeps the
        // gear teeth and the note readable once the icon is 16-32 px.
        float spread = Math.Max(0.7f, dest.Width * 0.008f);

        var offsets = new[]
        {
            (0f, 0f),
            (spread, 0f), (-spread, 0f), (0f, spread), (0f, -spread),
            (spread * 0.7f, spread * 0.7f), (-spread * 0.7f, spread * 0.7f),
            (spread * 0.7f, -spread * 0.7f), (-spread * 0.7f, -spread * 0.7f),
        };

        foreach (var (dx, dy) in offsets)
        {
            DrawOnce(g, art, content, new RectangleF(dest.X + dx, dest.Y + dy, dest.Width, dest.Height), attributes);
        }
    }

    private static void DrawOnce(
        Graphics g, Bitmap art, RectangleF content, RectangleF dest, ImageAttributes attributes)
    {
        g.DrawImage(
            art,
            Rectangle.Round(dest),
            content.X, content.Y, content.Width, content.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static RectangleF PlateRect(int size)
    {
        float inset = size * 0.012f;
        return new RectangleF(inset, inset, size - (inset * 2), size - (inset * 2));
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;

        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static void WriteIco(string path, Bitmap art, RectangleF content, Color lineColor)
    {
        var frames = new List<(int Size, Bitmap Image)>();

        try
        {
            foreach (var size in IconSizes)
            {
                // Keep every frame; IcoWriter picks PNG or DIB per size.
                frames.Add((size, RenderIcon(art, content, lineColor, size)));
            }

            IcoWriter.Write(path, frames);
        }
        finally
        {
            foreach (var (_, image) in frames) image.Dispose();
        }
    }
}
