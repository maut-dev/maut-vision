using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Datasets;

/// <summary>Durable position of a build: the contiguous prefix of produced samples (by position) and how they were produced.</summary>
internal sealed record DatasetBuildCheckpoint(int Done, int Linked, int Copied, int Converted);

/// <summary>
/// One dataset build: coordinates the images and the catalog (sources read again and the plan recomputed, so a stale plan is
/// refused) and the dataset tables. Preparation writes the version (building) and all sample rows at once. Then windows of
/// samples are produced in parallel — the source file is read once: its SHA-256 is checked, JPEG and PNG without orientation
/// become a hard link (the hash read is the hash of the written file), other frames a PNG of the oriented pixels; the label is
/// written — and each window's results are saved before the checkpoint. A window repeated after a crash replaces its files and
/// rows, so nothing is duplicated. Publication checks pixels across splits, writes manifest.json and data.yaml and makes the
/// version ready. Cancel and failure discard the version and its folder; a process that dies resumes from the checkpoint.
/// </summary>
internal sealed class DatasetBuildHandler(IProjectsManager projects) : IJobHandler<DatasetBuildInput>
{
    internal const string Kind = "datasets.build";

    public JobResource Resource => JobResource.Cpu;

    public bool Resumable => true;

    /// <summary>Readers of source files inside the one CPU slot the build holds: the CPU slot policy (half of the logical processors).</summary>
    internal int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Samples produced and saved together: one transaction and one checkpoint per window.</summary>
    internal int WindowSize { get; set; } = 4096;

    /// <summary>Smoke seam: awaited at "produced" (files and rows saved, checkpoint not) and "saved" points of each window.</summary>
    internal Func<string, int, Task>? Pause { get; set; }

    public async Task ExecuteAsync(DatasetBuildInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Specification);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var datasets = project.DatasetsManager;
        var versionId = context.JobId;
        var checkpoint = context.Checkpoint<DatasetBuildCheckpoint>() ?? new DatasetBuildCheckpoint(0, 0, 0, 0);
        var total = 0;
        var counts = new DatasetSplitCounts(0, 0, 0);
        DatasetBuildResult Summary() => new(versionId, total, checkpoint.Done, checkpoint.Linked, checkpoint.Copied, checkpoint.Converted, counts);
        void Report() => context.ReportProgress(total == 0 ? 0 : 100.0 * checkpoint.Done / total, null, checkpoint.Done, total);

        var state = await datasets.FindVersionStateAsync(versionId, token).ConfigureAwait(false);
        if (state == "ready")
        {
            // The process died after the version was published and before the job saved its end.
            total = checkpoint.Done;
            context.SetResult(Summary());
            return;
        }
        var completed = false;
        try
        {
            var watch = Stopwatch.StartNew();
            if (state != "building" || checkpoint.Done == 0)
            {
                var selection = await datasets.SelectForBuildAsync(input, token).ConfigureAwait(false);
                await datasets.CreateBuildAsync(versionId, input, selection, token).ConfigureAwait(false);
                checkpoint = new DatasetBuildCheckpoint(0, 0, 0, 0);
                context.Logger.LogInformation("Dataset build {VersionId} of project {ProjectId} prepared {Samples} samples in {Elapsed} ms.",
                    versionId, context.ProjectId, selection.Frames.Count, watch.ElapsedMilliseconds);
            }
            total = await datasets.CountSamplesAsync(versionId, token).ConfigureAwait(false);
            var files = await datasets.GetFilesAsync(token).ConfigureAwait(false);
            var parent = files.CreateDirectory(DatasetFiles.Kind);
            if (ProjectStore.ValidateEntry(parent) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("The datasets folder must be a directory.");
            var root = DatasetFiles.VersionDirectory(files, versionId);
            DatasetFiles.CreateLayout(root);
            if (ProjectStore.ValidateEntry(root) is not { } rootAttributes || (rootAttributes & FileAttributes.Directory) == 0)
                throw new IOException("The version folder must be a directory.");
            context.SetResult(Summary());
            Report();

            var window = 0;
            while (checkpoint.Done < total)
            {
                token.ThrowIfCancellationRequested();
                var rows = await datasets.ReadWindowAsync(versionId, checkpoint.Done, WindowSize, token).ConfigureAwait(false);
                if (rows.Count == 0) throw new InvalidOperationException($"Dataset build {versionId} lost its samples at position {checkpoint.Done}.");
                var (produced, kinds) = await ProduceWindowAsync(files, root, rows, token).ConfigureAwait(false);
                // From here the window is saved without observing cancellation: the rows match the files already in place.
                await datasets.SaveProducedAsync(versionId, produced, CancellationToken.None).ConfigureAwait(false);
                if (Pause is { } produce) await produce("produced", window).ConfigureAwait(false);
                checkpoint = new DatasetBuildCheckpoint(checkpoint.Done + rows.Count, checkpoint.Linked + kinds.Count(kind => kind == Linked),
                    checkpoint.Copied + kinds.Count(kind => kind == Copied), checkpoint.Converted + kinds.Count(kind => kind == Converted));
                await context.SaveCheckpointAsync(checkpoint).ConfigureAwait(false);
                context.SetResult(Summary());
                Report();
                if (Pause is { } pause) await pause("saved", window).ConfigureAwait(false);
                window++;
            }
            token.ThrowIfCancellationRequested();
            var publish = Stopwatch.StartNew();
            counts = await datasets.PublishBuildAsync(versionId, root, token).ConfigureAwait(false);
            completed = true;
            context.SetResult(Summary());
            context.Logger.LogInformation(
                "Dataset build {VersionId} of project {ProjectId}: {Samples} samples ({Linked} linked, {Copied} copied, {Converted} converted) in {Elapsed} ms, published in {Publish} ms.",
                versionId, context.ProjectId, total, checkpoint.Linked, checkpoint.Copied, checkpoint.Converted, watch.ElapsedMilliseconds, publish.ElapsedMilliseconds);
        }
        catch (Exception error) when (error is not (CoreException or OperationCanceledException or JobDataException))
        {
            context.Logger.LogError(error, "Dataset build {VersionId} of project {ProjectId} failed.", versionId, context.ProjectId);
            throw new CoreException(FailureCode(error), error.Message, error);
        }
        finally
        {
            // Success keeps the version; cancel and failure leave nothing. A process that dies does not get here and resumes.
            if (!completed) await datasets.DiscardBuildAsync(versionId).ConfigureAwait(false);
        }
    }

