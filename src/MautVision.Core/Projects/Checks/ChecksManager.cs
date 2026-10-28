using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Screen;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Projects.Training;
using MautVision.Core.Python;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Checks;

/// <summary>Stored part of an evaluation (model_evaluations); a queued evaluation has none yet.</summary>
internal sealed record EvaluationRow(Guid Id, string Phase, bool Succeeded, string? Metrics, string? ClassMetrics, int? ImageCount, string? Environment,
    string? ErrorCode, string? ErrorMessage, long Revision);

/// <summary>
/// Checks of one project. Evaluations are the job checks.evaluate with the table model_evaluations; their folder is
/// projects/&lt;id&gt;/checks/jobs/&lt;evaluationId&gt;/ while they run. Live checks live in memory: one worker process per check
/// (predict for an image or a video, live for the screen) in projects/&lt;id&gt;/checks/live/&lt;checkId&gt;/, holding the GPU slot
/// from start to end. Versions and datasets are read one way; neither knows about checks.
/// </summary>
internal sealed class ChecksManager : IChecksManager
{
    internal const string EvaluationKind = "checks.evaluate";
    internal const int MaxPage = 200;
    internal const int MaxJournalPage = 200;
    internal const int KeptChecks = 8;
    private const string Columns = "id, phase, succeeded, metrics, class_metrics, image_count, environment, error_code, error_message, revision";

    private static readonly JsonSerializerOptions Json = JobsManager.Json;

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Action<ChecksChangedEventArgs> changed;
    private readonly TrainingManager training;
    private readonly DatasetsManager datasets;
    private readonly PythonWorkers python;
    private readonly CheckServices services;
    private readonly ILogger logger;
    private readonly Lock sessionsGate = new();
    private readonly List<LiveCheckSession> sessions = [];

    internal ChecksManager(string projectId, ProjectStore store, Func<bool> isOpen, Action<ChecksChangedEventArgs> changed, TrainingManager training,
        DatasetsManager datasets, PythonWorkers python, CheckServices services, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.changed = changed;
        this.training = training;
        this.datasets = datasets;
        this.python = python;
        this.services = services;
        this.logger = logger;
    }

    internal PythonWorkers Python => python;

    internal TrainingManager Training => training;

    internal DatasetsManager Datasets => datasets;

    internal ILogger Logger => logger;

    // ---- Evaluations ----

