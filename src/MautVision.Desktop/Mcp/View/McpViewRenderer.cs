using System.Globalization;
using SkiaSharp;

namespace MautVision.Desktop.Mcp.View;

/// <summary>A box to draw: fractions 0–1 of the oriented frame from the top-left corner, a label and a color #rrggbb.</summary>
internal sealed record McpViewBox(double X, double Y, double Width, double Height, string Label, string Color);

/// <summary>A part of the frame in fractions 0–1; null is the whole frame.</summary>
internal sealed record McpViewRegion(double X, double Y, double Width, double Height);

/// <summary>A rendered picture: encoded bytes, their type and the size of the source frame and of the picture.</summary>
internal sealed record McpRenderedImage(byte[] Content, string MimeType, int SourceWidth, int SourceHeight, int Width, int Height);

/// <summary>
/// Pictures for the "eyes" of an AI: decodes an image with its EXIF orientation, cuts the requested region, fits it into maxSize, draws
/// boxes with labels and encodes JPEG or PNG. Boxes are given in fractions of the oriented frame, as Core stores annotation, so they land
/// exactly where the UI draws them. A JPEG is kept under about 1 MB by lowering the quality.
/// </summary>
internal static class McpViewRenderer
{
    internal const int DefaultMaxSize = 1024;
    internal const int MinMaxSize = 128;
    internal const int MaxMaxSize = 2048;
    private const long MaxEncodedBytes = 1_000_000;
    private const double MaxUpscale = 8;

    internal static McpRenderedImage Render(byte[] encoded, McpViewRegion? region, IReadOnlyList<McpViewBox> boxes, int maxSize, bool png)
    {
        using var surface = Draw(encoded, region, boxes, maxSize, out var sourceWidth, out var sourceHeight);
        using var image = surface.Snapshot();
        var content = Encode(image, png);
        return new McpRenderedImage(content.Data, content.MimeType, sourceWidth, sourceHeight, image.Width, image.Height);
    }

    /// <summary>The size of the oriented frame without decoding the pixels; null when the bytes are not an image Skia reads.</summary>
    internal static (int Width, int Height)? Measure(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        return Transposed(codec.EncodedOrigin) ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// A numbered grid of pictures (view_many): cells of equal size in a grid as square as possible; the number in the corner of a cell is
    /// its place in the request from 1. A null entry is a failed target, drawn as an empty cell with its number crossed out.
    /// </summary>
    internal static McpRenderedImage Grid(IReadOnlyList<byte[]?> encoded, IReadOnlyList<McpViewRegion?> regions, IReadOnlyList<IReadOnlyList<McpViewBox>> boxes,
        int maxSize, bool png)
    {
        var count = encoded.Count;
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);
        const int gap = 6;
        var cell = Math.Max(64, (maxSize - gap * (columns - 1)) / columns);
        var width = columns * cell + gap * (columns - 1);
        var height = rows * cell + gap * (rows - 1);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("The grid surface could not be created.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x20, 0x20, 0x24));
        using var font = new SKFont(SKTypeface.Default, Math.Max(14, cell / 12f));
        for (var index = 0; index < count; index++)
        {
            var left = index % columns * (cell + gap);
            var top = index / columns * (cell + gap);
            if (encoded[index] is { } bytes)
            {
                using var tile = Draw(bytes, regions[index], boxes[index], cell, out _, out _);
                using var picture = tile.Snapshot();
                canvas.DrawImage(picture, left + (cell - picture.Width) / 2f, top + (cell - picture.Height) / 2f, new SKSamplingOptions(SKFilterMode.Linear));
            }
            else
            {
                using var failed = new SKPaint { Color = new SKColor(0x55, 0x55, 0x5c), IsStroke = true, StrokeWidth = 3, IsAntialias = true };
                canvas.DrawLine(left + cell * 0.3f, top + cell * 0.3f, left + cell * 0.7f, top + cell * 0.7f, failed);
                canvas.DrawLine(left + cell * 0.7f, top + cell * 0.3f, left + cell * 0.3f, top + cell * 0.7f, failed);
            }
            Badge(canvas, font, (index + 1).ToString(CultureInfo.InvariantCulture), left + 4, top + 4);
        }
        using var image = surface.Snapshot();
        var content = Encode(image, png);
        return new McpRenderedImage(content.Data, content.MimeType, width, height, width, height);
    }

    private static SKSurface Draw(byte[] encoded, McpViewRegion? region, IReadOnlyList<McpViewBox> boxes, int maxSize, out int orientedWidth, out int orientedHeight)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new McpToolException("mcp.image_unreadable", "The file is not an image the application can read.");
        var stored = codec.Info;
        var origin = codec.EncodedOrigin;
        (orientedWidth, orientedHeight) = Transposed(origin) ? (stored.Height, stored.Width) : (stored.Width, stored.Height);
        var area = region ?? new McpViewRegion(0, 0, 1, 1);
        var regionLeft = area.X * orientedWidth;
        var regionTop = area.Y * orientedHeight;
        var regionWidth = Math.Max(1, area.Width * orientedWidth);
        var regionHeight = Math.Max(1, area.Height * orientedHeight);
        var scale = Math.Min(maxSize / Math.Max(regionWidth, regionHeight), MaxUpscale);
        var width = Math.Max(1, (int)Math.Round(regionWidth * scale));
        var height = Math.Max(1, (int)Math.Round(regionHeight * scale));

        using var bitmap = Decode(codec, scale);
        var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("The view surface could not be created.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        // Decoded pixels → stored pixels → oriented frame → region → picture.
        var matrix = SKMatrix.CreateScale((float)(stored.Width / (double)bitmap.Width), (float)(stored.Height / (double)bitmap.Height));
        matrix = matrix.PostConcat(Orientation(origin, stored.Width, stored.Height));
        matrix = matrix.PostConcat(SKMatrix.CreateTranslation((float)-regionLeft, (float)-regionTop));
        matrix = matrix.PostConcat(SKMatrix.CreateScale((float)scale, (float)scale));
        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.Save();
            canvas.SetMatrix(matrix);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Restore();
        }
        if (boxes.Count > 0) DrawBoxes(canvas, boxes, area, orientedWidth, orientedHeight, scale);
        return surface;
    }

