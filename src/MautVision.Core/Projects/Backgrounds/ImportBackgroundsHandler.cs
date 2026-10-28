using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Jobs;
using MautVision.Core.Media;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>
/// Position and running totals of a run. Count and Digest describe the expanded sources, so a run resumed after a restart
/// detects a replaced video or another list of files.
/// </summary>
internal sealed record BackgroundImportCheckpoint(int Next, int Count, string Digest, int Added, int Duplicates, int Similar, int Rejected,
    IReadOnlyList<BackgroundImportIssue> Issues);

/// <summary>
/// Prepares backgrounds into a set in batches. A batch goes through four steps: workers cut their sources in parallel
/// (decode with orientation or a video frame, size check or crop, signature); the decisions follow in source order
/// (rejections, frames similar to the set or to frames taken earlier, the requested count); workers blur and encode the
/// accepted frames in parallel; then exact repeats of the set are dropped, files are renamed into place, the rows are
/// inserted in one transaction and the checkpoint is saved. A background ID is derived from the job ID and the source index,
/// so a run repeated after a crash finds its own rows. The set revision is not needed: runs never conflict with edits.
/// </summary>
internal sealed class ImportBackgroundsHandler(IProjectsManager projects, MediaManager media) : IJobHandler<ImportBackgroundsInput>
{
    internal const string Kind = "backgrounds.import";
    internal const int BatchSize = 32;
    private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(500);

    public JobResource Resource => JobResource.Cpu;

    public bool Resumable => true;

    /// <summary>Concurrent frame workers inside the one CPU slot the job holds.</summary>
    internal int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>Smoke seam: awaited at "inserted" (rows committed, checkpoint not saved) and "saved" points of each batch.</summary>
    internal Func<string, int, Task>? Pause { get; set; }

