namespace MautVision.Core.Projects.Generation;

/// <summary>
/// The random generator of one frame: mulberry32 started at
/// <c>seed ^ imul(sampleIndex + 1, 0x9e3779b9)</c>. A frame depends only on the seed and its index, never on other frames or threads.
/// </summary>
internal struct FrameRandom(uint seed, int sampleIndex)
{
    private uint state = seed ^ unchecked((uint)(sampleIndex + 1) * 0x9e3779b9u);

    /// <summary>The next raw 32-bit value; <see cref="Next"/> divides it by 2^32.</summary>
    internal uint NextUInt32()
    {
        unchecked
        {
            state += 0x6d2b79f5u;
            var value = (state ^ (state >> 15)) * (1u | state);
            value ^= value + (value ^ (value >> 7)) * (61u | value);
            return value ^ (value >> 14);
        }
    }

    /// <summary>A value in [0, 1).</summary>
    internal double Next() => NextUInt32() / 4294967296.0;

    /// <summary>
    /// Background of a frame among <paramref name="count"/> backgrounds (recipe set order, then order of addition):
    /// <c>(seed + imul(sampleIndex + 1, 2654435761)) mod 2^32 mod count</c>. It does not use the frame's generator.
    /// </summary>
    internal static int BackgroundIndex(uint seed, int sampleIndex, int count)
        => (int)(unchecked(seed + (uint)(sampleIndex + 1) * 2654435761u) % (uint)count);
}

/// <summary>Where one object of a frame goes: binding and image indexes of the layout and the rectangle in pixels.</summary>
internal readonly record struct PlannedObject(int Binding, int Asset, int Left, int Top, int Width, int Height);

/// <summary>A planned frame: the background index, the placed objects in drawing order and how many did not fit.</summary>
internal sealed record FramePlan(int SampleIndex, int Background, IReadOnlyList<PlannedObject> Objects, int Missing);

/// <summary>A project variant with its share and the layout bindings it chooses among (in recipe order).</summary>
internal sealed record LayoutWeight(double Percentage, IReadOnlyList<int> Bindings);

/// <summary>
/// What sampling needs of a run: canvas, objects per frame, empty share, scale, the variant weights in recipe order, the image
/// count of every binding and the number of backgrounds. Sizes of images come from their trimmed sprites.
/// </summary>
internal sealed record GenerationLayout(int Width, int Height, int ObjectsPerImage, double EmptyProbability, double Scale,
    IReadOnlyList<LayoutWeight> Weights, IReadOnlyList<int> AssetCounts, int Backgrounds);

/// <summary>
/// Sampling of a frame. Order of draws: empty frame (r &lt; emptyProbability →
/// no objects); per object the project variant by weights (the first weight above 0 with r·100 below the running sum), a binding
/// of that variant uniformly, an image of the binding uniformly, the size, then up to 100 random positions without overlap
/// (touching is allowed) and a deterministic scan of edge candidates. Improvement: an object without room is skipped and counted
/// instead of failing the run.
/// </summary>
internal static class GenerationPlanner
{
    internal const int RandomAttempts = 100;

    /// <param name="spriteSize">Trimmed size of an image (binding, image); may decode it on first use.</param>
    internal static FramePlan Plan(GenerationLayout layout, uint seed, int sampleIndex, Func<int, int, (int Width, int Height)> spriteSize)
    {
        var random = new FrameRandom(seed, sampleIndex);
        var count = random.Next() < layout.EmptyProbability ? 0 : layout.ObjectsPerImage;
        var placed = new List<PlannedObject>(count);
        var missing = 0;
        for (var index = 0; index < count; index++)
        {
            var point = random.Next() * 100;
            var cumulative = 0.0;
            LayoutWeight? selected = null;
            foreach (var weight in layout.Weights)
            {
                cumulative += weight.Percentage;
                if (weight.Percentage > 0 && point < cumulative)
                {
                    selected = weight;
                    break;
                }
            }
            // The sum is 100 ± 1e-6, so a point can fall past it; the last
            // weighted variant takes it. The same draws follow either way.
            selected ??= layout.Weights.Last(weight => weight.Percentage > 0);
            var binding = selected.Bindings[(int)Math.Floor(random.Next() * selected.Bindings.Count)];
            var asset = (int)Math.Floor(random.Next() * layout.AssetCounts[binding]);
            var (spriteWidth, spriteHeight) = spriteSize(binding, asset);
            var (width, height) = ObjectSize(layout, spriteWidth, spriteHeight);
            if (Place(layout, placed, width, height, ref random) is { } position)
                placed.Add(new PlannedObject(binding, asset, position.Left, position.Top, width, height));
            else
                missing++;
        }
        return new FramePlan(sampleIndex, FrameRandom.BackgroundIndex(seed, sampleIndex, layout.Backgrounds), placed, missing);
    }

    /// <summary>
    /// <c>ratio = min(W·scale/w, H·scale/h, W/w, H/h, 1)</c>, side = max(1, round(side·ratio)): a sprite never grows and a
    /// scale above 1 acts as 1. Rounding is JavaScript Math.round (halves up).
    /// </summary>
    internal static (int Width, int Height) ObjectSize(GenerationLayout layout, int spriteWidth, int spriteHeight)
    {
        var ratio = Math.Min(Math.Min(Math.Min(Math.Min(layout.Width * layout.Scale / spriteWidth, layout.Height * layout.Scale / spriteHeight),
            (double)layout.Width / spriteWidth), (double)layout.Height / spriteHeight), 1);
        return (Math.Max(1, Round(spriteWidth * ratio)), Math.Max(1, Round(spriteHeight * ratio)));
    }

    private static (int Left, int Top)? Place(GenerationLayout layout, List<PlannedObject> placed, int width, int height, ref FrameRandom random)
    {
        for (var attempt = 0; attempt < RandomAttempts; attempt++)
        {
            var left = (int)Math.Floor(random.Next() * (layout.Width - width + 1));
            var top = (int)Math.Floor(random.Next() * (layout.Height - height + 1));
            if (Free(placed, left, top, width, height)) return (left, top);
        }
        // Exhaustive edge candidates avoid a false miss caused only by the random attempt budget (insertion order, no repeats).
        var xs = new List<int> { 0 };
        var ys = new List<int> { 0 };
        AddCandidate(xs, layout.Width - width);
        AddCandidate(ys, layout.Height - height);
        foreach (var other in placed)
        {
            AddCandidate(xs, other.Left + other.Width);
            AddCandidate(xs, other.Left - width);
            AddCandidate(ys, other.Top + other.Height);
            AddCandidate(ys, other.Top - height);
        }
        foreach (var x in xs)
        {
            if (x < 0 || x + width > layout.Width) continue;
            foreach (var y in ys)
            {
                if (y < 0 || y + height > layout.Height) continue;
                if (Free(placed, x, y, width, height)) return (x, y);
            }
        }
        return null;
    }

    private static void AddCandidate(List<int> values, int value)
    {
        if (!values.Contains(value)) values.Add(value);
    }

    /// <summary>No overlap with any placed object; touching edges are allowed.</summary>
    private static bool Free(List<PlannedObject> placed, int left, int top, int width, int height)
    {
        foreach (var other in placed)
        {
            if (!(left + width <= other.Left || other.Left + other.Width <= left || top + height <= other.Top || other.Top + other.Height <= top))
                return false;
        }
        return true;
    }

    /// <summary>JavaScript Math.round for non-negative values: the nearest integer, halves up.</summary>
    private static int Round(double value)
    {
        var floor = Math.Floor(value);
        return (int)(value - floor >= 0.5 ? floor + 1 : floor);
    }
}
