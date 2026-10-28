namespace MautVision.Core.Interfaces.Media;

/// <summary>
/// Video frames at marks StartMs + i × IntervalMs, i = 0…Count−1. The frame of a mark is the first frame whose time is not earlier
/// than the mark (with a 0.5 ms tolerance); the size is the original one.
/// </summary>
public sealed record VideoFrameRange(long StartMs, long IntervalMs, int Count)
{
    public long TimestampMs(int index) => StartMs + index * IntervalMs;

    /// <summary>
    /// Start ≥ 0, interval ≥ 1 ms, count ≥ 1 — otherwise <c>video.frames_invalid</c>; the last mark must be strictly before
    /// the end of the video — otherwise <c>video.range_invalid</c>.
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
