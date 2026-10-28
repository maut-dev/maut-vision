using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;
using static MautVision.Core.Projects.Datasets.DatasetsSql;

namespace MautVision.Core.Projects.Datasets;

/// <summary>What the build produced for one sample: output format, file size and hashes, the pixel hash of the written frame.</summary>
internal sealed record ProducedSample(long Position, ImageFileFormat Format, long ByteSize, string ImageHash, string LabelHash, string PixelHash);

/// <summary>
/// Dataset versions, plans and operations of one project: tables dataset_plans, dataset_versions and dataset_samples; the
/// operations are jobs (datasets.build, datasets.verify, datasets.export). The manager reads the catalog and the images through
/// their managers (one way: they do not know about datasets); the build, the check and the export are coordinated by their
/// handlers, which call the internal methods below.
/// </summary>
internal sealed class DatasetsManager : IDatasetsManager
{
    internal const int MaxPage = 200;
    private const int SampleBatch = 4096;

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Func<InputSize> projectInput;
    private readonly Action<DatasetsChangedEventArgs> changed;
    private readonly ClassesManager classes;
    private readonly ImagesManager images;
    private readonly FileCleanup cleanup;
    private readonly DatasetBlockers blockers;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<Guid, int> readers = new();

    internal DatasetsManager(string projectId, ProjectStore store, Func<bool> isOpen, Func<InputSize> projectInput, Action<DatasetsChangedEventArgs> changed,
        ClassesManager classes, ImagesManager images, FileCleanup cleanup, DatasetBlockers blockers, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.projectInput = projectInput;
        this.changed = changed;
        this.classes = classes;
        this.images = images;
        this.cleanup = cleanup;
        this.blockers = blockers;
        this.logger = logger;
    }

    // ---- Versions ----

