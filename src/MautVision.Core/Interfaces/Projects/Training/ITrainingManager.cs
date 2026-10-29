namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// Training of a project: runs (jobs <see cref="ITrainingRunJob"/>), their progress, epoch history and log, model versions. Works only
/// for an open project (<c>training.project_not_open</c>). A missing run and version are <c>training.run_not_found</c>,
/// <c>training.version_not_found</c>. Changes come with <see cref="IProjectsManager.TrainingChanged"/>, the state of the job with
/// JobChanged.
/// </summary>
public interface ITrainingManager
{
    /// <summary>Runs, newest first (limit 1–200, before — the ID of the last run of the previous page).</summary>
    Task<TrainingRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<TrainingRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a run and prepares the job input (ICore.Jobs.EnqueueAsync enqueues it with the same operationId). Checks: the worker environment
    /// (<c>training.runtime_not_configured</c>, <c>training.runtime_missing</c>), parameters (<c>training.parameter_invalid</c> with
    /// Details["field"]), the input size is a multiple of the model stride (<c>training.input_size_invalid</c>), the base model is installed
    /// (<c>training.base_model_not_installed</c>), the dataset is ready for training (codes datasets.*: unavailable, archived, corrupt,
    /// input_size_mismatch, classes_invalid), indexes of classes exist in the dataset (<c>training.parameter_invalid</c>, field classes).
    /// A repeat of the same operation returns its input.
    /// </summary>
    Task<TrainingRunInput> PrepareRunAsync(TrainingRunRequest request, string operationId, CancellationToken cancellationToken = default);

    /// <summary>The epoch history of a run in ascending order (limit 1–1000, after — the number of the last received epoch).</summary>
    Task<TrainingEpochPage> ListEpochsAsync(Guid runId, int? after = null, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>The log of a run in ascending order (limit 1–1000, after — Sequence of the last received record).</summary>
    Task<TrainingLogPage> ListLogAsync(Guid runId, long? after = null, int limit = 200, CancellationToken cancellationToken = default);

    /// <summary>The latest log records ("latest messages"), in ascending order; NextAfter — to continue from the end.</summary>
    Task<TrainingLogPage> TailLogAsync(Guid runId, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Model versions, newest first.</summary>
    Task<IReadOnlyList<ModelVersion>> ListVersionsAsync(CancellationToken cancellationToken = default);

    Task<ModelVersion> GetVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>"Make working": exactly one working version; a repeat changes nothing.</summary>
    Task<ModelVersion> SetWorkingVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What deleting a version removes, for the shared dialog: evaluations on datasets that go with it, the size of files and the reasons
    /// why it cannot be deleted now (<see cref="ModelVersionDeletionReview.Blockers"/>).
    /// </summary>
    Task<ModelVersionDeletionReview> ReviewVersionDeletionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a version: the row, the version files in the list and its evaluations on datasets — in one transaction, the files folder — in the background
    /// (FileCleanup). The working version and a version being checked or exported cannot be deleted:
    /// <c>training.version_in_use</c> with Details["reason"] — working | evaluation | check | export. The TrainingChanged event
    /// reports the version with Revision = null.
    /// </summary>
    Task<ModelVersionDeletionResult> DeleteVersionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The input of the job <see cref="IModelExportJob"/>: the version exists, the ZIP path is absolute, chosen in "Save as". A repeat of the
    /// same operation returns its input.
    /// </summary>
    Task<ModelExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default);
}
