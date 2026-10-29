namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>
/// Project datasets: immutable versions of ready frames and annotation in YOLO format, build plans and operations (build, integrity
/// check, ZIP export — Jobs jobs). Works only for an open project (<c>datasets.project_not_open</c>).
/// A missing or deleted version, plan, operation or sample — <c>datasets.unavailable</c>; changes check the revision
/// (<c>datasets.revision_conflict</c>) and are reported by the event <see cref="IProjectsManager.DatasetsChanged"/>. An archived
/// version is read-only: it cannot be changed, checked, exported or passed to training (<c>datasets.archived</c>).
/// </summary>
public interface IDatasetsManager
{
    /// <summary>Ready versions, newest first; includeArchived: false — without archived ones.</summary>
    Task<IReadOnlyList<DatasetVersion>> ListAsync(bool includeArchived = true, CancellationToken cancellationToken = default);

    /// <summary>Number of ready versions for the project overview: working (not archived and not corrupt), archived, and corrupt outside the archive.</summary>
    Task<DatasetsSummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<DatasetVersion> GetAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>Version passport for models: specification, classes, composition and manifest hash — also after the version is deleted.</summary>
    Task<DatasetVersion> GetPassportAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Edit": name 1–120 (<c>datasets.name_invalid</c>), description up to 4000 (<c>datasets.description_invalid</c>).
    /// </summary>
    Task<DatasetVersion> UpdateAsync(Guid versionId, string name, string description, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>"Archive" / "Restore"; repeating with the same value changes nothing.</summary>
    Task<DatasetVersion> SetArchivedAsync(Guid versionId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// "Check before build": validates the specification (<c>datasets.name_invalid</c>, <c>description_invalid</c>,
    /// <c>selection_invalid</c>, <c>variants_invalid</c>, <c>split_invalid</c>), groups (<c>datasets.group_unavailable</c>) and
    /// classes (<c>datasets.class_unavailable</c>), selects frames, assigns splits and counts the composition; the "Reject" role —
    /// <c>datasets.unselected_class</c>, <c>datasets.unassigned_variant</c>; a variant of a selected class without a role —
    /// <c>datasets.variant_role_missing</c>. Repeating the same operation returns its plan.
    /// </summary>
    Task<DatasetPlan> PlanAsync(DatasetSpecification specification, string operationId, CancellationToken cancellationToken = default);

    Task<DatasetPlan> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares the input of the job <see cref="IDatasetBuildJob"/> (enqueued by ICore.Jobs.EnqueueAsync with the same operationId): plan hash
    /// (<c>datasets.revision_conflict</c>), a stale plan (<c>datasets.plan_stale</c>), project input size
    /// (<c>datasets.input_size_mismatch</c>), an empty plan (<c>datasets.empty</c>), identical pixels in different splits
    /// (<c>datasets.split_collision</c>). Repeating the operation returns its input.
    /// </summary>
    Task<DatasetBuildInput> PrepareBuildAsync(Guid planId, string planHash, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Input of the job <see cref="IDatasetVerifyJob"/>: the version is ready and not archived. Repeating the operation returns its input.</summary>
    Task<DatasetVerifyInput> PrepareVerifyAsync(Guid versionId, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Input of the job <see cref="IDatasetExportJob"/>: the version is ready, not archived and not corrupt (<c>datasets.corrupt</c>), the path is
    /// an absolute .zip in an existing folder (<c>datasets.export_destination</c>). Repeating the operation returns its input.
    /// </summary>
    Task<DatasetExportInput> PrepareExportAsync(Guid versionId, string destinationPath, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Operations of a kind, newest first (limit 1–200, before — ID of the last job of the previous page).</summary>
    Task<DatasetOperationPage> ListOperationsAsync(DatasetOperationKind kind, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<DatasetOperation> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>"Remove from history": only a finished operation (<c>datasets.operation_active</c>); the ready version stays.</summary>
    Task DeleteOperationAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Samples of a split by ID (limit 1–200, after — NextCursor of the previous page of the same split).</summary>
    Task<DatasetSamplePage> ListSamplesAsync(Guid versionId, DatasetSplit split, int limit = 100, string? after = null,
        CancellationToken cancellationToken = default);

    Task<DatasetSample> GetSampleAsync(Guid versionId, Guid sampleId, CancellationToken cancellationToken = default);

    /// <summary>What a deletion removes: samples, history records, blockers (operations of this version, training and model checks).</summary>
    Task<DatasetDeletionReview> ReviewDeletionAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a version in two phases: sample rows and history in a transaction, files in the background. The version passport stays
    /// (<see cref="GetPassportAsync"/>). Errors: <c>datasets.deletion_revision_conflict</c>, <c>datasets.deletion_blocked</c>.
    /// </summary>
    Task<DatasetDeletionResult> DeleteAsync(Guid versionId, string reviewRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Version folder for training (read-only): the version is ready, not archived (<c>datasets.archived</c>), not corrupt
    /// (<c>datasets.corrupt</c>), its size equals the project input (<c>datasets.input_size_mismatch</c>), classes 0..n−1
    /// (<c>datasets.classes_invalid</c>).
    /// </summary>
    Task<DatasetTrainingSource> GetTrainingSourceAsync(Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a version into the training job folder: images/ and labels/ as hard links (copies if a link is impossible),
    /// data.yaml and manifest.json as copies. The folder must not exist or must be empty (<c>datasets.materialize_target</c>);
    /// the caller owns it, including after cancellation. Checks are the same as in <see cref="GetTrainingSourceAsync"/>; while the transfer runs,
    /// the version cannot be deleted.
    /// </summary>
    Task<DatasetTrainingSource> MaterializeAsync(Guid versionId, string targetDirectory, CancellationToken cancellationToken = default);
}
