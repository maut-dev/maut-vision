using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Transfer;

/// <summary>
/// Imports of project archives, a background operation of ProjectsManager like the installation of a base model: the snapshot comes at
/// once, progress and the outcome come as events, cancel is a separate command, nothing survives a restart. One import runs at a time.
/// An archive is checked as a whole before anything is written (format, names, sizes, free space), extracted by several threads into
/// projects/.import-&lt;guid&gt;/ on the same volume, its hard links are restored, project.db is checked, and the folder becomes
/// projects/&lt;id&gt; with one rename. Cancel or failure removes the staging folder; what a crash leaves is removed after the next start.
/// </summary>
internal sealed class ProjectImports
{
    internal const string StagingPrefix = ".import-";
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    // Free space beyond the archive content: the database grows at open (WAL), thumbnails appear later.
    private const long SpaceMargin = 256L * 1024 * 1024;
    private const int MaxOperationIdLength = 200;

    private readonly string projectsDirectory;
    private readonly int schemaVersion;
    private readonly Func<string, CancellationToken, Task<Project>> publish;
    private readonly Action<ProjectImportInfo> changed;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly List<Operation> operations = [];

    /// <param name="publish">Moves a checked staging folder into the library under a free id and name and returns the project.</param>
    internal ProjectImports(string projectsDirectory, int schemaVersion, Func<string, CancellationToken, Task<Project>> publish,
        Action<ProjectImportInfo> changed, ILogger logger)
    {
        this.projectsDirectory = projectsDirectory;
        this.schemaVersion = schemaVersion;
        this.publish = publish;
        this.changed = changed;
        this.logger = logger;
    }

    /// <summary>Extraction threads; positional reads of one handle let them read the archive at once.</summary>
    internal int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Smoke seam: awaited after each extracted file with the count of files so far.</summary>
    internal Func<long, Task>? Pause { get; set; }