    public async Task ExecuteAsync(ImportBackgroundsInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Validate();
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var backgrounds = project.BackgroundsManager;
        var set = await backgrounds.RequireImportTargetAsync(input.SetId, token).ConfigureAwait(false);
        var target = set.TargetSize;
        var blur = BlurPlan.Resolve(context.ProjectId, context.OperationId, input.Blur);
        var files = await backgrounds.GetFilesAsync(token).ConfigureAwait(false);
        var directory = files.CreateDirectory(BackgroundItem.ResourceKind);
        // The kind directory is created after the store was opened, so it is checked here before files are written into it.
        if (ProjectStore.ValidateEntry(directory) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("Background directory must be a directory.");
        if (input.Video is { } video)
        {
            // Probed on every run: the region must fit and the range must end before the video does.
            var info = await media.ProbeVideoAsync(video.Path, token).ConfigureAwait(false);
            BackgroundImportPlan.CheckVideo(info, target, video.Region);
            video.Frames.Validate(info.DurationMs);
        }
        var (items, digest) = BackgroundImportPlan.Expand(input);
        var total = items.Count;
        var requested = input.RequestedCount ?? total;
        var checkpoint = context.Checkpoint<BackgroundImportCheckpoint>();
        if (checkpoint is not null && (checkpoint.Count != total || checkpoint.Digest != digest))
        {
            context.SetResult(new ImportBackgroundsResult(checkpoint.Count, checkpoint.Next, checkpoint.Added, checkpoint.Duplicates, checkpoint.Similar,
                checkpoint.Rejected, input.RequestedCount, target, blur, checkpoint.Issues));
            throw new CoreException("backgrounds.import_sources_changed", $"Job {context.JobId}: the sources changed before the run resumed.");
        }
        checkpoint ??= new BackgroundImportCheckpoint(0, total, digest, 0, 0, 0, 0, []);
        var next = checkpoint.Next;
        var added = checkpoint.Added;
        var duplicates = checkpoint.Duplicates;
        var similarCount = checkpoint.Similar;
        var rejected = checkpoint.Rejected;
        var issues = checkpoint.Issues.ToList();
        ImportBackgroundsResult Summary() => new(total, next, added, duplicates, similarCount, rejected, input.RequestedCount, target, blur, issues.ToArray());
        void Report() => context.ReportProgress(Math.Min(100, 100.0 * Math.Max((double)next / Math.Max(1, total), (double)added / Math.Max(1, requested))),
            null, next, total);
        void Issue(BackgroundSourceItem item, string reason, int? width = null, int? height = null)
        {
            if (issues.Count < ImportBackgroundsResult.MaxIssues) issues.Add(new BackgroundImportIssue(item.Index, item.Name, reason, width, height, item.TimestampMs));
        }
        context.SetResult(Summary());
        Report();

        // Signatures of the set, removed backgrounds and frames of an interrupted earlier run of this job included.
        var similar = input.SkipSimilar ? await backgrounds.LoadSignaturesAsync(set.Id, token).ConfigureAwait(false) : null;
        // Pixel hashes this run put into the set, with their IDs.
        var known = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var lastEvent = Stopwatch.StartNew();
        var unannounced = false;
        using var hashing = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reader = input.Video is { } source && next < total ? new VideoFrameReader(media, source.Path, source.Frames) : null;
        var videoHash = reader is null ? null : MediaManager.HashFileAsync(input.Video!.Path, hashing.Token);
        try
        {
            for (var batch = 0; next < total && added < requested; batch++)
            {
                var count = Math.Min(BatchSize, total - next);
                // Without much left to add a batch is not larger than needed: the rest of the video is not decoded.
                if (requested < total) count = Math.Min(count, Math.Max(Workers, requested - added));
                var start = next;
                var cuts = new CutFrame?[count];
                var finished = new FinishedFrame?[count];
                var temporaries = new string?[count];
                try
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token },
                        async (offset, cancellation) => cuts[offset] = await CutAsync(items[start + offset], reader, target, cancellation).ConfigureAwait(false))
                        .ConfigureAwait(false);

                    // Decisions in source order; a frame is compared with the set and with the frames this run took before it.
                    var accepted = new List<int>();
                    var decided = count;
                    for (var offset = 0; offset < count; offset++)
                    {
                        if (added + accepted.Count >= requested)
                        {
                            decided = offset;
                            break;
                        }
                        var cut = cuts[offset]!;
                        var item = items[start + offset];
                        if (cut.Reason is { } reason)
                        {
                            rejected++;
                            Issue(item, reason, cut.ActualWidth, cut.ActualHeight);
                        }
                        else if (similar is not null && similar.IsSimilar(cut.Signature!))
                        {
                            similarCount++;
                            Issue(item, BackgroundReasons.Similar);
                        }
                        else
                        {
                            similar?.Add(cut.Signature!);
                            accepted.Add(offset);
                        }
                    }

                    await Parallel.ForEachAsync(accepted, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, (offset, _) =>
                    {
                        var item = items[start + offset];
                        var frame = BackgroundFrames.Finish(cuts[offset]!.Image!, blur is null ? 0 : BlurPlan.Radius(blur, item.Index));
                        finished[offset] = frame;
                        var temporary = Path.Combine(directory, "." + JobItemIds.Derive(context.JobId, item.Index).ToString("D") + ".import");
                        temporaries[offset] = temporary;
                        using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1);
                        output.Write(frame.Png.AsSpan());
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
                    var sourceHash = videoHash is null ? null : await videoHash.WaitAsync(token).ConfigureAwait(false);

                    // From here the batch is finished without observing cancellation: it is short and leaves no half-written state.
                    var ids = accepted.Select(offset => JobItemIds.Derive(context.JobId, items[start + offset].Index)).ToArray();
                    var existing = await backgrounds.FindPixelHashesAsync(set.Id, accepted.Select(offset => finished[offset]!.PixelHash).ToHashSet(StringComparer.Ordinal),
                        ids, CancellationToken.None).ConfigureAwait(false);
                    var rows = new List<(ImportedBackground Row, int Offset, string Path)>();
                    foreach (var (offset, id) in accepted.Zip(ids))
                    {
                        var item = items[start + offset];
                        var frame = finished[offset]!;
                        if (existing.Contains(frame.PixelHash) || (known.TryGetValue(frame.PixelHash, out var owner) && owner != id))
                        {
                            duplicates++;
                            Issue(item, BackgroundReasons.Duplicate);
                            TryDelete(temporaries[offset]);
                            continue;
                        }
                        var path = files.GetPath(BackgroundItem.ResourceKind, id, ".png");
                        File.Move(temporaries[offset]!, path, overwrite: true);
                        temporaries[offset] = null;
                        known.TryAdd(frame.PixelHash, id);
                        var cut = cuts[offset]!;
                        var radius = blur is null ? (double?)null : BlurPlan.Radius(blur, item.Index);
                        var origin = input.Video is not null
                            ? new BackgroundOrigin(BackgroundOriginKind.VideoFrame, context.OperationId, item.Name, sourceHash!, item.Name, item.TimestampMs,
                                item.Index, cut.Crop, radius, BackgroundFrames.ProcessorVersion)
                            : new BackgroundOrigin(cut.Crop is null ? BackgroundOriginKind.Uploaded : BackgroundOriginKind.ImageCrop, context.OperationId, item.Name,
                                cut.SourceHash!, null, null, null, cut.Crop, radius, BackgroundFrames.ProcessorVersion);
                        rows.Add((new ImportedBackground(id, target.Width, target.Height, frame.Png.Size, frame.Sha256, frame.PixelHash, cut.Signature!, origin),
                            offset, path));
                    }
                    var raise = lastEvent.Elapsed >= EventInterval;
                    BackgroundBatchOutcome outcome;
                    try
                    {
                        outcome = await backgrounds.InsertImportedAsync(set.Id, rows.Select(row => row.Row).ToArray(), raise, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The rows were rolled back (for example, the set was deleted meanwhile), so their files must go too.
                        foreach (var row in rows) TryDelete(row.Path);
                        throw;
                    }
                    added += outcome.Added.Count;
                    foreach (var duplicate in outcome.Duplicates)
                    {
                        // Another run into the same set committed these pixels first.
                        var row = rows.First(entry => entry.Row.Id == duplicate);
                        duplicates++;
                        Issue(items[start + row.Offset], BackgroundReasons.Duplicate);
                        TryDelete(row.Path);
                        known.Remove(row.Row.PixelHash);
                    }
                    if (outcome.Added.Count > 0)
                    {
                        if (raise) lastEvent.Restart();
                        unannounced = !raise;
                    }
                    next = start + decided;
                    if (Pause is { } pause) await pause("inserted", batch).ConfigureAwait(false);
                    await context.SaveCheckpointAsync(checkpoint with
                    {
                        Next = next,
                        Added = added,
                        Duplicates = duplicates,
                        Similar = similarCount,
                        Rejected = rejected,
                        Issues = issues.ToArray(),
                    }).ConfigureAwait(false);
                    context.SetResult(Summary());
                    Report();
                    if (Pause is { } paused) await paused("saved", batch).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var cut in cuts) cut?.Dispose();
                    foreach (var frame in finished) frame?.Dispose();
                    foreach (var temporary in temporaries) TryDelete(temporary);
                }
            }
        }
        finally
        {
            // The last throttled change is always announced, also after cancel or failure.
            if (unannounced) backgrounds.NotifyItemsChanged(set.Id);
            // The ffmpeg run ends with the job; an unfinished hash is stopped and observed.
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            await hashing.CancelAsync().ConfigureAwait(false);
            if (videoHash is not null)
            {
                try
                {
                    await videoHash.ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
                {
                    // Reported by the batch that needed it, or not needed after a cancel.
                }
            }
        }
        context.Logger.LogInformation("Job {JobId} prepared {Added} backgrounds into set {SetId}: {Duplicates} duplicates, {Similar} similar, {Rejected} rejected of {Processed}/{Total}.",
            context.JobId, added, set.Id, duplicates, similarCount, rejected, next, total);
        if (next > 0 && rejected == next)
            throw new CoreException("backgrounds.no_usable_media", $"Job {context.JobId}: none of {next} sources could be used.");
    }

    /// <summary>Cuts one source; a file problem or a timestamp past the video rejects the source, it never fails the run.</summary>
    private static async Task<CutFrame> CutAsync(BackgroundSourceItem item, VideoFrameReader? reader, InputSize target, CancellationToken cancellationToken)
    {
        if (reader is null) return BackgroundFrames.CutFile(item.Path, target, item.Crop);
        using var frame = await reader.ReadAsync(item.Index, cancellationToken).ConfigureAwait(false);
        return frame is null
            ? CutFrame.Rejected(BackgroundReasons.FrameUnavailable)
            : BackgroundFrames.Cut(frame.Buffer, frame.Width, frame.Height, target, item.Crop);
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover run file is never served: resources resolve only <id>.png of existing rows.
        }
    }
}
