using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MautVision.Perf;

/// <summary>
/// A few real JPEGs encoded once and reused for every material file: generation stays I/O bound and the files are
/// valid images of a typical small dataset size.
/// </summary>
internal static class SampleImages
{
    internal const int Width = 480;
    internal const int Height = 360;

    internal static byte[][] Create(int count)
    {
        byte[][] result = [];
        // WPF imaging objects are created on an STA thread of their own.
        var thread = new Thread(() => result = Enumerable.Range(0, count).Select(Encode).ToArray());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static byte[] Encode(int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[Width * Height * 3];
        var (r0, g0, b0) = (random.Next(256), random.Next(256), random.Next(256));
        // A gradient background with a few flat boxes and light noise, like an object on a scene.
        var boxes = Enumerable.Range(0, 4).Select(_ => (X: random.Next(Width - 80), Y: random.Next(Height - 80), W: 40 + random.Next(120),
            H: 40 + random.Next(120), Color: (byte)random.Next(256))).ToArray();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 3;
                var noise = random.Next(-3, 4);
                var box = boxes.FirstOrDefault(b => x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H);
                if (box.W > 0)
                {
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = Clamp(box.Color + noise);
                    continue;
                }
                pixels[offset] = Clamp(r0 + x * 64 / Width + noise);
                pixels[offset + 1] = Clamp(g0 + y * 64 / Height + noise);
                pixels[offset + 2] = Clamp(b0 + (x + y) * 32 / (Width + Height) + noise);
            }
        }
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Rgb24, null, pixels, Width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