    public async Task<ModelEvaluationInput> PrepareEvaluationAsync(ModelEvaluationRequest request, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, JobsManager.MaxOperationIdLength, nameof(operationId));
        RequireOpen();
        var existing = await store.ReadAsync((connection, token) => JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Info.Kind != EvaluationKind)
                throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {existing.Info.Kind}.", nameof(operationId));
            return ParseInput(existing.Input) ?? throw EvaluationNotFound(existing.Info.Id);
        }
        RequireRuntime();
        if (!IsDevice(request.Device)) throw Parameters("checks.evaluation_parameters", "device");
        if (request.Batch is < 1 or > 1024) throw Parameters("checks.evaluation_parameters", "batch");
        if (!Enum.IsDefined(request.Split)) throw Parameters("checks.evaluation_parameters", "split");
        var version = await training.GetVersionAsync(request.VersionId, cancellationToken).ConfigureAwait(false);
        if (!version.Files.Any(file => file.Kind == ModelFileKind.WeightsPt)) throw NoWeights(version.Id);
        var dataset = await datasets.GetAsync(request.DatasetVersionId, cancellationToken).ConfigureAwait(false);
        if (dataset.ArchivedAt is not null) throw new CoreException("datasets.archived", $"Dataset {dataset.Id} is archived.");
        if (dataset.IntegrityState == DatasetIntegrityState.Corrupt) throw new CoreException("datasets.corrupt", $"Dataset {dataset.Id} failed its integrity check.");
        if (!SameClasses(version.Classes, dataset.ClassMap))
            throw new CoreException("checks.evaluation_classes_mismatch", $"Classes of dataset {dataset.Id} differ from model version {version.Id}.");
        if (dataset.Width != version.Input.Width || dataset.Height != version.Input.Height)
            throw new CoreException("checks.evaluation_input_mismatch", $"Dataset {dataset.Id} is {dataset.Width}x{dataset.Height}, the version input {version.Input.Width}x{version.Input.Height}.")
            {
                Details = new Dictionary<string, string>
                {
                    ["datasetWidth"] = Text(dataset.Width),
                    ["datasetHeight"] = Text(dataset.Height),
                    ["versionWidth"] = Text(version.Input.Width),
                    ["versionHeight"] = Text(version.Input.Height),
                },
            };
        var images = request.Split == EvaluationSplit.Test ? dataset.Counts.Test : dataset.Counts.Validation;
        if (images == 0)
            throw new CoreException("checks.evaluation_split_empty", $"The {request.Split} part of dataset {dataset.Id} has no images.")
            {
                Details = new Dictionary<string, string> { ["split"] = request.Split == EvaluationSplit.Test ? "Test" : "Validation" },
            };
        return new ModelEvaluationInput(version.Id, version.Number, version.Architecture, version.Input.Width, version.Input.Height, version.ImageSize, dataset.Id,
            new TrainingDatasetReference(dataset.Id, dataset.Name, dataset.ManifestHash, dataset.ClassMap, dataset.Counts), request.Split, request.Device, request.Batch);
    }

    public async Task<ModelEvaluation> GetEvaluationAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var job = await JobsTable.GetStoredAsync(connection, null, projectId, evaluationId, token).ConfigureAwait(false);
            if (job is null || job.Info.Kind != EvaluationKind) return null;
            var rows = await ReadRowsAsync(connection, null, [evaluationId], token).ConfigureAwait(false);
            return ToEvaluation(job, rows.GetValueOrDefault(evaluationId));
        }, cancellationToken).ConfigureAwait(false) ?? throw EvaluationNotFound(evaluationId);
    }

    public async Task<ModelEvaluationPage> ListEvaluationsAsync(ModelEvaluationFilter filter, int limit = 50, Guid? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        var conditions = new List<string>();
        if (filter.VersionId is not null) conditions.Add("json_extract(input, '$.versionId') = $version");
        if (filter.DatasetVersionId is not null) conditions.Add("json_extract(input, '$.datasetVersionId') = $dataset");
        if (filter.Split is not null) conditions.Add("json_extract(input, '$.split') = $split");
        var condition = conditions.Count == 0 ? null : string.Join(" AND ", conditions);
        void Bind(SqliteCommand command)
        {
            if (filter.VersionId is { } version) command.Parameters.AddWithValue("$version", Key(version));
            if (filter.DatasetVersionId is { } dataset) command.Parameters.AddWithValue("$dataset", Key(dataset));
            if (filter.Split is { } split) command.Parameters.AddWithValue("$split", SplitKey(split));
        }
        return await store.ReadAsync(async (connection, token) =>
        {
            var jobs = await JobsTable.ListStoredByKindAsync(connection, projectId, EvaluationKind, limit, before, token, condition, Bind).ConfigureAwait(false);
            var rows = await ReadRowsAsync(connection, null, jobs.Select(job => job.Info.Id).ToArray(), token).ConfigureAwait(false);
            var total = await JobsTable.CountByKindAsync(connection, EvaluationKind, token, condition, Bind).ConfigureAwait(false);
            return new ModelEvaluationPage(jobs.Select(job => ToEvaluation(job, rows.GetValueOrDefault(job.Info.Id))).OfType<ModelEvaluation>().ToArray(), total);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelEvaluation>> ListLatestEvaluationsAsync(Guid? datasetVersionId = null, EvaluationSplit? split = null,
        CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"""
                SELECT e.id FROM model_evaluations e
                WHERE e.succeeded = 1 {(datasetVersionId is null ? "" : "AND e.dataset_version_id = $dataset")} {(split is null ? "" : "AND e.split = $split")}
                  AND NOT EXISTS (SELECT 1 FROM model_evaluations o WHERE o.succeeded = 1 AND o.dataset_version_id = e.dataset_version_id
                      AND o.split = e.split AND o.version_id = e.version_id AND (o.finished_at > e.finished_at OR (o.finished_at = e.finished_at AND o.id > e.id)))
                ORDER BY e.finished_at DESC;
                """);
            if (datasetVersionId is { } dataset) command.Parameters.AddWithValue("$dataset", Key(dataset));
            if (split is { } part) command.Parameters.AddWithValue("$split", SplitKey(part));
            var ids = await ListAsync(command, reader => ParseId(reader.GetString(0)), token).ConfigureAwait(false);
            var rows = await ReadRowsAsync(connection, null, ids, token).ConfigureAwait(false);
            var result = new List<ModelEvaluation>();
            foreach (var id in ids)
            {
                var job = await JobsTable.GetStoredAsync(connection, null, projectId, id, token).ConfigureAwait(false);
                if (job is not null && ToEvaluation(job, rows.GetValueOrDefault(id)) is { } evaluation) result.Add(evaluation);
            }
            return (IReadOnlyList<ModelEvaluation>)result;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ---- Evaluation run support (not gated on isOpen: a queued evaluation starts while the project is being opened) ----

    internal string EvaluationDirectory(Guid evaluationId) => Path.Combine(store.ProjectDirectory, "checks", "jobs", evaluationId.ToString("D"));

    /// <summary>The row of an evaluation when its job starts: phase preparing.</summary>
    internal async Task RegisterEvaluationAsync(Guid evaluationId, ModelEvaluationInput input)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                INSERT INTO model_evaluations (id, version_id, dataset_version_id, split, phase, revision, created_at, updated_at)
                VALUES ($id, $version, $dataset, $split, 'preparing', 1, $now, $now)
                ON CONFLICT (id) DO UPDATE SET phase = 'preparing', revision = revision + 1, updated_at = $now
                RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(evaluationId));
            command.Parameters.AddWithValue("$version", Key(input.VersionId));
            command.Parameters.AddWithValue("$dataset", Key(input.DatasetVersionId));
            command.Parameters.AddWithValue("$split", SplitKey(input.Split));
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        RaiseEvaluation(evaluationId, revision);
    }

    /// <summary>workerText — what the UI shows for a failure the worker reported itself, kept in error_message (<see cref="CodedTextJson"/>).</summary>
    internal async Task SetEvaluationPhaseAsync(Guid evaluationId, string phase, string? errorCode = null, string? errorMessage = null,
        CodedText? workerText = null)
    {
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE model_evaluations SET phase = $phase, error_code = $code, error_message = $message, revision = revision + 1, updated_at = $now,
                    finished_at = CASE WHEN $phase = 'finished' THEN $now ELSE finished_at END
                WHERE id = $id RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(evaluationId));
            command.Parameters.AddWithValue("$phase", phase);
            command.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", (object?)(workerText is not null ? CodedTextJson.Serialize(workerText with { Text = errorMessage }) : errorMessage)
                ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (revision > 0) RaiseEvaluation(evaluationId, revision);
    }

    /// <summary>A verified result: metrics, per class (classes of the version by output index), the image count; the row is finished.</summary>
    internal async Task AcceptEvaluationAsync(Guid evaluationId, ModelEvaluationInput input, ModelVersion version, WorkerEvaluateResult result)
    {
        var classes = result.ClassMetrics.Select(item =>
        {
            var known = item.Index >= 0 && item.Index < version.Classes.Count ? version.Classes[item.Index] : null;
            return new EvaluationClassMetrics(item.Index, known?.ClassId, known?.Name ?? item.Name, item.Metrics);
        }).ToArray();
        var images = result.ImageCount ?? (input.Split == EvaluationSplit.Test ? input.Dataset.Counts.Test : input.Dataset.Counts.Validation);
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE model_evaluations SET phase = 'finished', succeeded = 1, metrics = $metrics, class_metrics = $classes, image_count = $images,
                    environment = $environment, error_code = NULL, error_message = NULL, revision = revision + 1, updated_at = $now, finished_at = $now
                WHERE id = $id RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(evaluationId));
            command.Parameters.AddWithValue("$metrics", JsonSerializer.Serialize(result.Metrics, Json));
            command.Parameters.AddWithValue("$classes", JsonSerializer.Serialize(classes, Json));
            command.Parameters.AddWithValue("$images", images);
            command.Parameters.AddWithValue("$environment", result.Environment.GetRawText());
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await ScalarAsync(command, token).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        RaiseEvaluation(evaluationId, revision);
        logger.LogInformation("Project {ProjectId}: evaluation {EvaluationId} of version {VersionId} on dataset {DatasetId} ({Split}) succeeded: {Images} images.",
            projectId, evaluationId, input.VersionId, input.DatasetVersionId, SplitKey(input.Split), images);
    }

    /// <summary>
    /// After the application ended: evaluations whose job is not active any more are finished (the job is already interrupted — "Interrupted").
    /// Live check folders never survive; finished evaluation folders are cleaned in the background.
    /// </summary>
    internal async Task RecoverAsync(IReadOnlySet<Guid> activeJobs, CancellationToken cancellationToken)
    {
        var unfinished = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id FROM model_evaluations WHERE phase <> 'finished';");
            return await ListAsync(command, reader => ParseId(reader.GetString(0)), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var id in unfinished.Where(id => !activeJobs.Contains(id)))
        {
            await SetEvaluationPhaseAsync(id, "finished").ConfigureAwait(false);
            logger.LogInformation("Project {ProjectId}: evaluation {EvaluationId} was interrupted by the end of the application.", projectId, id);
        }
        _ = Task.Run(() => CleanFolders(activeJobs));
    }

    /// <summary>Rows deleted with a model version: its evaluations and their finished jobs (an active one blocks the deletion).</summary>
    internal static ModelVersionDependents VersionDependents { get; } = new(
        async (connection, transaction, versionId, token) =>
        {
            await using var command = Command(connection, transaction, "SELECT count(*) FROM jobs WHERE kind = 'checks.evaluate' AND json_extract(input, '$.versionId') = $version;");
            command.Parameters.AddWithValue("$version", Key(versionId));
            return (int)await ScalarAsync(command, token).ConfigureAwait(false);
        },
        async (connection, transaction, versionId, token) =>
        {
            await using var count = Command(connection, transaction, "SELECT count(*) FROM jobs WHERE kind = 'checks.evaluate' AND json_extract(input, '$.versionId') = $version;");
            count.Parameters.AddWithValue("$version", Key(versionId));
            var total = (int)await ScalarAsync(count, token).ConfigureAwait(false);
            await JobsTable.DeleteFinishedByTargetAsync(connection, transaction, EvaluationKind, versionId, token).ConfigureAwait(false);
            await using var rows = Command(connection, transaction, "DELETE FROM model_evaluations WHERE version_id = $version;");
            rows.Parameters.AddWithValue("$version", Key(versionId));
            await rows.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return total;
        });

    private void CleanFolders(IReadOnlySet<Guid> activeJobs)
    {
        try
        {
            var live = Path.Combine(store.ProjectDirectory, "checks", "live");
            if (Directory.Exists(live))
            {
                foreach (var directory in Directory.EnumerateDirectories(live))
                {
                    if (Guid.TryParseExact(Path.GetFileName(directory), "D", out var id) && FindSession(id) is { IsFinished: false }) continue;
                    CheckFiles.Clean(directory, keepLogs: false, error => logger.LogWarning(error, "Project {ProjectId}: live check folder was not removed.", projectId));
                }
            }
            var jobs = Path.Combine(store.ProjectDirectory, "checks", "jobs");
            if (!Directory.Exists(jobs)) return;
            foreach (var directory in Directory.EnumerateDirectories(jobs))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var id) || activeJobs.Contains(id)) continue;
                // Logs of failed evaluations stay for diagnosis; inputs and outputs go.
                CheckFiles.Clean(directory, keepLogs: true, error => logger.LogWarning(error, "Project {ProjectId}: evaluation folder was not cleaned.", projectId));
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Project {ProjectId}: check folders were not cleaned.", projectId);
        }
    }

    // ---- Live checks ----

    public Task<LiveCheck> StartImageCheckAsync(ImageCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return StartAsync(LiveCheckMode.Image, request.VersionId, request.Parameters, request.Source, null, cancellationToken);
    }

    public Task<LiveCheck> StartVideoCheckAsync(VideoCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.StepMs < 1) throw Parameters("checks.prediction_parameters", "stepMs");
        return StartAsync(LiveCheckMode.Video, request.VersionId, request.Parameters, request.Source, new LiveTarget(request.StepMs, request.Region, null, 0, 0),
            cancellationToken);
    }

    public Task<LiveCheck> StartScreenCheckAsync(ScreenCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.Fps) || request.Fps <= 0 || request.Fps > 60) throw Parameters("checks.prediction_parameters", "fps");
        if (request.MaxDurationSec is < 1 or > 24 * 3600) throw Parameters("checks.prediction_parameters", "maxDurationSec");
        return StartAsync(LiveCheckMode.Screen, request.VersionId, request.Parameters, null, new LiveTarget(null, request.Region, request.Monitor, request.Fps,
            request.MaxDurationSec), cancellationToken);
    }

    public Task<LiveCheck> StopCheckAsync(Guid checkId, LiveCheckStopReason reason = LiveCheckStopReason.User, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var session = FindSession(checkId) ?? throw CheckNotFound(checkId);
        session.Stop(reason);
        return Task.FromResult(session.Snapshot(0));
    }

    public Task<LiveCheck> GetCheckAsync(Guid checkId, int journalLimit = 40, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(journalLimit, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(journalLimit, MaxJournalPage);
        RequireOpen();
        return Task.FromResult((FindSession(checkId) ?? throw CheckNotFound(checkId)).Snapshot(journalLimit));
    }

    public Task<IReadOnlyList<LiveCheck>> ListChecksAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        lock (sessionsGate) return Task.FromResult<IReadOnlyList<LiveCheck>>(sessions.Select(session => session.Snapshot(0)).ToArray());
    }

    public Task<LiveCheckJournalPage> ListJournalAsync(Guid checkId, long? after = null, long? before = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxJournalPage);
        if (after is not null && before is not null) throw new ArgumentException("Use after or before, not both.");
        RequireOpen();
        return Task.FromResult((FindSession(checkId) ?? throw CheckNotFound(checkId)).Journal(after, before, limit));
    }

    public LiveCheckPreview? GetPreview(Guid checkId) => FindSession(checkId)?.Preview;

    /// <summary>The check sessions of this project that still run.</summary>
    internal IReadOnlyList<LiveCheckSession> Running()
    {
        lock (sessionsGate) return sessions.Where(session => !session.IsFinished).ToArray();
    }

    private LiveCheckSession? FindSession(Guid checkId)
    {
        lock (sessionsGate) return sessions.FirstOrDefault(session => session.Id == checkId);
    }

    /// <summary>Video: step and region; screen: region, monitor, frequency and the time limit.</summary>
    private sealed record LiveTarget(int? StepMs, CheckRegion? Region, int? Monitor, double Fps, int MaxDurationSec);

    private async Task<LiveCheck> StartAsync(LiveCheckMode mode, Guid versionId, LiveCheckParameters parameters, CheckSourceFile? source, LiveTarget? target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        RequireOpen();
        RequireRuntime();
        if (!double.IsFinite(parameters.Confidence) || parameters.Confidence is < 0 or > 1) throw Parameters("checks.prediction_parameters", "confidence");
        if (parameters.MaxDetections is < 1 or > WorkerChecks.MaxDetections) throw Parameters("checks.prediction_parameters", "maxDetections");
        if (!IsDevice(parameters.Device)) throw Parameters("checks.prediction_parameters", "device");
        var version = await training.GetVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        if (!version.Files.Any(file => file.Kind == ModelFileKind.WeightsPt)) throw NoWeights(version.Id);
        LiveCheckSource described;
        MonitorInfo? monitor = null;
        if (mode == LiveCheckMode.Screen)
        {
            ArgumentNullException.ThrowIfNull(target);
            var monitors = services.Screen.ListMonitors();
            monitor = monitors.FirstOrDefault(item => item.Index == target.Monitor)
                ?? throw new CoreException("screen.monitor_not_found", $"Monitor {target.Monitor} does not exist; there are {monitors.Count}.");
            RequireRegion(target.Region, version, monitor.Width, monitor.Height);
            described = new LiveCheckSource(null, target.Region?.Width ?? monitor.Width, target.Region?.Height ?? monitor.Height, null, null, monitor.Index);
        }
        else
        {
            if (source is null || !Path.IsPathFullyQualified(source.Path)) throw new CoreException("checks.prediction_source", "No file was chosen.");
            var extensions = mode == LiveCheckMode.Image ? CheckFiles.ImageExtensions : CheckFiles.VideoExtensions;
            if (!extensions.Contains(Path.GetExtension(source.Path).ToLowerInvariant()))
                throw new CoreException("checks.prediction_source", $"{Path.GetFileName(source.Path)} is not a {(mode == LiveCheckMode.Image ? "picture" : "video")}.");
            CheckFiles.RequireUnchanged(source);
            if (mode == LiveCheckMode.Video) RequireRegion(target!.Region, version, source.Width, source.Height);
            described = new LiveCheckSource(Path.GetFileName(source.Path), source.Width, source.Height, source.DurationMs, source.Fps, null);
        }
        var settings = new LiveCheckSettings(parameters.Confidence, parameters.MaxDetections, parameters.Device, version.ImageSize, target?.Region, target?.StepMs,
            monitor?.Index, mode == LiveCheckMode.Screen ? target!.Fps : null, mode == LiveCheckMode.Screen ? target!.MaxDurationSec : null);

        services.Hub.Admit(projectId);
        if (!services.Gpu.TryAcquire(GpuSlot.LiveCheckKind)) throw GpuBusy(services.Gpu.Holder);
        LiveCheckSession session;
        try
        {
            session = new LiveCheckSession(Guid.NewGuid(), projectId, mode, version, settings, described, (snapshot, preview) => Raise([], snapshot, preview));
            lock (sessionsGate)
            {
                sessions.Insert(0, session);
                // Finished checks beyond the last few are forgotten; a running one is always kept.
                for (var index = sessions.Count - 1; index >= 0 && sessions.Count > KeptChecks; index--)
                {
                    if (sessions[index].IsFinished) sessions.RemoveAt(index);
                }
            }
            services.Hub.Add(session);
        }
        catch
        {
            services.Gpu.Release();
            throw;
        }
        logger.LogInformation("Project {ProjectId}: live check {CheckId} ({Mode}) of model version {VersionId} started on device {Device}.", projectId, session.Id,
            mode, version.Id, parameters.Device);
        _ = Task.Run(() => RunLiveAsync(session, source, monitor));
        var started = session.Snapshot(0);
        Raise([], started, null);
        return started;
    }

    /// <summary>One worker for the whole check: folder, process, events → session; the GPU slot is released at the end, whatever happens.</summary>
    private async Task RunLiveAsync(LiveCheckSession session, CheckSourceFile? source, MonitorInfo? monitor)
    {
        var directory = Path.Combine(store.ProjectDirectory, "checks", "live", session.Id.ToString("D"));
        var token = session.StopToken;
        var failed = false;
        try
        {
            var operation = session.Mode == LiveCheckMode.Screen ? "live" : "predict";
            var operationId = session.Id.ToString("D");
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(Path.Combine(directory, "output", "previews"));
            Directory.CreateDirectory(Path.Combine(directory, "logs"));
            var weights = session.Version.Files.First(file => file.Kind == ModelFileKind.WeightsPt);
            var model = await CheckFiles.LinkWeightsAsync(await training.VersionDirectoryAsync(session.Version.Id, token).ConfigureAwait(false), weights, directory, token)
                .ConfigureAwait(false);
            var input = new JsonObject { ["model"] = model, ["manifest"] = "input/manifest.json" };
            if (source is not null)
            {
                var (relative, external) = CheckFiles.LinkSource(source, directory);
                if (relative is not null) input["source"] = relative;
                else input["externalSource"] = external;
                if (external is not null) logger.LogInformation("Live check {CheckId}: the source is on another volume and is read in place.", session.Id);
            }
            await CheckFiles.WriteJsonAsync(Path.Combine(directory, "input", "manifest.json"),
                CheckFiles.Manifest(operationId, session.Version, null, source is null ? null : Path.GetFileName(source.Path)), token).ConfigureAwait(false);
            var settings = session.Settings;
            var parameters = new JsonObject
            {
                ["architecture"] = session.Version.Architecture,
                ["imgsz"] = settings.ImageSize,
                ["device"] = settings.Device,
                ["conf"] = settings.Confidence,
                ["maxDetections"] = settings.MaxDetections,
            };
            if (settings.Region is { } region)
                parameters["region"] = new JsonObject { ["x"] = region.X, ["y"] = region.Y, ["width"] = region.Width, ["height"] = region.Height };
            switch (session.Mode)
            {
                case LiveCheckMode.Image:
                    parameters["source"] = "image";
                    parameters["previewIntervalMs"] = 0;
                    break;
                case LiveCheckMode.Video:
                    parameters["source"] = "video";
                    parameters["stepMs"] = settings.StepMs;
                    parameters["previewIntervalMs"] = 500;
                    break;
                default:
                    parameters["monitor"] = monitor!.Index;
                    parameters["fps"] = settings.Fps;
                    parameters["maxDurationSec"] = settings.MaxDurationSec;
                    parameters["previewIntervalMs"] = 1000;
                    parameters["expectedMonitor"] = new JsonObject
                    {
                        ["left"] = monitor.Left,
                        ["top"] = monitor.Top,
                        ["width"] = monitor.Width,
                        ["height"] = monitor.Height,
                    };
                    break;
            }
            var request = new JsonObject
            {
                ["protocolVersion"] = PythonWorkers.ProtocolVersion,
                ["operationId"] = operationId,
                ["operation"] = operation,
                ["input"] = input,
                ["outputRoot"] = "output",
                ["params"] = parameters,
            };
            await CheckFiles.WriteJsonAsync(Path.Combine(directory, "request.json"), request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            session.WorkerStarting();
            var output = Path.Combine(directory, "output");
            var outcome = await python.RunAsync(directory, operationId, operation, worker => OnLiveEventAsync(session, worker, output), WorkerChecks.ParsePredictAsync,
                token).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case WorkerOutcomeKind.Cancelled:
                    session.Finish(LiveCheckState.Cancelled, null);
                    break;
                case WorkerOutcomeKind.Failed:
                    failed = true;
                    session.Finish(LiveCheckState.Failed, new LiveCheckError("checks." + outcome.Code, outcome.Message ?? "", outcome.WorkerText));
                    logger.LogWarning("Project {ProjectId}: live check {CheckId} failed with checks.{Code}.", projectId, session.Id, outcome.Code);
                    break;
                default:
                    session.Succeeded(outcome.Result!);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            session.Finish(LiveCheckState.Cancelled, null);
        }
        catch (CoreException error)
        {
            failed = true;
            logger.LogWarning("Project {ProjectId}: live check {CheckId} failed with {Code}: {Message}", projectId, session.Id, error.Code, error.Message);
            session.Finish(LiveCheckState.Failed, new LiveCheckError(error.Code, error.Message));
        }
        catch (Exception error)
        {
            failed = true;
            logger.LogError(error, "Project {ProjectId}: live check {CheckId} failed.", projectId, session.Id);
            session.Finish(LiveCheckState.Failed, new LiveCheckError("checks.execution_failed", error.Message));
        }
        finally
        {
            services.Hub.Remove(session);
            services.Gpu.Release();
            CheckFiles.Clean(directory, keepLogs: failed, error => logger.LogWarning(error, "Live check folder {CheckId} was not removed.", session.Id));
            var final = session.Snapshot(0);
            logger.LogInformation("Project {ProjectId}: live check {CheckId} is {State}: {Frames} frames, first frame after {FirstFrame} ms, {Elapsed} ms in all.",
                projectId, session.Id, final.State, final.Progress.FramesProcessed, final.Progress.FirstFrameMs, final.Progress.ElapsedMs);
            session.Completed();
        }
    }

    /// <summary>Worker events of a live check: stages, frames (the preview file is read and removed), progress and warnings.</summary>
    private static Task OnLiveEventAsync(LiveCheckSession session, WorkerEvent worker, string output)
    {
        switch (worker)
        {
            case WorkerStageEvent { Stage: "predicting" or "capturing" }:
                session.Running();
                break;
            case WorkerOtherEvent { Type: "frame" } frame:
            {
                var parsed = WorkerChecks.ParseFrame(frame.Data);
                session.Frame(parsed, parsed.PreviewFile is { } file ? TakePreview(Path.Combine(output, file)) : null);
                break;
            }
            case WorkerOtherEvent { Type: "progress" } progress:
                session.Progress(WorkerChecks.ParseProgress(progress.Data));
                break;
            case WorkerWarningEvent warning:
                session.Warning(warning.Code, warning.Message, warning.Text);
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>A failure of an evaluation as stored: a worker failure keeps what the UI shows in error_message (<see cref="CodedTextJson"/>).</summary>
    private static ModelEvaluationError StoredError(string code, string? message)
    {
        var stored = CodedTextJson.Parse(message);
        return stored?.Code is not null ? new ModelEvaluationError(code, stored.Text ?? "", stored) : new ModelEvaluationError(code, message ?? "");
    }

    /// <summary>Reads a preview the worker published and removes it; a missing or oversized file only means no preview this time.</summary>
    private static byte[]? TakePreview(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 16 * 1024 * 1024) return null;
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- Reading ----

    private static async Task<Dictionary<Guid, EvaluationRow>> ReadRowsAsync(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<Guid> ids,
        CancellationToken token)
    {
        var result = new Dictionary<Guid, EvaluationRow>();
        if (ids.Count == 0) return result;
        await using var command = Command(connection, transaction, $"SELECT {Columns} FROM model_evaluations WHERE id IN (SELECT value FROM json_each($ids));");
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(Key)));
        foreach (var row in await ListAsync(command, ReadRow, token).ConfigureAwait(false)) result[row.Id] = row;
        return result;
    }

    private static EvaluationRow ReadRow(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt64(2) == 1,
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt64(9));

    private static ModelEvaluation? ToEvaluation(StoredJob job, EvaluationRow? row)
    {
        if (ParseInput(job.Input) is not { } input) return null;
        var info = job.Info;
        var state = info.Status switch
        {
            JobStatus.Queued => info.CancelRequested ? ModelEvaluationState.Cancelling : ModelEvaluationState.Queued,
            JobStatus.Running => info.CancelRequested ? ModelEvaluationState.Cancelling
                : row is null || row.Phase == "preparing" ? ModelEvaluationState.Preparing : ModelEvaluationState.Running,
            JobStatus.Succeeded => ModelEvaluationState.Succeeded,
            JobStatus.Cancelled => ModelEvaluationState.Cancelled,
            JobStatus.Interrupted => ModelEvaluationState.Interrupted,
            _ => ModelEvaluationState.Failed,
        };
        ModelEvaluationError? error = state is ModelEvaluationState.Failed or ModelEvaluationState.Interrupted
            ? row?.ErrorCode is { } code ? StoredError(code, row.ErrorMessage)
                : info.Error is { } jobError ? new ModelEvaluationError(jobError.Code, jobError.Message) : null
            : null;
        return new ModelEvaluation(
            info,
            state,
            input.VersionId,
            input.VersionNumber,
            input.Dataset,
            input.Split,
            input.Device,
            input.Batch,
            row?.ImageCount,
            row?.Metrics is { } metrics ? JsonSerializer.Deserialize<Dictionary<string, double>>(metrics, Json)! : new Dictionary<string, double>(),
            row?.ClassMetrics is { } classes ? JsonSerializer.Deserialize<EvaluationClassMetrics[]>(classes, Json)! : [],
            row?.Environment is { } environment ? Element(environment) : null,
            error,
            row?.Revision ?? 0);
    }

    internal static ModelEvaluationInput? ParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelEvaluationInput>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The dataset classes are the version outputs in the same order; a single-class version accepts one of its source classes.</summary>
    private static bool SameClasses(IReadOnlyList<ModelClass> version, IReadOnlyList<DatasetClassEntry> dataset)
    {
        if (version.Count != dataset.Count) return false;
        if (version is [{ ClassId: null } merged]) return dataset is [{ } only] && merged.SourceClassIds.Contains(only.ClassId);
        return version.OrderBy(item => item.Index).Zip(dataset.OrderBy(item => item.Index))
            .All(pair => pair.First.Index == pair.Second.Index && pair.First.ClassId == pair.Second.ClassId);
    }

    /// <summary>A region equals the version input and lies inside the frame (when its size is known).</summary>
    private static void RequireRegion(CheckRegion? region, ModelVersion version, int? frameWidth, int? frameHeight)
    {
        if (region is null) return;
        if (region.X < 0 || region.Y < 0 || region.Width != version.Input.Width || region.Height != version.Input.Height
            || (frameWidth is { } width && region.X + region.Width > width) || (frameHeight is { } height && region.Y + region.Height > height))
            throw new CoreException("checks.region_invalid", $"Region {region.Width}x{region.Height} at ({region.X}, {region.Y}) is not the version input inside the frame.");
    }

    private void RequireRuntime()
    {
        if (python.CheckRuntime() is { } missing) throw new CoreException("checks." + missing.Code, missing.Message);
    }

    private static bool IsDevice(string? device) => device is "cpu" || (device is { Length: >= 1 and <= 3 } && device.All(char.IsAsciiDigit));

    private static CoreException Parameters(string code, string field) => new(code, $"Parameter {field} is invalid.")
    {
        Details = new Dictionary<string, string> { ["field"] = field },
    };

    private static CoreException NoWeights(Guid versionId) => new("training.artifact_corrupt", $"Model version {versionId} has no best.pt.")
    {
        Details = new Dictionary<string, string> { ["name"] = "best.pt" },
    };

    /// <summary>checks.gpu_busy_training | checks.gpu_busy_evaluation | checks.gpu_busy_prediction by the holder of the slot.</summary>
    internal static CoreException GpuBusy(string? holder) => new(holder switch
    {
        EvaluationKind => "checks.gpu_busy_evaluation",
        GpuSlot.LiveCheckKind => "checks.gpu_busy_prediction",
        _ => "checks.gpu_busy_training",
    }, $"The GPU is busy with {holder}.");

    private static string SplitKey(EvaluationSplit split) => split == EvaluationSplit.Test ? "test" : "val";

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private void RaiseEvaluation(Guid evaluationId, long revision) => Raise([new EvaluationChange(evaluationId, revision)], null, null);

    private void Raise(IReadOnlyList<EvaluationChange> evaluations, LiveCheck? check, LiveCheckPreview? preview)
        => changed(new ChecksChangedEventArgs(projectId, evaluations, check, preview));

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("checks.project_not_open", $"Project {projectId} is not open.");
    }

    private static CoreException EvaluationNotFound(Guid evaluationId) => new("checks.evaluation_not_found", $"Evaluation {evaluationId} does not exist.");

    private static CoreException CheckNotFound(Guid checkId) => new("checks.check_not_found", $"Live check {checkId} is not known to this run of the application.");
}
