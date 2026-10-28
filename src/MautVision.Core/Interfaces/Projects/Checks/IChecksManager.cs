namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>
/// Checks of the project model versions: evaluation on a dataset split (the job <see cref="IModelEvaluationJob"/>, stored in project.db) and
/// live checks on an image, a video and the screen (in memory, one per application: it occupies the GPU). Works only for an open
/// project (<c>checks.project_not_open</c>). Changes — <see cref="IProjectsManager.ChecksChanged"/>, the state of evaluation jobs —
/// JobChanged. Versions and datasets are read one way; a missing version — <c>training.version_not_found</c>.
/// </summary>
public interface IChecksManager
{
    /// <summary>
    /// Validates an evaluation and prepares the job input (the job is enqueued by ICore.Jobs.EnqueueAsync with the same operationId). Checks: worker runtime
    /// (<c>checks.runtime_not_configured</c>, <c>checks.runtime_missing</c>), parameters (<c>checks.evaluation_parameters</c> with
    /// Details["field"]), the dataset is ready (datasets.unavailable, archived, corrupt), the classes and their order match the version
    /// (<c>checks.evaluation_classes_mismatch</c>), the frame size equals the version input (<c>checks.evaluation_input_mismatch</c>, Details:
    /// datasetWidth, datasetHeight, versionWidth, versionHeight), the split has frames (<c>checks.evaluation_split_empty</c>, Details:
    /// split — Validation | Test). Repeating the same operation returns its input.
    /// </summary>
    Task<ModelEvaluationInput> PrepareEvaluationAsync(ModelEvaluationRequest request, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Evaluation by ID; a missing one — <c>checks.evaluation_not_found</c>.</summary>
    Task<ModelEvaluation> GetEvaluationAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    /// <summary>"All checks", newest first (limit 1–200, before — ID of the last evaluation of the previous page).</summary>
    Task<ModelEvaluationPage> ListEvaluationsAsync(ModelEvaluationFilter filter, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Comparison matrix: the latest successful evaluation of each version on each "dataset · split" pair (null — any dataset and split).
    /// </summary>
    Task<IReadOnlyList<ModelEvaluation>> ListLatestEvaluationsAsync(Guid? datasetVersionId = null, EvaluationSplit? split = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a check on an image and immediately returns its snapshot (Preparing). Common refusals of live checks: worker runtime
    /// (<c>checks.runtime_*</c>), parameters (<c>checks.prediction_parameters</c> with Details["field"]), the file is not chosen, of the wrong kind
    /// or changed after choosing (<c>checks.prediction_source</c>, <c>checks.source_changed</c>), the GPU is busy —
    /// <c>checks.gpu_busy_training</c>, <c>checks.gpu_busy_evaluation</c>, <c>checks.gpu_busy_prediction</c>.
    /// </summary>
    Task<LiveCheck> StartImageCheckAsync(ImageCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Check on a video; a crop outside the frame or not of the input size — <c>checks.region_invalid</c>.</summary>
    Task<LiveCheck> StartVideoCheckAsync(VideoCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check from the screen until stopped or a time limit; no monitor — <c>screen.monitor_not_found</c>, a crop outside the monitor or not
    /// of the input size — <c>checks.region_invalid</c>.
    /// </summary>
    Task<LiveCheck> StartScreenCheckAsync(ScreenCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Stop": idempotent; the snapshot goes to Cancelling, the result is Cancelled after the worker stops. Reason — who stopped it: the button (User)
    /// or the host when the UI page was reloaded or the window closed (SessionEnded).
    /// </summary>
    Task<LiveCheck> StopCheckAsync(Guid checkId, LiveCheckStopReason reason = LiveCheckStopReason.User, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check snapshot with the last page of the journal (journalLimit 0–200 entries); live checks are not kept between application
    /// runs — an unknown one is <c>checks.check_not_found</c>.
    /// </summary>
    Task<LiveCheck> GetCheckAsync(Guid checkId, int journalLimit = 40, CancellationToken cancellationToken = default);

    /// <summary>Live checks of the project in memory, newest first: the running one and the latest finished ones.</summary>
    Task<IReadOnlyList<LiveCheck>> ListChecksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Journal of frames with detections in ascending order: after — entries after Ordinal (read new ones), before — entries before it (page
    /// back); without cursors — the last limit entries (1–200).
    /// </summary>
    Task<LiveCheckJournalPage> ListJournalAsync(Guid checkId, long? after = null, long? before = null, int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>The latest check preview (JPEG); null while there is none.</summary>
    LiveCheckPreview? GetPreview(Guid checkId);
}
