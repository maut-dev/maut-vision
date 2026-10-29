using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Hashing;
using System.Threading.Channels;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Transfer;

/// <summary>
/// Writes a project into one ZIP (<see cref="ProjectArchive"/>). The database goes in as a consistent snapshot (VACUUM INTO) without
/// the row of this job; files are listed once with their file IDs, so a hard-linked file (a dataset frame and its project image) is
/// stored once and its other paths become manifest links. A few reader threads read whole small files into pooled buffers and compute
/// their CRC-32 (hardware accelerated) while the one writer appends them to the archive in arrival order without seeking; large files
/// (weights) are streamed by the writer itself. File removal of the project is held while the job runs, and nothing else of the
/// project runs (admission), so the snapshot and the files agree.
/// </summary>
internal sealed class ProjectExportHandler(IProjectsManager projects) : IJobHandler<ProjectExportInput>
{
    internal const string Kind = "projects.export";
    // Files up to this size are read whole by a reader thread; larger ones are streamed by the writer.
    internal const int InMemoryLimit = 4 << 20;
    private const int QueueCapacity = 64;
    private const int CopyBuffer = 1 << 20;
    private const int WriteBuffer = 1 << 20;

    public JobResource Resource => JobResource.Cpu;

    /// <summary>Reader threads that feed the writer.</summary>
    internal int Readers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);

    /// <summary>Smoke seam: awaited after each written file with the count of files so far.</summary>
    internal Func<long, Task>? Pause { get; set; }

    /// <summary>The temporary archive of a job: a dot name next to the destination, so the final rename stays on one volume.</summary>
    internal static string TemporaryPath(string destination, Guid jobId)
        => Path.Combine(Path.GetDirectoryName(destination)!, "." + jobId.ToString("N") + ".tmp");

    public async Task ExecuteAsync(ProjectExportInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var store = project.Store;
        await store.EnsureOpenAsync(token).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        var fileName = Path.GetFileName(input.DestinationPath);
        var progress = new Progress();
        ProjectExportResult Summary(long archiveBytes) => new(fileName, Volatile.Read(ref progress.Files), progress.TotalFiles, Interlocked.Read(ref progress.Bytes),
            progress.TotalBytes, progress.Links, archiveBytes, watch.ElapsedMilliseconds);
        context.SetResult(Summary(0));

        var snapshot = Path.Combine(store.ProjectDirectory, ".export-" + context.JobId.ToString("N") + ".db");
        var temporary = TemporaryPath(input.DestinationPath, context.JobId);
        var moved = false;
        long archiveBytes = 0;
        // Material files removed meanwhile would be missing from the archive while the snapshot still names them.
        project.FileCleanup.Suspended = true;
        try
        {
            await project.FileCleanup.CleanupTask.ConfigureAwait(false);
            var schemaVersion = await SnapshotAsync(store, snapshot, context.JobId, token).ConfigureAwait(false);
            var metadata = await File.ReadAllBytesAsync(Path.Combine(store.ProjectDirectory, ProjectArchive.MetadataName), token).ConfigureAwait(false);
            var scanned = await Task.Run(() => FolderScan.Scan(Path.Combine(store.ProjectDirectory, MaterialFiles.DirectoryName), ProjectArchive.FilesPrefix,
                ProjectArchive.SkipDirectory, token), token).ConfigureAwait(false);
            var (stored, links) = Deduplicate(scanned);
            var databaseLength = new FileInfo(snapshot).Length;
            progress.TotalFiles = stored.Count + 2;
            progress.TotalBytes = stored.Sum(file => file.Length) + metadata.Length + databaseLength;
            progress.Links = links.Count;
            context.ReportProgress(0, null, 0, progress.TotalFiles);
            context.Logger.LogInformation("Project {ProjectId} export {JobId}: {Files} files ({Bytes} bytes), {Links} links listed in {Elapsed} ms.",
                context.ProjectId, context.JobId, progress.TotalFiles, progress.TotalBytes, links.Count, watch.ElapsedMilliseconds);

            var manifest = new ArchiveManifest
            {
                Format = ProjectArchive.Format,
                FormatVersion = ProjectArchive.FormatVersion,
                ProjectId = project.Id,
                ProjectName = project.Name,
                SchemaVersion = schemaVersion,
                CreatedAt = DateTimeOffset.UtcNow,
                Files = progress.TotalFiles,
                Bytes = progress.TotalBytes,
                Links = links,
            };
            archiveBytes = await Task.Run(() => WriteAsync(temporary, metadata, snapshot, stored, manifest, progress, context), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The user confirmed the overwrite in "Save as"; the old file is replaced only by a complete archive.
            File.Move(temporary, input.DestinationPath, overwrite: true);
            moved = true;
            context.Logger.LogInformation(
                "Project {ProjectId} exported by job {JobId}: {Files} files, {Bytes} bytes, {Links} links, archive {ArchiveBytes} bytes in {Elapsed} ms ({Rate} MB/s).",
                context.ProjectId, context.JobId, progress.Files, progress.Bytes, links.Count, archiveBytes, watch.ElapsedMilliseconds,
                (progress.Bytes / 1048576.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds)).ToString("F1", CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is not (CoreException or OperationCanceledException))
        {
            context.Logger.LogError(error, "Export of project {ProjectId} by job {JobId} failed.", context.ProjectId, context.JobId);
            throw new CoreException(FailureCode(error, "projects.export_failed"), error.Message, error);
        }
        finally
        {
            project.FileCleanup.Suspended = false;
            _ = project.FileCleanup.Request();
            TryDelete(snapshot, context.Logger);
            if (!moved) TryDelete(temporary, context.Logger);
            context.SetResult(Summary(archiveBytes));
        }
    }

    /// <summary>
    /// A consistent copy of project.db in one statement, without -wal/-shm; the row of this job is removed from the copy, so the imported
    /// project does not show an export that never finished there. Returns the schema version of the copy.
    /// </summary>
    private static async Task<int> SnapshotAsync(ProjectStore store, string snapshot, Guid jobId, CancellationToken cancellationToken)
    {
        File.Delete(snapshot);
        await store.ReadAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", snapshot);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var builder = new SqliteConnectionStringBuilder { DataSource = snapshot, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        await using var copy = new SqliteConnection(builder.ToString());
        await copy.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = copy.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = DELETE;
                DELETE FROM job_steps WHERE job_id = $id;
                DELETE FROM jobs WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var version = copy.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Files that share a file ID are one file under several names: the shallowest path (the material file, files/images/&lt;id&gt;, rather
    /// than its dataset frame) is stored, ties in ordinal order; the others are links to it.
    /// </summary>
    internal static (List<ScannedFile> Stored, List<ArchiveLink> Links) Deduplicate(IReadOnlyList<ScannedFile> scanned)
    {
        var stored = new List<ScannedFile>(scanned.Count);
        var links = new List<ArchiveLink>();
        var groups = new Dictionary<ulong, List<ScannedFile>>();
        foreach (var file in scanned)
        {
            if (file.FileId == 0)
            {
                stored.Add(file);
                continue;
            }
            if (!groups.TryGetValue(file.FileId, out var group)) groups.Add(file.FileId, group = new List<ScannedFile>(1));
            group.Add(file);
        }
        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                stored.Add(group[0]);
                continue;
            }
            group.Sort((left, right) => Depth(left.RelativePath) != Depth(right.RelativePath)
                ? Depth(left.RelativePath).CompareTo(Depth(right.RelativePath))
                : string.CompareOrdinal(left.RelativePath, right.RelativePath));
            stored.Add(group[0]);
            for (var index = 1; index < group.Count; index++) links.Add(new ArchiveLink(group[index].RelativePath, group[0].RelativePath));
        }
        links.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return (stored, links);
    }

    private static int Depth(string path) => path.Count(character => character == '/');

    private async Task<long> WriteAsync(string temporary, byte[] metadata, string snapshot, IReadOnlyList<ScannedFile> stored, ArchiveManifest manifest,
        Progress progress, JobContext context)
    {
        var token = context.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queue = Channel.CreateBounded<Prefetched>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var readers = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(stored, new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = stop.Token }, async (file, cancel) =>
                {
                    var item = file.Length <= InMemoryLimit ? Prefetch(file) : new Prefetched(file, null, 0);
                    try
                    {
                        await queue.Writer.WriteAsync(item, cancel).ConfigureAwait(false);
                    }
                    catch
                    {
                        if (item.Buffer is not null) ArrayPool<byte>.Shared.Return(item.Buffer);
                        throw;
                    }
                }).ConfigureAwait(false);
                queue.Writer.TryComplete();
            }
            catch (Exception error)
            {
                queue.Writer.TryComplete(error);
            }
        }, CancellationToken.None);

        var copy = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try
        {
            await using var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = WriteBuffer,
                // Reserving the size reports a full disk before the work, not at its end.
                PreallocationSize = progress.TotalBytes + 256L * (stored.Count + 8),
            });
            var zip = new ZipWriter(output);
            var created = manifest.CreatedAt.UtcDateTime;
            zip.AddDeflated(ProjectArchive.MetadataName, new MemoryStream(metadata), created, CompressionLevel.Optimal, copy, null, token);
            Count(progress, context, metadata.Length);
            await using (var database = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                zip.AddDeflated(ProjectArchive.DatabaseName, database, created, CompressionLevel.Fastest, copy,
                    read => Interlocked.Add(ref progress.Bytes, read), token);
            }
            Count(progress, context, 0);
            await foreach (var item in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var file = item.File;
                if (item.Buffer is { } buffer)
                {
                    try
                    {
                        zip.AddStored(file.RelativePath, buffer.AsSpan(0, (int)file.Length), item.Crc, file.LastWriteTimeUtc);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    Count(progress, context, file.Length);
                }
                else
                {
                    await using (var source = Open(file))
                    {
                        zip.AddStored(file.RelativePath, source, file.Length, file.LastWriteTimeUtc, copy, read => Interlocked.Add(ref progress.Bytes, read), token);
                    }
                    Count(progress, context, 0);
                }
                if (Pause is { } pause) await pause(progress.Files).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            zip.AddDeflated(ProjectArchive.ManifestName, new MemoryStream(ProjectArchive.WriteManifest(manifest)), created, CompressionLevel.Optimal, copy, null,
                token);
            zip.Finish();
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return zip.Position;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            // Buffers of files read but not written go back to the pool.
            while (queue.Reader.TryRead(out var left))
            {
                if (left.Buffer is not null) ArrayPool<byte>.Shared.Return(left.Buffer);
            }
            await readers.ConfigureAwait(false);
            while (queue.Reader.TryRead(out var left))
            {
                if (left.Buffer is not null) ArrayPool<byte>.Shared.Return(left.Buffer);
            }
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    private static void Count(Progress progress, JobContext context, long bytes)
    {
        var files = Interlocked.Increment(ref progress.Files);
        var total = Interlocked.Add(ref progress.Bytes, bytes);
        if (files % 64 == 0 || files == progress.TotalFiles)
        {
            context.ReportProgress(progress.TotalBytes == 0 ? 100 : Math.Min(100, 100.0 * total / progress.TotalBytes), null, files, progress.TotalFiles);
        }
    }

    /// <summary>Reads a small file whole into a pooled buffer and computes its CRC-32 on the reader thread.</summary>
    private static Prefetched Prefetch(ScannedFile file)
    {
        var length = (int)file.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            using var source = Open(file);
            if (source.Length != file.Length) throw new IOException($"File {file.RelativePath} changed while it was exported.");
            source.ReadExactly(buffer, 0, length);
            return new Prefetched(file, buffer, Crc32.HashToUInt32(buffer.AsSpan(0, length)));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static FileStream Open(ScannedFile file)
        => new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);

    /// <summary>projects.disk_full for a full disk, projects.out_of_memory, otherwise the given code.</summary>
    internal static string FailureCode(Exception error, string fallback)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "projects.disk_full";
            if (current is OutOfMemoryException) return "projects.out_of_memory";
        }
        return fallback;
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Temporary export file {File} was not removed.", Path.GetFileName(path));
        }
    }

    private sealed record Prefetched(ScannedFile File, byte[]? Buffer, uint Crc);

    /// <summary>Counters shared by the readers, the writer and the result.</summary>
    private sealed class Progress
    {
        internal long Files;
        internal long Bytes;
        internal long TotalFiles;
        internal long TotalBytes;
        internal long Links;
    }
}
