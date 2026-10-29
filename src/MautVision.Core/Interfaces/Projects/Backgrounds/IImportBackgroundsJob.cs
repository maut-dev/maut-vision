using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>Top-left corner of a set-sized crop frame in pixels of the source frame (with EXIF orientation applied).</summary>
public sealed record BackgroundCrop(int Left, int Top);

/// <summary>
/// Image file (PNG, JPEG, WebP, BMP). Path — absolute path; stored in the job input and never leaves it.
/// Crop — the "Cut from images" frame; without it the whole file is used and must be exactly the set size.
/// </summary>
public sealed record BackgroundFileSource(string Path, BackgroundCrop? Crop = null);

/// <summary>
/// "Cut from video": frames at the Frames marks, one Region crop is cut from each. Frames are usually all available
/// frames with the chosen step (<see cref="AvailableFrames"/>), and the stop is set by <see cref="ImportBackgroundsInput.RequestedCount"/>.
/// </summary>
public sealed record BackgroundVideoSource(string Path, VideoFrameRange Frames, BackgroundCrop Region)
{
    /// <summary>
    /// How many frames a video of durationMs gives with marks startMs + i × intervalMs:
    /// max(0, ⌊(durationMs − 1 − startMs) / intervalMs⌋ + 1). The last mark is strictly before the end of the video.
    /// </summary>
    public static int AvailableFrames(long durationMs, long startMs, long intervalMs)
    {
        if (intervalMs < 1 || startMs < 0 || startMs >= durationMs) return 0;
        return (int)Math.Min(int.MaxValue, (durationMs - 1 - startMs) / intervalMs + 1);
    }
}

/// <summary>
/// Gaussian blur (sigma = radius): the frame radius is chosen deterministically from Seed and the source index in the range
/// MinRadius–MaxRadius; below 0.3 px the background stays sharp. Seed null — derived from the project and operation IDs, so samples
/// and the run of one operation match; the run result stores the chosen seed.
/// </summary>
public sealed record BackgroundBlur(double MinRadius, double MaxRadius, uint? Seed = null);

/// <summary>
/// Input of the job that prepares backgrounds into a set. Exactly one way: Files (ready frames and cuts from images) or Video.
/// RequestedCount — how many backgrounds to add (further sources are not read; null — all sources). SkipSimilar — skip
/// frames whose signature barely differs (≤ 3/255) from the backgrounds of the set and those already taken by this run. Exact duplicates
/// (the same pixels in the set) are always skipped.
/// </summary>
public sealed record ImportBackgroundsInput(
    Guid SetId,
    IReadOnlyList<BackgroundFileSource> Files,
    BackgroundVideoSource? Video = null,
    int? RequestedCount = null,
    bool SkipSimilar = false,
    BackgroundBlur? Blur = null)
{
    public const double MaxBlurRadius = 1000;

    /// <summary>
    /// No sources — <c>backgrounds.sources_empty</c>; a radius outside 0–1000 or "from" greater than "to" — <c>backgrounds.blur_invalid</c>;
    /// a negative crop — <c>backgrounds.crop_invalid</c>; RequestedCount below 1 or above the number of sources —
    /// <c>backgrounds.count_invalid</c>; video range — <see cref="VideoFrameRange.Validate"/> (without duration: only
    /// <c>video.frames_invalid</c>).
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Files);
        if (Files.Count > 0 && Video is not null) throw new ArgumentException("A run takes either files or one video.");
        if (Files.Count == 0 && Video is null) throw new CoreException("backgrounds.sources_empty", "A background run has no sources.");
        if (Blur is { } blur && !(double.IsFinite(blur.MinRadius) && double.IsFinite(blur.MaxRadius)
            && blur.MinRadius >= 0 && blur.MaxRadius <= MaxBlurRadius && blur.MinRadius <= blur.MaxRadius))
        {
            throw new CoreException("backgrounds.blur_invalid", $"Blur radius {blur.MinRadius}–{blur.MaxRadius} is outside 0–{MaxBlurRadius} or reversed.");
        }
        foreach (var file in Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path)) throw new ArgumentException("A background source needs a path.");
            if (file.Crop is { } crop && (crop.Left < 0 || crop.Top < 0)) throw CropInvalid();
        }
        var sources = Files.Count;
        if (Video is { } video)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(video.Path);
            ArgumentNullException.ThrowIfNull(video.Frames);
            if (video.Region is null || video.Region.Left < 0 || video.Region.Top < 0) throw CropInvalid();
            if (video.Frames.StartMs < 0 || video.Frames.IntervalMs < 1 || video.Frames.Count < 1)
                throw new CoreException("video.frames_invalid", "Video frames of a background run are invalid.");
            sources = video.Frames.Count;
        }
        if (RequestedCount is { } count && (count < 1 || count > sources))
            throw new CoreException("backgrounds.count_invalid", $"Requested {count} backgrounds of {sources} sources.");
    }

    private static CoreException CropInvalid() => new("backgrounds.crop_invalid", "A crop frame has a negative position.");
}

/// <summary>
/// Source skip reason. SourceIndex — source index (for video — frame index), Name — file or video name,
/// Reason — code: <c>backgrounds.wrong_size</c> and <c>backgrounds.crop_out_of_bounds</c> (Width/Height — file or frame size),
/// <c>backgrounds.image_unreadable</c>, <c>backgrounds.similar</c>, <c>backgrounds.duplicate</c>, <c>video.frame_unavailable</c>.
/// </summary>
public sealed record BackgroundImportIssue(int SourceIndex, string Name, string Reason, int? Width = null, int? Height = null, long? TimestampMs = null);

/// <summary>
/// Run result (in <see cref="JobInfo.Result"/>, also after cancellation and failure): Total — sources (files or video frames),
/// Processed — examined, Added, Duplicates (exact duplicates), Similar (nearly identical), Rejected; RequestedCount — the run
/// target; TargetSize — set size; Blur — blur with the chosen seed; Issues — the first <see cref="MaxIssues"/> skips.
/// </summary>
public sealed record ImportBackgroundsResult(
    int Total,
    int Processed,
    int Added,
    int Duplicates,
    int Similar,
    int Rejected,
    int? RequestedCount,
    InputSize TargetSize,
    BackgroundBlur? Blur,
    IReadOnlyList<BackgroundImportIssue> Issues)
{
    public const int MaxIssues = 100;
}

/// <summary>
/// Preparing backgrounds into a set (CPU, resumable). Frame: decoding with orientation → size or crop check → cut →
/// 32×32 signature → blur → RGBA PNG of exactly the set size. Frames are processed in parallel, duplicate decisions are made
/// in source order. Set not found — <c>backgrounds.set_not_found</c>, archived — <c>backgrounds.set_archived</c>;
/// video smaller than the set — <c>backgrounds.video_too_small</c>, crop outside the frame — <c>backgrounds.region_invalid</c>, video
/// errors — <c>video.*</c> codes; sources changed before resuming — <c>backgrounds.import_sources_changed</c>; no
/// source fitted — <c>backgrounds.no_usable_media</c>. Cancellation and failure keep what was added.
/// </summary>
[JobKind("backgrounds.import")]
public interface IImportBackgroundsJob : IJobDefinition<ImportBackgroundsInput>
{
}
