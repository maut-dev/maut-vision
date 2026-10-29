using MautVision.Core.BaseModels;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Jobs;
using MautVision.Core.Media;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Generation;
using MautVision.Core.Resources;
using MautVision.Core.Hardware;
using MautVision.Core.Interfaces.Hardware;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Interfaces.Training;
using MautVision.Core.Projects.Training;
using MautVision.Core.Python;
using MautVision.Core.Training;
using MautVision.Core.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Screen;
using MautVision.Core.Screen;
using MautVision.Core.Projects.Transfer;
using MautVision.Core.Interfaces.Projects.Transfer;
using Microsoft.Extensions.Logging;

namespace MautVision.Core;

internal sealed class Core(CoreOptions options) : ICore
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;
    private IProjectsManager projects = null!;
    private IBaseModelsManager baseModels = null!;
    private IJobsManager jobs = null!;
    private IMediaManager media = null!;
    private ITrainingPresetsManager trainingPresets = null!;
    private IHardwareManager hardware = null!;
    private PythonWorkers python = null!;
    private ScreenManager screen = null!;

    public IProjectsManager Projects => initialized ? projects : throw NotInitialized();
    public IBaseModelsManager BaseModels => initialized ? baseModels : throw NotInitialized();
    public IJobsManager Jobs => initialized ? jobs : throw NotInitialized();
    public IMediaManager Media => initialized ? media : throw NotInitialized();
    public ITrainingPresetsManager TrainingPresets => initialized ? trainingPresets : throw NotInitialized();
    public IHardwareManager Hardware => initialized ? hardware : throw NotInitialized();
    public IPythonRuntime Python => initialized ? python : throw NotInitialized();
    public IScreenManager Screen => initialized ? screen : throw NotInitialized();

    /// <summary>The Python executor shared by training and model checks.</summary>
    internal PythonWorkers PythonWorkers => python;

    /// <summary>Material kinds that may be served; a feature that adds a kind registers it here during composition.</summary>
    internal ResourceKinds ResourceKinds { get; } = new();

    internal ThumbnailGenerator Thumbnails { get; private set; } = null!;

    /// <summary>Reasons that block permanent deletion of catalog objects: jobs, annotations, generation presets.</summary>
    internal CatalogBlockers CatalogBlockers { get; } = new();

    /// <summary>Reasons that block deleting images of a group: an import into it, a generation filling it.</summary>
    internal ImageBlockers ImageBlockers { get; } = new();

    /// <summary>Reasons that block deleting backgrounds of a set: a preparation run, generation presets that use it.</summary>
    internal BackgroundBlockers BackgroundBlockers { get; } = new();

    /// <summary>
    /// Reasons that block deleting a dataset version: a check or an export of it; training and model checks register
    /// their providers here during the composition.
    /// </summary>
    internal DatasetBlockers DatasetBlockers { get; } = new();

    internal ImportClassAssetsHandler ImportClassAssets { get; private set; } = null!;

    internal ImportImagesHandler ImportImages { get; private set; } = null!;

    internal ImportBackgroundsHandler ImportBackgrounds { get; private set; } = null!;

    internal GenerationRunHandler GenerationRuns { get; private set; } = null!;

    internal DatasetBuildHandler DatasetBuilds { get; private set; } = null!;

    internal DatasetVerifyHandler DatasetVerifications { get; private set; } = null!;

    internal DatasetExportHandler DatasetExports { get; private set; } = null!;

    internal TrainingRunHandler TrainingRuns { get; private set; } = null!;

    internal ModelExportHandler ModelExports { get; private set; } = null!;

    internal EvaluationHandler Evaluations { get; private set; } = null!;

    internal ProjectExportHandler ProjectExports { get; private set; } = null!;

    /// <summary>The one GPU operation: a GPU job of the queue or a live check.</summary>
    internal GpuSlot Gpu { get; } = new();

    /// <summary>What blocks deleting a model version and what goes with it (evaluations, live checks, exports).</summary>
    internal ModelVersionLinks ModelVersionLinks { get; } = new();

    /// <summary>How long deleting a project waits for its cancelled jobs to stop.</summary>
    internal TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Running live checks of every project.</summary>
    internal LiveCheckHub LiveChecks { get; } = new();

    /// <summary>
    /// Smoke seam for the end of the application: worker trees end and no project of this composition touches project.db again.
    /// Paused handlers stay paused, and deferred writes (job progress, the training recorder) never land on top of what another
    /// composition recovers — as with a process that is gone.
    /// </summary>
    internal async Task EndForTestingAsync()
    {
        python.CloseAllForTesting();
        foreach (var store in await ((ProjectsManager)projects).LoadedStoresForTestingAsync().ConfigureAwait(false))
            await store.EndForTestingAsync().ConfigureAwait(false);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(options.WorkDirectory);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var loggers = options.LoggerFactory;
            baseModels = new BaseModelsManager(options.BaseModelCatalogPath, Path.Combine(options.WorkDirectory, "BaseModels"),
                loggers.CreateLogger<BaseModelsManager>());
            Thumbnails = new ThumbnailGenerator(loggers.CreateLogger<ThumbnailGenerator>());
            var media = new MediaManager(options.FfmpegDirectory, loggers.CreateLogger<MediaManager>());
            var pythonWorkers = new PythonWorkers(options.PythonWorker, options.BaseModelCatalogPath, loggers.CreateLogger<PythonWorkers>());
            var screenManager = new ScreenManager(loggers.CreateLogger<ScreenManager>());
            if (options.SmokeFixtures && ResourceKinds.Find(ResourceKinds.SmokeFixtureKind) is null)
            {
                ResourceKinds.Register(ResourceKinds.SmokeFixtureKind, ResourceKinds.ByExtensions(ResourceKinds.SmokeFixtureKind, ".jpg", ".png"));
            }
            if (ResourceKinds.Find(CatalogDeletion.AssetKind) is null)
            {
                ResourceKinds.Register(CatalogDeletion.AssetKind, ClassesManager.FindAssetExtensionAsync);
            }
            if (ResourceKinds.Find(ProjectImage.ResourceKind) is null)
            {
                ResourceKinds.Register(ProjectImage.ResourceKind, ImagesManager.FindExtensionAsync);
            }
            if (ResourceKinds.Find(BackgroundItem.ResourceKind) is null)
            {
                ResourceKinds.Register(BackgroundItem.ResourceKind, BackgroundsManager.FindExtensionAsync);
            }
            if (ResourceKinds.FindNested(DatasetSample.ResourceKind) is null)
            {
                ResourceKinds.RegisterNested(DatasetSample.ResourceKind, DatasetsManager.FindSampleFileAsync);
            }
            if (ResourceKinds.FindNested(ModelVersion.ResourceKind) is null)
            {
                ResourceKinds.RegisterNested(ModelVersion.ResourceKind, TrainingManager.FindModelFileAsync);
            }
            // Projects depend on the base model contract one way: creation requires installed weights.
            var projectsManager = new ProjectsManager(options.WorkDirectory, baseModels, ResourceKinds, Thumbnails, CatalogBlockers, ImageBlockers,
                BackgroundBlockers, DatasetBlockers, media, pythonWorkers, ModelVersionLinks, new CheckServices(Gpu, screenManager, LiveChecks),
                loggers.CreateLogger<ProjectsManager>());
            var jobsManager = new JobsManager(projectsManager, baseModels, loggers.CreateLogger<JobsManager>(), Gpu);
            ImportClassAssets = new ImportClassAssetsHandler(projectsManager);
            jobsManager.Register<IImportClassAssetsJob, ImportClassAssetsInput>(ImportClassAssets);
            ImportImages = new ImportImagesHandler(projectsManager, media);
            jobsManager.Register<IImportImagesJob, ImportImagesInput>(ImportImages);
            ImportBackgrounds = new ImportBackgroundsHandler(projectsManager, media);
            jobsManager.Register<IImportBackgroundsJob, ImportBackgroundsInput>(ImportBackgrounds);
            GenerationRuns = new GenerationRunHandler(projectsManager);
            jobsManager.Register<IGenerationRunJob, GenerationRunInput>(GenerationRuns);
            DatasetBuilds = new DatasetBuildHandler(projectsManager);
            jobsManager.Register<IDatasetBuildJob, DatasetBuildInput>(DatasetBuilds);
            DatasetVerifications = new DatasetVerifyHandler(projectsManager);
            jobsManager.Register<IDatasetVerifyJob, DatasetVerifyInput>(DatasetVerifications);
            DatasetExports = new DatasetExportHandler(projectsManager);
            jobsManager.Register<IDatasetExportJob, DatasetExportInput>(DatasetExports);
            TrainingRuns = new TrainingRunHandler(projectsManager);
            jobsManager.Register<ITrainingRunJob, TrainingRunInput>(TrainingRuns);
            ModelExports = new ModelExportHandler(projectsManager);
            jobsManager.Register<IModelExportJob, ModelExportInput>(ModelExports);
            Evaluations = new EvaluationHandler(projectsManager);
            jobsManager.Register<IModelEvaluationJob, ModelEvaluationInput>(Evaluations);
            ProjectExports = new ProjectExportHandler(projectsManager);
            jobsManager.Register<IProjectExportJob, ProjectExportInput>(ProjectExports);
            // An export starts only in a project without other work and keeps it that way; a project being deleted gets nothing new.
            jobsManager.Admission = (projectId, kind, activeKinds) =>
            {
                if (projectsManager.IsDeleting(projectId)) throw new CoreException("projects.deleting", $"Project {projectId} is being deleted.");
                if (activeKinds.Contains(ProjectExportHandler.Kind)) throw ProjectTransfer.Active(projectId);
                if (kind != ProjectExportHandler.Kind) return;
                if (activeKinds.Count > 0) throw ProjectTransfer.Busy(activeKinds[0]);
                if (LiveChecks.Running().Any(session => session.ProjectId == projectId)) throw ProjectTransfer.Busy(ProjectActivity.LiveCheckKind);
            };
            LiveChecks.Admission = projectId =>
            {
                if (projectsManager.IsDeleting(projectId)) throw new CoreException("projects.deleting", $"Project {projectId} is being deleted.");
                if (jobsManager.ActiveJobs(projectId).Any(job => job.Kind == ProjectExportHandler.Kind)) throw ProjectTransfer.Active(projectId);
            };
            projectsManager.Activities = projectId => jobsManager.ActiveJobs(projectId)
                .Select(job => new ProjectActivity(job.Kind, job.Id, job.Label))
                .Concat(LiveChecks.Running().Where(session => session.ProjectId == projectId).Select(_ => new ProjectActivity(ProjectActivity.LiveCheckKind, null, null)))
                .ToArray();
            // Deleting a project stops its live checks and jobs first; a job that does not stop keeps the project in place.
            projectsManager.Stopping = async (project, cancellationToken) =>
            {
                await LiveChecks.StopProjectAsync(project.Id, LiveCheckStopReason.User).ConfigureAwait(false);
                if (!await jobsManager.DetachAsync(project.Id, StopWait, cancellationToken).ConfigureAwait(false))
                    throw new CoreException("projects.stop_timeout", $"Jobs of project {project.Id} did not stop within {StopWait.TotalSeconds} s.");
                await LiveChecks.StopProjectAsync(project.Id, LiveCheckStopReason.User).ConfigureAwait(false);
            };
            // Any queued or running job of the project blocks permanent deletion of catalog objects.
            CatalogBlockers.Register((projectId, _, _, _, _) =>
            {
                var active = jobsManager.ActiveJobs(projectId);
                IReadOnlyList<DeletionBlocker> blockers = active.Count == 0
                    ? []
                    : [new DeletionBlocker("jobs.active", active.Count, active.Select(job => job.Kind).Distinct(StringComparer.Ordinal).Take(5).ToArray())];
                return Task.FromResult(blockers);
            });
            // Images referenced by the current annotations block deleting their class, class variant or project variant.
            CatalogBlockers.Register((_, store, kind, targetId, token) => AnnotationCatalogBlocker.CollectAsync(store, kind, targetId, token));
            // Generation presets (archived ones too) that use a class, a class variant or a weighted project variant block its deletion.
            CatalogBlockers.Register((_, store, kind, targetId, token) => GenerationManager.CatalogBlockersAsync(store, kind, targetId, token));
            // A queued or running import into the group blocks deleting its images and archiving it; imports of other groups do not.
            ImageBlockers.Register((projectId, _, groupId, _) =>
            {
                var imports = jobsManager.ActiveInputs(projectId, ImportImagesHandler.Kind)
                    .Count(input => Target<ImportImagesInput>(input)?.GroupId == groupId);
                IReadOnlyList<DeletionBlocker> blockers = imports == 0 ? [] : [new DeletionBlocker(ImageBlockers.ImportActive, imports, [])];
                return Task.FromResult(blockers);
            });
            // A group a generation is filling (state producing) is read-only and cannot be deleted or archived.
            ImageBlockers.Register((_, store, groupId, token) => ImagesManager.GenerationBlockersAsync(store, groupId, token));
            // A queued or running dataset build that reads the group blocks deleting its images: the build links their files.
            ImageBlockers.Register((projectId, _, groupId, _) =>
            {
                var builds = jobsManager.ActiveInputs(projectId, DatasetBuildHandler.Kind)
                    .Count(input => Target<DatasetBuildInput>(input)?.Specification.GroupIds.Contains(groupId) == true);
                IReadOnlyList<DeletionBlocker> blockers = builds == 0 ? [] : [new DeletionBlocker("datasets.build_active", builds, [])];
                return Task.FromResult(blockers);
            });
            // A queued or running check or export of a version blocks its deletion; operations of other versions do not.
            DatasetBlockers.Register((projectId, _, versionId, _) =>
            {
                var result = new List<DeletionBlocker>();
                foreach (var (kind, code) in new[] { (DatasetVerifyHandler.Kind, DatasetBlockers.VerifyActive), (DatasetExportHandler.Kind, DatasetBlockers.ExportActive) })
                {
                    var count = jobsManager.ActiveInputs(projectId, kind).Count(input => VersionOf(input) == versionId);
                    if (count > 0) result.Add(new DeletionBlocker(code, count, []));
                }
                return Task.FromResult<IReadOnlyList<DeletionBlocker>>(result);
            });
            // A queued, preparing or running training on the version blocks its deletion; ready models do not (the passport stays).
            DatasetBlockers.Register((projectId, _, versionId, _) =>
            {
                var runs = jobsManager.ActiveInputs(projectId, TrainingRunHandler.Kind).Count(input => VersionOf(input, "datasetVersionId") == versionId);
                IReadOnlyList<DeletionBlocker> blockers = runs == 0 ? [] : [new DeletionBlocker("training.active", runs, [])];
                return Task.FromResult(blockers);
            });
            // A queued or running evaluation on the version blocks its deletion, as training does.
            DatasetBlockers.Register((projectId, _, versionId, _) =>
            {
                var evaluations = jobsManager.ActiveInputs(projectId, EvaluationHandler.Kind).Count(input => VersionOf(input, "datasetVersionId") == versionId);
                IReadOnlyList<DeletionBlocker> blockers = evaluations == 0 ? [] : [new DeletionBlocker("checks.evaluation_active", evaluations, [])];
                return Task.FromResult(blockers);
            });
            // A model version cannot be deleted while it is evaluated, checked live or exported; its evaluations go with it.
            ModelVersionLinks.RegisterBlocker((projectId, versionId, _) =>
            {
                var result = new List<DeletionBlocker>();
                var evaluations = jobsManager.ActiveInputs(projectId, EvaluationHandler.Kind).Count(input => VersionOf(input) == versionId);
                if (evaluations > 0) result.Add(new DeletionBlocker("checks.evaluation_active", evaluations, []));
                var live = LiveChecks.Running().Count(session => session.ProjectId == projectId && session.Version.Id == versionId);
                if (live > 0) result.Add(new DeletionBlocker("checks.live_active", live, []));
                var exports = jobsManager.ActiveInputs(projectId, ModelExportHandler.Kind).Count(input => VersionOf(input) == versionId);
                if (exports > 0) result.Add(new DeletionBlocker(TrainingManager.ExportBlocker, exports, []));
                return Task.FromResult<IReadOnlyList<DeletionBlocker>>(result);
            });
            ModelVersionLinks.RegisterDependents(ChecksManager.VersionDependents);
            // A queued or running run into the set blocks deleting its backgrounds and archiving it; runs into other sets do not.
            BackgroundBlockers.Register((projectId, _, setId, _, _) =>
            {
                var runs = jobsManager.ActiveInputs(projectId, ImportBackgroundsHandler.Kind).Count(input => Target<ImportBackgroundsInput>(input)?.SetId == setId);
                IReadOnlyList<DeletionBlocker> blockers = runs == 0 ? [] : [new DeletionBlocker(BackgroundBlockers.ImportActive, runs, [])];
                return Task.FromResult(blockers);
            });
            // Generation presets block deleting the set they use, or all of its active backgrounds.
            BackgroundBlockers.Register((_, store, setId, itemIds, token) => GenerationManager.BackgroundBlockersAsync(store, setId, itemIds, token));
            // Jobs of a project are recovered and executed only once that project is opened, never at startup;
            // files still queued by deletions (busy, or cut short by exit) are removed at the same moment, in the background.
            // Generation groups and snapshots left by runs that are not active any more are finished then as well.
            projectsManager.Opening = async (project, cancellationToken) =>
            {
                await jobsManager.AttachAsync(project, cancellationToken).ConfigureAwait(false);
                var activeRuns = jobsManager.ActiveJobs(project.Id).Where(job => job.Kind == GenerationRunHandler.Kind).Select(job => job.Id).ToHashSet();
                await project.ImagesManager.FinishOrphanedGenerationAsync(activeRuns, cancellationToken).ConfigureAwait(false);
                await project.GenerationManager.ReleaseOrphanedSnapshotsAsync(activeRuns, cancellationToken).ConfigureAwait(false);
                // Versions of builds that ended with the process are discarded, temporary archives of interrupted exports removed.
                var activeBuilds = jobsManager.ActiveJobs(project.Id).Where(job => job.Kind == DatasetBuildHandler.Kind).Select(job => job.Id).ToHashSet();
                await project.DatasetsManager.DiscardOrphanedBuildsAsync(activeBuilds, cancellationToken).ConfigureAwait(false);
                await project.DatasetsManager.RemoveInterruptedExportsAsync(cancellationToken).ConfigureAwait(false);
                // Training that ended with the application: a valid published result becomes a version and its job succeeds.
                var activeTraining = jobsManager.ActiveJobs(project.Id).Where(job => job.Kind == TrainingRunHandler.Kind).Select(job => job.Id).ToHashSet();
                foreach (var runId in await project.TrainingManager.RecoverAsync(activeTraining, cancellationToken).ConfigureAwait(false))
                {
                    await jobsManager.CompleteInterruptedAsync(project.Id, runId).ConfigureAwait(false);
                }
                // Evaluations that ended with the application are "Interrupted"; live checks never survive it.
                var activeEvaluations = jobsManager.ActiveJobs(project.Id).Where(job => job.Kind == EvaluationHandler.Kind).Select(job => job.Id).ToHashSet();
                await project.ChecksManager.RecoverAsync(activeEvaluations, cancellationToken).ConfigureAwait(false);
                await project.FileCleanup.StartAsync(cancellationToken).ConfigureAwait(false);
            };
            // Recovered jobs run only after the recovery above and once the project is open: their handlers use its managers, and a
            // queued training must not take the version number of a result accepted on open.
            projectsManager.Opened = project => jobsManager.Start(project.Id);
            // Opening another project is a switch: a live check of the previous one stops.
            projectsManager.Switched = project => LiveChecks.StopOutside(project.Id);
            projects = projectsManager;
            jobs = jobsManager;
            this.media = media;
            trainingPresets = new TrainingPresetsManager(options.WorkDirectory, loggers.CreateLogger<TrainingPresetsManager>());
            hardware = new HardwareManager(loggers.CreateLogger<HardwareManager>());
            python = pythonWorkers;
            screen = screenManager;
            initialized = true;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    /// <summary>A stored job input; unreadable input targets nothing (the job itself becomes blocked).</summary>
    private static T? Target<T>(string input) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(input, JobsManager.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The dataset version a stored check, export or training input names; Guid.Empty for unreadable input.</summary>
    private static Guid VersionOf(string input, string property = "versionId")
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(input);
            return document.RootElement.TryGetProperty(property, out var value) && value.TryGetGuid(out var id) ? id : Guid.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return Guid.Empty;
        }
    }

    private static InvalidOperationException NotInitialized() => new("Initialize Core before accessing its managers.");
}
