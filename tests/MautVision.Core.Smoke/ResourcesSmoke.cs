using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Resources;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Resource files of a project: originals, lazily generated thumbnails, the disposable cache and its keys,
/// coalescing and the generation limit, refused kinds and paths. Prints throughput and cached latency.
/// </summary>
internal static class ResourcesSmoke
{
    internal const string Kind = "fixture";
    private static readonly SKColor[] Quadrants = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];

    internal static async Task<int> RunAsync(
        string fixture,
        Func<string, ILoggerFactory?, Task<ICore>> createCore,
        Action<string> seedInstalledModel,
        Func<string, string, Task> linkDirectory,
        List<string> junctions)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task<T> Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        async Task RejectsCode(string code, Func<Task> action)
        {
            var error = await Rejects<CoreException>(action);
            if (error.Code != code) throw new InvalidOperationException($"Expected {code}, got {error.Code}.");
        }

        var root = Path.Combine(fixture, "resources-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var internals = (MautVision.Core.Core)core;
        internals.ResourceKinds.Register(Kind, ResourceKinds.ByExtensions(Kind, ".jpg", ".png", ".mp4"));
        await Rejects<InvalidOperationException>(() => Task.Run(() => internals.ResourceKinds.Register(Kind, ResourceKinds.ByExtensions(Kind, ".jpg"))));
        await Rejects<ArgumentException>(() => Task.Run(() => internals.ResourceKinds.Register("../escape", ResourceKinds.ByExtensions("x", ".jpg"))));
        var generator = internals.Thumbnails;
        Check(generator.Limit == Math.Max(1, Environment.ProcessorCount / 2), "Thumbnail generation is limited to half of the processors.");

        var project = await core.Projects.CreateAsync(new ProjectSettings("Ресурсы", "", "yolo26n", new InputSize(640, 640)));
        var projectDirectory = Path.Combine(root, "projects", project.Id);
        var kindDirectory = Path.Combine(projectDirectory, "files", Kind);
        var cacheDirectory = Path.Combine(projectDirectory, "cache");
        var resources = project.Resources;
        string Original(Guid id, string extension = ".jpg") => Path.Combine(kindDirectory, id.ToString("D") + extension);
        Guid Add(byte[] content, string extension = ".jpg")
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(kindDirectory);
            File.WriteAllBytes(Original(id, extension), content);
            return id;
        }
        async Task<byte[]> ReadAsync(ResourceReference reference, ResourceVariant variant)
        {
            await using var content = await resources.OpenAsync(reference, variant);
            using var copy = new MemoryStream();
            await content.Stream.CopyToAsync(copy);
            return copy.ToArray();
        }
        string[] Thumbnails() => Directory.Exists(cacheDirectory)
            ? Directory.GetFiles(cacheDirectory, "*.jpg", SearchOption.AllDirectories) : [];

        // Original: bytes, type and seekable stream for ranges.
        var plain = Jpeg(640, 480);
        var plainId = Add(plain);
        await using (var original = await resources.OpenAsync(new ResourceReference(Kind, plainId), ResourceVariant.Original))
        {
            Check(original.ContentType == "image/jpeg" && original.Length == plain.Length && original.Stream.CanSeek, "Original opens with its type and length.");
            original.Stream.Seek(100, SeekOrigin.Begin);
            var part = new byte[50];
            await original.Stream.ReadExactlyAsync(part);
            Check(part.AsSpan().SequenceEqual(plain.AsSpan(100, 50)), "Original stream reads a byte range in place.");
        }
        Check(Thumbnails().Length == 0, "Opening an original creates no thumbnail.");

        // Lazy thumbnail with EXIF orientation, for all eight orientations.
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            var id = Add(WithOrientation(QuadrantJpeg(600, 300), orientation));
            using var thumbnail = SKBitmap.Decode(await ReadAsync(new ResourceReference(Kind, id), ResourceVariant.Thumbnail));
            var transposed = orientation >= 5;
            Check(thumbnail.Width == (transposed ? 128 : 256) && thumbnail.Height == (transposed ? 256 : 128),
                $"Orientation {orientation}: thumbnail fits 256 px with displayed proportions.");
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                var (x, y) = (quadrant % 2, quadrant / 2);
                var actual = thumbnail.GetPixel((int)((x + 0.5) * thumbnail.Width / 2), (int)((y + 0.5) * thumbnail.Height / 2));
                var expected = ExpectedQuadrant(orientation, x, y, thumbnail.Width, thumbnail.Height);
                Check(Nearest(actual) == expected, $"Orientation {orientation}: quadrant {quadrant} shows the displayed corner.");
            }
        }

        // Cache hit, change of the original, deleted cache.
        var reference = new ResourceReference(Kind, plainId);
        var generatedBefore = generator.Generated;
        var first = await ReadAsync(reference, ResourceVariant.Thumbnail);
        var cachedFiles = Thumbnails().Where(file => Path.GetFileName(file).StartsWith(plainId.ToString("D"), StringComparison.Ordinal)).ToArray();
        Check(cachedFiles.Length == 1 && generator.Generated == generatedBefore + 1, "First request generates one cached thumbnail.");
        using (var decoded = SKBitmap.Decode(first))
        {
            Check(decoded.Width == 256 && decoded.Height == 192, "Landscape 640x480 thumbnail is 256x192.");
        }
        var cachedWrite = File.GetLastWriteTimeUtc(cachedFiles[0]);
        var second = await ReadAsync(reference, ResourceVariant.Thumbnail);
        Check(second.AsSpan().SequenceEqual(first) && generator.Generated == generatedBefore + 1 && File.GetLastWriteTimeUtc(cachedFiles[0]) == cachedWrite,
            "Cache hit returns the same file without regeneration.");
        Check(Path.GetRelativePath(projectDirectory, cachedFiles[0]).StartsWith(Path.Combine("cache", "thumbnails"), StringComparison.Ordinal),
            "Thumbnails are cached under the project cache directory.");
        File.WriteAllBytes(Original(plainId), Jpeg(320, 640));
        File.SetLastWriteTimeUtc(Original(plainId), DateTime.UtcNow.AddMinutes(1));
        var changed = await ReadAsync(reference, ResourceVariant.Thumbnail);
        var afterChange = Thumbnails().Where(file => Path.GetFileName(file).StartsWith(plainId.ToString("D"), StringComparison.Ordinal)).ToArray();
        using (var decoded = SKBitmap.Decode(changed))
        {
            Check(decoded.Width == 128 && decoded.Height == 256 && generator.Generated == generatedBefore + 2, "Changed original produces a new thumbnail.");
        }
        Check(afterChange.Length == 1 && afterChange[0] != cachedFiles[0], "The outdated thumbnail of a changed original is removed.");
        Directory.Delete(cacheDirectory, recursive: true);
        var regenerated = await ReadAsync(reference, ResourceVariant.Thumbnail);
        Check(regenerated.AsSpan().SequenceEqual(changed) && generator.Generated == generatedBefore + 3, "Deleting the cache is safe: the thumbnail is generated again.");

        // Coalescing and the generation limit.
        var shared = new ResourceReference(Kind, Add(Jpeg(1920, 1080)));
        generatedBefore = generator.Generated;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => ReadAsync(shared, ResourceVariant.Thumbnail))));
        Check(generator.Generated == generatedBefore + 1 && concurrent.All(bytes => bytes.AsSpan().SequenceEqual(concurrent[0])),
            "32 concurrent requests for one thumbnail share one generation.");
        using (var cancelled = new CancellationTokenSource())
        {
            var slow = new ResourceReference(Kind, Add(Jpeg(4000, 3000)));
            var waiting = resources.OpenAsync(slow, ResourceVariant.Thumbnail, cancelled.Token);
            cancelled.Cancel();
            await Rejects<OperationCanceledException>(() => waiting);
            await using var afterCancel = await resources.OpenAsync(slow, ResourceVariant.Thumbnail);
            Check(afterCancel.Length > 0, "A cancelled wait does not break the thumbnail for later requests.");
        }

        // Refusals: unknown kind and ID, paths, undecodable and non-image files.
        await RejectsCode("resources.kind_unknown", () => resources.OpenAsync(new ResourceReference("unregistered", plainId), ResourceVariant.Original));
        await RejectsCode("resources.kind_unknown", () => resources.OpenAsync(new ResourceReference("../" + Kind, plainId), ResourceVariant.Original));
        await RejectsCode("resources.kind_unknown", () => resources.OpenAsync(new ResourceReference(null!, plainId), ResourceVariant.Original));
        await RejectsCode("resources.not_found", () => resources.OpenAsync(new ResourceReference(Kind, Guid.NewGuid()), ResourceVariant.Original));
        await RejectsCode("resources.not_found", () => resources.OpenAsync(new ResourceReference(Kind, Guid.Empty), ResourceVariant.Thumbnail));
        await Rejects<ArgumentOutOfRangeException>(() => resources.OpenAsync(reference, (ResourceVariant)7));
        var broken = Add([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        await RejectsCode("resources.decode_failed", () => resources.OpenAsync(new ResourceReference(Kind, broken), ResourceVariant.Thumbnail));
        var video = Add(new byte[1024], ".mp4");
        await RejectsCode("resources.thumbnail_unsupported", () => resources.OpenAsync(new ResourceReference(Kind, video), ResourceVariant.Thumbnail));
        await using (var videoContent = await resources.OpenAsync(new ResourceReference(Kind, video), ResourceVariant.Original))
        {
            Check(videoContent.ContentType == "video/mp4", "A video original is served with its type.");
        }
        var transparent = Add(TransparentPng(300, 200), ".png");
        using (var decoded = SKBitmap.Decode(await ReadAsync(new ResourceReference(Kind, transparent), ResourceVariant.Thumbnail)))
        {
            Check(decoded.Width == 256 && decoded.Height == 171 && Nearest(decoded.GetPixel(10, 10), [SKColors.White, SKColors.Black]) == SKColors.White,
                "A transparent PNG becomes a JPEG thumbnail on white.");
        }

        var outside = Path.Combine(fixture, "resources-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, plainId.ToString("D") + ".jpg"), plain);
        Directory.Move(kindDirectory, kindDirectory + "-moved");
        await linkDirectory(kindDirectory, outside);
        junctions.Add(kindDirectory);
        await Rejects<IOException>(() => resources.OpenAsync(reference, ResourceVariant.Original));
        Directory.Delete(kindDirectory);
        junctions.Remove(kindDirectory);
        Directory.Move(kindDirectory + "-moved", kindDirectory);
        Directory.Delete(cacheDirectory, recursive: true);
        await linkDirectory(cacheDirectory, outside);
        junctions.Add(cacheDirectory);
        var fresh = await createCore(root, null);
        ((MautVision.Core.Core)fresh).ResourceKinds.Register(Kind, ResourceKinds.ByExtensions(Kind, ".jpg"));
        var freshProject = await fresh.Projects.GetAsync(project.Id);
        await Rejects<IOException>(() => freshProject.Resources.OpenAsync(reference, ResourceVariant.Thumbnail));
        Check(Directory.GetFiles(outside).Length == 1, "A linked cache directory receives no thumbnail.");
        Directory.Delete(cacheDirectory);
        junctions.Remove(cacheDirectory);

        // Measurements: parallel generation through the public API, then cached opens.
        var limit = generator.Limit;
        var small = Enumerable.Range(0, 400).Select(_ => new ResourceReference(Kind, Add(plain))).ToArray();
        var smallRate = await MeasureAsync(resources, small);
        Check(generator.PeakConcurrency <= limit && generator.PeakConcurrency >= 1, "Concurrent generations never exceed the limit.");
        var large = Jpeg(4000, 3000);
        var largeReferences = Enumerable.Range(0, 48).Select(_ => new ResourceReference(Kind, Add(large))).ToArray();
        var largeRate = await MeasureAsync(resources, largeReferences);
        Check(generator.PeakConcurrency <= limit, "The limit also holds for 12 MP originals.");
        var latencies = new List<double>();
        foreach (var item in small.Concat(small))
        {
            var start = Stopwatch.GetTimestamp();
            await using var content = await resources.OpenAsync(item, ResourceVariant.Thumbnail);
            var buffer = new byte[content.Length];
            await content.Stream.ReadExactlyAsync(buffer);
            latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        latencies.Sort();
        Console.WriteLine(FormattableString.Invariant(
            $"MEASURE: thumbnails from 640x480 JPEG {smallRate:F0}/s, from 12 MP JPEG {largeRate:F0}/s (limit {limit}, peak {generator.PeakConcurrency}); cached thumbnail open+read median {latencies[latencies.Count / 2]:F3} ms, p95 {latencies[(int)(latencies.Count * 0.95)]:F3} ms; 12 MP JPEG {large.Length / 1024} KB."));
        return checks;
    }

    private static async Task<double> MeasureAsync(IResourcesManager resources, IReadOnlyList<ResourceReference> references)
    {
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(references.Select(reference => Task.Run(async () =>
        {
            await using var content = await resources.OpenAsync(reference, ResourceVariant.Thumbnail);
        })));
        return references.Count / watch.Elapsed.TotalSeconds;
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.DarkSlateBlue, SKColors.Orange], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, width, height, paint);
            var random = new Random(width * 31 + height);
            using var dots = new SKPaint();
            for (var index = 0; index < 2000; index++)
            {
                dots.Color = new SKColor((uint)random.Next() | 0xFF000000);
                canvas.DrawCircle(random.Next(width), random.Next(height), random.Next(2, 1 + Math.Max(3, width / 100)), dots);
            }
        }
        // Sensor-like noise gives the file the entropy and size of a photo; flat fixtures decode unrealistically fast.
        var pixels = bitmap.GetPixelSpan();
        var noise = new byte[pixels.Length];
        new Random(width + height).NextBytes(noise);
        for (var index = 0; index < pixels.Length; index++)
        {
            if (index % 4 == 3) continue;
            pixels[index] = (byte)Math.Clamp(pixels[index] + (noise[index] >> 3) - 16, 0, 255);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] QuadrantJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint();
            for (var quadrant = 0; quadrant < 4; quadrant++)
            {
                paint.Color = Quadrants[quadrant];
                canvas.DrawRect(quadrant % 2 * width / 2f, quadrant / 2 * height / 2f, width / 2f, height / 2f, paint);
            }
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] TransparentPng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Inserts an APP1 Exif segment with one Orientation tag right after SOI; encoders here cannot write EXIF.</summary>
    internal static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        var length = tiff.Length + 2;
        return [.. jpeg.AsSpan(0, 2), 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. tiff, .. jpeg.AsSpan(2)];
    }

    /// <summary>Displayed pixel → stored pixel, written from the EXIF definitions independently of the generator.</summary>
    private static SKColor ExpectedQuadrant(int orientation, int x, int y, int width, int height)
    {
        // Centre of the displayed quadrant in displayed units, then the stored point it shows (stored size W x H).
        double dx = (x + 0.5) * width / 2, dy = (y + 0.5) * height / 2;
        double storedWidth = orientation >= 5 ? height : width, storedHeight = orientation >= 5 ? width : height;
        var (sx, sy) = orientation switch
        {
            1 => (dx, dy),
            2 => (storedWidth - dx, dy),
            3 => (storedWidth - dx, storedHeight - dy),
            4 => (dx, storedHeight - dy),
            5 => (dy, dx),
            6 => (dy, storedHeight - dx),
            7 => (storedWidth - dy, storedHeight - dx),
            8 => (storedWidth - dy, dx),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation)),
        };
        return Quadrants[(sx < storedWidth / 2 ? 0 : 1) + (sy < storedHeight / 2 ? 0 : 2)];
    }

    private static SKColor Nearest(SKColor actual, SKColor[]? palette = null)
        => (palette ?? Quadrants).MinBy(color => Math.Pow(color.Red - actual.Red, 2) + Math.Pow(color.Green - actual.Green, 2) + Math.Pow(color.Blue - actual.Blue, 2));
}
