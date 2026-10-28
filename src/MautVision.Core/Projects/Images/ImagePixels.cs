using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MautVision.Core.Interfaces.Projects.Images;
using SkiaSharp;

namespace MautVision.Core.Projects.Images;

/// <summary>What one full decode of an image file gives: format, size with EXIF orientation applied and the pixel hash.</summary>
internal readonly record struct DecodedImage(ImageFileFormat Format, int Width, int Height, string PixelHash);

/// <summary>
/// Decodes an image once at full size and hashes its pixels as the user sees them: RGBA 8-bit (unpremultiplied, in the
/// file's own colour space) after the EXIF orientation, prefixed with the oriented width and height. The same picture in
/// PNG and BMP gets the same hash; a rotated JPEG gets the hash of the rotated frame. SHA-256 runs on the platform
/// implementation, which uses the CPU's SHA extensions where present. A truncated or corrupt file — any decode result
/// other than complete success — gives null.
/// </summary>
internal static class ImagePixels
{
    // Rows decoded per scanline call: large enough to amortize the call, small enough to stay in cache.
    private const int ScanlineBufferSize = 1 << 18;

    internal static DecodedImage? Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;
        ImageFileFormat? format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ImageFileFormat.Png,
            SKEncodedImageFormat.Jpeg => ImageFileFormat.Jpeg,
            SKEncodedImageFormat.Webp => ImageFileFormat.Webp,
            SKEncodedImageFormat.Bmp => ImageFileFormat.Bmp,
            _ => null,
        };
        var source = codec.Info;
        if (format is null || source.Width <= 0 || source.Height <= 0) return null;
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888,
            source.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul, source.ColorSpace);
        var origin = codec.EncodedOrigin;
        var (width, height) = IsTransposed(origin) ? (info.Height, info.Width) : (info.Width, info.Height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[16];
        "RGBA8\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], height);
        hash.AppendData(header);

        var rowBytes = info.RowBytes;
        if (origin == SKEncodedOrigin.TopLeft && codec.StartScanlineDecode(info) == SKCodecResult.Success)
        {
            // The usual path for JPEG, non-interlaced PNG and BMP: rows stream through a small pooled buffer into the hash.
            var rows = Math.Max(1, Math.Min(info.Height, ScanlineBufferSize / rowBytes));
            var complete = WithPinned(rows * rowBytes, (buffer, address) =>
            {
                for (var line = 0; line < info.Height; line += rows)
                {
                    var count = Math.Min(rows, info.Height - line);
                    if (codec.GetScanlines(address, count, rowBytes) != count) return false;
                    hash.AppendData(buffer, 0, count * rowBytes);
                }
                return true;
            });
            return complete ? new DecodedImage(format.Value, width, height, Convert.ToHexStringLower(hash.GetHashAndReset())) : null;
        }
        // WebP, interlaced PNG and oriented images: the whole frame, then rows in display order.
        if (info.BytesSize64 > Array.MaxLength) return null;
        var decoded = WithPinned((int)info.BytesSize64, (buffer, address) =>
        {
            if (codec.GetPixels(info, address) != SKCodecResult.Success) return false;
            HashOriented(hash, MemoryMarshal.Cast<byte, uint>(buffer.AsSpan(0, (int)info.BytesSize64)), info.Width, info.Height, origin);
            return true;
        });
        return decoded ? new DecodedImage(format.Value, width, height, Convert.ToHexStringLower(hash.GetHashAndReset())) : null;
    }

    internal static bool IsTransposed(SKEncodedOrigin origin)
        => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Pixel hash of an already oriented RGBA 8888 bitmap, the same value <see cref="Decode"/> gives for a file showing it;
    /// lets checks compare the orientation mapping with an independent rendering.
    /// </summary>
    internal static string Hash(SKBitmap oriented)
    {
        if (oriented.ColorType != SKColorType.Rgba8888) throw new ArgumentException("Expected an RGBA 8888 bitmap.", nameof(oriented));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[16];
        "RGBA8\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], oriented.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], oriented.Height);
        hash.AppendData(header);
        var pixels = oriented.GetPixelSpan();
        for (var y = 0; y < oriented.Height; y++) hash.AppendData(pixels.Slice(y * oriented.RowBytes, oriented.Width * 4));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Pixel hash of a frame given as RGBA 8888 rows without padding — the value <see cref="Decode"/> gives for its PNG.</summary>
    internal static string Hash(ReadOnlySpan<byte> rgba, int width, int height)
    {
        Span<byte> header = stackalloc byte[16];
        "RGBA8\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(rgba[..(width * height * 4)]);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Lossless PNG of an RGBA 8888 frame (rows without padding). An opaque frame is written as RGB; decoding the file gives
    /// back exactly these pixels.
    /// </summary>
    internal static SKData EncodePng(byte[] rgba, int width, int height)
    {
        var length = width * height * 4;
        var opaque = IsOpaque(rgba.AsSpan(0, length));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            return pixmap.Encode(PngOptions)
                ?? throw new InvalidOperationException("PNG encoding failed.");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// The Paeth filter at zlib level 1: for a 1080p frame about 4 times faster than Skia's default (all filters, level 6)
    /// for a file about 20% larger; encoding is the costliest step of a video frame. Settable for measurements.
    /// </summary>
    internal static SKPngEncoderOptions PngOptions { get; set; } = new(SKPngEncoderFilterFlags.Paeth, 1);

    /// <summary>True when every alpha byte is 255; compares 32 or 64 bytes at a time with the widest vectors the CPU has.</summary>
    internal static bool IsOpaque(ReadOnlySpan<byte> rgba)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(rgba);
        var alpha = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;
        var index = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated && pixels.Length >= System.Numerics.Vector<uint>.Count)
        {
            var mask = new System.Numerics.Vector<uint>(alpha);
            for (; index <= pixels.Length - System.Numerics.Vector<uint>.Count; index += System.Numerics.Vector<uint>.Count)
            {
                if (!System.Numerics.Vector.EqualsAll(new System.Numerics.Vector<uint>(pixels[index..]) & mask, mask)) return false;
            }
        }
        for (; index < pixels.Length; index++)
        {
            if ((pixels[index] & alpha) != alpha) return false;
        }
        return true;
    }

    /// <summary>
    /// Appends the stored frame (width × height pixels) in display order as EXIF Orientation describes, one output row at a
    /// time; the mapping matches ThumbnailGenerator.Orientation.
    /// </summary>
    private static void HashOriented(IncrementalHash hash, ReadOnlySpan<uint> pixels, int width, int height, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            hash.AppendData(MemoryMarshal.AsBytes(pixels));
            return;
        }
        var transposed = IsTransposed(origin);
        var outWidth = transposed ? height : width;
        var outHeight = transposed ? width : height;
        var row = ArrayPool<uint>.Shared.Rent(outWidth);
        try
        {
            var target = row.AsSpan(0, outWidth);
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
                    target[x] = pixels[v * width + u];
                }
                hash.AppendData(MemoryMarshal.AsBytes(target));
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(row);
        }
    }

    private static bool WithPinned(int size, Func<byte[], IntPtr, bool> decode)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return decode(buffer, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
