using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Images;
using SkiaSharp;

namespace MautVision.Core.Projects.Generation;

/// <summary>
/// An object image ready for compositing: decoded with orientation into sRGB, trimmed to its pixels with alpha above 0, and
/// scaled once to the size every frame of the run gives it (the size depends only on the trimmed size and the recipe).
/// </summary>
internal sealed class Sprite(int trimmedWidth, int trimmedHeight, SKImage image) : IDisposable
{
    internal int TrimmedWidth { get; } = trimmedWidth;
    internal int TrimmedHeight { get; } = trimmedHeight;

    /// <summary>Premultiplied pixels of the drawn size.</summary>
    internal SKImage Image { get; } = image;

    public void Dispose() => Image.Dispose();
}

/// <summary>A rendered frame: PNG bytes, their SHA-256, the pixel hash and the plan it was drawn from.</summary>
internal sealed record RenderedFrame(FramePlan Plan, SKData Png, string Sha256, string PixelHash) : IDisposable
{
    public void Dispose() => Png.Dispose();
}

/// <summary>
/// Draws frames of one run. Sprites are made once per image (decode, orientation, transparency check, trim, one high-quality
/// scale) and backgrounds are decoded into a bounded cache; a frame is a copy of its background, the sprites drawn source-over
/// at integer positions and one PNG encoding. Every frame depends only on the seed, its index and the manifest, so any number
/// of threads produce the same bytes.
/// </summary>
internal sealed class GenerationRenderer : IDisposable
{
    /// <summary>Decoded backgrounds kept in memory; a frame of 640×640 takes 1.6 MB.</summary>
    internal const long BackgroundCacheBytes = 256L << 20;

    private readonly GenerationLayout layout;
    private readonly uint seed;
    private readonly Func<int, int, string> assetPath;
    private readonly Func<int, string> backgroundPath;
    private readonly Lazy<Sprite>[][] sprites;
    private readonly ConcurrentDictionary<int, Lazy<CachedBackground>> backgrounds = new();
    private readonly ConcurrentQueue<int> backgroundOrder = new();
    private readonly int backgroundLimit;

    internal GenerationRenderer(GenerationLayout layout, uint seed, Func<int, int, string> assetPath, Func<int, string> backgroundPath)
    {
        this.layout = layout;
        this.seed = seed;
        this.assetPath = assetPath;
        this.backgroundPath = backgroundPath;
        sprites = layout.AssetCounts.Select((count, binding) => Enumerable.Range(0, count)
            .Select(asset => new Lazy<Sprite>(() => MakeSprite(binding, asset), LazyThreadSafetyMode.ExecutionAndPublication)).ToArray()).ToArray();
        backgroundLimit = (int)Math.Max(4, BackgroundCacheBytes / ((long)layout.Width * layout.Height * 4));
    }

    internal GenerationLayout Layout => layout;

    internal FramePlan Plan(int sampleIndex) => GenerationPlanner.Plan(layout, seed, sampleIndex, (binding, asset) =>
    {
        var sprite = sprites[binding][asset].Value;
        return (sprite.TrimmedWidth, sprite.TrimmedHeight);
    });

