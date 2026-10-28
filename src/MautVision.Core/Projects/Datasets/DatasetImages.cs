using System.Runtime.InteropServices;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Projects.Images;
using SkiaSharp;

namespace MautVision.Core.Projects.Datasets;

/// <summary>A sample image written as a new PNG: its bytes and the pixel hash of the oriented frame (the same value the import gives).</summary>
internal sealed record ConvertedImage(byte[] Png, string PixelHash, int Width, int Height);

/// <summary>
/// Decides whether a project file can be linked into a version as it is and otherwise writes the frame as the user sees it.
/// JPEG and PNG without an EXIF orientation are linked; WebP, BMP and oriented files become a lossless PNG of the oriented RGBA
/// pixels (unpremultiplied, in the file's own colour space, as <see cref="ImagePixels"/> hashes them), so the pixel hash of
/// the sample equals the pixel hash of the source image.
/// </summary>
internal static class DatasetImages
{
    /// <summary>True when the bytes can be used as they are: JPEG or PNG whose header has no orientation other than top-left.</summary>
    internal static bool CanLink(ImageFileFormat format, byte[] bytes)
    {
        if (format is not (ImageFileFormat.Jpeg or ImageFileFormat.Png)) return false;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The source image of a dataset sample cannot be decoded.");
        return codec.EncodedOrigin == SKEncodedOrigin.TopLeft;
    }

    internal static ConvertedImage Convert(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("The source image of a dataset sample cannot be decoded.");
        var source = codec.Info;
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888,
            source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul, source.ColorSpace);
        var pixels = new byte[info.Width * info.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (codec.GetPixels(info, handle.AddrOfPinnedObject()) != SKCodecResult.Success)
                throw new InvalidDataException("The source image of a dataset sample is incomplete.");
        }
        finally
        {
            handle.Free();
        }
        var origin = codec.EncodedOrigin;
        var (width, height) = ImagePixels.IsTransposed(origin) ? (info.Height, info.Width) : (info.Width, info.Height);
        var oriented = origin == SKEncodedOrigin.TopLeft ? pixels : Orient(pixels, info.Width, info.Height, origin);
        using var png = ImagePixels.EncodePng(oriented, width, height);
        return new ConvertedImage(png.ToArray(), ImagePixels.Hash(oriented, width, height), width, height);
    }

    /// <summary>The stored frame in display order as EXIF Orientation describes; the mapping of ImagePixels and the thumbnails.</summary>
    private static byte[] Orient(byte[] stored, int width, int height, SKEncodedOrigin origin)
    {
        var input = MemoryMarshal.Cast<byte, uint>(stored.AsSpan());
        var transposed = ImagePixels.IsTransposed(origin);
        var (outWidth, outHeight) = transposed ? (height, width) : (width, height);
        var result = new byte[stored.Length];
        var output = MemoryMarshal.Cast<byte, uint>(result.AsSpan());
        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
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
                output[y * outWidth + x] = input[v * width + u];
            }
        }
        return result;
    }
}
