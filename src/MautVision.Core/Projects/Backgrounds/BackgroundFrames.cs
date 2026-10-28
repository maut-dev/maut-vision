using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Projects.Images;
using SkiaSharp;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>Reason codes of a skipped source; Desktop gives each a Russian text.</summary>
internal static class BackgroundReasons
{
    internal const string WrongSize = "backgrounds.wrong_size";
    internal const string CropOutOfBounds = "backgrounds.crop_out_of_bounds";
    internal const string Unreadable = "backgrounds.image_unreadable";
    internal const string Similar = "backgrounds.similar";
    internal const string Duplicate = "backgrounds.duplicate";
    internal const string FrameUnavailable = "video.frame_unavailable";
}

/// <summary>
/// The blur radius of every source of a run: a hash of the seed and the source index
/// spreads the radius over MinRadius–MaxRadius, rounded to 0.01 px; below 0.3 px the frame stays sharp. The default seed is
/// the first big-endian uint32 of SHA-256(projectId + ":" + operationId), so samples and the run of one operation agree.
/// </summary>
internal static class BlurPlan
{
    internal const double SharpBelow = 0.3;

    /// <summary>The blur of a run with its seed; null when the run does not blur (no settings or a maximum of 0).</summary>
    internal static BackgroundBlur? Resolve(string projectId, string operationId, BackgroundBlur? blur)
        => blur is null || blur.MaxRadius == 0 ? null : blur with { Seed = blur.Seed ?? DefaultSeed(projectId, operationId) };

    internal static uint DefaultSeed(string projectId, string operationId)
        => BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(projectId + ":" + operationId)));

    internal static double Radius(BackgroundBlur blur, int sourceIndex)
    {
        var seed = blur.Seed ?? throw new ArgumentException("The blur has no seed yet.", nameof(blur));
        unchecked
        {
            var value = seed ^ ((uint)(sourceIndex + 1) * 0x9e3779b9u);
            value = (value ^ (value >> 16)) * 0x45d9f3bu;
            value = (value ^ (value >> 16)) * 0x45d9f3bu;
            value ^= value >> 16;
            // JavaScript-style Math.round: floor(x + 0.5).
            var radius = Math.Floor((blur.MinRadius + (blur.MaxRadius - blur.MinRadius) * value / 4294967296.0) * 100 + 0.5) / 100;
            return radius < SharpBelow ? 0 : radius;
        }
    }
}

/// <summary>A source cut to the set size with its signature, or the reason it cannot be.</summary>
internal sealed class CutFrame : IDisposable
{
    private CutFrame(RgbaImage? image, BackgroundCropArea? crop, byte[]? signature, string? reason, int? width, int? height)
    {
        Image = image;
        Crop = crop;
        Signature = signature;
        Reason = reason;
        ActualWidth = width;
        ActualHeight = height;
    }

    internal RgbaImage? Image { get; }

    internal BackgroundCropArea? Crop { get; }

    internal byte[]? Signature { get; }

    internal string? Reason { get; }

    internal int? ActualWidth { get; }

    internal int? ActualHeight { get; }

    /// <summary>SHA-256 of the source file a cut came from; null for a video frame (its video is hashed once per run).</summary>
    internal string? SourceHash { get; init; }

    internal static CutFrame Accepted(RgbaImage image, BackgroundCropArea? crop, byte[] signature, string? sourceHash)
        => new(image, crop, signature, null, null, null) { SourceHash = sourceHash };

    internal static CutFrame Rejected(string reason, int? width = null, int? height = null) => new(null, null, null, reason, width, height);

    public void Dispose() => Image?.Dispose();
}

/// <summary>The stored form of a background: PNG bytes, their SHA-256 and the pixel hash of the RGBA frame.</summary>
internal sealed record FinishedFrame(SKData Png, string Sha256, string PixelHash) : IDisposable
{
    public void Dispose() => Png.Dispose();
}

/// <summary>
/// One frame of a run: size check or cut at the crop, the signature of the sharp cut, then blur and lossless PNG RGBA of
/// exactly the set size. Cutting and finishing are separate so a run decides about similar frames before it spends time on
/// blur and encoding.
/// </summary>
internal static class BackgroundFrames
{
    internal const string ProcessorVersion = "sharp-frame-v1";

    /// <summary>
    /// Without a crop the frame must be exactly the set size (<see cref="BackgroundReasons.WrongSize"/>); with one the frame of
    /// the set size at (Left, Top) must lie inside (<see cref="BackgroundReasons.CropOutOfBounds"/>).
    /// </summary>
    internal static CutFrame Cut(byte[] pixels, int width, int height, InputSize target, BackgroundCrop? crop, string? sourceHash = null)
    {
        RgbaImage image;
        BackgroundCropArea? area = null;
        if (crop is null)
        {
            if (width != target.Width || height != target.Height) return CutFrame.Rejected(BackgroundReasons.WrongSize, width, height);
            image = BackgroundPixels.Cut(pixels, width, 0, 0, width, height);
        }
        else
        {
            if ((long)crop.Left + target.Width > width || (long)crop.Top + target.Height > height)
                return CutFrame.Rejected(BackgroundReasons.CropOutOfBounds, width, height);
            image = BackgroundPixels.Cut(pixels, width, crop.Left, crop.Top, target.Width, target.Height);
            area = new BackgroundCropArea(crop.Left, crop.Top, target.Width, target.Height);
        }
        return CutFrame.Accepted(image, area, BackgroundSignature.Compute(image.Buffer, image.Width, image.Height), sourceHash);
    }

    /// <summary>
    /// Reads a file once: its SHA-256 and one full decode with orientation, then the cut. An unreadable or undecodable file
    /// is a <see cref="BackgroundReasons.Unreadable"/> rejection, never an error of the run.
    /// </summary>
    internal static CutFrame CutFile(string path, InputSize target, BackgroundCrop? crop)
    {
        try
        {
            using var data = BackgroundPixels.ReadFile(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(data.AsSpan()));
            using var image = BackgroundPixels.Decode(data);
            if (image is null) return CutFrame.Rejected(BackgroundReasons.Unreadable);
            return Cut(image.Buffer, image.Width, image.Height, target, crop, hash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CutFrame.Rejected(BackgroundReasons.Unreadable);
        }
    }

    /// <summary>Blurs the cut in place (radius 0 — sharp) and encodes it; the pixel hash is the one the stored PNG decodes to.</summary>
    internal static FinishedFrame Finish(RgbaImage image, double radius)
    {
        BackgroundPixels.Blur(image, radius);
        var pixelHash = ImagePixels.Hash(image.Buffer.AsSpan(0, image.Length), image.Width, image.Height);
        var png = ImagePixels.EncodePng(image.Buffer, image.Width, image.Height);
        return new FinishedFrame(png, Convert.ToHexStringLower(SHA256.HashData(png.AsSpan())), pixelHash);
    }
}