    internal RenderedFrame Render(int sampleIndex)
    {
        var plan = Plan(sampleIndex);
        var background = Background(plan.Background);
        var length = layout.Width * layout.Height * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            background.Premultiplied.AsSpan().CopyTo(buffer);
            var info = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var surface = SKSurface.Create(info, handle.AddrOfPinnedObject(), layout.Width * 4)
                ?? throw new InvalidOperationException("Frame surface could not be created."))
            {
                var canvas = surface.Canvas;
                var sampling = new SKSamplingOptions(SKFilterMode.Nearest);
                foreach (var item in plan.Objects) canvas.DrawImage(sprites[item.Binding][item.Asset].Value.Image, item.Left, item.Top, sampling);
                surface.Flush();
                // Sprites are drawn over the background, so an opaque background stays opaque and premultiplied equals unpremultiplied.
                if (!background.Opaque)
                {
                    using var pixmap = surface.PeekPixels();
                    var unpremultiplied = new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    if (!pixmap.ReadPixels(unpremultiplied, handle.AddrOfPinnedObject(), layout.Width * 4))
                        throw new InvalidOperationException("Frame pixels could not be read.");
                }
            }
            var png = ImagePixels.EncodePng(buffer, layout.Width, layout.Height);
            return new RenderedFrame(plan, png, Convert.ToHexStringLower(SHA256.HashData(png.AsSpan())),
                ImagePixels.Hash(buffer.AsSpan(0, length), layout.Width, layout.Height));
        }
        finally
        {
            handle.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The rectangle of a placed object in fractions of the frame.</summary>
    internal ImageBox Box(PlannedObject item) => new((double)item.Left / layout.Width, (double)item.Top / layout.Height,
        (double)item.Width / layout.Width, (double)item.Height / layout.Height);

    public void Dispose()
    {
        foreach (var sprite in sprites.SelectMany(row => row).Where(lazy => lazy.IsValueCreated))
        {
            try
            {
                sprite.Value.Dispose();
            }
            catch (CoreException)
            {
                // An unsupported image has no sprite to release.
            }
        }
    }

    private sealed record CachedBackground(byte[] Premultiplied, bool Opaque);

    private CachedBackground Background(int index)
    {
        if (backgrounds.TryGetValue(index, out var cached)) return cached.Value;
        var created = new Lazy<CachedBackground>(() => LoadBackground(index), LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = backgrounds.GetOrAdd(index, created);
        if (ReferenceEquals(lazy, created))
        {
            backgroundOrder.Enqueue(index);
            // Oldest first; a frame that still uses a forgotten background keeps its array alive until it is drawn.
            while (backgrounds.Count > backgroundLimit && backgroundOrder.TryDequeue(out var oldest)) backgrounds.TryRemove(oldest, out _);
        }
        return lazy.Value;
    }

    private CachedBackground LoadBackground(int index)
    {
        using var data = BackgroundPixels.ReadFile(backgroundPath(index));
        using var image = BackgroundPixels.Decode(data)
            ?? throw new CoreException("generation.execution_failed", $"Background {index} of the run could not be decoded.");
        if (image.Width != layout.Width || image.Height != layout.Height)
            throw new CoreException("generation.background_size_mismatch", $"Background {index} of the run is not of the canvas size.");
        var length = image.Length;
        var pixels = image.Buffer.AsSpan(0, length).ToArray();
        var opaque = ImagePixels.IsOpaque(pixels);
        if (!opaque) Premultiply(pixels, layout.Width, layout.Height);
        return new CachedBackground(pixels, opaque);
    }

    private static void Premultiply(byte[] pixels, int width, int height)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            using var source = new SKPixmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), address, width * 4);
            if (!source.ReadPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), address, width * 4))
                throw new InvalidOperationException("Background pixels could not be premultiplied.");
        }
        finally
        {
            handle.Free();
        }
    }

    private Sprite MakeSprite(int binding, int asset)
    {
        RgbaImage? decoded;
        try
        {
            using var data = BackgroundPixels.ReadFile(assetPath(binding, asset));
            decoded = BackgroundPixels.Decode(data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoreException("generation.preparation_failed", $"Image {asset} of binding {binding} could not be read.", error);
        }
        if (decoded is null) throw Unsupported(binding, asset);
        using (decoded)
        {
            var bounds = SpritePixels.AlphaBounds(decoded.Buffer, decoded.Width, decoded.Height) ?? throw Unsupported(binding, asset);
            var (width, height) = GenerationPlanner.ObjectSize(layout, bounds.Width, bounds.Height);
            return new Sprite(bounds.Width, bounds.Height, SpritePixels.Scale(decoded, bounds, width, height));
        }
    }

    private static CoreException Unsupported(int binding, int asset)
        => new("generation.unsupported_asset", $"Image {asset} of binding {binding} has no transparent pixels or no visible pixels.");
}

/// <summary>Pixel work of sprites: the transparency check with its trim bounds and the one high-quality scale.</summary>
internal static class SpritePixels
{
    /// <summary>
    /// Bounds of the pixels with alpha above 0, or null when the image has no transparent pixel (every alpha is 255) or no visible
    /// one; the check is on pixels, not on the presence of an alpha channel. The opaque test is vectorized.
    /// </summary>
    internal static SKRectI? AlphaBounds(byte[] rgba, int width, int height)
    {
        if (ImagePixels.IsOpaque(rgba.AsSpan(0, width * height * 4))) return null;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            var first = -1;
            for (var x = 0; x < width; x++)
            {
                if (rgba[row + x * 4 + 3] == 0) continue;
                first = x;
                break;
            }
            if (first < 0) continue;
            var last = first;
            for (var x = width - 1; x > first; x--)
            {
                if (rgba[row + x * 4 + 3] == 0) continue;
                last = x;
                break;
            }
            if (top > y) top = y;
            bottom = y;
            if (first < left) left = first;
            if (last > right) right = last;
        }
        return right < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// The trimmed region scaled to width × height as premultiplied pixels. Large reductions halve with bilinear filtering (a 2×2
    /// average) until within a factor of 2, then a Mitchell cubic gives the exact size; enlargement never happens.
    /// </summary>
    internal static SKImage Scale(RgbaImage decoded, SKRectI bounds, int width, int height)
    {
        var handle = GCHandle.Alloc(decoded.Buffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var whole = new SKPixmap(info, handle.AddrOfPinnedObject(), decoded.Width * 4);
            using var trimmed = whole.ExtractSubset(bounds) ?? throw new InvalidOperationException("Sprite bounds are outside the image.");
            var current = SKImage.FromPixelCopy(trimmed) ?? throw new InvalidOperationException("Sprite pixels could not be copied.");
            try
            {
                while (current.Width >= width * 2 && current.Height >= height * 2)
                {
                    var half = Draw(current, Math.Max(width, current.Width / 2), Math.Max(height, current.Height / 2),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    current.Dispose();
                    current = half;
                }
                // The last draw also premultiplies: the same size is copied as is.
                var exact = current.Width == width && current.Height == height;
                return Draw(current, width, height, exact ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            finally
            {
                current.Dispose();
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static SKImage Draw(SKImage source, int width, int height, SKSamplingOptions sampling)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Sprite surface could not be created.");
        surface.Canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
        {
            surface.Canvas.DrawImage(source, new SKRect(0, 0, source.Width, source.Height), new SKRect(0, 0, width, height), sampling, paint);
        }
        surface.Flush();
        return surface.Snapshot();
    }
}