    /// <summary>Decodes at the smallest size the codec supports that still has the needed pixels (JPEG and WebP scale while decoding).</summary>
    private static SKBitmap Decode(SKCodec codec, double scale)
    {
        var stored = codec.Info;
        var size = new SKSizeI(stored.Width, stored.Height);
        if (scale < 0.5)
        {
            var scaled = codec.GetScaledDimensions((float)Math.Min(1, scale * 1.25));
            if (scaled.Width >= stored.Width * scale && scaled.Height >= stored.Height * scale && scaled.Width > 0 && scaled.Height > 0) size = scaled;
        }
        var bitmap = TryDecode(codec, size) ?? (size.Width != stored.Width ? TryDecode(codec, new SKSizeI(stored.Width, stored.Height)) : null);
        return bitmap ?? throw new McpToolException("mcp.image_unreadable", "The image could not be decoded.");
    }

    private static SKBitmap? TryDecode(SKCodec codec, SKSizeI size)
    {
        if ((long)size.Width * size.Height * 4 > Array.MaxLength) return null;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
        bitmap.Dispose();
        return null;
    }

    private static void DrawBoxes(SKCanvas canvas, IReadOnlyList<McpViewBox> boxes, McpViewRegion area, int orientedWidth, int orientedHeight, double scale)
    {
        var stroke = (float)Math.Clamp(Math.Max(canvas.DeviceClipBounds.Width, canvas.DeviceClipBounds.Height) / 400.0, 2, 4);
        using var font = new SKFont(SKTypeface.Default, Math.Max(12, stroke * 6));
        foreach (var box in boxes)
        {
            var left = (float)((box.X - area.X) * orientedWidth * scale);
            var top = (float)((box.Y - area.Y) * orientedHeight * scale);
            var right = left + (float)(box.Width * orientedWidth * scale);
            var bottom = top + (float)(box.Height * orientedHeight * scale);
            var color = SKColor.TryParse(box.Color, out var parsed) ? parsed : SKColors.Lime;
            using var outline = new SKPaint { Color = SKColors.Black.WithAlpha(160), IsStroke = true, StrokeWidth = stroke + 2, IsAntialias = true };
            using var paint = new SKPaint { Color = color, IsStroke = true, StrokeWidth = stroke, IsAntialias = true };
            canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), outline);
            canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), paint);
            if (box.Label.Length == 0) continue;
            var textWidth = font.MeasureText(box.Label);
            var labelHeight = font.Size * 1.3f;
            var labelTop = top - labelHeight >= 0 ? top - labelHeight : top;
            using var fill = new SKPaint { Color = color.WithAlpha(220), IsAntialias = true };
            canvas.DrawRect(SKRect.Create(left, labelTop, textWidth + 8, labelHeight), fill);
            using var text = new SKPaint { Color = Luminance(color) > 0.55 ? SKColors.Black : SKColors.White, IsAntialias = true };
            canvas.DrawText(box.Label, left + 4, labelTop + font.Size, SKTextAlign.Left, font, text);
        }
    }

    private static void Badge(SKCanvas canvas, SKFont font, string text, float left, float top)
    {
        var width = font.MeasureText(text) + 12;
        var height = font.Size * 1.4f;
        using var fill = new SKPaint { Color = new SKColor(0xfa, 0xcc, 0x15), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(left, top, width, height), 4, 4, fill);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, left + 6, top + font.Size * 1.08f, SKTextAlign.Left, font, ink);
    }

    private static double Luminance(SKColor color) => (0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue) / 255;

    private static (byte[] Data, string MimeType) Encode(SKImage image, bool png)
    {
        if (png)
        {
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return (encoded.ToArray(), "image/png");
        }
        foreach (var quality in new[] { 85, 75, 60, 45 })
        {
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (encoded.Size <= MaxEncodedBytes || quality == 45) return (encoded.ToArray(), "image/jpeg");
        }
        throw new InvalidOperationException("Unreachable.");
    }

    private static bool Transposed(SKEncodedOrigin origin)
        => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Maps stored pixels to the displayed frame for an EXIF orientation (w, h — the stored size).</summary>
    internal static SKMatrix Orientation(SKEncodedOrigin origin, int w, int h) => origin switch
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
}
