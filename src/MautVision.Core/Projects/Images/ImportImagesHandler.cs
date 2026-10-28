using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Media;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Images;

/// <summary>
/// Position and running totals of an import. Count and Digest describe the expanded file list, so a run resumed after a
/// restart detects that the folders changed meanwhile.
/// </summary>
internal sealed record ImageImportCheckpoint(int Next, int Count, string Digest, int Added, int Duplicates, int Rejected,
    IReadOnlyList<ImportRejectedFile> RejectedFiles);

/// <summary>
/// Imports images into a manual group in batches. Within a batch a few workers handle one file each: its bytes are read
/// once, hashed with SHA-256 and fully decoded once (size with EXIF orientation, pixel validation, pixel hash); a valid file
/// is written unchanged next to its final place. Then, in order, duplicates of the group are skipped (when asked), files
/// are renamed into place, the rows of the batch are inserted in one transaction at the end of the group and the checkpoint
/// is saved. An image ID is derived from the job ID and the file index, so a run repeated after a crash rewrites the same
/// file and finds its own row instead of producing a copy. The group revision is not needed: imports never conflict with
/// edits of the group.
/// <para>
/// Frames of a video are items too: one <see cref="VideoFrameReader"/> per video decodes them in order for the workers,
/// which hash the pixels and write PNG files in parallel; the family hash of the video file is read once alongside. A
/// resumed run reads again from the first frame after the checkpoint.
/// </para>
/// </summary>
internal sealed class ImportImagesHandler(IProjectsManager projects, MediaManager media) : IJobHandler<ImportImagesInput>
{
    internal const string Kind = "images.import";
    internal const int BatchSize = 64;
    internal const string InvalidReason = "images.file_invalid";
    internal const string UnreadableReason = "images.file_unreadable";
    internal const string FrameUnavailableReason = "video.frame_unavailable";
    private const int CopyBufferSize = 1 << 20;
    // Files up to this size are read once into memory; camera photos and video frames are well below it.
    private const int InMemoryLimit = 32 << 20;
    private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(500);

    public JobResource Resource => JobResource.Cpu;

    public bool Resumable => true;

    /// <summary>Concurrent file workers inside the one CPU slot the job holds: decoding is CPU bound, copying is I/O bound.</summary>
    internal int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    /// <summary>Smoke seam: awaited at "inserted" (rows committed, checkpoint not saved) and "saved" points of each batch.</summary>
    internal Func<string, int, Task>? Pause { get; set; }

    public async Task ExecuteAsync(ImportImagesInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input?.Sources);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var images = project.ImagesManager;
        var groupId = input.GroupId;
        await images.RequireImportTargetAsync(groupId, token).ConfigureAwait(false);
        var files = await images.GetFilesAsync(token).ConfigureAwait(false);
        var directory = files.CreateDirectory(ProjectImage.ResourceKind);
        // The kind directory is created after the store was opened, so it is checked here before files are written into it.
        if (ProjectStore.ValidateEntry(directory) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            throw new IOException("Image directory must be a directory.");

        // A video is probed on every run: its range must end before the video does, and the tools must be present.
        foreach (var source in input.Sources.Where(source => source?.Kind == ImageImportSourceKind.Video))
        {
            var range = source.Frames ?? throw new Interfaces.CoreException("video.frames_invalid", "A video source has no frame range.");
            range.Validate((await media.ProbeVideoAsync(source.Path, token).ConfigureAwait(false)).DurationMs);
        }
        var (items, digest) = await Task.Run(() => ImportPlan.Expand(input.Sources, token), token).ConfigureAwait(false);
        var checkpoint = context.Checkpoint<ImageImportCheckpoint>();
        var total = items.Count;
        if (checkpoint is not null && (checkpoint.Count != total || checkpoint.Digest != digest))
        {
            context.SetResult(new ImportImagesResult(checkpoint.Count, checkpoint.Added, checkpoint.Duplicates, checkpoint.Rejected, checkpoint.RejectedFiles));
            throw new Interfaces.CoreException("images.import_sources_changed",
                $"Job {context.JobId}: the selected folders changed before the import resumed ({checkpoint.Count} → {total} files).");
        }
        checkpoint ??= new ImageImportCheckpoint(0, total, digest, 0, 0, 0, []);
        var added = checkpoint.Added;
        var duplicates = checkpoint.Duplicates;
        var rejected = checkpoint.Rejected;
        var rejectedFiles = checkpoint.RejectedFiles.ToList();
        ImportImagesResult Summary() => new(total, added, duplicates, rejected, rejectedFiles.ToArray());
        context.SetResult(Summary());
        context.ReportProgress(total == 0 ? 100 : 100.0 * checkpoint.Next / total, null, checkpoint.Next, total);

        // Pixel hashes this run put into the group, with their image IDs.
        var known = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var processed = checkpoint.Next;
        var progressGate = new Lock();
        var lastEvent = Stopwatch.StartNew();
        var unannounced = false;
        // Each video still to read gets one frame reader and the hash of its file, read once alongside the decoding.
        var videos = items.Skip(checkpoint.Next).Select(item => item.Frame?.Source).OfType<int>().Distinct().ToArray();
        using var hashing = CancellationTokenSource.CreateLinkedTokenSource(token);
        var readers = videos.ToDictionary(source => source, source => new VideoFrameReader(media, input.Sources[source].Path, input.Sources[source].Frames!));
        var families = videos.ToDictionary(source => source, source => MediaManager.HashFileAsync(input.Sources[source].Path, hashing.Token));
        try
        {
            for (var start = checkpoint.Next; start < total; start += BatchSize)
            {
                var count = Math.Min(BatchSize, total - start);
                var prepared = new Prepared[count];
                try
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token },
                        async (offset, cancellation) =>
                        {
                            var index = start + offset;
                            var id = JobItemIds.Derive(context.JobId, index);
                            prepared[offset] = items[index].Frame is { } frame
                                ? await PrepareFrameAsync(items[index], frame, readers[frame.Source], families[frame.Source], id, directory, cancellation)
                                    .ConfigureAwait(false)
                                : await PrepareAsync(items[index], id, directory, cancellation).ConfigureAwait(false);
                            lock (progressGate)
                            {
                                processed++;
                                context.ReportProgress(100.0 * processed / total, null, processed, total);
                            }
                        }).ConfigureAwait(false);
                }
                catch
                {
                    foreach (var item in prepared) item?.DiscardTemporary();
                    throw;
                }