    public async Task<IReadOnlyList<DatasetVersion>> ListAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"""
                SELECT {VersionColumns} FROM dataset_versions WHERE state = 'ready' {(includeArchived ? "" : "AND archived_at IS NULL")}
                ORDER BY created_at DESC, id DESC;
                """);
            return (IReadOnlyList<DatasetVersion>)(await CatalogSql.ListAsync(command, ReadVersion, token).ConfigureAwait(false)).Select(row => row.Version).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetsSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, """
                SELECT count(*) FILTER (WHERE archived_at IS NULL AND integrity_state <> 'corrupt'),
                    count(*) FILTER (WHERE archived_at IS NOT NULL),
                    count(*) FILTER (WHERE archived_at IS NULL AND integrity_state = 'corrupt')
                FROM dataset_versions WHERE state = 'ready';
                """);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            return new DatasetsSummary(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetVersion> GetAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return (await RequireReadyAsync(versionId, cancellationToken).ConfigureAwait(false)).Version;
    }

    public async Task<DatasetVersion> GetPassportAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var row = await FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        return row is { State: "ready" or "deleted" } ? row.Version : throw Unavailable(versionId);
    }

    public async Task<DatasetVersion> UpdateAsync(Guid versionId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > DatasetSelection.MaxNameLength)
            throw new CoreException("datasets.name_invalid", "A dataset name has 1–120 characters.") { Details = Field("name") };
        var text = description?.Trim() ?? "";
        if (text.Length > DatasetSelection.MaxDescriptionLength)
            throw new CoreException("datasets.description_invalid", "A description has at most 4000 characters.") { Details = Field("description") };
        RequireOpen();
        return await ChangeAsync(versionId, expectedRevision, current =>
        {
            if (current.ArchivedAt is not null) throw Archived(versionId);
            if (current.Name == trimmed && current.Description == text) return null;
            return ("name = $name, description = $description", command =>
            {
                command.Parameters.AddWithValue("$name", trimmed);
                command.Parameters.AddWithValue("$description", text);
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DatasetVersion> SetArchivedAsync(Guid versionId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return ChangeAsync(versionId, expectedRevision, current => (current.ArchivedAt is not null) == archived
            ? null
            : ("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value)), cancellationToken);
    }

    // ---- Plans ----

    public async Task<DatasetPlan> PlanAsync(DatasetSpecification specification, string operationId, CancellationToken cancellationToken = default)
    {
        RequireOperationId(operationId);
        var validated = DatasetSelection.Validate(specification);
        RequireOpen();
        var existing = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {PlanColumns} FROM dataset_plans WHERE operation_id = $operation;");
            command.Parameters.AddWithValue("$operation", operationId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadPlan(reader).Plan : null;
        }, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        var watch = Stopwatch.StartNew();
        var size = projectInput();
        var selection = await SelectAsync(validated, size.Width, size.Height, cancellationToken).ConfigureAwait(false);
        var plan = new DatasetPlan(Guid.NewGuid(), validated, size.Width, size.Height, selection.Classes, selection.Composition, selection.Skipped,
            selection.Warnings, selection.Sufficient, selection.Hash, Now());
        var stored = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using (var find = Command(connection, transaction, $"SELECT {PlanColumns} FROM dataset_plans WHERE operation_id = $operation;"))
            {
                find.Parameters.AddWithValue("$operation", operationId);
                await using var reader = await find.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) return ReadPlan(reader).Plan;
            }
            await using var insert = Command(connection, transaction, $"""
                INSERT INTO dataset_plans ({PlanColumns}, operation_id)
                VALUES ($id, $specification, $width, $height, $classes, $composition, $skipped, $warnings, $sufficient, $hash, $source, $now, $operation);
                """);
            insert.Parameters.AddWithValue("$id", Key(plan.Id));
            insert.Parameters.AddWithValue("$specification", Serialize(plan.Specification));
            insert.Parameters.AddWithValue("$width", plan.Width);
            insert.Parameters.AddWithValue("$height", plan.Height);
            insert.Parameters.AddWithValue("$classes", Serialize(plan.Classes));
            insert.Parameters.AddWithValue("$composition", Serialize(plan.Composition));
            insert.Parameters.AddWithValue("$skipped", Serialize(plan.Skipped));
            insert.Parameters.AddWithValue("$warnings", Serialize(plan.Warnings));
            insert.Parameters.AddWithValue("$sufficient", plan.SufficientSamples ? 1 : 0);
            insert.Parameters.AddWithValue("$hash", plan.Hash);
            insert.Parameters.AddWithValue("$source", selection.SourceRevision);
            insert.Parameters.AddWithValue("$now", Time(plan.CreatedAt));
            insert.Parameters.AddWithValue("$operation", operationId);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return plan;
        }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Project {ProjectId}: dataset plan {PlanId} of {Frames} frames ({Skipped} skipped) made in {Elapsed} ms.",
            projectId, stored.Id, stored.Counts.Total, stored.Skipped.Total, watch.ElapsedMilliseconds);
        return stored;
    }

    public async Task<DatasetPlan> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return (await FindPlanAsync(planId, cancellationToken).ConfigureAwait(false) ?? throw Unavailable(planId)).Plan;
    }

    public async Task<DatasetBuildInput> PrepareBuildAsync(Guid planId, string planHash, string operationId, CancellationToken cancellationToken = default)
    {
        RequireOperationId(operationId);
        RequireOpen();
        if (await FindJobByOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is { } job)
            return Existing<DatasetBuildInput>(job, DatasetBuildHandler.Kind, operationId);
        var (plan, sourceRevision) = await FindPlanAsync(planId, cancellationToken).ConfigureAwait(false) ?? throw Unavailable(planId);
        if (!string.Equals(plan.Hash, planHash, StringComparison.Ordinal))
            throw new CoreException("datasets.revision_conflict", $"Plan {planId} has another hash than the one the build refers to.");
        var size = projectInput();
        if (plan.Width != size.Width || plan.Height != size.Height)
            throw new CoreException("datasets.input_size_mismatch", $"Plan {planId} is {plan.Width}×{plan.Height}, the project input is {size.Width}×{size.Height}.");
        var catalog = await classes.ReadDatasetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var imagesRevision = await images.ReadDatasetRevisionAsync(plan.Specification.GroupIds, cancellationToken).ConfigureAwait(false);
        if (DatasetSelection.SourceRevision(imagesRevision, catalog, size.Width, size.Height) != sourceRevision)
            throw PlanStale(planId);
        RequireBuildable(plan.Counts.Total, plan.Warnings);
        return new DatasetBuildInput(plan.Id, plan.Hash, plan.Specification, plan.Width, plan.Height);
    }

    public async Task<DatasetVerifyInput> PrepareVerifyAsync(Guid versionId, string operationId, CancellationToken cancellationToken = default)
    {
        RequireOperationId(operationId);
        RequireOpen();
        if (await FindJobByOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is { } job)
            return Existing<DatasetVerifyInput>(job, DatasetVerifyHandler.Kind, operationId);
        var version = await RequireOperableAsync(versionId, export: false, cancellationToken).ConfigureAwait(false);
        return new DatasetVerifyInput(versionId, version.Version.Name);
    }

    public async Task<DatasetExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default)
    {
        RequireOperationId(operationId);
        RequireOpen();
        if (await FindJobByOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is { } job)
            return Existing<DatasetExportInput>(job, DatasetExportHandler.Kind, operationId);
        var version = await RequireOperableAsync(versionId, export: true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath)
            || !string.Equals(Path.GetExtension(destinationPath), ".zip", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.GetDirectoryName(destinationPath)) || Directory.Exists(destinationPath))
        {
            throw new CoreException("datasets.export_destination", "The export destination is not a .zip file in an existing folder.");
        }
        return new DatasetExportInput(versionId, version.Version.Name, Path.GetFullPath(destinationPath));
    }

    // ---- Operations ----

    public async Task<DatasetOperationPage> ListOperationsAsync(DatasetOperationKind kind, int limit = 50, Guid? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        var jobKind = JobKind(kind);
        return await store.ReadAsync(async (connection, token) =>
        {
            var jobs = await JobsTable.ListStoredByKindAsync(connection, projectId, jobKind, limit, before, token).ConfigureAwait(false);
            var total = await JobsTable.CountByKindAsync(connection, jobKind, token).ConfigureAwait(false);
            var operations = jobs.Select(ToOperation).OfType<DatasetOperation>().ToArray();
            var available = await ReadyVersionsAsync(connection, operations.Select(operation => operation.VersionId).Distinct().ToArray(), token).ConfigureAwait(false);
            return new DatasetOperationPage(operations.Select(operation => operation with { VersionAvailable = available.Contains(operation.VersionId) }).ToArray(), total);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetOperation> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var job = await JobsTable.GetStoredAsync(connection, null, projectId, operationId, token).ConfigureAwait(false);
            var operation = (job is null ? null : ToOperation(job)) ?? throw Unavailable(operationId);
            var available = await ReadyVersionsAsync(connection, [operation.VersionId], token).ConfigureAwait(false);
            return operation with { VersionAvailable = available.Contains(operation.VersionId) };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            var job = await JobsTable.GetStoredAsync(connection, transaction, projectId, operationId, token).ConfigureAwait(false);
            if (job is null || ToOperation(job) is null) throw Unavailable(operationId);
            if (!await JobsTable.DeleteFinishedAsync(connection, transaction, operationId, token).ConfigureAwait(false))
                throw new CoreException("datasets.operation_active", $"Dataset operation {operationId} is not finished.");
        }, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Project {ProjectId}: dataset operation {OperationId} removed from the history.", projectId, operationId);
    }

    // ---- Samples ----

    public async Task<DatasetSamplePage> ListSamplesAsync(Guid versionId, DatasetSplit split, int limit = 100, string? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        if (!Enum.IsDefined(split)) throw new ArgumentOutOfRangeException(nameof(split));
        Guid? cursor = null;
        if (after is not null)
        {
            cursor = Guid.TryParseExact(after, "D", out var parsed) ? parsed
                : throw new CoreException("datasets.cursor_invalid", "The sample cursor is not one this list gave.");
        }
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await RequireReadyAsync(connection, versionId, token).ConfigureAwait(false);
            await using var command = Command(connection, null, $"""
                SELECT {SampleColumns} FROM dataset_samples WHERE version_id = $version AND split = $split {(cursor is null ? "" : "AND image_id > $after")}
                ORDER BY image_id LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$version", Key(versionId));
            command.Parameters.AddWithValue("$split", DatasetFiles.SplitKey(split));
            command.Parameters.AddWithValue("$limit", limit + 1);
            if (cursor is { } value) command.Parameters.AddWithValue("$after", Key(value));
            var rows = await CatalogSql.ListAsync(command, ReadSample, token).ConfigureAwait(false);
            var next = rows.Count > limit ? Key(rows[limit - 1].ImageId) : null;
            return new DatasetSamplePage(rows.Take(limit).Select(row => row.ToSample()).ToArray(), next);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetSample> GetSampleAsync(Guid versionId, Guid sampleId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await RequireReadyAsync(connection, versionId, token).ConfigureAwait(false);
            await using var command = Command(connection, null, $"""
                SELECT {SampleColumns} FROM dataset_samples WHERE version_id = $version AND split IN ('train', 'val', 'test') AND image_id = $id;
                """);
            command.Parameters.AddWithValue("$version", Key(versionId));
            command.Parameters.AddWithValue("$id", Key(sampleId));
            return (await SingleOrDefaultAsync(command, ReadSample, token).ConfigureAwait(false) ?? throw Unavailable(sampleId)).ToSample();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Path of a sample file under files/datasets (the resource kind) for the resource channel; null when the resource is not a produced
    /// sample of a ready version.
    /// </summary>
    internal static async ValueTask<string?> FindSampleFileAsync(ProjectStore store, MaterialFiles files, Guid resourceId, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, """
                SELECT s.version_id, s.split, s.image_id, s.format FROM dataset_samples s JOIN dataset_versions v ON v.id = s.version_id
                WHERE s.resource_id = $id AND v.state = 'ready' AND s.format IS NOT NULL;
                """);
            command.Parameters.AddWithValue("$id", Key(resourceId));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            return reader.GetString(0) + "/" + DatasetFiles.ImagePath(DatasetFiles.ParseSplit(reader.GetString(1)), ParseId(reader.GetString(2)),
                ImagesSql.ParseFormat(reader.GetString(3)));
        }, cancellationToken).ConfigureAwait(false);

    // ---- Deletion ----

    public async Task<DatasetDeletionReview> ReviewDeletionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var composition = await store.ReadAsync((connection, token) => ComposeDeletionAsync(connection, null, versionId, token), cancellationToken).ConfigureAwait(false);
        var found = await CollectBlockersAsync(versionId, cancellationToken).ConfigureAwait(false);
        return new DatasetDeletionReview(versionId, composition.Name, composition.Samples, composition.Operations.Count, found, composition.Revision);
    }

    public async Task<DatasetDeletionResult> DeleteAsync(Guid versionId, string reviewRevision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRevision);
        RequireOpen();
        if ((await CollectBlockersAsync(versionId, cancellationToken).ConfigureAwait(false)).Count > 0)
            throw new CoreException("datasets.deletion_blocked", $"Dataset {versionId} is used by an operation, training or a model check.");
        var watch = Stopwatch.StartNew();
        var removed = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var composition = await ComposeDeletionAsync(connection, transaction, versionId, token).ConfigureAwait(false);
            if (composition.Revision != reviewRevision)
                throw new CoreException("datasets.deletion_revision_conflict", $"Dataset {versionId} changed after its deletion review.");
            await using (var samples = Command(connection, transaction, "DELETE FROM dataset_samples WHERE version_id = $id;"))
            {
                samples.Parameters.AddWithValue("$id", Key(versionId));
                await samples.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using (var version = Command(connection, transaction, """
                UPDATE dataset_versions SET state = 'deleted', deleted_at = $now, updated_at = $now, revision = revision + 1 WHERE id = $id;
                """))
            {
                version.Parameters.AddWithValue("$id", Key(versionId));
                version.Parameters.AddWithValue("$now", Time(Now()));
                await version.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            foreach (var operation in composition.Operations) await JobsTable.DeleteFinishedAsync(connection, transaction, operation, token).ConfigureAwait(false);
            await FileCleanup.QueueAsync(connection, transaction, [VersionFolder(versionId)], token).ConfigureAwait(false);
            return composition;
        }, cancellationToken).ConfigureAwait(false);
        var pending = await cleanup.RemoveAfterCommitAsync([VersionFolder(versionId)]).ConfigureAwait(false);
        Raise(new DatasetEntityChange(versionId, null));
        logger.LogInformation("Project {ProjectId}: dataset {VersionId} deleted ({Samples} samples, {Operations} history records) in {Elapsed} ms; files pending: {Pending}.",
            projectId, versionId, removed.Samples, removed.Operations.Count, watch.ElapsedMilliseconds, pending);
        return new DatasetDeletionResult(versionId, removed.Samples, removed.Operations.Count, pending);
    }

    // ---- Training ----

    public async Task<DatasetTrainingSource> GetTrainingSourceAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var row = await RequireTrainableAsync(versionId, cancellationToken).ConfigureAwait(false);
        var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        return Source(row.Version, DatasetFiles.VersionDirectory(files, versionId));
    }

    public Task<DatasetTrainingSource> MaterializeAsync(Guid versionId, string targetDirectory, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return MaterializeForJobAsync(versionId, targetDirectory, cancellationToken);
    }

    /// <summary>
    /// <see cref="MaterializeAsync"/> for a job: not gated on isOpen, a queued run may start while the project is being opened. An
    /// evaluation passes the input of its model version, which may differ from the current project input.
    /// </summary>
    internal async Task<DatasetTrainingSource> MaterializeForJobAsync(Guid versionId, string targetDirectory, CancellationToken cancellationToken,
        InputSize? expectedInput = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (!Path.IsPathFullyQualified(targetDirectory) || File.Exists(targetDirectory)
            || (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any()))
        {
            throw new CoreException("datasets.materialize_target", "The training folder must be an absolute path to a missing or empty folder.");
        }
        readers.AddOrUpdate(versionId, 1, (_, count) => count + 1);
        try
        {
            var row = await RequireTrainableAsync(versionId, cancellationToken, expectedInput).ConfigureAwait(false);
            var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
            var source = DatasetFiles.VersionDirectory(files, versionId);
            var target = Path.GetFullPath(targetDirectory);
            var watch = Stopwatch.StartNew();
            DatasetFiles.CreateLayout(target);
            var samples = await ReadSamplesAsync(versionId, cancellationToken).ConfigureAwait(false);
            await Parallel.ForEachAsync(samples, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Workers }, (sample, _) =>
            {
                var image = DatasetFiles.ImagePath(sample.Split, sample.ImageId, sample.Format ?? ImageFileFormat.Png);
                var label = DatasetFiles.LabelPath(sample.Split, sample.ImageId);
                DatasetFiles.Link(DatasetFiles.Full(source, image), DatasetFiles.Full(target, image));
                DatasetFiles.Link(DatasetFiles.Full(source, label), DatasetFiles.Full(target, label));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
            File.Copy(Path.Combine(source, DatasetFiles.DataYamlName), Path.Combine(target, DatasetFiles.DataYamlName), overwrite: true);
            File.Copy(Path.Combine(source, DatasetFiles.ManifestName), Path.Combine(target, DatasetFiles.ManifestName), overwrite: true);
            logger.LogInformation("Project {ProjectId}: dataset {VersionId} materialized ({Samples} samples) in {Elapsed} ms.",
                projectId, versionId, samples.Count, watch.ElapsedMilliseconds);
            return Source(row.Version, target);
        }
        finally
        {
            readers.AddOrUpdate(versionId, 0, (_, count) => count - 1);
            readers.TryRemove(KeyValuePair.Create(versionId, 0));
        }
    }

    // ---- Build support (not gated on isOpen: recovered builds run while the project is being opened) ----

    internal int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => store.GetFilesAsync(cancellationToken);

    internal async Task<string?> FindVersionStateAsync(Guid versionId, CancellationToken cancellationToken)
        => (await FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false))?.State;

    /// <summary>Selects again from the current sources; a different hash means the plan is stale.</summary>
    internal async Task<DatasetSelectionResult> SelectForBuildAsync(DatasetBuildInput input, CancellationToken cancellationToken)
    {
        var size = projectInput();
        if (input.Width != size.Width || input.Height != size.Height)
            throw new CoreException("datasets.input_size_mismatch", $"The build is {input.Width}×{input.Height}, the project input is {size.Width}×{size.Height}.");
        var selection = await SelectAsync(input.Specification, input.Width, input.Height, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(selection.Hash, input.PlanHash, StringComparison.Ordinal)) throw PlanStale(input.PlanId);
        RequireBuildable(selection.Frames.Count, selection.Warnings);
        return selection;
    }

    /// <summary>The version row (building) and every sample row in one transaction; an earlier unfinished attempt is replaced.</summary>
    internal async Task CreateBuildAsync(Guid versionId, DatasetBuildInput input, DatasetSelectionResult selection, CancellationToken cancellationToken)
    {
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using (var stale = Command(connection, transaction, "DELETE FROM dataset_versions WHERE id = $id AND state = 'building';"))
            {
                stale.Parameters.AddWithValue("$id", Key(versionId));
                await stale.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var now = Time(Now());
            await using (var version = Command(connection, transaction, $"""
                INSERT INTO dataset_versions (id, state, plan_id, plan_hash, name, description, revision, integrity_state, width, height, specification,
                    class_map, variants, composition, manifest_hash, data_yaml_hash, created_at, updated_at)
                VALUES ($id, 'building', $plan, $hash, $name, $description, 1, 'unverified', $width, $height, $specification, $classes, $variants,
                    $composition, '', '', $now, $now);
                """))
            {
                version.Parameters.AddWithValue("$id", Key(versionId));
                version.Parameters.AddWithValue("$plan", Key(input.PlanId));
                version.Parameters.AddWithValue("$hash", input.PlanHash);
                version.Parameters.AddWithValue("$name", input.Specification.Name);
                version.Parameters.AddWithValue("$description", input.Specification.Description);
                version.Parameters.AddWithValue("$width", input.Width);
                version.Parameters.AddWithValue("$height", input.Height);
                version.Parameters.AddWithValue("$specification", Serialize(input.Specification));
                version.Parameters.AddWithValue("$classes", Serialize(selection.Classes));
                version.Parameters.AddWithValue("$variants", Serialize(selection.Variants));
                version.Parameters.AddWithValue("$composition", Serialize(selection.Composition));
                version.Parameters.AddWithValue("$now", now);
                await version.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using var insert = Command(connection, transaction, """
                INSERT INTO dataset_samples (version_id, split, image_id, position, resource_id, group_id, generated, source_format, source_sha256,
                    width, height, pixel_hash, annotation_revision, roi_x, roi_y, roi_width, roi_height, boxes, negatives)
                VALUES ($version, $split, $image, $position, $resource, $group, $generated, $format, $sha, $width, $height, $pixels, $annotation,
                    $roiX, $roiY, $roiWidth, $roiHeight, $boxes, $negatives);
                """);
            insert.Parameters.AddWithValue("$version", Key(versionId));
            var split = insert.Parameters.Add("$split", SqliteType.Text);
            var image = insert.Parameters.Add("$image", SqliteType.Text);
            var position = insert.Parameters.Add("$position", SqliteType.Integer);
            var resource = insert.Parameters.Add("$resource", SqliteType.Text);
            var group = insert.Parameters.Add("$group", SqliteType.Text);
            var generated = insert.Parameters.Add("$generated", SqliteType.Integer);
            var format = insert.Parameters.Add("$format", SqliteType.Text);
            var sha = insert.Parameters.Add("$sha", SqliteType.Text);
            var width = insert.Parameters.Add("$width", SqliteType.Integer);
            var height = insert.Parameters.Add("$height", SqliteType.Integer);
            var pixels = insert.Parameters.Add("$pixels", SqliteType.Text);
            var annotation = insert.Parameters.Add("$annotation", SqliteType.Integer);
            var roiX = insert.Parameters.Add("$roiX", SqliteType.Real);
            var roiY = insert.Parameters.Add("$roiY", SqliteType.Real);
            var roiWidth = insert.Parameters.Add("$roiWidth", SqliteType.Real);
            var roiHeight = insert.Parameters.Add("$roiHeight", SqliteType.Real);
            var boxes = insert.Parameters.Add("$boxes", SqliteType.Text);
            var negatives = insert.Parameters.Add("$negatives", SqliteType.Text);
            for (var index = 0; index < selection.Frames.Count; index++)
            {
                var frame = selection.Frames[index];
                var source = frame.Image;
                split.Value = DatasetFiles.SplitKey(frame.Split);
                image.Value = Key(source.Id);
                position.Value = index;
                resource.Value = Key(ResourceId(versionId, source.Id));
                group.Value = Key(source.GroupId);
                generated.Value = frame.Group.Origin == ImageGroupOrigin.Generated ? 1 : 0;
                format.Value = ImagesSql.FormatKey(source.Format);
                sha.Value = source.Sha256;
                width.Value = source.Width;
                height.Value = source.Height;
                pixels.Value = source.PixelHash;
                annotation.Value = source.AnnotationRevision;
                roiX.Value = (object?)source.Roi?.X ?? DBNull.Value;
                roiY.Value = (object?)source.Roi?.Y ?? DBNull.Value;
                roiWidth.Value = (object?)source.Roi?.Width ?? DBNull.Value;
                roiHeight.Value = (object?)source.Roi?.Height ?? DBNull.Value;
                boxes.Value = Serialize(frame.Boxes);
                negatives.Value = Serialize(frame.Negatives);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal Task<int> CountSamplesAsync(Guid versionId, CancellationToken cancellationToken) => store.ReadAsync(async (connection, token) =>
    {
        await using var command = Command(connection, null, "SELECT count(*) FROM dataset_samples WHERE version_id = $id;");
        command.Parameters.AddWithValue("$id", Key(versionId));
        return (int)await ScalarAsync(command, token).ConfigureAwait(false);
    }, cancellationToken);

    /// <summary>Samples from a position in build order.</summary>
    internal Task<List<SampleRow>> ReadWindowAsync(Guid versionId, long fromPosition, int count, CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"""
                SELECT {SampleColumns} FROM dataset_samples WHERE version_id = $version AND position >= $from ORDER BY position LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$version", Key(versionId));
            command.Parameters.AddWithValue("$from", fromPosition);
            command.Parameters.AddWithValue("$limit", count);
            return await CatalogSql.ListAsync(command, ReadSample, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>All samples of a version in build order, read in pages.</summary>
    internal async Task<List<SampleRow>> ReadSamplesAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var result = new List<SampleRow>();
        while (true)
        {
            var page = await ReadWindowAsync(versionId, result.Count == 0 ? 0 : result[^1].Position + 1, SampleBatch, cancellationToken).ConfigureAwait(false);
            result.AddRange(page);
            if (page.Count < SampleBatch) return result;
        }
    }

    internal async Task SaveProducedAsync(Guid versionId, IReadOnlyList<ProducedSample> produced, CancellationToken cancellationToken)
    {
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE dataset_samples SET format = $format, byte_size = $size, image_hash = $image, label_hash = $label, pixel_hash = $pixels
                WHERE version_id = $version AND position = $position;
                """);
            command.Parameters.AddWithValue("$version", Key(versionId));
            var format = command.Parameters.Add("$format", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var image = command.Parameters.Add("$image", SqliteType.Text);
            var label = command.Parameters.Add("$label", SqliteType.Text);
            var pixels = command.Parameters.Add("$pixels", SqliteType.Text);
            var position = command.Parameters.Add("$position", SqliteType.Integer);
            foreach (var sample in produced)
            {
                format.Value = ImagesSql.FormatKey(sample.Format);
                size.Value = sample.ByteSize;
                image.Value = sample.ImageHash;
                label.Value = sample.LabelHash;
                pixels.Value = sample.PixelHash;
                position.Value = sample.Position;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that no pixels are in two splits, writes manifest.json and data.yaml and publishes the version (ready, verified)
    /// in one transaction.
    /// </summary>
    internal async Task<DatasetSplitCounts> PublishBuildAsync(Guid versionId, string root, CancellationToken cancellationToken)
    {
        var collisions = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, """
                SELECT count(*) FROM dataset_samples WHERE version_id = $id AND pixel_hash IN (
                    SELECT pixel_hash FROM dataset_samples WHERE version_id = $id GROUP BY pixel_hash HAVING count(DISTINCT split) > 1);
                """);
            command.Parameters.AddWithValue("$id", Key(versionId));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (collisions > 0)
        {
            throw new CoreException(DatasetSelection.SplitCollision, $"{collisions} samples of version {versionId} have the same pixels in different splits.")
                { Details = new Dictionary<string, string> { ["count"] = collisions.ToString(CultureInfo.InvariantCulture) } };
        }
        var version = (await FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false) ?? throw Unavailable(versionId)).Version;
        var samples = await ReadSamplesAsync(versionId, cancellationToken).ConfigureAwait(false);
        var dataYaml = DatasetFiles.DataYaml(version.ClassMap, samples.Any(sample => sample.Split == DatasetSplit.Test));
        DatasetFiles.WriteAtomic(Path.Combine(root, DatasetFiles.DataYamlName), dataYaml);
        var manifestPath = Path.Combine(root, DatasetFiles.ManifestName);
        DatasetManifest.Write(manifestPath, version, samples);
        var (manifestHash, _) = DatasetFiles.HashFile(manifestPath);
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE dataset_versions SET state = 'ready', integrity_state = 'verified', verified_at = $now, manifest_hash = $manifest, data_yaml_hash = $yaml,
                    updated_at = $now WHERE id = $id AND state = 'building' RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(versionId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            command.Parameters.AddWithValue("$manifest", manifestHash);
            command.Parameters.AddWithValue("$yaml", DatasetFiles.Hash(dataYaml));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is long value ? value : (long?)null;
        }, cancellationToken).ConfigureAwait(false);
        if (revision is { } published) Raise(new DatasetEntityChange(versionId, published));
        return version.Counts;
    }

    /// <summary>A build that ends without a version leaves nothing: its rows go now, its folder with the file cleanup.</summary>
    internal async Task DiscardBuildAsync(Guid versionId)
    {
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, "DELETE FROM dataset_versions WHERE id = $id AND state = 'building';");
            command.Parameters.AddWithValue("$id", Key(versionId));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await FileCleanup.QueueAsync(connection, transaction, [VersionFolder(versionId)], token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        await cleanup.RemoveAfterCommitAsync([VersionFolder(versionId)]).ConfigureAwait(false);
    }

    /// <summary>Versions left building by builds that are not queued or running any more are discarded; called when the project opens.</summary>
    internal async Task DiscardOrphanedBuildsAsync(IReadOnlySet<Guid> activeBuilds, CancellationToken cancellationToken)
    {
        var building = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id FROM dataset_versions WHERE state = 'building';");
            return await CatalogSql.ListAsync(command, reader => ParseId(reader.GetString(0)), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var versionId in building.Where(id => !activeBuilds.Contains(id))) await DiscardBuildAsync(versionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Temporary ZIP files of exports that the process left behind (interrupted jobs) are removed; called when the project opens.
    /// </summary>
    internal async Task RemoveInterruptedExportsAsync(CancellationToken cancellationToken)
    {
        var inputs = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id, input FROM jobs WHERE kind = 'datasets.export' AND status = 'interrupted';");
            return await CatalogSql.ListAsync(command, reader => (Id: ParseId(reader.GetString(0)), Input: reader.GetString(1)), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var (id, json) in inputs)
        {
            if (Parse<DatasetExportInput>(json) is not { } input) continue;
            try
            {
                File.Delete(DatasetExportHandler.TemporaryPath(input.DestinationPath, id));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(error, "Project {ProjectId}: temporary file of the interrupted export {JobId} was not removed.", projectId, id);
            }
        }
    }

    // ---- Operation support ----

    /// <summary>A ready version a check (export: false) or an export may run on: not archived and, for an export, not corrupt.</summary>
    internal async Task<VersionRow> RequireOperableAsync(Guid versionId, bool export, CancellationToken cancellationToken)
    {
        var row = await FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: "ready" }) throw Unavailable(versionId);
        if (row.Version.ArchivedAt is not null) throw Archived(versionId);
        if (export && row.Version.IntegrityState == DatasetIntegrityState.Corrupt)
            throw new CoreException("datasets.corrupt", $"Dataset {versionId} failed its integrity check and cannot be exported.");
        return row;
    }

    /// <summary>Saves the outcome of a check (or a damage an export found); the revision grows and the change is announced.</summary>
    internal async Task SetIntegrityAsync(Guid versionId, DatasetIntegrityState state, CancellationToken cancellationToken)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE dataset_versions SET integrity_state = $state, verified_at = $now, updated_at = $now, revision = revision + 1
                WHERE id = $id AND state = 'ready' RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(versionId));
            command.Parameters.AddWithValue("$state", IntegrityKey(state));
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is long value ? value : (long?)null;
        }, cancellationToken).ConfigureAwait(false);
        if (revision is { } saved) Raise(new DatasetEntityChange(versionId, saved));
    }

    // ---- Helpers ----

    private async Task<DatasetSelectionResult> SelectAsync(DatasetSpecification specification, int width, int height, CancellationToken cancellationToken)
    {
        var catalog = await classes.ReadDatasetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var sources = await images.ReadDatasetSourcesAsync(specification.GroupIds, cancellationToken).ConfigureAwait(false);
        return DatasetSelection.Select(specification, catalog, sources, width, height);
    }

    private static void RequireBuildable(int frames, IReadOnlyList<DatasetPlanWarning> warnings)
    {
        if (frames == 0) throw new CoreException("datasets.empty", "The plan has no frames to build.");
        if (warnings.FirstOrDefault(warning => warning.Code == DatasetSelection.SplitCollision) is { } collision)
        {
            throw new CoreException(DatasetSelection.SplitCollision, $"{collision.Count} frames with the same pixels are in different splits.")
                { Details = new Dictionary<string, string> { ["count"] = collision.Count.ToString(CultureInfo.InvariantCulture) } };
        }
    }

    private async Task<VersionRow> RequireTrainableAsync(Guid versionId, CancellationToken cancellationToken, InputSize? expectedInput = null)
    {
        var row = await FindVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: "ready" }) throw Unavailable(versionId);
        var version = row.Version;
        if (version.ArchivedAt is not null) throw Archived(versionId);
        if (version.IntegrityState == DatasetIntegrityState.Corrupt)
            throw new CoreException("datasets.corrupt", $"Dataset {versionId} failed its integrity check and cannot be used for training.");
        var size = expectedInput ?? projectInput();
        if (version.Width != size.Width || version.Height != size.Height)
            throw new CoreException("datasets.input_size_mismatch", $"Dataset {versionId} is {version.Width}×{version.Height}, the expected input is {size.Width}×{size.Height}.");
        if (version.ClassMap.Count == 0 || version.ClassMap.Select((entry, index) => entry.Index == index).Any(same => !same))
            throw new CoreException("datasets.classes_invalid", $"Dataset {versionId} has no class map with indexes 0..n−1.");
        return row;
    }

    private static DatasetTrainingSource Source(DatasetVersion version, string directory) => new(version.Id, version.Name, version.Width, version.Height,
        version.ClassMap, version.Counts, version.ManifestHash, directory, Path.Combine(directory, DatasetFiles.DataYamlName),
        Path.Combine(directory, DatasetFiles.ManifestName));

    private async Task<IReadOnlyList<DeletionBlocker>> CollectBlockersAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var result = new List<DeletionBlocker>(await blockers.CollectAsync(projectId, store, versionId, cancellationToken).ConfigureAwait(false));
        if (readers.TryGetValue(versionId, out var count) && count > 0) result.Add(new DeletionBlocker(DatasetBlockers.ReaderActive, count, []));
        return result;
    }

    private sealed record DeletionComposition(string Name, long Samples, IReadOnlyList<Guid> Operations, string Revision);

    /// <summary>The version, its sample count and its finished history (its build, checks and exports) with a revision over all of it.</summary>
    private async Task<DeletionComposition> ComposeDeletionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid versionId,
        CancellationToken cancellationToken)
    {
        var row = await FindVersionAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);
        if (row is not { State: "ready" }) throw Unavailable(versionId);
        await using var count = Command(connection, transaction, "SELECT count(*) FROM dataset_samples WHERE version_id = $id;");
        count.Parameters.AddWithValue("$id", Key(versionId));
        var samples = await ScalarAsync(count, cancellationToken).ConfigureAwait(false);
        await using var history = Command(connection, transaction, """
            SELECT id FROM jobs WHERE kind = 'datasets.build' AND id = $id AND status NOT IN ('queued', 'running', 'blocked')
            UNION ALL SELECT id FROM jobs WHERE kind = 'datasets.verify' AND json_extract(input, '$.versionId') = $id AND status NOT IN ('queued', 'running', 'blocked')
            UNION ALL SELECT id FROM jobs WHERE kind = 'datasets.export' AND json_extract(input, '$.versionId') = $id AND status NOT IN ('queued', 'running', 'blocked');
            """);
        history.Parameters.AddWithValue("$id", Key(versionId));
        var operations = (await CatalogSql.ListAsync(history, reader => ParseId(reader.GetString(0)), cancellationToken).ConfigureAwait(false)).Order().ToArray();
        var text = new StringBuilder().Append(Key(versionId)).Append(':').Append(row.Version.Revision).Append(':').Append(samples);
        foreach (var operation in operations) text.Append(':').Append(Key(operation));
        return new DeletionComposition(row.Version.Name, samples, operations, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }

    private DatasetOperation? ToOperation(StoredJob job)
    {
        switch (job.Info.Kind)
        {
            case DatasetBuildHandler.Kind when Parse<DatasetBuildInput>(job.Input) is { } build:
                return new DatasetOperation(job.Info, DatasetOperationKind.Build, job.Info.Id, build.Specification.Name, false, build.PlanId, build.Specification, null);
            case DatasetVerifyHandler.Kind when Parse<DatasetVerifyInput>(job.Input) is { } verify:
                return new DatasetOperation(job.Info, DatasetOperationKind.Verify, verify.VersionId, verify.VersionName, false, null, null, null);
            case DatasetExportHandler.Kind when Parse<DatasetExportInput>(job.Input) is { } export:
                return new DatasetOperation(job.Info, DatasetOperationKind.Export, export.VersionId, export.VersionName, false, null, null,
                    Path.GetFileName(export.DestinationPath));
            default:
                return null;
        }
    }

    /// <summary>The ready versions among the IDs, with their current names for the operation cards.</summary>
    private static async Task<HashSet<Guid>> ReadyVersionsAsync(SqliteConnection connection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];
        await using var command = Command(connection, null, "SELECT id FROM dataset_versions WHERE state = 'ready' AND id IN (SELECT value FROM json_each($ids));");
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(Key)));
        return (await CatalogSql.ListAsync(command, reader => ParseId(reader.GetString(0)), cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    private Task<StoredJob?> FindJobByOperationAsync(string operationId, CancellationToken cancellationToken)
        => store.ReadAsync((connection, token) => JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token), cancellationToken);

    private static T Existing<T>(StoredJob job, string kind, string operationId) where T : class
    {
        if (job.Info.Kind != kind) throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {job.Info.Kind}.", nameof(operationId));
        return Parse<T>(job.Input) ?? throw new CoreException("datasets.unavailable", $"The input of job {job.Info.Id} cannot be read.");
    }

    internal static T? Parse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(DatasetPlan Plan, string SourceRevision)?> FindPlanAsync(Guid planId, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {PlanColumns} FROM dataset_plans WHERE id = $id;");
            command.Parameters.AddWithValue("$id", Key(planId));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadPlan(reader) : ((DatasetPlan, string)?)null;
        }, cancellationToken).ConfigureAwait(false);

    private Task<VersionRow?> FindVersionAsync(Guid versionId, CancellationToken cancellationToken)
        => store.ReadAsync((connection, token) => FindVersionAsync(connection, null, versionId, token), cancellationToken);

    private static async Task<VersionRow?> FindVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid versionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {VersionColumns} FROM dataset_versions WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(versionId));
        return await SingleOrDefaultAsync(command, ReadVersion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VersionRow> RequireReadyAsync(Guid versionId, CancellationToken cancellationToken)
        => await store.ReadAsync((connection, token) => RequireReadyAsync(connection, versionId, token), cancellationToken).ConfigureAwait(false);

    private static async Task<VersionRow> RequireReadyAsync(SqliteConnection connection, Guid versionId, CancellationToken cancellationToken)
    {
        var row = await FindVersionAsync(connection, null, versionId, cancellationToken).ConfigureAwait(false);
        return row is { State: "ready" } ? row : throw Unavailable(versionId);
    }

    private async Task<DatasetVersion> ChangeAsync(Guid versionId, long expectedRevision, Func<DatasetVersion, (string Set, Action<SqliteCommand> Bind)?> change,
        CancellationToken cancellationToken)
    {
        var (version, saved) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindVersionAsync(connection, transaction, versionId, token).ConfigureAwait(false);
            if (current is not { State: "ready" }) throw Unavailable(versionId);
            if (current.Version.Revision != expectedRevision)
                throw new CoreException("datasets.revision_conflict", $"Dataset {versionId} has revision {current.Version.Revision}, expected {expectedRevision}.");
            if (change(current.Version) is not { } update) return (current.Version, false);
            await using var command = Command(connection, transaction,
                $"UPDATE dataset_versions SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id RETURNING {VersionColumns};");
            command.Parameters.AddWithValue("$id", Key(versionId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            update.Bind(command);
            return ((await SingleOrDefaultAsync(command, ReadVersion, token).ConfigureAwait(false))!.Version, true);
        }, cancellationToken).ConfigureAwait(false);
        if (saved) Raise(new DatasetEntityChange(version.Id, version.Revision));
        return version;
    }

    private static QueuedFile VersionFolder(Guid versionId) => new(DatasetFiles.Kind, versionId, FileCleanup.DirectoryMarker);

    internal static string JobKind(DatasetOperationKind kind) => kind switch
    {
        DatasetOperationKind.Build => DatasetBuildHandler.Kind,
        DatasetOperationKind.Verify => DatasetVerifyHandler.Kind,
        DatasetOperationKind.Export => DatasetExportHandler.Kind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown dataset operation."),
    };

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("datasets.project_not_open", $"Project {projectId} is not open; its datasets are not available.");
    }

    private static void RequireOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, JobsManager.MaxOperationIdLength, nameof(operationId));
    }

    private void Raise(DatasetEntityChange change) => changed(new DatasetsChangedEventArgs(projectId, [change]));

    private static Dictionary<string, string> Field(string field) => new() { ["field"] = field };

    private static CoreException Unavailable(Guid id) => new("datasets.unavailable", $"Dataset, plan, operation or sample {id} was not found.");

    private static CoreException Archived(Guid id) => new("datasets.archived", $"Dataset {id} is archived; restore it first.");

    private static CoreException PlanStale(Guid planId) => new("datasets.plan_stale", $"The sources of plan {planId} changed; check the plan again.");
}
