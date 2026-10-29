using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Python;
using MautVision.Core.Training;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Training;

/// <summary>Stored live part of a run (training_runs); a queued run has none yet.</summary>
internal sealed record TrainingRunRow(Guid Id, string Phase, string? Stage, int? Epoch, int? Epochs, int? Batch, int? Batches, string? Metrics,
    string? EffectiveParams, string? Environment, string? ErrorCode, string? ErrorMessage, Guid? ResultVersionId, int EpochCount, long LogCount, long Revision);

/// <summary>
/// Training of one project: runs are the job training.run with the table training_runs (live progress, epochs, log), model versions
/// with their files under files/models/&lt;versionId&gt;/. The manager reads datasets and base models one way; the run itself is
/// coordinated by <see cref="TrainingRunHandler"/>. Job folders live in projects/&lt;id&gt;/training/jobs/&lt;runId&gt;/ while a run
/// prepares and trains; a successful run removes its folder, a failed one keeps its logs.
/// </summary>
internal sealed class TrainingManager : ITrainingManager
{
    internal const string Kind = "training.run";
    internal const int MaxPage = 200;
    internal const int MaxHistoryPage = 1000;
    internal const int MaxPresetNameLength = 80;
    private const string RunColumns = "id, phase, stage, epoch, epochs, batch, batches, metrics, effective_params, environment, error_code, error_message, "
        + "result_version_id, epoch_count, log_count, revision";
    private const string VersionColumns = "id, number, run_id, dataset, architecture, input_width, input_height, image_size, parameters, effective_params, "
        + "epochs_completed, stopped_early, best_epoch, classes, metrics, notes, environment, working, revision, created_at";

    private static readonly JsonSerializerOptions Json = JobsManager.Json;

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Func<ProjectMetadata> metadata;
    private readonly Action<TrainingChangedEventArgs> changed;
    private readonly DatasetsManager datasets;
    private readonly IBaseModelsManager baseModels;
    private readonly PythonWorkers python;
    private readonly ModelVersionLinks links;
    private readonly FileCleanup fileCleanup;
    private readonly ILogger logger;

    internal TrainingManager(string projectId, ProjectStore store, Func<bool> isOpen, Func<ProjectMetadata> metadata, Action<TrainingChangedEventArgs> changed,
        DatasetsManager datasets, IBaseModelsManager baseModels, PythonWorkers python, ModelVersionLinks links, FileCleanup fileCleanup, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.metadata = metadata;
        this.changed = changed;
        this.datasets = datasets;
        this.baseModels = baseModels;
        this.python = python;
        this.links = links;
        this.fileCleanup = fileCleanup;
        this.logger = logger;
    }

    internal PythonWorkers Python => python;

    internal DatasetsManager Datasets => datasets;

    internal IBaseModelsManager BaseModels => baseModels;

    internal ILogger Logger => logger;

    // ---- Runs ----