    private const int Linked = 0;
    private const int Copied = 1;
    private const int Converted = 2;

    /// <summary>A sample after its source was read: the link to create (null — the PNG is already written) and its label.</summary>
    private sealed record PreparedSample(SampleRow Row, string? LinkSource, ProducedSample Sample, byte[] Label)
    {
        internal int Index { get; init; }
    }

    /// <summary>
    /// One window in two steps. First the sources are read in parallel: each is checked against its SHA-256 and either kept for a
    /// link or converted to a PNG right away. Then the directory entries are made: NTFS serialises insertions into one directory
    /// (about 4 500 per second on an SSD whatever the thread count, and more threads only add contention), so every directory —
    /// images and labels of each split — gets one stream of its own and the streams run side by side.
    /// </summary>
    private async Task<(ProducedSample[] Produced, int[] Kinds)> ProduceWindowAsync(MaterialFiles files, string root, List<SampleRow> rows,
        CancellationToken token)
    {
        var prepared = new PreparedSample[rows.Count];
        var kinds = new int[rows.Count];
        await Parallel.ForAsync(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, (index, _) =>
        {
            prepared[index] = Prepare(files, root, rows[index]) with { Index = index };
            kinds[index] = prepared[index].LinkSource is null ? Converted : Linked;
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        var streams = new List<Task>();
        foreach (var split in prepared.GroupBy(item => item.Row.Split))
        {
            var items = split.ToArray();
            streams.Add(Task.Run(() =>
            {
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.LinkSource is null) continue;
                    var target = DatasetFiles.Full(root, DatasetFiles.ImagePath(item.Row.Split, item.Row.ImageId, item.Row.SourceFormat));
                    if (!DatasetFiles.Link(item.LinkSource, target)) kinds[item.Index] = Copied;
                }
            }, token));
            streams.Add(Task.Run(() =>
            {
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    DatasetFiles.Write(DatasetFiles.Full(root, DatasetFiles.LabelPath(item.Row.Split, item.Row.ImageId)), item.Label);
                }
            }, token));
        }
        await Task.WhenAll(streams).ConfigureAwait(false);
        return (prepared.Select(item => item.Sample).ToArray(), kinds);
    }

    /// <summary>One sample's source read once and checked; a frame that cannot be linked is converted and written here.</summary>
    private static PreparedSample Prepare(MaterialFiles files, string root, SampleRow row)
    {
        var source = files.GetPath(ProjectImage.ResourceKind, row.ImageId, ImagesSql.Extension(row.SourceFormat));
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(source);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CoreException("datasets.source_missing", $"The file of image {row.ImageId} is missing.", error)
                { Details = new Dictionary<string, string> { ["imageId"] = row.ImageId.ToString("D") } };
        }
        var sha = DatasetFiles.Hash(bytes);
        if (!string.Equals(sha, row.SourceSha256, StringComparison.Ordinal))
        {
            throw new CoreException("datasets.source_changed", $"The file of image {row.ImageId} differs from the one imported.")
                { Details = new Dictionary<string, string> { ["imageId"] = row.ImageId.ToString("D") } };
        }
        var label = DatasetFiles.Label(row.Boxes);
        var labelHash = DatasetFiles.Hash(label);
        if (DatasetImages.CanLink(row.SourceFormat, bytes))
        {
            // The bytes hashed here are the bytes of the link: project files never change.
            return new PreparedSample(row, source, new ProducedSample(row.Position, row.SourceFormat, bytes.LongLength, sha, labelHash, row.PixelHash), label);
        }
        var converted = DatasetImages.Convert(bytes);
        DatasetFiles.Write(DatasetFiles.Full(root, DatasetFiles.ImagePath(row.Split, row.ImageId, ImageFileFormat.Png)), converted.Png);
        return new PreparedSample(row, null, new ProducedSample(row.Position, ImageFileFormat.Png, converted.Png.LongLength, DatasetFiles.Hash(converted.Png),
            labelHash, converted.PixelHash), label);
    }
    /// <summary>datasets.disk_full, datasets.out_of_memory, otherwise datasets.build_failed.</summary>
    internal static string FailureCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL; SQLITE_FULL, SQLITE_NOMEM.
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "datasets.disk_full";
            if (current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 13 }) return "datasets.disk_full";
            if (current is OutOfMemoryException or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 7 }) return "datasets.out_of_memory";
        }
        return "datasets.build_failed";
    }
}