                // From here the batch is finished without observing cancellation: it is short and leaves no half-written state.
                var valid = prepared.Where(item => item.Image is not null).ToArray();
                var existing = input.SkipDuplicates
                    ? await images.FindPixelHashesAsync(groupId, valid.Select(item => item.Image!.PixelHash).ToHashSet(StringComparer.Ordinal),
                        valid.Select(item => item.Image!.Id).ToArray(), CancellationToken.None).ConfigureAwait(false)
                    : [];
                var batch = new List<(ImportedImage Image, string Path)>();
                foreach (var item in prepared)
                {
                    if (item.Reason is { } reason)
                    {
                        rejected++;
                        if (rejectedFiles.Count < ImportImagesResult.MaxRejectedFiles) rejectedFiles.Add(new ImportRejectedFile(item.Name, reason));
                        continue;
                    }
                    var image = item.Image!;
                    if (input.SkipDuplicates && (existing.Contains(image.PixelHash) || (known.TryGetValue(image.PixelHash, out var owner) && owner != image.Id)))
                    {
                        duplicates++;
                        item.DiscardTemporary();
                        continue;
                    }
                    var path = files.GetPath(ProjectImage.ResourceKind, image.Id, ImagesSql.Extension(image.Format));
                    File.Move(item.Temporary!, path, overwrite: true);
                    known.TryAdd(image.PixelHash, image.Id);
                    batch.Add((image, path));
                }
                var raise = lastEvent.Elapsed >= EventInterval;
                ImageBatchOutcome outcome;
                try
                {
                    outcome = await images.InsertImportedAsync(groupId, batch.Select(item => item.Image).ToArray(), context.OperationId, input.SkipDuplicates,
                        raise, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The rows were rolled back (for example, the group was deleted meanwhile), so their files must go too.
                    foreach (var item in batch) TryDelete(item.Path);
                    throw;
                }
                added += outcome.Added.Count;
                duplicates += outcome.Duplicates.Count;
                foreach (var duplicate in outcome.Duplicates)
                {
                    // Another import into the same group committed these pixels first.
                    var item = batch.First(entry => entry.Image.Id == duplicate);
                    TryDelete(item.Path);
                    known.Remove(item.Image.PixelHash);
                }
                if (outcome.Added.Count > 0)
                {
                    if (raise) lastEvent.Restart();
                    unannounced = !raise;
                }
                var batchIndex = start / BatchSize;
                if (Pause is { } pause) await pause("inserted", batchIndex).ConfigureAwait(false);
                await context.SaveCheckpointAsync(checkpoint with
                {
                    Next = start + count,
                    Added = added,
                    Duplicates = duplicates,
                    Rejected = rejected,
                    RejectedFiles = rejectedFiles.ToArray(),
                }).ConfigureAwait(false);
                context.SetResult(Summary());
                if (Pause is { } paused) await paused("saved", batchIndex).ConfigureAwait(false);
            }
        }
        finally
        {
            // The last throttled change is always announced, also after cancel or failure.
            if (unannounced) images.NotifyImagesChanged(groupId);
            // ffmpeg runs end with the job, also after cancel or failure; an unfinished hash is stopped and observed.
            foreach (var reader in readers.Values) await reader.DisposeAsync().ConfigureAwait(false);
            await hashing.CancelAsync().ConfigureAwait(false);
            foreach (var family in families.Values)
            {
                try
                {
                    await family.ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
                {
                    // Reported by the frame that needed it, or not needed after a cancel.
                }
            }
        }
        context.Logger.LogInformation("Job {JobId} imported {Added} images into group {GroupId}: {Duplicates} duplicates, {Rejected} rejected of {Total}.",
            context.JobId, added, groupId, duplicates, rejected, total);
    }

    /// <summary>
    /// Reads one source file once: the bytes are hashed and fully decoded, and only a valid image is written next to its
    /// final place. A file above <see cref="InMemoryLimit"/> is streamed to disk and decoded from the copy instead.
    /// Problems with the file reject it; they never fail the job.
    /// </summary>
    private static async Task<Prepared> PrepareAsync(ImportItem item, Guid id, string directory, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, "." + id.ToString("D") + ".import");
        try
        {
            await using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = input.Length;
            if (length == 0) return Prepared.Rejected(item.Name, InvalidReason);
            var (sha, decoded) = length <= InMemoryLimit
                ? await ReadInMemoryAsync(input, (int)length, temporary, cancellationToken).ConfigureAwait(false)
                : await StreamAsync(input, length, temporary, cancellationToken).ConfigureAwait(false);
            if (decoded is not { } image)
            {
                TryDelete(temporary);
                return Prepared.Rejected(item.Name, InvalidReason);
            }
            var lineage = new ImageLineage(ImageLineageKind.File, "file:" + image.PixelHash);
            return new Prepared(item.Name, null,
                new ImportedImage(id, item.Name, image.Format, image.Width, image.Height, length, sha, image.PixelHash, lineage), temporary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TryDelete(temporary);
            return Prepared.Rejected(item.Name, UnreadableReason);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// A video frame: its pixels come decoded from the job's ffmpeg run, so they are hashed directly and written once as PNG
    /// (lossless; decoding the file gives the same pixel hash). A timestamp the video does not reach rejects the frame.
    /// </summary>
    private static async Task<Prepared> PrepareFrameAsync(ImportItem item, ImportFrame frame, VideoFrameReader reader, Task<string> family, Guid id,
        string directory, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, "." + id.ToString("D") + ".import");
        int width, height, length;
        string sha, pixelHash;
        using (var pixels = await reader.ReadAsync(frame.Index, cancellationToken).ConfigureAwait(false))
        {
            if (pixels is null) return Prepared.Rejected(item.Name, FrameUnavailableReason);
            (width, height) = (pixels.Width, pixels.Height);
            pixelHash = ImagePixels.Hash(pixels.Buffer, width, height);
            using var png = ImagePixels.EncodePng(pixels.Buffer, width, height);
            length = (int)png.Size;
            sha = Convert.ToHexStringLower(SHA256.HashData(png.AsSpan()));
            try
            {
                using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1);
                output.Write(png.AsSpan());
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
        try
        {
            var lineage = new ImageLineage(ImageLineageKind.Video, "video:" + await family.WaitAsync(cancellationToken).ConfigureAwait(false),
                frame.VideoName, frame.TimestampMs, frame.Index);
            return new Prepared(item.Name, null, new ImportedImage(id, item.Name, ImageFileFormat.Png, width, height, length, sha, pixelHash, lineage), temporary);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>The usual path: one read into a pooled buffer; the decoded and hashed bytes are exactly the bytes written.</summary>
    private static async Task<(string Sha, DecodedImage? Image)> ReadInMemoryAsync(FileStream input, int length, string temporary,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var filled = 0;
            while (filled < length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(filled, length - filled), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new IOException("Source file changed while it was read.");
                filled += read;
            }
            if (input.Length != length) throw new IOException("Source file changed while it was read.");
            var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, length)));
            DecodedImage? image;
            using (var memory = new MemoryStream(buffer, 0, length, writable: false))
            {
                image = ImagePixels.Decode(memory);
            }
            if (image is null) return (sha, null);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return (sha, image);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A very large file: copied with the hash computed on the way, then the stored copy is decoded.</summary>
    private static async Task<(string Sha, DecodedImage? Image)> StreamAsync(FileStream input, long length, string temporary,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            output.SetLength(length);
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (output.Position != length) throw new IOException("Source file changed while it was copied.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        using var stored = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), ImagePixels.Decode(stored));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover import file is never served: resources resolve only <id>.<ext> of existing rows.
        }
    }

    private sealed record Prepared(string Name, string? Reason, ImportedImage? Image, string? Temporary)
    {
        internal static Prepared Rejected(string name, string reason) => new(name, reason, null, null);

        internal void DiscardTemporary()
        {
            if (Temporary is not null) TryDelete(Temporary);
        }
    }
}
