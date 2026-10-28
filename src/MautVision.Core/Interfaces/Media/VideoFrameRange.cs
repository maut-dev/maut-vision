namespace MautVision.Core.Interfaces.Media;

/// <summary>
/// Кадры видео на отметках StartMs + i × IntervalMs, i = 0…Count−1. Кадр отметки — первый кадр, время которого не раньше
/// отметки (с допуском 0,5 мс); размер — исходный.
/// </summary>
public sealed record VideoFrameRange(long StartMs, long IntervalMs, int Count)
{
    public long TimestampMs(int index) => StartMs + index * IntervalMs;

    /// <summary>
    /// Начало ≥ 0, интервал ≥ 1 мс, количество ≥ 1 — иначе <c>video.frames_invalid</c>; последняя отметка строго раньше
    /// конца видео — иначе <c>video.range_invalid</c>.
    /// </summary>
    public void Validate(long durationMs)
    {
        if (StartMs < 0 || IntervalMs < 1 || Count < 1)
            throw new CoreException("video.frames_invalid", $"Frame range {StartMs}+{IntervalMs}×{Count} is invalid.");
        long last;
        try
        {
            last = checked(StartMs + (Count - 1L) * IntervalMs);
        }
        catch (OverflowException)
        {
            last = long.MaxValue;
        }
        if (last >= durationMs)
            throw new CoreException("video.range_invalid", $"The last frame at {last} ms is not before the end of the video ({durationMs} ms).");
    }
}
