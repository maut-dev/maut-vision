using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Transfer;

namespace MautVision.Core.Interfaces.Projects;

/// <summary>Provides access to projects without exposing their implementation.</summary>
public interface IProjectsManager
{
    event EventHandler<ProjectOpenedEventArgs>? ProjectOpened;

    /// <summary>A project was created, imported, changed or archived; may come from a background thread.</summary>
    event EventHandler<ProjectChangedEventArgs>? ProjectChanged;

    /// <summary>A project was deleted: it is no longer in the list, the instance is invalid; may come from a background thread.</summary>
    event EventHandler<ProjectRemovedEventArgs>? ProjectRemoved;

    /// <summary>The snapshot of an import changed (progress, outcome); thinned out, may come from a background thread.</summary>
    event EventHandler<ProjectImportChangedEventArgs>? ImportChanged;

    /// <summary>The catalog (variants, classes, bindings, images) of an open project changed; may come from a background thread.</summary>
    event EventHandler<CatalogChangedEventArgs>? CatalogChanged;

    /// <summary>Image groups, images or annotation of an open project changed; may come from a background thread.</summary>
    event EventHandler<ImagesChangedEventArgs>? ImagesChanged;

    /// <summary>Background sets or backgrounds of an open project changed; may come from a background thread.</summary>
    event EventHandler<BackgroundsChangedEventArgs>? BackgroundsChanged;

    /// <summary>Generation presets or data of runs of an open project changed; may come from a background thread.</summary>
    event EventHandler<GenerationChangedEventArgs>? GenerationChanged;

    /// <summary>Dataset versions of an open project changed (appeared, changed, deleted); may come from a background thread.</summary>
    event EventHandler<DatasetsChangedEventArgs>? DatasetsChanged;

    /// <summary>Training runs (progress, epochs, log, outcome) or model versions of an open project changed; may come from a background thread.</summary>
    event EventHandler<TrainingChangedEventArgs>? TrainingChanged;

    /// <summary>Evaluations on datasets or the live check of an open project changed; may come from a background thread.</summary>
    event EventHandler<ChecksChangedEventArgs>? ChecksChanged;

    /// <summary>All projects, archived ones included. A project with damaged metadata is skipped.</summary>
    Task<IReadOnlyList<IProject>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>A project by ID; a missing one is CoreException <c>projects.not_found</c>.</summary>
    Task<IProject> GetAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares a project for work: creates or upgrades its storage. Storage newer than the application is
    /// CoreException <c>projects.storage_newer</c>, a failed upgrade is <c>projects.storage_migration_failed</c>.
    /// Opening is a move into the project: the last of the started calls is remembered as the last project and stops live checks
    /// of other projects; an earlier slow opening that finishes later does not do that (its project is just ready for work).
    /// </summary>
    Task<IProject> OpenAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares a project for work like <see cref="OpenAsync"/> (storage, job recovery, child managers) without moving into it: the last
    /// project stays and live checks of other projects keep running. For work in the background, such as the tools of an AI client.
    /// </summary>
    Task<IProject> PrepareAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Creates a project; the chosen base model must be installed.</summary>
    Task<IProject> CreateAsync(ProjectSettings settings, CancellationToken cancellationToken = default);

    /// <summary>The last opened project (remembered by OpenAsync, forgotten on deletion); null when there is none.</summary>
    Task<string?> GetLastProjectIdAsync(CancellationToken cancellationToken = default);

    /// <summary>What deleting the project cancels: running jobs and the live check; TransferActive — an export is running.</summary>
    Task<ProjectDeletionReview> ReviewDeletionAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a project from the computer: cancels jobs and the live check (only with cancelJobs, otherwise when there are any —
    /// <c>projects.busy</c>) and waits for them to stop, closes the storage, marks the deletion (project.json → project.deleted),
    /// renames the folder to projects/.deleted-&lt;id&gt;-&lt;guid&gt; and deletes it in the background. A changed revision is
    /// <c>projects.deletion_conflict</c>; a running export of the project is <c>projects.transfer_active</c>. Locked files are deleted on the
    /// next start (<see cref="CleanUpLeftoversAsync"/>), the outcome is then CleanupPending.
    /// </summary>
    Task<ProjectDeletionResult> DeleteAsync(string projectId, long expectedRevision, bool cancelJobs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepares an export of the project to destinationPath (.zip in an existing folder; otherwise <c>projects.export_destination</c>): prepares
    /// the project storage without making it the last opened one and checks that the project has no active jobs and no live check
    /// (<c>projects.transfer_busy</c>). The consumer enqueues the job: <c>IJobsManager.EnqueueAsync&lt;IProjectExportJob, ProjectExportInput&gt;</c>
    /// with the same operationId; a repeat with the operationId of an enqueued job returns its input without checks.
    /// </summary>
    Task<ProjectExportInput> PrepareExportAsync(string projectId, string destinationPath, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an import of a project archive and returns the snapshot at once; progress and outcome come with the event <see cref="ImportChanged"/>. A repeat with
    /// the same operationId returns the same import. One import runs at a time (<c>projects.import_active</c>). Refusals come
    /// in the snapshot: <c>projects.archive_changed</c>, <c>projects.archive_damaged</c>, <c>projects.archive_unknown</c>,
    /// <c>projects.archive_legacy</c> (an archive of an unsupported early format), <c>projects.disk_full</c>, <c>projects.storage_newer</c>.
    /// Cancellation and an error leave no changes. copyLabel — the word of the suffix a copy gets when its name is taken (the UI passes it in the language of the user); null — "import".
    /// </summary>
    Task<ProjectImportInfo> StartImportAsync(ProjectArchiveSource source, string operationId, string? copyLabel = null, CancellationToken cancellationToken = default);

    /// <summary>An import by ID; an unknown one is <c>projects.import_not_found</c>.</summary>
    ProjectImportInfo GetImport(Guid importId);

    /// <summary>Imports of this run of the application, newest first.</summary>
    IReadOnlyList<ProjectImportInfo> ListImports();

    /// <summary>Idempotent cancellation of an import: a snapshot with CancelRequested at once, Cancelled — with an event after the rollback.</summary>
    ProjectImportInfo CancelImport(Guid importId);

    /// <summary>
    /// Background cleanup of leftovers of earlier runs: folders of deleted projects (.deleted-*, marks project.deleted) and unfinished
    /// imports (.import-*). Looks only at names in projects/; called after the UI is shown, not during initialization.
    /// </summary>
    Task CleanUpLeftoversAsync(CancellationToken cancellationToken = default);
}
