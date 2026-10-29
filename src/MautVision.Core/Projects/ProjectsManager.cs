using MautVision.Core.Media;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Projects.Images;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Projects.Checks;
using MautVision.Core.Projects.Training;
using MautVision.Core.Python;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Projects.Transfer;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Resources;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects;

/// <summary>Owns project metadata and cached instances. The root contains the projects directory.</summary>
internal sealed class ProjectsManager : IProjectsManager
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
    };

    internal const string LastProjectFileName = "last-project.json";
    internal const string DeletedPrefix = ".deleted-";
    internal const string TombstoneName = "project.deleted";
    /// <summary>A small deletion usually finishes its files within this wait; a larger one goes on in the background.</summary>
    internal static readonly TimeSpan RemovalGrace = TimeSpan.FromSeconds(2);

    private readonly string projectsDirectory;
    private readonly string lastProjectFile;
    private readonly IBaseModelsManager baseModels;
    private readonly ILogger logger;
    private readonly ResourceKinds resourceKinds;
    private readonly ThumbnailGenerator thumbnails;
    private readonly CatalogBlockers catalogBlockers;
    private readonly ImageBlockers imageBlockers;
    private readonly BackgroundBlockers backgroundBlockers;
    private readonly DatasetBlockers datasetBlockers;
    private readonly MediaManager media;
    private readonly PythonWorkers python;
    private readonly ModelVersionLinks modelLinks;
    private readonly CheckServices checks;
    // Serialises reads and writes of project.json so that a revision check and its write are atomic.
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly Dictionary<string, Project> projects = new(StringComparer.Ordinal);
    // Projects whose deletion is stopping their work: they are not opened and get no new jobs.
    private readonly HashSet<string> deleting = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim lastProjectGate = new(1, 1);
    private readonly Lock removalsGate = new();
    private readonly List<Task> removals = [];
    private readonly SemaphoreSlim leftoversGate = new(1, 1);
    private long openTicket;

    internal ProjectsManager(string rootDirectory, IBaseModelsManager baseModels, ResourceKinds resourceKinds, ThumbnailGenerator thumbnails,
        CatalogBlockers catalogBlockers, ImageBlockers imageBlockers, BackgroundBlockers backgroundBlockers, DatasetBlockers datasetBlockers, MediaManager media,
        PythonWorkers python, ModelVersionLinks modelLinks, CheckServices checks, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(baseModels);
        ArgumentNullException.ThrowIfNull(resourceKinds);
        ArgumentNullException.ThrowIfNull(thumbnails);
        ArgumentNullException.ThrowIfNull(logger);
        projectsDirectory = Path.Combine(Path.GetFullPath(rootDirectory), "projects");
        lastProjectFile = Path.Combine(Path.GetFullPath(rootDirectory), LastProjectFileName);
        this.baseModels = baseModels;
        this.resourceKinds = resourceKinds;
        this.thumbnails = thumbnails;
        this.catalogBlockers = catalogBlockers;
        this.imageBlockers = imageBlockers;
        this.backgroundBlockers = backgroundBlockers;
        this.datasetBlockers = datasetBlockers;
        this.media = media;
        this.python = python;
        this.modelLinks = modelLinks;
        this.checks = checks;
        this.logger = logger;
        Imports = new ProjectImports(projectsDirectory, ProjectSchema.Migrations.Count, PublishImportAsync,
            info => Raise(ImportChanged, new ProjectImportChangedEventArgs(info)), logger);
    }

    /// <summary>Imports of project archives; their seams (workers, pause) are set by smoke.</summary>
    internal ProjectImports Imports { get; }

    public event EventHandler<ProjectOpenedEventArgs>? ProjectOpened;
    public event EventHandler<ProjectChangedEventArgs>? ProjectChanged;
    public event EventHandler<ProjectRemovedEventArgs>? ProjectRemoved;
    public event EventHandler<ProjectImportChangedEventArgs>? ImportChanged;
    public event EventHandler<CatalogChangedEventArgs>? CatalogChanged;

    public event EventHandler<ImagesChangedEventArgs>? ImagesChanged;

    public event EventHandler<BackgroundsChangedEventArgs>? BackgroundsChanged;

    public event EventHandler<GenerationChangedEventArgs>? GenerationChanged;

    public event EventHandler<DatasetsChangedEventArgs>? DatasetsChanged;

    public event EventHandler<TrainingChangedEventArgs>? TrainingChanged;

    public event EventHandler<ChecksChangedEventArgs>? ChecksChanged;

    internal void RaiseCatalogChanged(CatalogChangedEventArgs args) => Raise(CatalogChanged, args);

    internal void RaiseImagesChanged(ImagesChangedEventArgs args) => Raise(ImagesChanged, args);

    internal void RaiseBackgroundsChanged(BackgroundsChangedEventArgs args) => Raise(BackgroundsChanged, args);

    internal void RaiseGenerationChanged(GenerationChangedEventArgs args) => Raise(GenerationChanged, args);

    internal void RaiseDatasetsChanged(DatasetsChangedEventArgs args) => Raise(DatasetsChanged, args);

    internal void RaiseTrainingChanged(TrainingChangedEventArgs args) => Raise(TrainingChanged, args);

    internal void RaiseChecksChanged(ChecksChangedEventArgs args) => Raise(ChecksChanged, args);

    /// <summary>
    /// Set once by the Core composition: work that belongs to opening a project, such as job recovery.
    /// It runs after the store is open; its failure fails the open. ProjectsManager does not know what it does.
    /// </summary>
    internal Func<Project, CancellationToken, Task>? Opening { get; set; }

    /// <summary>Set once by the Core composition: work that may start only once the project is open, such as its recovered jobs.</summary>
    internal Action<Project>? Opened { get; set; }

    /// <summary>
    /// Set once by the Core composition: what a switch to the project does to the others (their live checks stop). Called only for the
    /// latest of the opens in progress: a slow open that finishes after a newer one is not a switch.
    /// </summary>
    internal Action<Project>? Switched { get; set; }

    /// <summary>Set once by the Core composition: the active jobs and live checks of a project, from memory.</summary>
    internal Func<string, IReadOnlyList<ProjectActivity>>? Activities { get; set; }

    /// <summary>
    /// Set once by the Core composition: cancels the jobs and live checks of a project that is being deleted, waits until they stopped
    /// and forgets its jobs. Its failure leaves the project in place.
    /// </summary>
    internal Func<Project, CancellationToken, Task>? Stopping { get; set; }

    /// <summary>Whether the project is being deleted: it is not opened and gets no new jobs or checks.</summary>
    internal bool IsDeleting(string projectId)
    {
        lock (deleting) return deleting.Contains(projectId);
    }

    /// <summary>Smoke seam: completes when the background removals of deleted project folders started so far are finished.</summary>
    internal Task RemovalsForTestingAsync()
    {
        lock (removalsGate) return Task.WhenAll(removals.ToArray());
    }

    /// <summary>Smoke seam: the stores of the projects this composition has loaded.</summary>
    internal async Task<IReadOnlyList<ProjectStore>> LoadedStoresForTestingAsync()
    {
        await metadataGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return projects.Values.Select(project => project.Store).ToArray();
        }
        finally
        {
            metadataGate.Release();
        }
    }

    public async Task<IProject> OpenAsync(string projectId, CancellationToken cancellationToken = default)
        => await OpenCoreAsync(projectId, switching: true, cancellationToken).ConfigureAwait(false);

    /// <param name="switching">The user goes to the project (projects.open): the latest such open is remembered as the last project and
    /// stops the live checks of the others. An export opens the store without a switch.</param>
    private async Task<Project> OpenCoreAsync(string projectId, bool switching, CancellationToken cancellationToken)
    {
        var ticket = switching ? Interlocked.Increment(ref openTicket) : 0;
        var project = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        RequireNotDeleting(projectId);
        // Opening is the moment project data is prepared; listing and getting never touch project.db.
        await project.Store.EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        if (Opening is { } opening)
        {
            await opening(project, cancellationToken).ConfigureAwait(false);
        }
        project.MarkOpen();
        Opened?.Invoke(project);
        if (switching && ticket == Interlocked.Read(ref openTicket))
        {
            Switched?.Invoke(project);
            await RememberLastAsync(project.Id).ConfigureAwait(false);
            Raise(ProjectOpened, new ProjectOpenedEventArgs(project));
        }
        return project;
    }

    private void RequireNotDeleting(string projectId)
    {
        if (IsDeleting(projectId)) throw new CoreException("projects.deleting", $"Project {projectId} is being deleted.");
    }

    public async Task<IReadOnlyList<IProject>> ListAsync(CancellationToken cancellationToken = default)
    {
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ListLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            metadataGate.Release();
        }
    }

    /// <summary>The library, under the metadata gate: canonical project folders with project.json; a damaged one is skipped.</summary>
    private async Task<IReadOnlyList<Project>> ListLoadedAsync(CancellationToken cancellationToken)
    {
        if (ValidatePath(projectsDirectory) is not { } attributes)
        {
            return Array.Empty<Project>();
        }
        RequireDirectory(attributes);
        var result = new List<Project>();
        foreach (var directory in Directory.EnumerateDirectories(projectsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (!IsCanonicalId(id) || ValidatePath(Path.Combine(directory, "project.json")) is null)
            {
                continue;
            }
            try
            {
                result.Add(await ReadAsync(id, cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidDataException error)
            {
                // One damaged project must not hide the rest of the library; opening it reports the error.
                logger.LogWarning(error, "Damaged project {ProjectId} is skipped in the library.", id);
            }
        }
        return result.OrderBy(project => project.CreatedAt).ThenBy(project => project.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IProject> GetAsync(string projectId, CancellationToken cancellationToken = default)
        => await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

    private async Task<Project> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        ValidateId(projectId);
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            metadataGate.Release();
        }
    }

    public async Task<IProject> CreateAsync(ProjectSettings settings, CancellationToken cancellationToken = default)
    {
        var valid = await ValidateAsync(settings, previousBaseModelId: null, cancellationToken).ConfigureAwait(false);
        Project project;
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? directory = null;
        var published = false;
        try
        {
            ValidatePath(projectsDirectory);
            Directory.CreateDirectory(projectsDirectory);
            RequireDirectory(ValidatePath(projectsDirectory)!.Value);
            string id;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                id = Guid.NewGuid().ToString("D");
                directory = Path.Combine(projectsDirectory, id);
            }
            while (ValidatePath(directory) is not null);

            Directory.CreateDirectory(directory);
            RequireDirectory(ValidatePath(directory)!.Value);
            var now = DateTimeOffset.UtcNow;
            var metadata = new ProjectMetadata
            {
                FormatVersion = ProjectMetadata.CurrentFormatVersion,
                Id = id,
                Name = valid.Name,
                Description = valid.Description,
                BaseModelId = valid.BaseModelId,
                InputSize = valid.InputSize,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await WriteAsync(metadata, overwrite: false, cancellationToken).ConfigureAwait(false);
            published = true;
            project = CreateInstance(metadata);
            projects.Add(id, project);
        }
        finally
        {
            if (!published && directory is not null)
            {
                // Only our new empty directory can be removed, never another project tree.
                TryDiscardDirectory(directory);
            }
            metadataGate.Release();
        }
        Raise(ProjectChanged, new ProjectChangedEventArgs(project));
        return project;
    }

    internal async Task UpdateAsync(Project project, ProjectSettings settings, long expectedRevision, CancellationToken cancellationToken)
    {
        var valid = await ValidateAsync(settings, project.BaseModelId, cancellationToken).ConfigureAwait(false);
        await ChangeAsync(project, expectedRevision, current =>
        {
            if (current.ArchivedAt is not null)
                throw new CoreException("projects.archived", $"Project {current.Id} is archived and cannot be changed.");
            return current with
            {
                Name = valid.Name,
                Description = valid.Description,
                BaseModelId = valid.BaseModelId,
                InputSize = valid.InputSize,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task SetArchivedAsync(Project project, bool archived, CancellationToken cancellationToken)
    {
        if ((project.ArchivedAt is not null) == archived) return;
        await ChangeAsync(project, expectedRevision: null, current =>
            (current.ArchivedAt is not null) == archived ? null : current with { ArchivedAt = archived ? DateTimeOffset.UtcNow : null },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a change to the current metadata under the gate; null from change means nothing to save.</summary>
    private async Task ChangeAsync(Project project, long? expectedRevision, Func<ProjectMetadata, ProjectMetadata?> change, CancellationToken cancellationToken)
    {
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = project.Metadata;
            if (expectedRevision is { } revision && revision != current.Revision)
                throw new CoreException("projects.revision_conflict", $"Project {current.Id} has revision {current.Revision}, expected {revision}.");
            var changed = change(current);
            if (changed is null) return;
            var next = changed with { Revision = current.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            await WriteAsync(next, overwrite: true, cancellationToken).ConfigureAwait(false);
            project.Apply(next);
        }
        finally
        {
            metadataGate.Release();
        }
        Raise(ProjectChanged, new ProjectChangedEventArgs(project));
    }

    private async Task<ProjectSettings> ValidateAsync(ProjectSettings settings, string? previousBaseModelId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var name = settings.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            throw new CoreException("projects.name_invalid", $"Project name must contain between 1 and {MaxNameLength} characters.");
        var description = settings.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength)
            throw new CoreException("projects.description_invalid", $"Project description must not exceed {MaxDescriptionLength} characters.");
        if (string.IsNullOrWhiteSpace(settings.BaseModelId))
            throw new CoreException("projects.base_model_not_found", "Project base model is not selected.");
        var model = await baseModels.GetOrNullAsync(settings.BaseModelId, cancellationToken).ConfigureAwait(false)
            ?? throw new CoreException("projects.base_model_not_found", $"Base model {settings.BaseModelId} is not in the catalogue.");
        // A project keeps its model even if the weights were removed later; a new choice must be installed.
        if (settings.BaseModelId != previousBaseModelId && model.Status != BaseModelStatus.Installed)
            throw new CoreException("projects.base_model_not_installed", $"Base model {model.Id} is not installed.");
        var size = settings.InputSize;
        if (size is null || !IsValidSide(size.Width, model.Stride) || !IsValidSide(size.Height, model.Stride))
            throw new CoreException("projects.input_size_invalid", $"Input size must be positive multiples of {model.Stride}.");
        return new ProjectSettings(name, description, model.Id, new InputSize(size.Width, size.Height));
    }

    private static bool IsValidSide(int value, int stride) => value >= stride && value % stride == 0;

    private async Task<Project> ReadAsync(string id, CancellationToken cancellationToken)
    {
        if (projects.TryGetValue(id, out var existing))
        {
            // The shared instance is the source of truth after loading; every change is written through it.
            return existing;
        }
        var file = Path.Combine(projectsDirectory, id, "project.json");
        var attributes = ValidatePath(file)
            ?? throw new CoreException("projects.not_found", $"Project {id} was not found.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Project metadata must be a file.");
        }
        ProjectMetadata? metadata;
        try
        {
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            metadata = await JsonSerializer.DeserializeAsync<ProjectMetadata>(input, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Project metadata is invalid.", error);
        }
        if (metadata is null || !IsValidStored(metadata, id))
        {
            throw new InvalidDataException("Project metadata does not match its directory or contains invalid fields.");
        }
        var project = CreateInstance(metadata);
        projects.Add(id, project);
        return project;
    }

    private Project CreateInstance(ProjectMetadata metadata)
        => new(this, metadata, new ProjectStore(metadata.Id, Path.Combine(projectsDirectory, metadata.Id), logger), resourceKinds, thumbnails,
            catalogBlockers, imageBlockers, backgroundBlockers, datasetBlockers, media, baseModels, python, modelLinks, checks, logger);

    private static bool IsValidStored(ProjectMetadata metadata, string id) =>
        metadata.FormatVersion == ProjectMetadata.CurrentFormatVersion
        && metadata.Id == id
        && metadata.Name.Length is >= 1 and <= MaxNameLength && metadata.Name == metadata.Name.Trim()
        && metadata.Description.Length <= MaxDescriptionLength
        && !string.IsNullOrWhiteSpace(metadata.BaseModelId)
        && metadata.InputSize is { Width: > 0, Height: > 0 }
        && metadata.Revision >= 1
        && metadata.UpdatedAt >= metadata.CreatedAt;

    /// <summary>Writes project.json through a staging file in the same directory, then renames it.</summary>
    /// <param name="directory">The project folder; null — projects/&lt;id&gt;.</param>
    private async Task WriteAsync(ProjectMetadata metadata, bool overwrite, CancellationToken cancellationToken, string? directory = null)
    {
        directory ??= Path.Combine(projectsDirectory, metadata.Id);
        RequireDirectory(ValidatePath(directory) ?? throw new CoreException("projects.not_found", $"Project {metadata.Id} was not found."));
        var destination = Path.Combine(directory, "project.json");
        var temporary = Path.Combine(directory, $".project-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, metadata, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePath(destination);
            File.Move(temporary, destination, overwrite);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Project {ProjectId} metadata write failed.", metadata.Id);
            throw;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    // ---- Last project ----

    public async Task<string?> GetLastProjectIdAsync(CancellationToken cancellationToken = default)
    {
        await lastProjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(lastProjectFile)) return null;
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(lastProjectFile, cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty("projectId", out var value) && value.ValueKind == JsonValueKind.String && IsCanonicalId(value.GetString())
                ? value.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Only a highlight in the library depends on it.
            logger.LogWarning(error, "The last project could not be read.");
            return null;
        }
        finally
        {
            lastProjectGate.Release();
        }
    }

    /// <summary>Writes (or with null removes) the last project file through a temporary file; a failure is logged, not thrown.</summary>
    private async Task RememberLastAsync(string? projectId, string? onlyIf = null)
    {
        await lastProjectGate.WaitAsync().ConfigureAwait(false);
        var temporary = lastProjectFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (projectId is null)
            {
                if (onlyIf is not null && File.Exists(lastProjectFile))
                {
                    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(lastProjectFile).ConfigureAwait(false));
                    if (!document.RootElement.TryGetProperty("projectId", out var value) || value.GetString() != onlyIf) return;
                }
                File.Delete(lastProjectFile);
                return;
            }
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new { projectId })).ConfigureAwait(false);
            File.Move(temporary, lastProjectFile, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(error, "The last project could not be saved.");
        }
        finally
        {
            TryDeleteFile(temporary);
            lastProjectGate.Release();
        }
    }

    // ---- Deletion ----

    public async Task<ProjectDeletionReview> ReviewDeletionAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var activities = Activities?.Invoke(projectId) ?? [];
        return new ProjectDeletionReview(project.Id, project.Name, project.Revision, activities.Where(item => item.Kind != ProjectExportHandler.Kind).ToArray(),
            activities.Any(item => item.Kind == ProjectExportHandler.Kind));
    }

    public async Task<ProjectDeletionResult> DeleteAsync(string projectId, long expectedRevision, bool cancelJobs, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        // Read outside the lock below: the job admission takes the jobs lock first and then asks IsDeleting.
        var activities = Activities?.Invoke(projectId) ?? [];
        lock (deleting)
        {
            if (deleting.Contains(projectId)) throw new CoreException("projects.deleting", $"Project {projectId} is already being deleted.");
            if (project.Revision != expectedRevision)
                throw new CoreException("projects.deletion_conflict", $"Project {projectId} has revision {project.Revision}, expected {expectedRevision}.");
            if (activities.Any(item => item.Kind == ProjectExportHandler.Kind))
                throw new CoreException("projects.transfer_active", $"Project {projectId} is being exported.");
            if (activities.Count > 0 && !cancelJobs)
            {
                throw new CoreException("projects.busy", $"Project {projectId} has {activities.Count} active jobs or checks.")
                {
                    Details = new Dictionary<string, string> { ["count"] = activities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                };
            }
            deleting.Add(projectId);
        }
        var directory = Path.Combine(projectsDirectory, projectId);
        try
        {
            // New jobs and checks are refused from here (IsDeleting); the running ones are cancelled and awaited.
            if (Stopping is { } stopping) await stopping(project, cancellationToken).ConfigureAwait(false);
            project.FileCleanup.Suspended = true;
            await project.FileCleanup.CleanupTask.ConfigureAwait(false);
            await project.Store.CloseAsync().ConfigureAwait(false);
            await metadataGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // The tombstone is the moment of deletion: without project.json the folder is never listed again, even if it stays.
                ValidatePath(directory);
                File.Move(Path.Combine(directory, "project.json"), Path.Combine(directory, TombstoneName), overwrite: true);
                projects.Remove(projectId);
            }
            finally
            {
                metadataGate.Release();
            }
        }
        finally
        {
            lock (deleting) deleting.Remove(projectId);
        }
        logger.LogInformation("Project {ProjectId} deleted (tombstone written).", projectId);
        await RememberLastAsync(null, onlyIf: projectId).ConfigureAwait(false);
        Raise(ProjectRemoved, new ProjectRemovedEventArgs(projectId));

        var pending = true;
        var removal = RenameAndRemove(directory, projectId);
        if (await Task.WhenAny(removal, Task.Delay(RemovalGrace, CancellationToken.None)).ConfigureAwait(false) == removal) pending = !await removal.ConfigureAwait(false);
        else pending = false;
        return new ProjectDeletionResult(projectId, pending);
    }

    /// <summary>
    /// Renames a tombstoned folder to projects/.deleted-&lt;id&gt;-&lt;guid&gt; and removes it in the background. True — everything is gone; false —
    /// some file is busy (the folder or what is left of it is removed after the next start).
    /// </summary>
    private Task<bool> RenameAndRemove(string directory, string projectId)
    {
        var target = Path.Combine(projectsDirectory, DeletedPrefix + projectId + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(directory, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Folder of deleted project {ProjectId} is busy; it is removed after the next start.", projectId);
            return Task.FromResult(false);
        }
        var removal = Task.Run(() => RemoveTree(target));
        lock (removalsGate)
        {
            removals.RemoveAll(task => task.IsCompleted);
            removals.Add(removal);
        }
        return removal;
    }

    /// <summary>Removes a folder of a deleted project or an unfinished import; a busy file leaves the rest for the next start.</summary>
    private bool RemoveTree(string folder)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            logger.LogInformation("Folder {Folder} removed in {Elapsed} ms.", Path.GetFileName(folder), watch.ElapsedMilliseconds);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Folder {Folder} was not removed completely; the rest is removed after the next start.", Path.GetFileName(folder));
            return false;
        }
    }

    public async Task CleanUpLeftoversAsync(CancellationToken cancellationToken = default)
    {
        await leftoversGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(projectsDirectory)) return;
                foreach (var folder in Directory.EnumerateDirectories(projectsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(folder);
                    if (name.StartsWith(DeletedPrefix, StringComparison.Ordinal))
                    {
                        RemoveTree(folder);
                    }
                    else if (name.StartsWith(ProjectImports.StagingPrefix, StringComparison.Ordinal))
                    {
                        if (!Imports.IsActiveStaging(name)) RemoveTree(folder);
                    }
                    else if (IsCanonicalId(name) && !File.Exists(Path.Combine(folder, "project.json")) && File.Exists(Path.Combine(folder, TombstoneName)))
                    {
                        var removal = RenameAndRemove(folder, name);
                        removal.Wait(cancellationToken);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Leftovers of deleted projects and imports were not cleaned up.");
        }
        finally
        {
            leftoversGate.Release();
        }
    }

    // ---- Transfer ----

    public async Task<ProjectExportInput> PrepareExportAsync(string projectId, string destinationPath, string operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        string destination;
        try
        {
            destination = Path.GetFullPath(destinationPath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CoreException("projects.export_destination", "The export destination is not a valid path.", error);
        }
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath)
            || !string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.GetDirectoryName(destination)) || Directory.Exists(destination)
            || destination.StartsWith(projectsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new CoreException("projects.export_destination", "The export destination is not a .zip file in an existing folder outside the library.");
        }
        var project = await OpenCoreAsync(projectId, switching: false, cancellationToken).ConfigureAwait(false);
        // A repeated request of a queued export returns its input; the job itself counts as the project's work otherwise.
        if (await project.Store.ReadAsync((connection, token) => Jobs.JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token),
            cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (existing.Info.Kind != ProjectExportHandler.Kind)
                throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {existing.Info.Kind}.", nameof(operationId));
            return JsonSerializer.Deserialize<ProjectExportInput>(existing.Input, Jobs.JobsManager.Json)
                ?? throw new InvalidDataException($"Input of export job {existing.Info.Id} is empty.");
        }
        var activities = Activities?.Invoke(projectId) ?? [];
        if (activities.Any(item => item.Kind == ProjectExportHandler.Kind)) throw ProjectTransfer.Active(projectId);
        if (activities.Count > 0) throw ProjectTransfer.Busy(activities[0].Kind);
        return new ProjectExportInput(project.Name, destination);
    }

    public async Task<ProjectImportInfo> StartImportAsync(ProjectArchiveSource source, string operationId, string? copyLabel = null,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var label = copyLabel?.Trim();
        return Imports.Start(source, operationId, string.IsNullOrEmpty(label) ? null : label[..Math.Min(label.Length, MaxCopyLabelLength)]);
    }

    public ProjectImportInfo GetImport(Guid importId) => Imports.Get(importId);

    public IReadOnlyList<ProjectImportInfo> ListImports() => Imports.List();

    public ProjectImportInfo CancelImport(Guid importId) => Imports.Cancel(importId);

    /// <summary>
    /// Publishes a checked import folder: a project whose id is in the library gets a new id, a taken name gets " (import)", " (import 2)"…
    /// (the word chosen by the UI);
    /// then one rename makes it projects/&lt;id&gt; and the project appears (ProjectChanged).
    /// </summary>
    private async Task<Project> PublishImportAsync(string staging, string? copyLabel, CancellationToken cancellationToken)
    {
        ProjectMetadata? metadata;
        try
        {
            await using var input = new FileStream(Path.Combine(staging, "project.json"), FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous);
            metadata = await JsonSerializer.DeserializeAsync<ProjectMetadata>(input, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new CoreException("projects.archive_damaged", "project.json of the archive is invalid.", error);
        }
        if (metadata is null || !IsCanonicalId(metadata.Id) || !IsValidStored(metadata, metadata.Id))
            throw new CoreException("projects.archive_damaged", "project.json of the archive has invalid fields.");
        Project project;
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ListLoadedAsync(cancellationToken).ConfigureAwait(false);
            var id = metadata.Id;
            while (projects.ContainsKey(id) || ValidatePath(Path.Combine(projectsDirectory, id)) is not null)
            {
                id = Guid.NewGuid().ToString("D");
            }
            var name = UniqueName(metadata.Name, library.Select(item => item.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase), copyLabel);
            if (id != metadata.Id || name != metadata.Name)
            {
                await WriteAsync(metadata with { Id = id, Name = name }, overwrite: true, cancellationToken, staging).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, Path.Combine(projectsDirectory, id));
            project = await ReadAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            metadataGate.Release();
        }
        Raise(ProjectChanged, new ProjectChangedEventArgs(project));
        return project;
    }

    /// <summary>The longest word of the copy suffix the UI may pass.</summary>
    internal const int MaxCopyLabelLength = 40;

    /// <summary>
    /// The name, or with " (&lt;label&gt;)", " (&lt;label&gt; 2)"… when a project of the library has it (label — the word the UI chose in the language of
    /// the user; English "import" without it); the base is shortened to fit 120 characters.
    /// </summary>
    internal static string UniqueName(string name, IReadOnlySet<string> taken, string? label = null)
    {
        if (!taken.Contains(name)) return name;
        label ??= "import";
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? $" ({label})" : $" ({label} {number})";
            var stem = name.Length + suffix.Length <= MaxNameLength ? name : name[..(MaxNameLength - suffix.Length)].TrimEnd();
            var candidate = stem + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private void Raise<TArgs>(EventHandler<TArgs>? handlers, TArgs args) where TArgs : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<TArgs> subscriber in handlers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception error)
            {
                // Observers cannot turn a successful project operation into a failure.
                logger.LogError(error, "Project event observer failed.");
            }
        }
    }

    private static bool IsCanonicalId(string? id) => Guid.TryParseExact(id, "D", out var guid) && guid != Guid.Empty && guid.ToString("D") == id;

    private static void ValidateId(string projectId)
    {
        if (!IsCanonicalId(projectId))
        {
            throw new ArgumentException("Project ID must be a canonical GUID.", nameof(projectId));
        }
    }

    internal static void RequireDirectory(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("Managed project storage must be a directory.");
        }
    }

    /// <summary>Rejects links/reparse points at every existing ancestor, including the managed root.</summary>
    internal static FileAttributes? ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        FileAttributes? attributes = null;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Reparse points are not permitted in managed project storage.");
            }
            if (index < segments.Length - 1)
            {
                RequireDirectory(attributes.Value);
            }
        }
        return attributes;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (ValidatePath(path) is not null) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover staging file is never read as project metadata.
        }
    }

    private static void TryDiscardDirectory(string directory)
    {
        try
        {
            if (ValidatePath(directory) is not null)
            {
                Directory.Delete(directory, recursive: false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover directory never becomes a published Project without project.json.
        }
    }
}