    public async Task<TrainingRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var jobs = await JobsTable.ListStoredByKindAsync(connection, projectId, Kind, limit, before, token).ConfigureAwait(false);
            var rows = await ReadRunsAsync(connection, null, jobs.Select(job => job.Info.Id).ToArray(), token).ConfigureAwait(false);
            var total = await JobsTable.CountByKindAsync(connection, Kind, token).ConfigureAwait(false);
            return new TrainingRunPage(jobs.Select(job => ToRun(job, rows.GetValueOrDefault(job.Info.Id))).OfType<TrainingRun>().ToArray(), total);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindRunAsync(connection, null, runId, token), cancellationToken).ConfigureAwait(false)
            ?? throw RunNotFound(runId);
    }

    public async Task<TrainingRunInput> PrepareRunAsync(TrainingRunRequest request, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, JobsManager.MaxOperationIdLength, nameof(operationId));
        RequireOpen();
        var existing = await store.ReadAsync((connection, token) => JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // A repeated request of the same operation returns the run it started, whatever changed since.
            if (existing.Info.Kind != Kind)
                throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {existing.Info.Kind}.", nameof(operationId));
            return ParseInput(existing.Input) ?? throw RunNotFound(existing.Info.Id);
        }
        if (python.CheckRuntime() is { } missing) throw new CoreException("training." + missing.Code, missing.Message);
        var parameters = TrainingParameterRules.Complete(request.Parameters);
        var presetName = request.PresetName?.Trim();
        if (presetName is { Length: 0 }) presetName = null;
        if (presetName is { Length: > MaxPresetNameLength }) presetName = presetName[..MaxPresetNameLength];
        var project = metadata();
        var architecture = project.BaseModelId;
        var model = await baseModels.GetOrNullAsync(architecture, cancellationToken).ConfigureAwait(false);
        if (model is null || model.Status != BaseModelStatus.Installed)
            throw new CoreException("training.base_model_not_installed", $"Base model {architecture} is not installed.")
            {
                Details = new Dictionary<string, string> { ["baseModelId"] = architecture },
            };
        var input = project.InputSize;
        if (input.Width % model.Stride != 0 || input.Height % model.Stride != 0)
            throw new CoreException("training.input_size_invalid", $"Input {input.Width}x{input.Height} is not a multiple of the model stride {model.Stride}.");
        var source = await datasets.GetTrainingSourceAsync(request.DatasetVersionId, cancellationToken).ConfigureAwait(false);
        if (parameters["classes"] is { ValueKind: JsonValueKind.Array } selected && selected.EnumerateArray().Any(item => item.GetDouble() >= source.Classes.Count))
            throw TrainingParameterRules.Invalid("classes");
        return new TrainingRunInput(source.VersionId, new TrainingDatasetReference(source.VersionId, source.Name, source.ManifestHash, source.Classes, source.Counts),
            architecture, input.Width, input.Height, Math.Max(input.Width, input.Height), parameters, presetName);
    }

    public async Task<TrainingEpochPage> ListEpochsAsync(Guid runId, int? after = null, int limit = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxHistoryPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await RequireRunAsync(connection, runId, token).ConfigureAwait(false);
            await using var command = Command(connection, null,
                "SELECT epoch, losses, metrics, lr, fitness FROM training_epochs WHERE run_id = $run AND epoch > $after ORDER BY epoch LIMIT $limit;");
            command.Parameters.AddWithValue("$run", Key(runId));
            command.Parameters.AddWithValue("$after", after ?? 0);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var items = await ListAsync(command, ReadEpoch, token).ConfigureAwait(false);
            await using var count = Command(connection, null, "SELECT count(*) FROM training_epochs WHERE run_id = $run;");
            count.Parameters.AddWithValue("$run", Key(runId));
            var total = (int)await ScalarAsync(count, token).ConfigureAwait(false);
            var more = items.Count > limit;
            if (more) items.RemoveAt(items.Count - 1);
            return new TrainingEpochPage(items, total, more ? items[^1].Epoch : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingLogPage> ListLogAsync(Guid runId, long? after = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxHistoryPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await RequireRunAsync(connection, runId, token).ConfigureAwait(false);
            await using var command = Command(connection, null,
                "SELECT seq, at, kind, message FROM training_log WHERE run_id = $run AND seq > $after ORDER BY seq LIMIT $limit;");
            command.Parameters.AddWithValue("$run", Key(runId));
            command.Parameters.AddWithValue("$after", after ?? 0);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var items = await ListAsync(command, ReadLog, token).ConfigureAwait(false);
            var more = items.Count > limit;
            if (more) items.RemoveAt(items.Count - 1);
            return new TrainingLogPage(items, await CountLogAsync(connection, runId, token).ConfigureAwait(false), more ? items[^1].Sequence : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingLogPage> TailLogAsync(Guid runId, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxHistoryPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await RequireRunAsync(connection, runId, token).ConfigureAwait(false);
            await using var command = Command(connection, null, "SELECT seq, at, kind, message FROM training_log WHERE run_id = $run ORDER BY seq DESC LIMIT $limit;");
            command.Parameters.AddWithValue("$run", Key(runId));
            command.Parameters.AddWithValue("$limit", limit);
            var items = await ListAsync(command, ReadLog, token).ConfigureAwait(false);
            items.Reverse();
            return new TrainingLogPage(items, await CountLogAsync(connection, runId, token).ConfigureAwait(false), items.Count > 0 ? items[^1].Sequence : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- Versions ----

    public async Task<IReadOnlyList<ModelVersion>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {VersionColumns} FROM model_versions ORDER BY number DESC;");
            var rows = await ListAsync(command, VersionRow, token).ConfigureAwait(false);
            var files = await ReadFilesAsync(connection, null, null, token).ConfigureAwait(false);
            return (IReadOnlyList<ModelVersion>)rows.Select(row => row.ToVersion(files.GetValueOrDefault(row.Id) ?? [])).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelVersion> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindVersionAsync(connection, null, versionId, token), cancellationToken).ConfigureAwait(false)
            ?? throw VersionNotFound(versionId);
    }

    public async Task<ModelVersion> SetWorkingVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var (version, previous) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindVersionAsync(connection, transaction, versionId, token).ConfigureAwait(false) ?? throw VersionNotFound(versionId);
            if (current.IsWorking) return (current, (TrainingEntityChange?)null);
            TrainingEntityChange? demoted = null;
            await using (var clear = Command(connection, transaction, "UPDATE model_versions SET working = 0, revision = revision + 1 WHERE working = 1 RETURNING id, revision;"))
            {
                await using var reader = await clear.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) demoted = new TrainingEntityChange(ParseId(reader.GetString(0)), reader.GetInt64(1));
            }
            await using (var set = Command(connection, transaction, "UPDATE model_versions SET working = 1, revision = revision + 1 WHERE id = $id;"))
            {
                set.Parameters.AddWithValue("$id", Key(versionId));
                await set.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return ((await FindVersionAsync(connection, transaction, versionId, token).ConfigureAwait(false))!, demoted);
        }, cancellationToken).ConfigureAwait(false);
        if (previous is not null || version.IsWorking)
        {
            var versions = new List<TrainingEntityChange> { new(version.Id, version.Revision) };
            if (previous is not null) versions.Add(previous);
            Raise([], versions);
        }
        logger.LogInformation("Project {ProjectId}: model version {VersionId} is the working one.", projectId, versionId);
        return version;
    }

    public async Task<ModelVersionDeletionReview> ReviewVersionDeletionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var (version, evaluations) = await store.ReadAsync(async (connection, token) =>
        {
            var found = await FindVersionAsync(connection, null, versionId, token).ConfigureAwait(false) ?? throw VersionNotFound(versionId);
            return (found, await links.CountDependentsAsync(connection, null, versionId, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        return new ModelVersionDeletionReview(version.Id, version.Number, version.IsWorking, evaluations, version.Files.Sum(file => file.ByteSize),
            await CollectBlockersAsync(version, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ModelVersionDeletionResult> DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var current = await GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if ((await CollectBlockersAsync(current, cancellationToken).ConfigureAwait(false)).FirstOrDefault() is { } blocker) throw InUse(versionId, blocker.Code);
        var folder = new QueuedFile(ModelVersion.ResourceKind, versionId, FileCleanup.DirectoryMarker);
        var (number, evaluations) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var version = await FindVersionAsync(connection, transaction, versionId, token).ConfigureAwait(false) ?? throw VersionNotFound(versionId);
            // "Make working" may have run since the review: the working flag is checked again inside the transaction.
            if (version.IsWorking) throw InUse(versionId, WorkingBlocker);
            var removed = await links.DeleteDependentsAsync(connection, transaction, versionId, token).ConfigureAwait(false);
            await using (var delete = Command(connection, transaction, "DELETE FROM model_versions WHERE id = $id;"))
            {
                delete.Parameters.AddWithValue("$id", Key(versionId));
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await FileCleanup.QueueAsync(connection, transaction, [folder], token).ConfigureAwait(false);
            return (version.Number, removed);
        }, cancellationToken).ConfigureAwait(false);
        var pending = await fileCleanup.RemoveAfterCommitAsync([folder]).ConfigureAwait(false);
        Raise([], [new TrainingEntityChange(versionId, null)]);
        logger.LogInformation("Project {ProjectId}: model version {VersionId} ({Number}) deleted with {Evaluations} evaluations; files pending: {Pending}.",
            projectId, versionId, number, evaluations, pending);
        return new ModelVersionDeletionResult(versionId, number, evaluations, pending);
    }

    public async Task<ModelExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, JobsManager.MaxOperationIdLength, nameof(operationId));
        if (!Path.IsPathFullyQualified(destinationPath)) throw new ArgumentException("The destination must be an absolute path.", nameof(destinationPath));
        RequireOpen();
        var existing = await store.ReadAsync((connection, token) => JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Info.Kind != ExportKind)
                throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {existing.Info.Kind}.", nameof(operationId));
            return JsonSerializer.Deserialize<ModelExportInput>(existing.Input, Json)!;
        }
        var version = await GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (!version.Files.Any(file => file.Kind == ModelFileKind.WeightsPt))
            throw new CoreException("training.artifact_corrupt", $"Model version {versionId} has no best.pt.") { Details = new Dictionary<string, string> { ["name"] = "best.pt" } };
        return new ModelExportInput(versionId, version.Number, Path.GetFullPath(destinationPath));
    }

    /// <summary>Blockers of deleting a version: the working flag first, then what the composition registered (checks, exports).</summary>
    private async Task<IReadOnlyList<DeletionBlocker>> CollectBlockersAsync(ModelVersion version, CancellationToken cancellationToken)
    {
        var result = new List<DeletionBlocker>();
        if (version.IsWorking) result.Add(new(WorkingBlocker, 1, []));
        result.AddRange(await links.CollectBlockersAsync(projectId, version.Id, cancellationToken).ConfigureAwait(false));
        return result;
    }

    /// <summary>training.version_in_use with the reason of the first blocker: working | evaluation | check | export.</summary>
    private static CoreException InUse(Guid versionId, string blocker) => new("training.version_in_use", $"Model version {versionId} is in use: {blocker}.")
    {
        Details = new Dictionary<string, string>
        {
            ["reason"] = blocker switch
            {
                WorkingBlocker => "working",
                "checks.evaluation_active" => "evaluation",
                "checks.live_active" => "check",
                ExportBlocker => "export",
                _ => blocker,
            },
        },
    };

    /// <summary>The working version cannot be deleted; another version is made working first.</summary>
    internal const string WorkingBlocker = "training.version_working";

    /// <summary>A queued or running ZIP export of the version.</summary>
    internal const string ExportBlocker = "training.export_active";

    internal const string ExportKind = "training.export";

    // ---- Version support for jobs and checks (not gated on isOpen) ----

    /// <summary>A version for a job of the queue, which may start while the project is being opened; null when it is gone.</summary>
    internal Task<ModelVersion?> FindVersionForJobAsync(Guid versionId, CancellationToken cancellationToken)
        => store.ReadAsync((connection, token) => FindVersionAsync(connection, null, versionId, token), cancellationToken);

    /// <summary>The folder of a version's files: files/models/&lt;versionId&gt;/.</summary>
    internal async Task<string> VersionDirectoryAsync(Guid versionId, CancellationToken cancellationToken)
        => Path.Combine((await store.GetFilesAsync(cancellationToken).ConfigureAwait(false)).GetDirectory(ModelVersion.ResourceKind), versionId.ToString("D"));

    // ---- Run support (not gated on isOpen: a queued run starts while the project is being opened) ----

    internal string JobDirectory(Guid runId) => Path.Combine(store.ProjectDirectory, "training", "jobs", runId.ToString("D"));

    internal static string OperationIdOf(Guid runId) => runId.ToString("D");

    internal InputSize ProjectInput => metadata().InputSize;

    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => store.GetFilesAsync(cancellationToken);

    internal TrainingRunRecorder CreateRecorder(Guid runId) => new(this, runId, logger);

    /// <summary>The run row of a job when it starts: phase preparing.</summary>
    internal async Task RegisterRunAsync(Guid runId)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                INSERT INTO training_runs (id, phase, revision, created_at, updated_at) VALUES ($id, 'preparing', 1, $now, $now)
                ON CONFLICT (id) DO UPDATE SET phase = 'preparing', revision = revision + 1, updated_at = $now
                RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(runId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Raise([new(runId, revision)], []);
    }

    /// <summary>
    /// Changes the phase of a run. errorMessage — the message for the log; workerText — what the UI shows for a failure the worker reported
    /// itself (stored in error_message as <see cref="CodedTextJson"/> with the message as its text); systemLine — a line of the log.
    /// </summary>
    internal async Task SetPhaseAsync(Guid runId, string phase, string? errorCode = null, string? errorMessage = null, CodedText? systemLine = null,
        CodedText? workerText = null)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            if (systemLine is not null) await AppendLogAsync(connection, transaction, runId, [(Now(), TrainingLogKind.System, systemLine)], token).ConfigureAwait(false);
            await using var command = Command(connection, transaction, """
                UPDATE training_runs SET phase = $phase, error_code = $code, error_message = $message, revision = revision + 1, updated_at = $now
                WHERE id = $id RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(runId));
            command.Parameters.AddWithValue("$phase", phase);
            command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", (object?)(workerText is not null ? CodedTextJson.Serialize(workerText with { Text = errorMessage }) : errorMessage)
                ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (revision > 0) Raise([new(runId, revision)], []);
    }

    /// <summary>One batched write of a running training: live fields, new epochs and log lines; raises one change.</summary>
    internal async Task WriteSnapshotAsync(Guid runId, TrainingRunSnapshot snapshot)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            foreach (var epoch in snapshot.NewEpochs) await InsertEpochAsync(connection, transaction, runId, epoch, token).ConfigureAwait(false);
            await AppendLogAsync(connection, transaction, runId, snapshot.Lines, token).ConfigureAwait(false);
            await using var command = Command(connection, transaction, """
                UPDATE training_runs SET stage = $stage, epoch = $epoch, epochs = $epochs, batch = $batch, batches = $batches, metrics = $metrics,
                    effective_params = COALESCE($effective, effective_params), environment = COALESCE($environment, environment),
                    epoch_count = (SELECT count(*) FROM training_epochs WHERE run_id = $id), revision = revision + 1, updated_at = $now
                WHERE id = $id RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(runId));
            command.Parameters.AddWithValue("$stage", (object?)snapshot.Stage ?? DBNull.Value);
            command.Parameters.AddWithValue("$epoch", (object?)snapshot.Epoch ?? DBNull.Value);
            command.Parameters.AddWithValue("$epochs", (object?)snapshot.Epochs ?? DBNull.Value);
            command.Parameters.AddWithValue("$batch", (object?)snapshot.Batch ?? DBNull.Value);
            command.Parameters.AddWithValue("$batches", (object?)snapshot.Batches ?? DBNull.Value);
            command.Parameters.AddWithValue("$metrics", snapshot.Metrics is null ? DBNull.Value : JsonSerializer.Serialize(snapshot.Metrics, Json));
            command.Parameters.AddWithValue("$effective", snapshot.EffectiveParams is { } effective ? effective.GetRawText() : DBNull.Value);
            command.Parameters.AddWithValue("$environment", snapshot.Environment is { } environment ? environment.GetRawText() : DBNull.Value);
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (revision > 0) Raise([new(runId, revision)], []);
    }

    /// <summary>
    /// Accepts a verified result: files are moved into files/models/&lt;versionId&gt;/, then one transaction adds the version (number,
    /// passport, files, working when it is the first one), replaces the run history with history.jsonl and finishes the run.
    /// Repeating it for the same run returns the version it created.
    /// </summary>
    internal async Task<Guid> AcceptAsync(Guid runId, TrainingRunInput input, WorkerTrainResult result, CancellationToken cancellationToken)
    {
        var versionId = JobItemIds.Derive(runId, 1);
        var existing = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id FROM model_versions WHERE run_id = $run;");
            command.Parameters.AddWithValue("$run", Key(runId));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return ParseId(existing);

        var classes = OutputClasses(input, result);
        var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        var modelsDirectory = files.CreateDirectory(ModelVersion.ResourceKind);
        var target = Path.Combine(modelsDirectory, versionId.ToString("D"));
        var staging = Path.Combine(modelsDirectory, "." + versionId.ToString("D") + ".staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var stored = new List<ModelVersionFile>();
        foreach (var file in result.Artifacts.Concat(result.Reports))
        {
            var name = file.RelativePath.StartsWith("artifacts/", StringComparison.Ordinal) ? file.RelativePath["artifacts/".Length..] : file.RelativePath;
            var destination = Path.Combine([staging, .. name.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.AbsolutePath, destination, overwrite: true);
            stored.Add(new ModelVersionFile(KindOf(file.Kind), name, file.ByteSize, file.Sha256, Guid.NewGuid()));
        }
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);

        var bestEpoch = result.History.Where(epoch => epoch.Fitness is not null).OrderByDescending(epoch => epoch.Fitness).Select(epoch => (int?)epoch.Epoch).FirstOrDefault();
        var notes = result.Warnings.Select(warning => CodedTextJson.Serialize(warning.Text)).ToArray();
        var now = Now();
        long runRevision = 0;
        TrainingEntityChange? versionChange = null;
        try
        {
            (runRevision, versionChange) = await store.WriteAsync(async (connection, transaction, token) =>
            {
                long number;
                await using (var next = Command(connection, transaction, "SELECT COALESCE(MAX(number), 0) + 1 FROM model_versions;"))
                    number = await ScalarAsync(next, token).ConfigureAwait(false);
                bool working;
                await using (var any = Command(connection, transaction, "SELECT NOT EXISTS (SELECT 1 FROM model_versions WHERE working = 1);"))
                    working = await ScalarAsync(any, token).ConfigureAwait(false) == 1;
                await using (var insert = Command(connection, transaction, $"""
                    INSERT INTO model_versions ({VersionColumns})
                    VALUES ($id, $number, $run, $dataset, $architecture, $width, $height, $imgsz, $parameters, $effective, $epochs, $stopped, $best, $classes,
                        $metrics, $notes, $environment, $working, 1, $now);
                    """))
                {
                    insert.Parameters.AddWithValue("$id", Key(versionId));
                    insert.Parameters.AddWithValue("$number", number);
                    insert.Parameters.AddWithValue("$run", Key(runId));
                    insert.Parameters.AddWithValue("$dataset", JsonSerializer.Serialize(input.Dataset, Json));
                    insert.Parameters.AddWithValue("$architecture", input.Architecture);
                    insert.Parameters.AddWithValue("$width", input.Width);
                    insert.Parameters.AddWithValue("$height", input.Height);
                    insert.Parameters.AddWithValue("$imgsz", input.ImageSize);
                    insert.Parameters.AddWithValue("$parameters", JsonSerializer.Serialize(input.Parameters, Json));
                    insert.Parameters.AddWithValue("$effective", result.EffectiveParams.GetRawText());
                    insert.Parameters.AddWithValue("$epochs", result.EpochsCompleted);
                    insert.Parameters.AddWithValue("$stopped", result.StoppedEarly ? 1 : 0);
                    insert.Parameters.AddWithValue("$best", (object?)bestEpoch ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$classes", JsonSerializer.Serialize(classes, Json));
                    insert.Parameters.AddWithValue("$metrics", JsonSerializer.Serialize(result.Metrics, Json));
                    insert.Parameters.AddWithValue("$notes", JsonSerializer.Serialize(notes, Json));
                    insert.Parameters.AddWithValue("$environment", result.Environment.GetRawText());
                    insert.Parameters.AddWithValue("$working", working ? 1 : 0);
                    insert.Parameters.AddWithValue("$now", Time(now));
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                foreach (var file in stored)
                {
                    await using var row = Command(connection, transaction, """
                        INSERT INTO model_version_files (version_id, name, kind, byte_size, sha256, resource_id) VALUES ($version, $name, $kind, $size, $sha, $resource);
                        """);
                    row.Parameters.AddWithValue("$version", Key(versionId));
                    row.Parameters.AddWithValue("$name", file.Name);
                    row.Parameters.AddWithValue("$kind", KindKey(file.Kind));
                    row.Parameters.AddWithValue("$size", file.ByteSize);
                    row.Parameters.AddWithValue("$sha", file.Sha256);
                    row.Parameters.AddWithValue("$resource", Key(file.ResourceId));
                    await row.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                // history.jsonl is the authoritative history: it replaces the epochs collected from events.
                await using (var clear = Command(connection, transaction, "DELETE FROM training_epochs WHERE run_id = $run;"))
                {
                    clear.Parameters.AddWithValue("$run", Key(runId));
                    await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                foreach (var epoch in result.History)
                {
                    await InsertEpochAsync(connection, transaction, runId, new TrainingEpoch(epoch.Epoch, epoch.Losses, epoch.Metrics, epoch.Lr, epoch.Fitness), token)
                        .ConfigureAwait(false);
                }
                await AppendLogAsync(connection, transaction, runId, [(now, TrainingLogKind.System, CodedText.Of("trainingLog.version_created", ("number", number)))], token).ConfigureAwait(false);
                await using var finish = Command(connection, transaction, """
                    INSERT INTO training_runs (id, phase, revision, created_at, updated_at) VALUES ($id, 'finished', 0, $now, $now) ON CONFLICT (id) DO NOTHING;
                    UPDATE training_runs SET phase = 'finished', result_version_id = $version, effective_params = $effective, environment = $environment,
                        error_code = NULL, error_message = NULL, epoch_count = $count, stage = NULL, batch = NULL, batches = NULL,
                        epoch = COALESCE($last, epoch), revision = revision + 1, updated_at = $now
                    WHERE id = $id RETURNING revision;
                    """);
                finish.Parameters.AddWithValue("$id", Key(runId));
                finish.Parameters.AddWithValue("$version", Key(versionId));
                finish.Parameters.AddWithValue("$effective", result.EffectiveParams.GetRawText());
                finish.Parameters.AddWithValue("$environment", result.Environment.GetRawText());
                finish.Parameters.AddWithValue("$count", result.History.Count);
                finish.Parameters.AddWithValue("$last", result.History.Count > 0 ? result.History[^1].Epoch : DBNull.Value);
                finish.Parameters.AddWithValue("$now", Time(now));
                return (await ScalarAsync(finish, token).ConfigureAwait(false), new TrainingEntityChange(versionId, 1));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The version did not appear: its files go, the job folder keeps the result for the next attempt.
            TryDeleteDirectory(target);
            throw;
        }
        Raise([new(runId, runRevision)], [versionChange!]);
        logger.LogInformation("Project {ProjectId}: training run {RunId} produced model version {VersionId} ({Files} files).", projectId, runId, versionId, stored.Count);
        return versionId;
    }

    /// <summary>
    /// After the application ended during training: a run whose job is not active any more accepts a valid published result.json
    /// (the returned runs; their jobs are then marked succeeded), otherwise it is finished as the job ended (interrupted). Job
    /// folders of finished runs are cleaned in the background.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> RecoverAsync(IReadOnlySet<Guid> activeJobs, CancellationToken cancellationToken)
    {
        var unfinished = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {RunColumns} FROM training_runs WHERE phase <> 'finished';");
            return await ListAsync(command, ReadRunRow, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        var accepted = new List<Guid>();
        foreach (var row in unfinished.Where(row => !activeJobs.Contains(row.Id)))
        {
            var job = await store.ReadAsync((connection, token) => JobsTable.GetStoredAsync(connection, null, projectId, row.Id, token), cancellationToken)
                .ConfigureAwait(false);
            var input = job is null ? null : ParseInput(job.Input);
            if (job is { Info.Status: JobStatus.Interrupted } && input is not null)
            {
                var published = await python.ReadPublishedAsync(JobDirectory(row.Id), OperationIdOf(row.Id), "train", WorkerResults.ParseTrainAsync, cancellationToken)
                    .ConfigureAwait(false);
                if (published is { Kind: WorkerOutcomeKind.Succeeded, Result: { } result })
                {
                    await AcceptAsync(row.Id, input, result, cancellationToken).ConfigureAwait(false);
                    accepted.Add(row.Id);
                    logger.LogInformation("Project {ProjectId}: training run {RunId} accepted its result published before the application ended.", projectId, row.Id);
                    continue;
                }
                if (published is { Kind: WorkerOutcomeKind.Failed })
                    logger.LogWarning("Project {ProjectId}: published result of training run {RunId} is not accepted: {Code}.", projectId, row.Id, published.Code);
            }
            await SetPhaseAsync(row.Id, "finished", systemLine: CodedText.Of("trainingLog.interrupted")).ConfigureAwait(false);
        }
        _ = Task.Run(() => CleanJobFoldersAsync(activeJobs));
        return accepted;
    }

    /// <summary>Removes a finished run's folder: all of it after success, inputs and output (not the logs) otherwise.</summary>
    internal void CleanJobFolder(Guid runId, bool succeeded)
    {
        var directory = JobDirectory(runId);
        if (!Directory.Exists(directory)) return;
        if (succeeded)
        {
            TryDeleteDirectory(directory);
            return;
        }
        TryDeleteDirectory(Path.Combine(directory, "input"));
        TryDeleteDirectory(Path.Combine(directory, "output"));
    }

    private async Task CleanJobFoldersAsync(IReadOnlySet<Guid> activeJobs)
    {
        try
        {
            var root = Path.Combine(store.ProjectDirectory, "training", "jobs");
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var runId) || activeJobs.Contains(runId)) continue;
                var row = await store.ReadAsync((connection, token) => FindRunRowAsync(connection, null, runId, token)).ConfigureAwait(false);
                if (row is { Phase: not "finished" }) continue;
                CleanJobFolder(runId, row is null || row.ResultVersionId is not null);
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Project {ProjectId}: training job folders were not cleaned.", projectId);
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open (antivirus, a viewer) keeps the folder; the next open of the project tries again.
            logger.LogWarning(error, "Project {ProjectId}: training folder {Folder} was not removed.", projectId, Path.GetFileName(directory));
        }
    }

    /// <summary>Resource channel: the file of a model version by its resource ID («&lt;versionId&gt;/plots/results.png»).</summary>
    internal static async ValueTask<string?> FindModelFileAsync(ProjectStore store, MaterialFiles files, Guid resourceId, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT version_id, name FROM model_version_files WHERE resource_id = $id;");
            command.Parameters.AddWithValue("$id", Key(resourceId));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            return reader.GetString(0) + "/" + reader.GetString(1);
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Classes in the order of model outputs. With singleCls the model has one output that stands for all selected classes; its
    /// name is the one the trainer reports. Otherwise the outputs are the dataset classes (the classes filter keeps their indexes).
    /// </summary>
    private static IReadOnlyList<ModelClass> OutputClasses(TrainingRunInput input, WorkerTrainResult result)
    {
        var outputs = result.EffectiveParams.TryGetProperty("outputClasses", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => (Index: item.GetProperty("index").GetInt32(), Name: item.GetProperty("name").GetString() ?? "")).ToArray()
            : null;
        if (input.Parameters.TryGetValue("singleCls", out var single) && single.ValueKind == JsonValueKind.True)
        {
            var selected = input.Parameters.TryGetValue("classes", out var filter) && filter.ValueKind == JsonValueKind.Array
                ? filter.EnumerateArray().Select(item => (int)item.GetDouble()).ToHashSet()
                : null;
            var sources = input.Dataset.Classes.Where(item => selected is null || selected.Contains(item.Index)).Select(item => item.ClassId).ToArray();
            var name = outputs is [{ Index: 0 } only] && only.Name.Length > 0 ? only.Name : string.Join(", ", input.Dataset.Classes.Where(item => sources.Contains(item.ClassId)).Select(item => item.Name));
            return [new ModelClass(0, null, name, sources)];
        }
        if (outputs is not null && outputs.Length != input.Dataset.Classes.Count)
            throw new CoreException("training.result_invalid", $"The model has {outputs.Length} outputs, the dataset {input.Dataset.Classes.Count} classes.");
        return input.Dataset.Classes.OrderBy(item => item.Index).Select(item => new ModelClass(item.Index, item.ClassId, item.Name, [item.ClassId])).ToArray();
    }

    // ---- Reading ----

    private async Task<TrainingRun?> FindRunAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid runId, CancellationToken token)
    {
        var job = await JobsTable.GetStoredAsync(connection, transaction, projectId, runId, token).ConfigureAwait(false);
        if (job is null || job.Info.Kind != Kind) return null;
        var rows = await ReadRunsAsync(connection, transaction, [runId], token).ConfigureAwait(false);
        return ToRun(job, rows.GetValueOrDefault(runId));
    }

    private async Task RequireRunAsync(SqliteConnection connection, Guid runId, CancellationToken token)
    {
        var job = await JobsTable.GetAsync(connection, null, projectId, runId, token).ConfigureAwait(false);
        if (job is null || job.Kind != Kind) throw RunNotFound(runId);
    }

    private static async Task<long> CountLogAsync(SqliteConnection connection, Guid runId, CancellationToken token)
    {
        await using var count = Command(connection, null, "SELECT count(*) FROM training_log WHERE run_id = $run;");
        count.Parameters.AddWithValue("$run", Key(runId));
        return await ScalarAsync(count, token).ConfigureAwait(false);
    }

    private static async Task<TrainingRunRow?> FindRunRowAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid runId, CancellationToken token)
        => (await ReadRunsAsync(connection, transaction, [runId], token).ConfigureAwait(false)).GetValueOrDefault(runId);

    private static async Task<Dictionary<Guid, TrainingRunRow>> ReadRunsAsync(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<Guid> ids,
        CancellationToken token)
    {
        var result = new Dictionary<Guid, TrainingRunRow>();
        if (ids.Count == 0) return result;
        await using var command = Command(connection, transaction, $"SELECT {RunColumns} FROM training_runs WHERE id IN (SELECT value FROM json_each($ids));");
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(Key)));
        foreach (var row in await ListAsync(command, ReadRunRow, token).ConfigureAwait(false)) result[row.Id] = row;
        return result;
    }

    private static TrainingRunRow ReadRunRow(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetInt32(3),
        reader.IsDBNull(4) ? null : reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : ParseId(reader.GetString(12)),
        reader.GetInt32(13),
        reader.GetInt64(14),
        reader.GetInt64(15));

    private static TrainingRun? ToRun(StoredJob job, TrainingRunRow? row)
    {
        if (ParseInput(job.Input) is not { } input) return null;
        var info = job.Info;
        var state = info.Status switch
        {
            JobStatus.Queued => info.CancelRequested ? TrainingRunState.Cancelling : TrainingRunState.Queued,
            JobStatus.Running => info.CancelRequested ? TrainingRunState.Cancelling
                : row is null || row.Phase == "preparing" ? TrainingRunState.Preparing : TrainingRunState.Training,
            JobStatus.Succeeded => TrainingRunState.Succeeded,
            JobStatus.Cancelled => TrainingRunState.Cancelled,
            JobStatus.Interrupted => TrainingRunState.Interrupted,
            _ => TrainingRunState.Failed,
        };
        TrainingRunError? error = state is TrainingRunState.Failed or TrainingRunState.Interrupted
            ? row?.ErrorCode is { } code ? StoredError(code, row.ErrorMessage)
                : info.Error is { } jobError ? new TrainingRunError(jobError.Code, jobError.Message) : null
            : null;
        var metrics = row?.Metrics is { } json ? JsonSerializer.Deserialize<Dictionary<string, double>>(json, Json)! : new Dictionary<string, double>();
        var epochs = row?.Epochs ?? (input.Parameters.TryGetValue("epochs", out var requested) && requested.TryGetInt32(out var count) ? count : null);
        return new TrainingRun(
            info,
            state,
            input.Dataset,
            input.Architecture,
            new InputSize(input.Width, input.Height),
            input.ImageSize,
            input.Parameters,
            input.PresetName,
            row?.EffectiveParams is { } effective ? Element(effective) : null,
            new TrainingRunProgress(row?.Stage, row?.Epoch, epochs, row?.Batch, row?.Batches, metrics),
            row?.EpochCount ?? 0,
            row?.LogCount ?? 0,
            row?.Environment is { } environment ? Element(environment) : null,
            error,
            row?.ResultVersionId,
            row?.Revision ?? 0);
    }

    internal static TrainingRunInput? ParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TrainingRunInput>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrainingEpoch ReadEpoch(SqliteDataReader reader) => new(
        reader.GetInt32(0),
        JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(1), Json)!,
        JsonSerializer.Deserialize<Dictionary<string, double>>(reader.GetString(2), Json)!,
        reader.IsDBNull(3) ? null : reader.GetDouble(3),
        reader.IsDBNull(4) ? null : reader.GetDouble(4));

    /// <summary>A failure of a run as stored: a worker failure keeps what the UI shows in error_message (<see cref="CodedTextJson"/>).</summary>
    private static TrainingRunError StoredError(string code, string? message)
    {
        var stored = CodedTextJson.Parse(message);
        return stored?.Code is not null ? new TrainingRunError(code, stored.Text ?? "", stored) : new TrainingRunError(code, message ?? "");
    }

    /// <summary>System lines and warnings hold codes (<see cref="CodedTextJson"/>); console lines and stages are raw text.</summary>
    private static TrainingLogEntry ReadLog(SqliteDataReader reader)
    {
        var kind = reader.GetString(2) switch
        {
            "stage" => TrainingLogKind.Stage,
            "warning" => TrainingLogKind.Warning,
            "system" => TrainingLogKind.System,
            _ => TrainingLogKind.Log,
        };
        var message = reader.GetString(3);
        return new TrainingLogEntry(reader.GetInt64(0), Time(reader.GetInt64(1)), kind,
            kind is TrainingLogKind.System or TrainingLogKind.Warning ? CodedTextJson.Parse(message)! : CodedText.Plain(message));
    }

    private static async Task InsertEpochAsync(SqliteConnection connection, SqliteTransaction transaction, Guid runId, TrainingEpoch epoch, CancellationToken token)
    {
        await using var command = Command(connection, transaction, """
            INSERT OR REPLACE INTO training_epochs (run_id, epoch, losses, metrics, lr, fitness) VALUES ($run, $epoch, $losses, $metrics, $lr, $fitness);
            """);
        command.Parameters.AddWithValue("$run", Key(runId));
        command.Parameters.AddWithValue("$epoch", epoch.Epoch);
        command.Parameters.AddWithValue("$losses", JsonSerializer.Serialize(epoch.Losses, Json));
        command.Parameters.AddWithValue("$metrics", JsonSerializer.Serialize(epoch.Metrics, Json));
        command.Parameters.AddWithValue("$lr", (object?)epoch.Lr ?? DBNull.Value);
        command.Parameters.AddWithValue("$fitness", (object?)epoch.Fitness ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    /// <summary>Appends log lines after the last one of the run and counts them; sequence numbers start at 1.</summary>
    private static async Task AppendLogAsync(SqliteConnection connection, SqliteTransaction transaction, Guid runId,
        IReadOnlyList<(DateTimeOffset At, TrainingLogKind Kind, CodedText Message)> lines, CancellationToken token)
    {
        if (lines.Count == 0) return;
        long last;
        await using (var count = Command(connection, transaction, "SELECT COALESCE(MAX(seq), 0) FROM training_log WHERE run_id = $run;"))
        {
            count.Parameters.AddWithValue("$run", Key(runId));
            last = await ScalarAsync(count, token).ConfigureAwait(false);
        }
        foreach (var line in lines)
        {
            await using var insert = Command(connection, transaction, "INSERT INTO training_log (run_id, seq, at, kind, message) VALUES ($run, $seq, $at, $kind, $message);");
            insert.Parameters.AddWithValue("$run", Key(runId));
            insert.Parameters.AddWithValue("$seq", ++last);
            insert.Parameters.AddWithValue("$at", Time(line.At));
            insert.Parameters.AddWithValue("$kind", line.Kind switch
            {
                TrainingLogKind.Stage => "stage",
                TrainingLogKind.Warning => "warning",
                TrainingLogKind.System => "system",
                _ => "log",
            });
            insert.Parameters.AddWithValue("$message", line.Kind is TrainingLogKind.System or TrainingLogKind.Warning
                ? CodedTextJson.Serialize(line.Message)
                : line.Message.Text ?? "");
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using var update = Command(connection, transaction, "UPDATE training_runs SET log_count = $count WHERE id = $run;");
        update.Parameters.AddWithValue("$run", Key(runId));
        update.Parameters.AddWithValue("$count", last);
        await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<ModelVersion?> FindVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid versionId, CancellationToken token)
    {
        await using var command = Command(connection, transaction, $"SELECT {VersionColumns} FROM model_versions WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(versionId));
        var rows = await ListAsync(command, VersionRow, token).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        var files = await ReadFilesAsync(connection, transaction, versionId, token).ConfigureAwait(false);
        return rows[0].ToVersion(files.GetValueOrDefault(versionId) ?? []);
    }


    private static async Task<Dictionary<Guid, List<ModelVersionFile>>> ReadFilesAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid? versionId,
        CancellationToken token)
    {
        await using var command = Command(connection, transaction, versionId is null
            ? "SELECT version_id, name, kind, byte_size, sha256, resource_id FROM model_version_files ORDER BY version_id, kind, name;"
            : "SELECT version_id, name, kind, byte_size, sha256, resource_id FROM model_version_files WHERE version_id = $id ORDER BY kind, name;");
        if (versionId is { } id) command.Parameters.AddWithValue("$id", Key(id));
        var result = new Dictionary<Guid, List<ModelVersionFile>>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var version = ParseId(reader.GetString(0));
            if (!result.TryGetValue(version, out var list)) result[version] = list = [];
            list.Add(new ModelVersionFile(ParseKind(reader.GetString(2)), reader.GetString(1), reader.GetInt64(3), reader.GetString(4), ParseId(reader.GetString(5))));
        }
        return result;
    }

    private static VersionRowData VersionRow(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetInt32(1),
        ParseId(reader.GetString(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetInt32(10),
        reader.GetInt64(11) == 1,
        reader.IsDBNull(12) ? null : reader.GetInt32(12),
        reader.GetString(13),
        reader.GetString(14),
        reader.GetString(15),
        reader.GetString(16),
        reader.GetInt64(17) == 1,
        reader.GetInt64(18),
        Time(reader.GetInt64(19)));

    private sealed record VersionRowData(Guid Id, int Number, Guid RunId, string Dataset, string Architecture, int Width, int Height, int ImageSize, string Parameters,
        string EffectiveParams, int EpochsCompleted, bool StoppedEarly, int? BestEpoch, string Classes, string Metrics, string Notes, string Environment, bool Working,
        long Revision, DateTimeOffset CreatedAt)
    {
        internal ModelVersion ToVersion(IReadOnlyList<ModelVersionFile> files) => new(
            Id,
            Number,
            RunId,
            JsonSerializer.Deserialize<TrainingDatasetReference>(Dataset, Json)!,
            Architecture,
            new InputSize(Width, Height),
            ImageSize,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Parameters, Json)!,
            Element(EffectiveParams),
            EpochsCompleted,
            StoppedEarly,
            BestEpoch,
            JsonSerializer.Deserialize<ModelClass[]>(Classes, Json)!,
            JsonSerializer.Deserialize<Dictionary<string, double>>(Metrics, Json)!,
            files,
            JsonSerializer.Deserialize<string[]>(Notes, Json)!.Select(note => CodedTextJson.Parse(note)!).ToArray(),
            Element(Environment),
            Working,
            Revision,
            CreatedAt);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ModelFileKind KindOf(string kind) => kind switch
    {
        "weights_pt" => ModelFileKind.WeightsPt,
        "onnx" => ModelFileKind.Onnx,
        "plot" => ModelFileKind.Plot,
        _ => ModelFileKind.Log,
    };

    private static ModelFileKind ParseKind(string kind) => KindOf(kind);

    private static string KindKey(ModelFileKind kind) => kind switch
    {
        ModelFileKind.WeightsPt => "weights_pt",
        ModelFileKind.Onnx => "onnx",
        ModelFileKind.Plot => "plot",
        _ => "log",
    };

    private void Raise(IReadOnlyList<TrainingEntityChange> runs, IReadOnlyList<TrainingEntityChange> versions) => changed(new TrainingChangedEventArgs(projectId, runs, versions));

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("training.project_not_open", $"Project {projectId} is not open.");
    }

    private static CoreException RunNotFound(Guid runId) => new("training.run_not_found", $"Training run {runId} does not exist.");

    private static CoreException VersionNotFound(Guid versionId) => new("training.version_not_found", $"Model version {versionId} does not exist.");
}
