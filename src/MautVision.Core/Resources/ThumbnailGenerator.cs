using System.Collections.Concurrent;
using System.Diagnostics;
using MautVision.Core.Interfaces;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MautVision.Core.Resources;

/// <summary>
/// Creates thumbnail files for all projects. Generation runs on the thread pool, at most <see cref="Limit"/> at a time;
/// concurrent requests for the same destination share one generation. A requester's cancellation stops only its wait:
/// the file is still written, so the next request finds it in the cache.
/// </summary>
internal sealed class ThumbnailGenerator
{
    internal const int MaxSide = 256;
    internal const int Quality = 80;

    private readonly SemaphoreSlim gate;
    private readonly ConcurrentDictionary<string, Lazy<Task>> inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger logger;
    private int active;
    private int peak;
    private long generated;

    internal ThumbnailGenerator(ILogger logger, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
        Limit = limit ?? Math.Max(1, Environment.ProcessorCount / 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(Limit, 1, nameof(limit));
        gate = new SemaphoreSlim(Limit, Limit);
    }

    internal int Limit { get; }

    /// <summary>Generations finished since start; for checks and measurements.</summary>
    internal long Generated => Interlocked.Read(ref generated);

    /// <summary>Largest number of generations that ran at the same time.</summary>
    internal int PeakConcurrency => Volatile.Read(ref peak);

    /// <summary>
    /// Writes the thumbnail of source to destination unless another caller is already doing it.
    /// staleSearchPattern selects earlier thumbnails of the same material in the destination directory; they are removed.
    /// </summary>
    internal Task EnsureAsync(string source, string destination, string staleSearchPattern, CancellationToken cancellationToken)
    {
        var entry = inflight.GetOrAdd(destination, key => new Lazy<Task>(
            () => GenerateAsync(source, key, staleSearchPattern), LazyThreadSafetyMode.ExecutionAndPublication));
        return entry.Value.WaitAsync(cancellationToken);
    }

    private async Task GenerateAsync(string source, string destination, string staleSearchPattern)
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => Generate(source, destination, staleSearchPattern)).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            inflight.TryRemove(destination, out _);
        }
    }

    private void Generate(string source, string destination, string staleSearchPattern)
    {
        var running = Interlocked.Increment(ref active);
        for (var seen = Volatile.Read(ref peak); running > seen; seen = Volatile.Read(ref peak))
        {
            if (Interlocked.CompareExchange(ref peak, running, seen) == seen) break;
        }
        var watch = Stopwatch.StartNew();
        try
        {
            if (File.Exists(destination))
            {
                return;
            }
            byte[] encoded;
            int width;
            int height;
            using (var data = ReadAll(source))
            {
                (encoded, width, height) = Encode(data);
            }
            var directory = Path.GetDirectoryName(destination)!;
            var temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
                {
                    output.Write(encoded);
                }
                // The key contains the size and time of the original, so an existing file already has the same content.
                File.Move(temporary, destination, overwrite: false);
            }
            catch (IOException) when (File.Exists(destination))
            {
            }
            finally
            {
                TryDelete(temporary);
            }
            Interlocked.Increment(ref generated);
            RemoveStale(directory, staleSearchPattern, destination);
            logger.LogDebug("Thumbnail {Width}x{Height} generated in {ElapsedMilliseconds} ms.", width, height, watch.ElapsedMilliseconds);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }

    private static SKData ReadAll(string path)
    {
        // Skia opens paths through its own narrow-character API; a .NET stream keeps non-ASCII profile paths working.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
        return SKData.Create(input) ?? throw Undecodable();
    }

    /// <summary>Decodes at the smallest codec scale that still covers the target, then fits, orients and encodes.</summary>
    internal static (byte[] Jpeg, int Width, int Height) Encode(SKData data)
    {
        using var codec = SKCodec.Create(data) ?? throw Undecodable();
        var origin = codec.EncodedOrigin;
        var transposed = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var stored = codec.Info;
        if (stored.Width <= 0 || stored.Height <= 0)
        {
            throw Undecodable();
        }
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(stored.Width, stored.Height));
        // Target size before orientation; the stored image is rotated only while drawing.
        var targetWidth = Math.Max(1, (int)Math.Round(stored.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(stored.Height * scale));

        var decodeSize = codec.GetScaledDimensions((float)scale);
        if (decodeSize.Width < targetWidth || decodeSize.Height < targetHeight)
        {
            decodeSize = new SKSizeI(stored.Width, stored.Height);
        }
        using var srgb = SKColorSpace.CreateSrgb();
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw Undecodable();
        }
        decoded.SetImmutable();
        using var image = SKImage.FromBitmap(decoded);

        var outputWidth = transposed ? targetHeight : targetWidth;
        var outputHeight = transposed ? targetWidth : targetHeight;
        using var output = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul, srgb));
        using (var canvas = new SKCanvas(output))
        {
            // JPEG has no alpha; transparent areas become white, as in most image viewers.
            canvas.Clear(SKColors.White);
            canvas.SetMatrix(Orientation(origin, targetWidth, targetHeight));
            using var paint = new SKPaint();
            canvas.DrawImage(image, new SKRect(0, 0, targetWidth, targetHeight), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        using var encoded = output.Encode(SKEncodedImageFormat.Jpeg, Quality) ?? throw Undecodable();
        return (encoded.ToArray(), outputWidth, outputHeight);
    }

    /// <summary>Maps the stored image (width x height) to its display orientation, as EXIF Orientation describes.</summary>
    internal static SKMatrix Orientation(SKEncodedOrigin origin, float width, float height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private void RemoveStale(string directory, string searchPattern, string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, searchPattern))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An outdated thumbnail is never served: its name holds the size and time of an earlier original.
            logger.LogDebug(error, "Outdated thumbnails were not removed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cache files are disposable; a leftover is removed with the cache.
        }
    }

    private static CoreException Undecodable() => new("resources.decode_failed", "The image could not be decoded.");
}
