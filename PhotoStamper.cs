using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace IMPWeldPhotos;

/// <summary>
/// Stamps a photo with its own file name (SPEC.md §4 "Stamping"). Made to match the
/// endoscope's own overlay in the old reports: plain white Arial, small (3.4 % of the
/// height), with a soft dark shadow so it reads on bright pipe walls. It sits in the
/// bottom-right corner, because the endoscope writes its date bottom-left and the isometrija
/// code bottom-centre, and two texts on top of each other read as neither.
/// </summary>
public static class PhotoStamper
{
    public const int JpegQuality = 92;

    public sealed record Stamped(byte[] Bytes, int Width, int Height);

    public static Stamped Stamp(byte[] source, string text, string extension, DateTime? dateTimeOriginal)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("slike ni mogoče prebrati");
        using var decoded = SKBitmap.Decode(codec) ?? throw new InvalidDataException("slike ni mogoče dekodirati");

        // Pixels are written upright and the output carries Orientation = 1, so no
        // viewer rotates the photo a second time.
        using var upright = Upright(decoded, codec.EncodedOrigin);
        using (var canvas = new SKCanvas(upright))
            DrawStamp(canvas, text, upright.Width, upright.Height);

        return new Stamped(Encode(upright, extension, dateTimeOriginal), upright.Width, upright.Height);
    }

    /// <summary>The .part file is complete: right length, and it decodes in full at
    /// the expected size.</summary>
    public static bool Verify(string path, int width, int height, long expectedLength)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length != expectedLength) return false;
        using var codec = SKCodec.Create(path);
        if (codec == null || codec.Info.Width != width || codec.Info.Height != height) return false;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        return codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success;
    }

    public static void DrawStamp(SKCanvas canvas, string text, int width, int height)
    {
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyleWeight.Normal,
                                                       SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                             ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Normal,
                                                          SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                             ?? SKTypeface.Default;
        using var fill = new SKPaint
        {
            Typeface = typeface,
            TextSize = height * 0.034f,
            IsAntialias = true,
            SubpixelText = true,
            Color = SKColors.White,
        };

        // Centre on the ink, not the advance, so the text sits where it looks centred.
        var bounds = new SKRect();
        fill.MeasureText(text, ref bounds);
        var maxWidth = width * 0.6f; // the endoscope's own code sits bottom-centre
        while (bounds.Width > maxWidth && fill.TextSize > 4)
        {
            fill.TextSize = Math.Max(4, fill.TextSize * Math.Min(0.98f, maxWidth / bounds.Width));
            fill.MeasureText(text, ref bounds);
        }

        var size = fill.TextSize;
        var x = width * 0.985f - bounds.Right;
        var y = height * 0.965f - bounds.MidY;
        var offset = Math.Max(1f, size * 0.08f);

        using var shadow = fill.Clone();
        shadow.Color = new SKColor(0, 0, 0, 170);
        shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1f, size * 0.10f));
        canvas.DrawText(text, x + offset, y + offset, shadow);
        canvas.DrawText(text, x, y, fill);
    }

    private static SKBitmap Upright(SKBitmap src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                          or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var alpha = src.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var dst = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, alpha));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(OrientationMatrix(origin, w, h));
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Maps stored pixels to upright ones for each EXIF orientation.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static byte[] Encode(SKBitmap bitmap, string extension, DateTime? dateTimeOriginal)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".jpg":
            case ".jpeg":
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
                                 ?? throw new InvalidDataException("kodiranje JPEG ni uspelo");
                return ExifSegment.Insert(data.ToArray(), dateTimeOriginal);
            }
            case ".png":
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                                 ?? throw new InvalidDataException("kodiranje PNG ni uspelo");
                return data.ToArray();
            }
            case ".bmp":
                // Skia decodes BMP but cannot encode it.
                return BmpWriter.Write(bitmap);
            default:
                throw new NotSupportedException($"vrsta slike {extension} ni podprta");
        }
    }
}

/// <summary>Minimal EXIF APP1 for a stamped JPEG: Orientation and, when the source
/// had it, DateTimeOriginal. Skia's encoder writes no metadata of its own.</summary>
public static class ExifSegment
{
    public static byte[] Insert(byte[] jpeg, DateTime? dateTimeOriginal, ushort orientation = 1)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("izhod ni JPEG");

        // After a JFIF APP0 if there is one: JFIF readers want it first, EXIF readers
        // look past it.
        var pos = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) pos = 4 + ((jpeg[4] << 8) | jpeg[5]);

        var segment = Build(dateTimeOriginal, orientation);
        var result = new byte[jpeg.Length + segment.Length];
        Buffer.BlockCopy(jpeg, 0, result, 0, pos);
        Buffer.BlockCopy(segment, 0, result, pos, segment.Length);
        Buffer.BlockCopy(jpeg, pos, result, pos + segment.Length, jpeg.Length - pos);
        return result;
    }

    private static byte[] Build(DateTime? dateTimeOriginal, ushort orientation)
    {
        // Little-endian TIFF. Offsets are from the start of the TIFF header.
        using var tiff = new MemoryStream();
        using var w = new BinaryWriter(tiff);
        w.Write((byte)'I');
        w.Write((byte)'I');
        w.Write((ushort)42);
        w.Write(8u);

        var hasDate = dateTimeOriginal.HasValue;
        var ifd0Count = (ushort)(hasDate ? 2 : 1);
        var exifIfdOffset = (uint)(8 + 2 + 12 * ifd0Count + 4);

        w.Write(ifd0Count);
        w.Write((ushort)0x0112); // Orientation, SHORT
        w.Write((ushort)3);
        w.Write(1u);
        w.Write(orientation);
        w.Write((ushort)0);
        if (hasDate)
        {
            w.Write((ushort)0x8769); // Exif IFD pointer, LONG
            w.Write((ushort)4);
            w.Write(1u);
            w.Write(exifIfdOffset);
        }
        w.Write(0u);

        if (hasDate)
        {
            var dataOffset = exifIfdOffset + 2 + 12 + 4;
            w.Write((ushort)1);
            w.Write((ushort)0x9003); // DateTimeOriginal, ASCII, 20 bytes with NUL
            w.Write((ushort)2);
            w.Write(20u);
            w.Write(dataOffset);
            w.Write(0u);
            w.Write(Encoding.ASCII.GetBytes(
                dateTimeOriginal!.Value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)));
            w.Write((byte)0);
        }
        w.Flush();

        var payload = tiff.ToArray();
        var segment = new byte[4 + 6 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(2 + 6 + payload.Length));
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        payload.CopyTo(segment, 10);
        return segment;
    }
}

/// <summary>24-bit bottom-up BMP from an Rgba8888 bitmap.</summary>
public static class BmpWriter
{
    public static byte[] Write(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888) throw new ArgumentException("pričakovan Rgba8888", nameof(bitmap));
        int w = bitmap.Width, h = bitmap.Height;
        var stride = (w * 3 + 3) & ~3;
        var imageSize = stride * h;
        var bytes = new byte[54 + imageSize];
        var span = bytes.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], w);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], h);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], 2835);

        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        for (var y = 0; y < h; y++)
        {
            var src = (h - 1 - y) * rowBytes;
            var dst = 54 + y * stride;
            for (var x = 0; x < w; x++)
            {
                var s = src + x * 4;
                var d = dst + x * 3;
                bytes[d] = pixels[s + 2];
                bytes[d + 1] = pixels[s + 1];
                bytes[d + 2] = pixels[s];
            }
        }
        return bytes;
    }
}
