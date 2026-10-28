using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Media;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>
/// What the add dialog shows before a run: file headers, a scaled frame for placing the crop and up to six samples made
/// exactly as the run would make them (same cut, radius and pixels). Nothing is stored; the caller decides how to show it.
/// </summary>
internal sealed class BackgroundPreviews(MediaManager media)
{
    internal const int MaxSamples = 6;
    internal const int MinPreviewSide = 64;
    internal const int MaxPreviewSide = 4096;

    // A sample of a video looks at most this many frames for one that is not similar, and all samples together this many:
    // each frame is a separate seek, and the answer must come within the request timeout.
    private const int MaxAttemptsPerSample = 4;
    private const int MaxVideoReads = 20;

    /// <summary>Headers of the files in parallel; the order of the answer is the order of the paths.</summary>
    internal static IReadOnlyList<BackgroundSourceInfo> Inspect(IReadOnlyList<string> paths, InputSize target, CancellationToken cancellationToken)
    {
        var result = new BackgroundSourceInfo[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), CancellationToken = cancellationToken },
            index =>
            {
                var path = paths[index];
                var name = BackgroundImportPlan.Shorten(Path.GetFileName(path));
                if (BackgroundPixels.ReadSize(path) is not { } size)
                {
                    result[index] = new BackgroundSourceInfo(name, null, null, BackgroundSourceFit.Unreadable);
                    return;
                }
                var (width, height) = size;
                var fit = width == target.Width && height == target.Height ? BackgroundSourceFit.Exact
                    : width >= target.Width && height >= target.Height ? BackgroundSourceFit.Larger
                    : BackgroundSourceFit.Smaller;
                result[index] = new BackgroundSourceInfo(name, width, height, fit);
            });
        return result;
    }

    internal Task<BackgroundSourcePreview> PreviewFileAsync(string path, int maxSide, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CheckSide(maxSide);
        return Task.Run(() =>
        {
            try
            {
                using var data = BackgroundPixels.ReadFile(path);
                return BackgroundPixels.PreviewFile(data, maxSide);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new CoreException("backgrounds.image_unreadable", "The image file could not be read.", error);
            }
        }, cancellationToken);
    }

    internal async Task<BackgroundSourcePreview> PreviewVideoFrameAsync(string path, long timestampMs, int maxSide, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CheckSide(maxSide);
        var video = await media.ProbeVideoAsync(path, cancellationToken).ConfigureAwait(false);
        var range = new VideoFrameRange(timestampMs, 1, 1);
        range.Validate(video.DurationMs);
        await using var reader = new VideoFrameReader(media, path, range, seekAboveMs: 0);
        using var frame = await reader.ReadAsync(0, cancellationToken).ConfigureAwait(false)
            ?? throw new CoreException(BackgroundReasons.FrameUnavailable, $"The video has no frame at {timestampMs} ms.");
        return await Task.Run(() => BackgroundPixels.PreviewFrame(frame.Buffer, frame.Width, frame.Height, maxSide), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Samples spread over the sources (for a video, over the first RequestedCount frames). With a similarity index a sample
    /// that repeats the set or an earlier sample is replaced by the next frame of a video, or reported as similar for a file.
    /// Exact repeats of the set are reported as duplicates.
    /// </summary>
    internal async Task<IReadOnlyList<BackgroundImportSample>> SamplesAsync(ImportBackgroundsInput input, InputSize target, BackgroundBlur? blur,
        SimilarityIndex? similar, Func<IReadOnlyCollection<string>, CancellationToken, Task<HashSet<string>>> existingHashes, CancellationToken cancellationToken)
    {
        var (items, _) = BackgroundImportPlan.Expand(input);
        var samples = new List<(BackgroundImportSample Sample, string? PixelHash)>();
        if (input.Video is { } video)
        {
            var info = await media.ProbeVideoAsync(video.Path, cancellationToken).ConfigureAwait(false);
            BackgroundImportPlan.CheckVideo(info, target, video.Region);
            video.Frames.Validate(info.DurationMs);
            var wanted = Math.Min(input.RequestedCount ?? items.Count, items.Count);
            var positions = BackgroundImportPlan.Spread(wanted, MaxSamples);
            await using var reader = new VideoFrameReader(media, video.Path, video.Frames, seekAboveMs: 0);
            var reads = 0;
            for (var position = 0; position < positions.Count; position++)
            {
                var first = positions[position];
                var end = position + 1 < positions.Count ? positions[position + 1] : items.Count;
                (BackgroundImportSample, string?)? taken = null;
                var reason = BackgroundReasons.Similar;
                for (var index = first; index < end && index < first + MaxAttemptsPerSample && reads < MaxVideoReads; index++)
                {
                    reads++;
                    using var frame = await reader.ReadAsync(index, cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                    {
                        reason = BackgroundReasons.FrameUnavailable;
                        break;
                    }
                    using var cut = BackgroundFrames.Cut(frame.Buffer, frame.Width, frame.Height, target, video.Region);
                    if (similar is not null && similar.IsSimilar(cut.Signature!)) continue;
                    similar?.Add(cut.Signature!);
                    taken = await Task.Run(() => Finish(items[index], cut, blur), cancellationToken).ConfigureAwait(false);
                    break;
                }
                samples.Add(taken ?? (Rejected(items[first], reason), null));
            }
        }
        else
        {
            var indexes = BackgroundImportPlan.Spread(items.Count, MaxSamples);
            var cuts = await Task.WhenAll(indexes.Select(index => Task.Run(() =>
                BackgroundFrames.CutFile(items[index].Path, target, items[index].Crop), cancellationToken))).ConfigureAwait(false);
            try
            {
                for (var position = 0; position < indexes.Count; position++)
                {
                    var item = items[indexes[position]];
                    var cut = cuts[position];
                    if (cut.Reason is { } rejected)
                    {
                        samples.Add((Rejected(item, rejected, cut.ActualWidth, cut.ActualHeight), null));
                        continue;
                    }
                    if (similar is not null && similar.IsSimilar(cut.Signature!))
                    {
                        samples.Add((Rejected(item, BackgroundReasons.Similar), null));
                        continue;
                    }
                    similar?.Add(cut.Signature!);
                    samples.Add(await Task.Run(() => Finish(item, cut, blur), cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                foreach (var cut in cuts) cut.Dispose();
            }
        }
        // Exact repeats: pixels already in the set, or the same pixels as an earlier sample.
        var hashes = samples.Select(entry => entry.PixelHash).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var existing = await existingHashes(hashes, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return samples.Select(entry => entry.PixelHash is { } hash && (existing.Contains(hash) || !seen.Add(hash))
            ? entry.Sample with { Reason = BackgroundReasons.Duplicate, Image = null }
            : entry.Sample).ToArray();
    }

    private static (BackgroundImportSample, string?) Finish(BackgroundSourceItem item, CutFrame cut, BackgroundBlur? blur)
    {
        var radius = blur is null ? (double?)null : BlurPlan.Radius(blur, item.Index);
        using var finished = BackgroundFrames.Finish(cut.Image!, radius ?? 0);
        var image = BackgroundPixels.PngImage(finished.Png, cut.Image!.Width, cut.Image.Height);
        return (new BackgroundImportSample(item.Index, item.Name, item.TimestampMs, null, null, null, cut.Crop, radius, image), finished.PixelHash);
    }

    private static BackgroundImportSample Rejected(BackgroundSourceItem item, string reason, int? width = null, int? height = null)
        => new(item.Index, item.Name, item.TimestampMs, reason, width, height, null, null, null);

    private static void CheckSide(int maxSide)
    {
        if (maxSide is < MinPreviewSide or > MaxPreviewSide)
            throw new ArgumentOutOfRangeException(nameof(maxSide), maxSide, $"A preview side is {MinPreviewSide}–{MaxPreviewSide} px.");
    }
}
