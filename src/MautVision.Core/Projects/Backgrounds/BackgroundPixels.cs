using System.Buffers;
using System.Runtime.InteropServices;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Projects.Images;
using MautVision.Core.Resources;
using SkiaSharp;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>RGBA 8-bit unpremultiplied sRGB pixels, rows without padding, in a pooled buffer returned on disposal.</summary>
internal sealed class RgbaImage(int width, int height, byte[] buffer) : IDisposable
{
    private byte[]? buffer = buffer;

    internal int Width { get; } = width;

    internal int Height { get; } = height;

    internal int Length => Width * Height * 4;

    internal byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(RgbaImage));

    internal static RgbaImage Rent(int width, int height) => new(width, height, ArrayPool<byte>.Shared.Rent(checked(width * height * 4)));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref buffer, null) is { } returned) ArrayPool<byte>.Shared.Return(returned);
    }
}

/// <summary>
/// Decoding, cutting, blurring and previews of background frames with SkiaSharp. Pixels are converted to sRGB once when a
/// file is decoded and then treated as untagged, so blur and PNG output never convert colours again.
/// </summary>
internal static class BackgroundPixels
{
    private static readonly SKColorSpace Srgb = SKColorSpace.CreateSrgb();

    /// <summary>
    /// Size of an image file as shown (EXIF orientation applied) from its header only; null when the file is not a readable
    /// PNG, JPEG, WebP or BMP.
    /// </summary>
    internal static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 14);
            using var codec = SKCodec.Create(input);
            if (codec is null || !IsAccepted(codec.EncodedFormat) || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
            return Oriented(codec);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Full decode with EXIF orientation into sRGB RGBA; null when the data is not a complete PNG, JPEG, WebP or BMP image.
    /// </summary>
    internal static RgbaImage? Decode(SKData data)
    {
        using var codec = SKCodec.Create(data);
        if (codec is null || !IsAccepted(codec.EncodedFormat)) return null;
        var stored = codec.Info;
        if (stored.Width <= 0 || stored.Height <= 0 || (long)stored.Width * stored.Height * 4 > Array.MaxLength) return null;
        var info = new SKImageInfo(stored.Width, stored.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, Srgb);
        var decoded = RgbaImage.Rent(stored.Width, stored.Height);
        try
        {
            if (!WithPinned(decoded.Buffer, address => codec.GetPixels(info, address) == SKCodecResult.Success))
            {
                decoded.Dispose();
                return null;
            }
            var origin = codec.EncodedOrigin;
            if (origin == SKEncodedOrigin.TopLeft) return decoded;
            using (decoded)
            {
                return Orient(decoded, origin);
            }
        }
        catch
        {
            decoded.Dispose();
            throw;
        }
    }

    /// <summary>Reads a whole file into Skia data; Skia's own path API does not accept every Windows path.</summary>
    internal static SKData ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return SKData.Create(input) ?? throw new IOException("The file could not be read.");
    }

    /// <summary>Copies the target-size region at (left, top) of an RGBA frame into a new pooled image.</summary>
    internal static RgbaImage Cut(byte[] pixels, int width, int left, int top, int targetWidth, int targetHeight)
    {
        var result = RgbaImage.Rent(targetWidth, targetHeight);
        var row = targetWidth * 4;
        var source = pixels.AsSpan();
        var target = result.Buffer.AsSpan();
        for (var y = 0; y < targetHeight; y++)
        {
            source.Slice(((top + y) * width + left) * 4, row).CopyTo(target.Slice(y * row, row));
        }
        return result;
    }

    /// <summary>
    /// Gaussian blur with sigma = radius in place (Skia raster, edges clamped so the frame keeps opaque borders). A radius of
    /// zero leaves the pixels unchanged.
    /// </summary>
    internal static void Blur(RgbaImage image, double radius)
    {
        if (radius <= 0) return;
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(image.Buffer, GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Blur surface could not be created.");
            using (var source = SKImage.FromPixels(info, address, image.Width * 4))
            using (var filter = SKImageFilter.CreateBlur((float)radius, (float)radius, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { ImageFilter = filter })
            {
                surface.Canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
                surface.Flush();
            }
            if (!surface.ReadPixels(info, address, image.Width * 4, 0, 0)) throw new InvalidOperationException("Blurred pixels could not be read.");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// A copy that fits maxSide (never enlarged) for showing a frame: JPEG q85 when opaque, PNG otherwise. Decoding a file
    /// uses the codec's scaled decode (JPEG decodes at 1/2, 1/4 or 1/8 directly), so a large photo is never decoded in full.
    /// </summary>
    internal static BackgroundSourcePreview PreviewFile(SKData data, int maxSide)
    {
        using var codec = SKCodec.Create(data);
        if (codec is null || !IsAccepted(codec.EncodedFormat) || codec.Info.Width <= 0 || codec.Info.Height <= 0) throw Unreadable();
        var origin = codec.EncodedOrigin;
        var (width, height) = Oriented(codec);
        var stored = codec.Info;
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(stored.Width, stored.Height));
        var targetWidth = Math.Max(1, (int)Math.Round(stored.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(stored.Height * scale));
        var decodeSize = codec.GetScaledDimensions((float)scale);
        if (decodeSize.Width < targetWidth || decodeSize.Height < targetHeight) decodeSize = new SKSizeI(stored.Width, stored.Height);
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb);
        using var decoded = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, decoded.GetPixels()) != SKCodecResult.Success) throw Unreadable();
        decoded.SetImmutable();
        using var image = SKImage.FromBitmap(decoded);
        var transposed = ImagePixels.IsTransposed(origin);
        var outputWidth = transposed ? targetHeight : targetWidth;
        var outputHeight = transposed ? targetWidth : targetHeight;
        using var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul, Srgb));
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(ThumbnailGenerator.Orientation(origin, targetWidth, targetHeight));
            canvas.DrawImage(image, new SKRect(0, 0, targetWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        }
        return new BackgroundSourcePreview(width, height, Encode(output, codec.Info.AlphaType == SKAlphaType.Opaque));
    }

    /// <summary>A copy of an RGBA frame (a video frame) that fits maxSide, for placing the region.</summary>
    internal static BackgroundSourcePreview PreviewFrame(byte[] pixels, int width, int height, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var source = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            using var output = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var scaled = output.PeekPixels())
            {
                if (!source.ScalePixels(scaled, new SKSamplingOptions(SKCubicResampler.Mitchell))) throw new InvalidOperationException("Frame could not be scaled.");
            }
            return new BackgroundSourcePreview(width, height, Encode(output, opaque: true));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A finished background (target size) as the PNG the run would store, for samples.</summary>
    internal static BackgroundPreviewImage PngImage(SKData png, int width, int height) => new(png.ToArray(), "image/png", width, height);

    private static BackgroundPreviewImage Encode(SKBitmap bitmap, bool opaque)
    {
        using var encoded = opaque ? bitmap.Encode(SKEncodedImageFormat.Jpeg, 85) : bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null) throw new InvalidOperationException("Preview encoding failed.");
        return new BackgroundPreviewImage(encoded.ToArray(), opaque ? "image/jpeg" : "image/png", bitmap.Width, bitmap.Height);
    }

    private static bool IsAccepted(SKEncodedImageFormat format)
        => format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp or SKEncodedImageFormat.Bmp;

    private static (int Width, int Height) Oriented(SKCodec codec)
        => ImagePixels.IsTransposed(codec.EncodedOrigin) ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);

    /// <summary>The stored frame in display order as EXIF Orientation describes; the mapping matches ThumbnailGenerator.Orientation.</summary>
    private static RgbaImage Orient(RgbaImage stored, SKEncodedOrigin origin)
    {
        var (width, height) = (stored.Width, stored.Height);
        var transposed = ImagePixels.IsTransposed(origin);
        var result = RgbaImage.Rent(transposed ? height : width, transposed ? width : height);
        var source = MemoryMarshal.Cast<byte, uint>(stored.Buffer.AsSpan(0, stored.Length));
        var target = MemoryMarshal.Cast<byte, uint>(result.Buffer.AsSpan(0, result.Length));
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var (u, v) = origin switch
                {
                    SKEncodedOrigin.TopRight => (width - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (width - 1 - x, height - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, height - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (y, height - 1 - x),
                    SKEncodedOrigin.RightBottom => (width - 1 - y, height - 1 - x),
                    SKEncodedOrigin.LeftBottom => (width - 1 - y, x),
                    _ => (x, y),
                };
                target[y * result.Width + x] = source[v * width + u];
            }
        }
        return result;
    }

    private static bool WithPinned(byte[] buffer, Func<IntPtr, bool> action)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return action(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static Interfaces.CoreException Unreadable() => new("backgrounds.image_unreadable", "The image could not be decoded.");
}

