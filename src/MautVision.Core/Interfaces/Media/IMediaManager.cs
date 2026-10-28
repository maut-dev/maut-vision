namespace MautVision.Core.Interfaces.Media;

/// <summary>
/// Видео для импорта кадров (изображения, позже фоны). Читается ffmpeg и ffprobe из <see cref="CoreOptions.FfmpegDirectory"/>;
/// без них — <c>video.ffmpeg_missing</c>. Сетевые протоколы не читаются, только локальный файл.
/// </summary>
public interface IMediaManager
{
    /// <summary>
    /// Свойства первой видеодорожки: размер кадра с учётом поворота, длительность, частота кадров, кодек. Файл не хешируется.
    /// Расширение не из <see cref="VideoFormats.Extensions"/> — <c>video.unsupported</c>; файл не читается, нет видеодорожки
    /// или длительности — <c>video.unreadable</c>.
    /// </summary>
    Task<VideoInfo> ProbeVideoAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Width/Height — размер выдаваемых кадров (поворот из метаданных уже применён); FrameRate — средняя частота, 0 если неизвестна.</summary>
public sealed record VideoInfo(int Width, int Height, long DurationMs, double FrameRate, string Codec);

public static class VideoFormats
{
    /// <summary>MP4, MKV, AVI, MOV, WebM, M4V.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v"];

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