    internal ProjectImportInfo Start(ProjectArchiveSource source, string operationId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, MaxOperationIdLength, nameof(operationId));
        if (!Path.IsPathFullyQualified(source.Path)) throw new ArgumentException("The archive path must be absolute.", nameof(source));
        Operation operation;
        lock (gate)
        {
            if (operations.FirstOrDefault(item => item.OperationId == operationId) is { } existing) return existing.Snapshot();
            if (operations.Any(item => item.Status == ProjectImportStatus.Running))
                throw new CoreException("projects.import_active", "Another project import is running.");
            operation = new Operation(Guid.NewGuid(), operationId, source, Path.Combine(projectsDirectory, StagingPrefix + Guid.NewGuid().ToString("N")));
            operations.Insert(0, operation);
        }
        var started = operation.Snapshot();
        changed(started);
        logger.LogInformation("Project import {ImportId} operation {OperationId} started.", operation.Id, operationId);
        operation.Task = Task.Run(() => RunAsync(operation));
        return started;
    }

    internal ProjectImportInfo Get(Guid importId) => Find(importId).Snapshot();

    internal IReadOnlyList<ProjectImportInfo> List()
    {
        lock (gate) return operations.Select(operation => operation.Snapshot()).ToArray();
    }

    internal ProjectImportInfo Cancel(Guid importId)
    {
        var operation = Find(importId);
        if (!operation.RequestCancel()) return operation.Snapshot();
        var snapshot = operation.Snapshot();
        changed(snapshot);
        return snapshot;
    }

    /// <summary>Whether a staging folder name belongs to a running import (startup cleanup leaves it alone).</summary>
    internal bool IsActiveStaging(string directoryName)
    {
        lock (gate)
        {
            return operations.Any(operation => operation.Status == ProjectImportStatus.Running
                && string.Equals(Path.GetFileName(operation.Staging), directoryName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Smoke seam: the task of an import, complete when its staging folder is removed or published.</summary>
    internal Task CompletionForTesting(Guid importId) => Find(importId).Task ?? Task.CompletedTask;

    private Operation Find(Guid importId)
    {
        lock (gate)
        {
            return operations.FirstOrDefault(operation => operation.Id == importId)
                ?? throw new CoreException("projects.import_not_found", $"Project import {importId} is unknown.");
        }
    }

    private async Task RunAsync(Operation operation)
    {
        var token = operation.Cancellation.Token;
        var watch = Stopwatch.StartNew();
        try
        {
            RequireUnchanged(operation.Source);
            using (var zip = ZipDirectory.Open(operation.Source.Path))
            {
                if (zip.Length != operation.Source.Length) throw Changed();
                var plan = Plan(zip);
                operation.SetTotals(plan.Entries.Count + plan.Links.Count, plan.Bytes);
                Report(operation, force: true);
                RequireSpace(plan.Bytes);
                Directory.CreateDirectory(projectsDirectory);
                ProjectsManager.RequireDirectory(ProjectsManager.ValidatePath(projectsDirectory)!.Value);
                Directory.CreateDirectory(operation.Staging);
                await ExtractAsync(zip, plan, operation, token).ConfigureAwait(false);
                await LinkAsync(plan, operation, token).ConfigureAwait(false);
            }
            CheckDatabase(Path.Combine(operation.Staging, ProjectArchive.DatabaseName));
            RequireUnchanged(operation.Source);
            token.ThrowIfCancellationRequested();
            var project = await publish(operation.Staging, token).ConfigureAwait(false);
            operation.Finish(ProjectImportStatus.Succeeded, project.Id, project.Name, null, null);
            logger.LogInformation("Project import {ImportId}: project {ProjectId} added with {Files} files ({Bytes} bytes) in {Elapsed} ms ({Rate} MB/s).",
                operation.Id, project.Id, operation.ProcessedFiles, operation.ProcessedBytes, watch.ElapsedMilliseconds,
                (operation.ProcessedBytes / 1048576.0 / Math.Max(0.001, watch.Elapsed.TotalSeconds)).ToString("F1", CultureInfo.InvariantCulture));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RemoveStaging(operation);
            operation.Finish(ProjectImportStatus.Cancelled, null, null, null, null);
            logger.LogInformation("Project import {ImportId} cancelled; no changes.", operation.Id);
        }
        catch (Exception error)
        {
            var code = Code(error);
            if (code is "projects.import_failed" or "projects.disk_full" or "projects.out_of_memory")
                logger.LogError(error, "Project import {ImportId} failed with {Code}.", operation.Id, code);
            else
                logger.LogWarning(error, "Project import {ImportId} refused the archive: {Code}.", operation.Id, code);
            RemoveStaging(operation);
            operation.Finish(ProjectImportStatus.Failed, null, null, code, error.Message);
        }
        Report(operation, force: true);
    }

    /// <summary>The entries to extract and the links to restore, after every check that needs no writing.</summary>
    private static ImportPlan Plan(ZipDirectory zip)
    {
        var byName = new Dictionary<string, ZipEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (!byName.TryAdd(entry.Name, entry)) throw new CoreException("projects.archive_damaged", $"Entry {entry.Name} is repeated.");
        }
        if (!byName.TryGetValue(ProjectArchive.ManifestName, out var manifestEntry))
        {
            if (byName.Keys.Any(name => name.StartsWith("tables/", StringComparison.OrdinalIgnoreCase)))
                throw new CoreException("projects.archive_legacy", "The archive has an unsupported early format (tables/ without manifest).");
            throw new CoreException("projects.archive_unknown", "The archive has no manifest.json.");
        }
        var manifest = ProjectArchive.ReadManifest(zip.ReadAll(manifestEntry, ProjectArchive.MaxManifestBytes));
        if (!Guid.TryParseExact(manifest.ProjectId, "D", out var id) || id == Guid.Empty)
            throw new CoreException("projects.archive_unknown", "The manifest names no valid project.");
        foreach (var name in byName.Keys)
        {
            if (!ProjectArchive.IsAllowedName(name)) throw new CoreException("projects.archive_unknown", $"Entry {name} is not part of a project archive.");
        }
        if (!byName.ContainsKey(ProjectArchive.MetadataName) || !byName.ContainsKey(ProjectArchive.DatabaseName))
            throw new CoreException("projects.archive_damaged", "The archive lacks project.json or project.db.");
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<(ArchiveLink Link, long Length)>(manifest.Links.Count);
        foreach (var link in manifest.Links)
        {
            if (link?.Path is null || link.Target is null || !link.Path.StartsWith(ProjectArchive.FilesPrefix, StringComparison.Ordinal)
                || !ProjectArchive.IsAllowedName(link.Path) || byName.ContainsKey(link.Path) || !linked.Add(link.Path)
                || !link.Target.StartsWith(ProjectArchive.FilesPrefix, StringComparison.Ordinal) || !byName.TryGetValue(link.Target, out var target))
                throw new CoreException("projects.archive_damaged", "A link of the manifest is invalid.");
            links.Add((link, target.Length));
        }
        var entries = zip.Entries.Where(entry => entry.Name != ProjectArchive.ManifestName).OrderBy(entry => entry.HeaderOffset).ToArray();
        return new ImportPlan(entries, links, entries.Sum(entry => entry.Length));
    }

    private void RequireSpace(long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(projectsDirectory));
        if (string.IsNullOrEmpty(root)) return;
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < bytes + SpaceMargin)
        {
            throw new CoreException("projects.disk_full", $"The import needs {bytes + SpaceMargin} bytes, {available} are free.")
            {
                Details = new Dictionary<string, string>
                {
                    ["requiredMb"] = ((bytes + SpaceMargin + 1048575) / 1048576).ToString(CultureInfo.InvariantCulture),
                    ["availableMb"] = (available / 1048576).ToString(CultureInfo.InvariantCulture),
                },
            };
        }
    }

    private async Task ExtractAsync(ZipDirectory zip, ImportPlan plan, Operation operation, CancellationToken token)
    {
        // Folders first, in order, so that the extraction threads only create files.
        var folders = plan.Entries.Select(entry => entry.Name).Concat(plan.Links.Select(item => item.Link.Path))
            .Select(name => name.LastIndexOf('/') is var slash and > 0 ? name[..slash] : "").Where(folder => folder.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        foreach (var folder in folders) Directory.CreateDirectory(Full(operation.Staging, folder));
        await Parallel.ForEachAsync(plan.Entries, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, async (entry, cancel) =>
        {
            using (var output = new FileStream(Full(operation.Staging, entry.Name), new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 0,
                // Reserving space costs a call per file; only large files (weights) gain from it.
                PreallocationSize = entry.Length > (1 << 20) ? entry.Length : 0,
            }))
            {
                zip.CopyTo(entry, output, read => operation.AddBytes(read), cancel);
            }
            var files = operation.AddFile();
            Report(operation, force: false);
            if (Pause is { } pause) await pause(files).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>A hard link to the extracted file, as in the exported project; a copy where the volume has no hard links.</summary>
    private async Task LinkAsync(ImportPlan plan, Operation operation, CancellationToken token)
    {
        var copies = 0;
        await Parallel.ForEachAsync(plan.Links, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, (item, _) =>
        {
            var path = Full(operation.Staging, item.Link.Path);
            var target = Full(operation.Staging, item.Link.Target);
            if (!HardLink.TryCreate(path, target))
            {
                File.Copy(target, path);
                Interlocked.Increment(ref copies);
            }
            operation.AddFile();
            Report(operation, force: false);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        if (copies > 0) logger.LogWarning("Project import {ImportId}: {Copies} links were copied, the volume refused hard links.", operation.Id, copies);
    }

    /// <summary>quick_check of the extracted database and its schema version; a newer schema is refused before the project appears.</summary>
    private void CheckDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                var value = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (value > schemaVersion)
                    throw new CoreException("projects.storage_newer", $"The archived project has storage version {value}, this version supports {schemaVersion}.");
                if (value < 1) throw new CoreException("projects.archive_damaged", $"The archived database has version {value}.");
            }
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            using var reader = check.ExecuteReader();
            var results = new List<string>();
            while (reader.Read() && results.Count < 5) results.Add(reader.GetString(0));
            if (results is not ["ok"]) throw new CoreException("projects.archive_damaged", "The archived database fails quick_check: " + string.Join("; ", results));
        }
        catch (SqliteException error)
        {
            throw new CoreException("projects.archive_damaged", "The archived database cannot be read.", error);
        }
    }

    private static void RequireUnchanged(ProjectArchiveSource source)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(source.Path);
            if (!info.Exists) throw Changed();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoreException("projects.archive_changed", "The chosen archive cannot be read.", error);
        }
        if (info.Length != source.Length || new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) != source.LastWriteTimeUtc) throw Changed();
    }

    private static CoreException Changed() => new("projects.archive_changed", "The chosen archive changed after it was chosen.");

    private void RemoveStaging(Operation operation)
    {
        try
        {
            if (Directory.Exists(operation.Staging)) Directory.Delete(operation.Staging, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Staging folder of project import {ImportId} was not removed; it is removed after the next start.", operation.Id);
        }
    }

    private void Report(Operation operation, bool force)
    {
        if (operation.TakeReport(force) is { } snapshot) changed(snapshot);
    }

    private static string Code(Exception error) => error switch
    {
        CoreException core => core.Code,
        ZipUnsupportedException => "projects.archive_unknown",
        ZipDamagedException or InvalidDataException or EndOfStreamException => "projects.archive_damaged",
        _ => ProjectExportHandler.FailureCode(error, "projects.import_failed"),
    };

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed record ImportPlan(IReadOnlyList<ZipEntry> Entries, IReadOnlyList<(ArchiveLink Link, long Length)> Links, long Bytes);

    /// <summary>State of one import; counters are updated by the extraction threads, the rest under the lock.</summary>
    private sealed class Operation(Guid id, string operationId, ProjectArchiveSource source, string staging)
    {
        private readonly Lock gate = new();
        private long processedFiles;
        private long processedBytes;
        private long totalFiles;
        private long totalBytes;
        private ProjectImportStatus status = ProjectImportStatus.Running;
        private bool cancelRequested;
        private string? projectId;
        private string? projectName;
        private string? errorCode;
        private string? errorMessage;
        private DateTimeOffset? finishedAt;
        private long sequence = 1;
        private long lastReport;

        internal Guid Id { get; } = id;
        internal string OperationId { get; } = operationId;
        internal ProjectArchiveSource Source { get; } = source;
        internal string Staging { get; } = staging;
        internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task? Task { get; set; }
        internal long ProcessedFiles => Interlocked.Read(ref processedFiles);
        internal long ProcessedBytes => Interlocked.Read(ref processedBytes);

        internal ProjectImportStatus Status
        {
            get
            {
                lock (gate) return status;
            }
        }

        internal void SetTotals(long files, long bytes)
        {
            lock (gate)
            {
                totalFiles = files;
                totalBytes = bytes;
            }
        }

        internal long AddFile() => Interlocked.Increment(ref processedFiles);

        internal void AddBytes(long count) => Interlocked.Add(ref processedBytes, count);

        internal bool RequestCancel()
        {
            lock (gate)
            {
                if (status != ProjectImportStatus.Running || cancelRequested) return false;
                cancelRequested = true;
                sequence++;
            }
            Cancellation.Cancel();
            return true;
        }

        internal void Finish(ProjectImportStatus result, string? project, string? name, string? code, string? message)
        {
            lock (gate)
            {
                status = result;
                projectId = project;
                projectName = name;
                errorCode = code;
                errorMessage = message is { Length: > 500 } ? message[..500] : message;
                finishedAt = DateTimeOffset.UtcNow;
            }
        }

        /// <summary>A snapshot to report: always when forced (transitions), otherwise at most every <see cref="ProgressInterval"/>.</summary>
        internal ProjectImportInfo? TakeReport(bool force)
        {
            var now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                if (!force && Stopwatch.GetElapsedTime(lastReport, now) < ProgressInterval) return null;
                lastReport = now;
                sequence++;
                return SnapshotLocked();
            }
        }

        internal ProjectImportInfo Snapshot()
        {
            lock (gate) return SnapshotLocked();
        }

        private ProjectImportInfo SnapshotLocked() => new(Id, OperationId, Path.GetFileName(Source.Path), status, cancelRequested, ProcessedFiles, totalFiles,
            ProcessedBytes, totalBytes, projectId, projectName, errorCode, errorMessage, StartedAt, finishedAt, sequence);
    }
}
