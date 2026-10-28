using MautVision.Core.Media;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Projects.Generation;
using MautVision.Core.Projects.Training;
using MautVision.Core.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Python;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Resources;
using MautVision.Core.Resources;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects;

/// <summary>Shared runtime instance of one project; persistence and validation belong to ProjectsManager.</summary>
internal sealed class Project : IProject
{
    private readonly ProjectsManager owner;
    private volatile ProjectMetadata metadata;
    private volatile bool open;

    internal Project(ProjectsManager owner, ProjectMetadata metadata, ProjectStore store, ResourceKinds resourceKinds, ThumbnailGenerator thumbnails,
        CatalogBlockers catalogBlockers, ImageBlockers imageBlockers, BackgroundBlockers backgroundBlockers, DatasetBlockers datasetBlockers, MediaManager media,
        IBaseModelsManager baseModels, PythonWorkers python, ModelVersionLinks modelLinks, CheckServices checks, ILogger logger)
    {
        this.owner = owner;
        this.metadata = metadata;
        Store = store;
        var resources = new ResourcesManager(store, resourceKinds, thumbnails);
        Resources = resources;
        FileCleanup = new FileCleanup(metadata.Id, store, resources.EvictThumbnails, logger);
        Catalog = new CatalogContext(metadata.Id, store, catalogBlockers, () => open, owner.RaiseCatalogChanged, FileCleanup, logger);
        Variants = new VariantsManager(Catalog);
        ClassesManager = new ClassesManager(Catalog);
        ImagesManager = new ImagesManager(metadata.Id, store, () => open, owner.RaiseImagesChanged, FileCleanup, imageBlockers, logger);
        BackgroundsManager = new BackgroundsManager(metadata.Id, store, () => open, () => this.metadata.InputSize, owner.RaiseBackgroundsChanged, FileCleanup,
            backgroundBlockers, media, logger);
        // Generation reads the catalog and the backgrounds one way; they do not know about it.
        GenerationManager = new GenerationManager(metadata.Id, store, () => open, () => this.metadata.InputSize, owner.RaiseGenerationChanged,
            ClassesManager, BackgroundsManager, logger);
        // Datasets read the catalog and the images one way; they do not know about datasets.
        DatasetsManager = new DatasetsManager(metadata.Id, store, () => open, () => this.metadata.InputSize, owner.RaiseDatasetsChanged,
            ClassesManager, ImagesManager, FileCleanup, datasetBlockers, logger);
        // Training reads the datasets and the base models one way; they do not know about training.
        TrainingManager = new TrainingManager(metadata.Id, store, () => open, () => this.metadata, owner.RaiseTrainingChanged, DatasetsManager, baseModels, python,
            modelLinks, FileCleanup, logger);
        // Checks read model versions and datasets one way; neither knows about checks.
        ChecksManager = new ChecksManager(metadata.Id, store, () => open, owner.RaiseChecksChanged, TrainingManager, DatasetsManager, python, checks, logger);
    }

    public string Id => metadata.Id;
    public string Name => metadata.Name;
    public string Description => metadata.Description;
    public string BaseModelId => metadata.BaseModelId;
    public InputSize InputSize => metadata.InputSize;
    public long Revision => metadata.Revision;
    public DateTimeOffset CreatedAt => metadata.CreatedAt;
    public DateTimeOffset UpdatedAt => metadata.UpdatedAt;
    public DateTimeOffset? ArchivedAt => metadata.ArchivedAt;
    public IResourcesManager Resources { get; }
    public IVariantsManager Variants { get; }
    public IClassesManager Classes => ClassesManager;
    public IImagesManager Images => ImagesManager;
    public IBackgroundsManager Backgrounds => BackgroundsManager;
    public IGenerationManager Generation => GenerationManager;
    public IDatasetsManager Datasets => DatasetsManager;
    public ITrainingManager Training => TrainingManager;
    public IChecksManager Checks => ChecksManager;

    /// <summary>The same instance as <see cref="Classes"/>, with the reads generation needs.</summary>
    internal ClassesManager ClassesManager { get; }

    /// <summary>The same instance as <see cref="Images"/>, with the operations the import job needs.</summary>
    internal ImagesManager ImagesManager { get; }

    /// <summary>The same instance as <see cref="Backgrounds"/>, with the operations the preparation run needs.</summary>
    internal BackgroundsManager BackgroundsManager { get; }

    /// <summary>The same instance as <see cref="Generation"/>, with the operations the run needs.</summary>
    internal GenerationManager GenerationManager { get; }

    /// <summary>The same instance as <see cref="Datasets"/>, with the operations the build, the check and the export need.</summary>
    internal DatasetsManager DatasetsManager { get; }

    /// <summary>The same instance as <see cref="Training"/>, with the operations the run and its recovery need.</summary>
    internal TrainingManager TrainingManager { get; }

    /// <summary>The same instance as <see cref="Checks"/>, with the operations evaluations and their recovery need.</summary>
    internal ChecksManager ChecksManager { get; }

    /// <summary>Second phase of permanent deletion for every material kind of the project.</summary>
    internal FileCleanup FileCleanup { get; }

    /// <summary>True once IProjectsManager.OpenAsync prepared the store and ran the open work; material managers require it.</summary>
    internal bool IsOpen => open;

    internal CatalogContext Catalog { get; }

    /// <summary>Opens lazily; child managers of the project receive it through their constructors.</summary>
    internal ProjectStore Store { get; }

    /// <summary>Consistent snapshot for readers that need several fields of one revision.</summary>
    internal ProjectMetadata Metadata => metadata;

    public Task UpdateAsync(ProjectSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
        => owner.UpdateAsync(this, settings, expectedRevision, cancellationToken);

    public Task SetArchivedAsync(bool archived, CancellationToken cancellationToken = default)
        => owner.SetArchivedAsync(this, archived, cancellationToken);

    internal void Apply(ProjectMetadata value) => metadata = value;

    internal void MarkOpen() => open = true;
}
