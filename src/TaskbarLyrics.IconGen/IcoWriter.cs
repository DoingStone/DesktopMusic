using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace TaskbarLyrics.IconGen;

/// <summary>
/// Writes a multi-size .ico.
/// <para>
/// Large frames are PNG-compressed (the convention since Vista, and what keeps the
/// file small), but small frames are written as uncompressed BMP/DIB. That split is
/// deliberate: PNG-compressed frames below 32 px are not decoded reliably by every
/// consumer — notably <c>System.Drawing.Icon</c>, which throws when asked for a
/// PNG-compressed 16 px frame. Traditional DIB frames are read everywhere.
/// </para>
/// </summary>
internal static class IcoWriter
{
    /// <summary>Frames at or above this size are stored as PNG.</summary>
    private const int PngThreshold = 64;

    public static void Write(string path, IReadOnlyList<(int Size, Bitmap Image)> frames)
    {
        var encoded = new List<(int Size, byte[] Data)>();

        foreach (var (size, image) in frames)
        {
            encoded.Add((size, size >= PngThreshold ? EncodePng(image) : EncodeDib(image)));
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);
        w.Write((ushort)1);              // 1 = icon
        w.Write((ushort)encoded.Count);

        // ICONDIRENTRY per frame
        int offset = 6 + (16 * encoded.Count);
        foreach (var (size, data) in encoded)
        {
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette size
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // colour planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in encoded) w.Write(data);
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Encode one frame as a DIB: a BITMAPINFOHEADER, the bottom-up BGRA pixels, and
    /// a 1-bpp AND mask. The mask is all zeros because transparency comes from the
    /// alpha channel, but the field is mandatory.
    /// </summary>
    private static byte[] EncodeDib(Bitmap image)
    {
        int w = image.Width;
        int h = image.Height;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        const int HeaderSize = 40;
        int xorSize = w * h * 4;
        int maskStride = ((w + 31) / 32) * 4;   // 1 bpp, rows padded to 4 bytes
        int maskSize = maskStride * h;

        // BITMAPINFOHEADER. biHeight is doubled to cover the XOR image plus the mask.
        bw.Write(HeaderSize);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((ushort)1);          // planes
        bw.Write((ushort)32);         // bpp
        bw.Write(0);                  // BI_RGB
        bw.Write(xorSize + maskSize);
        bw.Write(0);                  // x pixels per metre
        bw.Write(0);                  // y pixels per metre
        bw.Write(0);                  // colours used
        bw.Write(0);                  // important colours

        // XOR image, bottom-up.
        var data = image.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;
            var row = new byte[w * 4];

            for (int y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + (y * stride), row, 0, w * 4);
                bw.Write(row);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }

        // AND mask: fully transparent mask, alpha carries the real shape.
        var empty = new byte[maskStride];
        for (int y = 0; y < h; y++) bw.Write(empty);

        return ms.ToArray();
    }
}
