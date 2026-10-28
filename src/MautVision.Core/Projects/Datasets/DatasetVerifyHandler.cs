using System.Collections.Concurrent;
using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Datasets;

/// <summary>
/// Integrity check of a version: every file — manifest.json, data.yaml, each image and label — is read and compared with its
/// SHA-256, in parallel (reads dominate, so the disk sets the pace). The check does not stop at
/// the first problem: all of them are counted and the first <see cref="DatasetVerifyResult.MaxIssues"/> listed. The version
/// becomes verified or corrupt; a cancelled check leaves its state as it was.
/// </summary>
internal sealed class DatasetVerifyHandler(IProjectsManager projects) : IJobHandler<DatasetVerifyInput>
{
    internal const string Kind = "datasets.verify";
    internal const string FileMissing = "datasets.file_missing";
    internal const string FileChanged = "datasets.file_changed";
    internal const string FileUnreadable = "datasets.file_unreadable";

    public JobResource Resource => JobResource.Cpu;

    /// <summary>Parallel readers; more than the CPU slot policy pays off on SSDs, whose queue depth hides latency.</summary>
    internal int Workers { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

    public async Task ExecuteAsync(DatasetVerifyInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var datasets = project.DatasetsManager;
        var row = await datasets.RequireOperableAsync(input.VersionId, export: false, token).ConfigureAwait(false);
        var files = await datasets.GetFilesAsync(token).ConfigureAwait(false);
        var root = DatasetFiles.VersionDirectory(files, input.VersionId);
        var samples = await datasets.ReadSamplesAsync(input.VersionId, token).ConfigureAwait(false);
        var checks = new List<(Guid? Sample, DatasetSplit? Split, DatasetFileRole Role, string Path, string Hash)>(2 + 2 * samples.Count)
        {
            (null, null, DatasetFileRole.Manifest, Path.Combine(root, DatasetFiles.ManifestName), row.Version.ManifestHash),
            (null, null, DatasetFileRole.DataYaml, Path.Combine(root, DatasetFiles.DataYamlName), row.DataYamlHash),
        };
        foreach (var sample in samples)
        {
            checks.Add((sample.ImageId, sample.Split, DatasetFileRole.Image,
                DatasetFiles.Full(root, DatasetFiles.ImagePath(sample.Split, sample.ImageId, sample.Format ?? ImageFileFormat.Png)), sample.ImageHash ?? ""));
            checks.Add((sample.ImageId, sample.Split, DatasetFileRole.Label, DatasetFiles.Full(root, DatasetFiles.LabelPath(sample.Split, sample.ImageId)),
                sample.LabelHash ?? ""));
        }
        var issues = new ConcurrentBag<(int Order, DatasetIntegrityIssue Issue)>();
        var watch = Stopwatch.StartNew();
        long bytes = 0;
        var done = 0;
        DatasetVerifyResult Summary(DatasetIntegrityState? state)
        {
            var ordered = issues.OrderBy(item => item.Order).Select(item => item.Issue).ToArray();
            return new DatasetVerifyResult(input.VersionId, checks.Count, Volatile.Read(ref done), ordered.Length, ordered.Take(DatasetVerifyResult.MaxIssues).ToArray(),
                state, Interlocked.Read(ref bytes), watch.ElapsedMilliseconds);
        }
        context.SetResult(Summary(null));
        context.ReportProgress(0, null, 0, checks.Count);
        try
        {
            await Parallel.ForAsync(0, checks.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, (index, _) =>
            {
                var check = checks[index];
                var code = Check(check.Path, check.Hash, out var length);
                Interlocked.Add(ref bytes, length);
                if (code is not null) issues.Add((index, new DatasetIntegrityIssue(check.Sample, check.Split, check.Role, code)));
                var count = Interlocked.Increment(ref done);
                if (count % 256 == 0) context.ReportProgress(100.0 * count / checks.Count, null, count, checks.Count);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }
        finally
        {
            context.SetResult(Summary(null));
        }
        var state = issues.IsEmpty ? DatasetIntegrityState.Verified : DatasetIntegrityState.Corrupt;
        await datasets.SetIntegrityAsync(input.VersionId, state, CancellationToken.None).ConfigureAwait(false);
        context.ReportProgress(100, null, checks.Count, checks.Count);
        var result = Summary(state);
        context.SetResult(result);
        context.Logger.LogInformation("Dataset {VersionId} of project {ProjectId} checked: {Files} files, {Bytes} bytes in {Elapsed} ms, {Issues} problems.",
            input.VersionId, context.ProjectId, checks.Count, result.Bytes, result.ElapsedMs, result.IssueCount);
    }

    /// <summary>Null when the file is there with its hash; otherwise the issue code.</summary>
    private static string? Check(string path, string expected, out long length)
    {
        length = 0;
        try
        {
            var (hash, size) = DatasetFiles.HashFile(path);
            length = size;
            return string.Equals(hash, expected, StringComparison.Ordinal) ? null : FileChanged;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileMissing;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return FileUnreadable;
        }
    }
}
