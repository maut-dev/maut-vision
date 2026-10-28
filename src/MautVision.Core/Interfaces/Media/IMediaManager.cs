namespace MautVision.Core.Interfaces.Media;

/// <summary>
/// Video for importing frames (images, later backgrounds). Read by ffmpeg and ffprobe from <see cref="CoreOptions.FfmpegDirectory"/>;
/// without them — <c>video.ffmpeg_missing</c>. Network protocols are not read, only a local file.
/// </summary>
public interface IMediaManager
{
    /// <summary>
    /// Properties of the first video track: frame size with rotation applied, duration, frame rate, codec. The file is not hashed.
    /// An extension not in <see cref="VideoFormats.Extensions"/> — <c>video.unsupported</c>; the file is unreadable, has no video track
    /// or no duration — <c>video.unreadable</c>.
    /// </summary>
    Task<VideoInfo> ProbeVideoAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Width/Height — size of the produced frames (rotation from metadata already applied); FrameRate — average rate, 0 if unknown.</summary>
public sealed record VideoInfo(int Width, int Height, long DurationMs, double FrameRate, string Codec);

public static class VideoFormats
{
    /// <summary>MP4, MKV, AVI, MOV, WebM, M4V.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v"];

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
