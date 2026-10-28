using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Datasets;

/// <summary>
/// ZIP export of a version, the layout of the previous exporter: data.yaml, manifest.json, dataset-metadata.json, images/
/// (stored without compression: JPEG and PNG do not shrink), labels/ and negatives/&lt;split&gt;/&lt;id&gt;.json (deflate). Files are
/// streamed from disk and their SHA-256 is checked on the way, so a damaged version is never exported (it becomes corrupt). The
/// archive is written to a temporary file next to the destination and replaces it at the end; cancel and failure remove the
/// temporary file and leave the destination as it was.
/// </summary>
internal sealed class DatasetExportHandler(IProjectsManager projects) : IJobHandler<DatasetExportInput>
{
    internal const string Kind = "datasets.export";
    private const int CopyBuffer = 1 << 20;

    public JobResource Resource => JobResource.Cpu;

    /// <summary>Smoke seam: awaited after each exported image with the count so far.</summary>
    internal Func<int, Task>? Pause { get; set; }

    /// <summary>The temporary archive of a job: next to the destination, so the final rename stays on one volume.</summary>
    internal static string TemporaryPath(string destination, Guid jobId) => destination + "." + jobId.ToString("N") + ".tmp";

    public async Task ExecuteAsync(DatasetExportInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var datasets = project.DatasetsManager;
        var row = await datasets.RequireOperableAsync(input.VersionId, export: true, token).ConfigureAwait(false);
        var version = row.Version;
        var files = await datasets.GetFilesAsync(token).ConfigureAwait(false);
        var root = DatasetFiles.VersionDirectory(files, input.VersionId);
        var samples = await datasets.ReadSamplesAsync(input.VersionId, token).ConfigureAwait(false);
        var fileName = Path.GetFileName(input.DestinationPath);
        var watch = Stopwatch.StartNew();
        var exported = 0;
        long bytes = 0;
        DatasetExportResult Summary() => new(input.VersionId, fileName, samples.Count, exported, bytes, watch.ElapsedMilliseconds);
        context.SetResult(Summary());
        context.ReportProgress(0, null, 0, samples.Count);

        var temporary = TemporaryPath(input.DestinationPath, context.JobId);
        var moved = false;
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBuffer);
        try
        {
            var stamp = version.CreatedAt;
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddFile(zip, DatasetFiles.DataYamlName, Path.Combine(root, DatasetFiles.DataYamlName), row.DataYamlHash, CompressionLevel.Optimal, stamp);
                    AddFile(zip, DatasetFiles.ManifestName, Path.Combine(root, DatasetFiles.ManifestName), version.ManifestHash, CompressionLevel.Optimal,
                        stamp);
                    AddBytes(zip, "dataset-metadata.json", DatasetManifest.Metadata(version), stamp);
                    foreach (var sample in samples)
                    {
                        token.ThrowIfCancellationRequested();
                        var image = DatasetFiles.ImagePath(sample.Split, sample.ImageId, sample.Format ?? ImageFileFormat.Png);
                        AddFile(zip, image, DatasetFiles.Full(root, image), sample.ImageHash ?? "", CompressionLevel.NoCompression, stamp);
                        var label = DatasetFiles.LabelPath(sample.Split, sample.ImageId);
                        AddFile(zip, label, DatasetFiles.Full(root, label), sample.LabelHash ?? "", CompressionLevel.Fastest, stamp);
                        if (sample.Negatives.Length > 0)
                        {
                            AddBytes(zip, $"negatives/{DatasetFiles.SplitKey(sample.Split)}/{sample.ImageId:D}.json",
                                DatasetManifest.Negatives(sample.Negatives), stamp);
                        }
                        exported++;
                        bytes = output.Position;
                        if (exported % 64 == 0 || exported == samples.Count)
                        {
                            context.SetResult(Summary());
                            context.ReportProgress(100.0 * exported / samples.Count, null, exported, samples.Count);
                        }
                        if (Pause is { } pause) await pause(exported).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                }
                output.Flush();
                bytes = output.Length;
            }
            // The user confirmed the overwrite in «Сохранить как»; the old file is replaced only by a complete archive.
            File.Move(temporary, input.DestinationPath, overwrite: true);
            moved = true;
            context.SetResult(Summary());
            context.Logger.LogInformation("Dataset {VersionId} of project {ProjectId} exported: {Samples} samples, {Bytes} bytes in {Elapsed} ms.",
                input.VersionId, context.ProjectId, samples.Count, bytes, watch.ElapsedMilliseconds);
        }
        catch (CoreException error) when (error.Code == "datasets.corrupt")
        {
            await datasets.SetIntegrityAsync(input.VersionId, DatasetIntegrityState.Corrupt, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is not (CoreException or OperationCanceledException))
        {
            context.Logger.LogError(error, "Export of dataset {VersionId} of project {ProjectId} failed.", input.VersionId, context.ProjectId);
            throw new CoreException(FailureCode(error), error.Message, error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!moved)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    context.Logger.LogWarning(error, "Temporary export file of job {JobId} was not removed.", context.JobId);
                }
            }
            context.SetResult(Summary());
        }

        void AddFile(ZipArchive zip, string name, string path, string expected, CompressionLevel level, DateTimeOffset time)
        {
            FileStream source;
            try
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new CoreException("datasets.corrupt", $"File {name} of dataset {input.VersionId} is missing.", error);
            }
            using (source)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = time;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var target = entry.Open();
                int read;
                while ((read = source.Read(buffer, 0, CopyBuffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected, StringComparison.Ordinal))
                    throw new CoreException("datasets.corrupt", $"File {name} of dataset {input.VersionId} differs from the built one.");
            }
        }

        static void AddBytes(ZipArchive zip, string name, byte[] content, DateTimeOffset time)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            entry.LastWriteTime = time;
            using var target = entry.Open();
            target.Write(content);
        }
    }
    /// <summary>datasets.disk_full, otherwise datasets.export_failed (a destination that cannot be written, a lost drive).</summary>
    private static string FailureCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "datasets.disk_full";
            if (current is OutOfMemoryException) return "datasets.out_of_memory";
        }
        return "datasets.export_failed";
    }
}
