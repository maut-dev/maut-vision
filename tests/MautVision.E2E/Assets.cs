using System.Diagnostics;
using SkiaSharp;

/// <summary>Synthetic inputs made at runtime: objects with transparency, backgrounds, a larger photo to crop and ffmpeg clips.</summary>
internal static class Assets
{
    /// <summary>A shape of one class on a transparent canvas; variant changes size and turn so that each image is different.</summary>
    internal static void WriteObject(string path, int classIndex, int variant)
    {
        var size = 96 + variant * 24;
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(size / 2f, size / 2f);
        canvas.RotateDegrees(variant * 17);
        var colors = new[] { new SKColor(230, 40, 40), new SKColor(30, 200, 60), new SKColor(40, 90, 240) };
        using var fill = new SKPaint { Color = colors[classIndex], IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5 };
        var radius = size * 0.42f;
        switch (classIndex)
        {
            case 0:
                canvas.DrawCircle(0, 0, radius, fill);
                canvas.DrawCircle(0, 0, radius * 0.5f, stroke);
                break;
            case 1:
                var (x, y) = (radius * 0.87f, radius * 0.5f);
                using (var triangle = SKPath.ParseSvgPathData(FormattableString.Invariant($"M0,{-radius} L{x},{y} L{-x},{y} Z")))
                {
                    canvas.DrawPath(triangle, fill);
                    canvas.DrawPath(triangle, stroke);
                }
                break;
            default:
                canvas.DrawRect(-radius * 0.8f, -radius * 0.8f, radius * 1.6f, radius * 1.6f, fill);
                canvas.DrawLine(-radius * 0.8f, -radius * 0.8f, radius * 0.8f, radius * 0.8f, stroke);
                break;
        }
        Save(surface, path, SKEncodedImageFormat.Png);
    }

    /// <summary>A noisy gradient background; the seed makes every background different (exact duplicates are skipped by Core).</summary>
    internal static void WriteBackground(string path, int width, int height, int seed, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var (r0, g0, b0) = (random.Next(40, 200), random.Next(40, 200), random.Next(40, 200));
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var noise = random.Next(-24, 25);
                pixels[offset] = Clamp(r0 + x * 60 / width + noise);
                pixels[offset + 1] = Clamp(g0 + y * 60 / height + noise);
                pixels[offset + 2] = Clamp(b0 - x * 40 / width + noise);
                pixels[offset + 3] = 255;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 92);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    internal static async Task FfmpegAsync(string ffmpeg, params string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("ffmpeg did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"ffmpeg exited with {process.ExitCode}: {await stderr}{await stdout}");
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void Save(SKSurface surface, string path, SKEncodedImageFormat format)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
