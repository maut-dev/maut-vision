using System.Globalization;
using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.RequestDto;
using MautVision.Desktop.Controllers.Projects.ResponseDto;
using MautVision.Desktop.Host;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects;

/// <param name="selections">The destination of an export and the archive of an import, chosen in native dialogs.</param>
/// <param name="resources">URLs of a deleted project stop working.</param>
internal sealed class ProjectsController(IProjectsManager projects, IJobsManager jobs, FileSelections selections, NativeDialogs dialogs,
    ResourceHandles resources)
{
    internal const int MaxFileNameLength = 100;

    private readonly Lock openGate = new();
    private long latestOpen;
    // Exports started through this controller, by job: they are cancelled when the application exits.
    private readonly global::System.Collections.Concurrent.ConcurrentDictionary<Guid, string> exports = new();

    public async Task<ProjectLibraryResponseDto> ListAsync(ListProjectsRequestDto request, CancellationToken cancellationToken)
    {
        var items = (await projects.ListAsync(cancellationToken)).Select(ProjectResponseMapper.Map).ToArray();
        var last = await projects.GetLastProjectIdAsync(cancellationToken);
        return new ProjectLibraryResponseDto(items, items.Any(item => item.Id == last) ? last : null);
    }

    public async Task<ProjectResponseDto> CreateAsync(CreateProjectRequestDto request, CancellationToken cancellationToken)
        => ProjectResponseMapper.Map(await projects.CreateAsync(
            new ProjectSettings(request.Name, request.Description, request.BaseModelId, Map(request.InputSize)), cancellationToken));

    public async Task<ProjectResponseDto> UpdateAsync(UpdateProjectRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        await project.UpdateAsync(
            new ProjectSettings(request.Name, request.Description, request.BaseModelId, Map(request.InputSize)),
            request.Revision, cancellationToken);
        return ProjectResponseMapper.Map(project);
    }

    public async Task<ProjectResponseDto> SetArchivedAsync(SetProjectArchivedRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        await project.SetArchivedAsync(request.Archived, cancellationToken);
        return ProjectResponseMapper.Map(project);
    }

    /// <summary>
    /// Opens a project. Opens of one page run side by side; when a newer open (a larger Sequence) started while this one ran, its result is
    /// stale: <c>projects.open_superseded</c>, which the page ignores. A slow open (a long migration) never holds a newer switch back.
    /// </summary>
    public async Task<ProjectResponseDto> OpenAsync(OpenProjectRequestDto request, CancellationToken cancellationToken)
    {
        if (request.Sequence is { } sequence)
        {
            lock (openGate) latestOpen = Math.Max(latestOpen, sequence);
        }
        var project = await projects.OpenAsync(request.ProjectId, cancellationToken);
        if (request.Sequence is { } own)
        {
            lock (openGate)
            {
                if (own < latestOpen) throw new CoreException("projects.open_superseded", $"Open {own} of project {request.ProjectId} was superseded by open {latestOpen}.");
            }
        }
        return ProjectResponseMapper.Map(project);
    }

    /// <summary>A new page counts its opens from the start.</summary>
    internal void ResetSession()
    {
        lock (openGate) latestOpen = 0;
    }

    /// <summary>"Save as" with "&lt;name&gt; &lt;YYYY-MM-DD&gt;.zip"; overwriting is confirmed in the dialog itself. Null token — the dialog was closed.</summary>
    public async Task<ProjectExportSelectionResponseDto> SelectExportDestinationAsync(SelectProjectExportRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var path = await dialogs.PickSaveFile(Strings.DialogExportProject, DefaultFileName(project.Name, DateTime.Now, request.FallbackName), Strings.FilterProjectArchive, cancellationToken);
        if (string.IsNullOrEmpty(path)) return new ProjectExportSelectionResponseDto(null, null);
        return new ProjectExportSelectionResponseDto(selections.Register([path], FileSelectionKind.SaveFile), Path.GetFileName(path));
    }

    /// <summary>Queues projects.export with the project name as its label and returns its snapshot at once.</summary>
    public async Task<JobResponseDto> StartExportAsync(StartProjectExportRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var selection = selections.Resolve(request.SelectionToken, request.OperationId);
        if (selection is not { Kind: FileSelectionKind.SaveFile, Paths.Count: 1 })
            throw new CoreException("projects.export_selection_expired", "The export destination is unknown, expired or belongs to another operation.");
        // projects.transfer_busy carries the area of the running work in details.section (images, datasets…); the page names it.
        var input = await projects.PrepareExportAsync(request.ProjectId, selection.Paths[0], request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(input, request.ProjectId, request.OperationId, JobLabels.Name(input.ProjectName),
            cancellationToken);
        exports[job.Id] = request.ProjectId;
        return JobResponseMapper.Map(job);
    }

    /// <summary>The project archive to import; its size and time of change are kept, a file changed after the choice is refused.</summary>
    public async Task<ProjectArchiveSelectionResponseDto> SelectImportArchiveAsync(SelectProjectArchiveRequestDto request, CancellationToken cancellationToken)
    {
        var path = await dialogs.PickArchive(Strings.DialogImportProject, cancellationToken);
        if (string.IsNullOrEmpty(path)) return new ProjectArchiveSelectionResponseDto(null, null, null);
        var stamp = FileStamp.Of(path);
        return new ProjectArchiveSelectionResponseDto(selections.Register([path], FileSelectionKind.Archive, stamps: [stamp]), Path.GetFileName(path), stamp.Length);
    }

    public async Task<ProjectImportResponseDto> StartImportAsync(StartProjectImportRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var selection = selections.Resolve(request.SelectionToken, request.OperationId);
        if (selection is not { Kind: FileSelectionKind.Archive, Paths.Count: 1, Stamps: [var stamp] })
            throw new CoreException("projects.import_selection_expired", "The archive is unknown, expired or belongs to another operation.");
        var import = await projects.StartImportAsync(new ProjectArchiveSource(selection.Paths[0], stamp.Length, stamp.LastWriteTimeUtc), request.OperationId,
            request.CopyLabel, cancellationToken);
        return ProjectResponseMapper.Map(import);
    }

    public Task<ProjectImportResponseDto> GetImportAsync(GetProjectImportRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(ProjectResponseMapper.Map(projects.GetImport(request.ImportId)));

    public Task<ProjectImportResponseDto[]> ListImportsAsync(ListProjectImportsRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(projects.ListImports().Select(item => ProjectResponseMapper.Map(item)).ToArray());

    public Task<ProjectImportResponseDto> CancelImportAsync(CancelProjectImportRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(ProjectResponseMapper.Map(projects.CancelImport(request.ImportId)));

    public async Task<ProjectDeletionReviewResponseDto> ReviewDeletionAsync(ReviewProjectDeletionRequestDto request, CancellationToken cancellationToken)
    {
        var review = await projects.ReviewDeletionAsync(request.ProjectId, cancellationToken);
        return new ProjectDeletionReviewResponseDto(review.ProjectId, review.Name, review.Revision,
            review.Activities.Select(item => new ProjectActivityResponseDto(item.Kind, item.JobId, CodedTextDto.MapOrNull(item.Label))).ToArray(), review.TransferActive);
    }

    public async Task<ProjectDeletionResponseDto> DeleteAsync(DeleteProjectRequestDto request, CancellationToken cancellationToken)
    {
        var result = await projects.DeleteAsync(request.ProjectId, request.Revision, request.CancelJobs, cancellationToken);
        resources.RevokeProject(result.ProjectId);
        return new ProjectDeletionResponseDto(result.ProjectId, result.CleanupPending);
    }

    /// <summary>
    /// The application exits: running exports and imports are cancelled ("the transfer will be cancelled") and awaited for at most the wait, so an
    /// export removes its temporary file and an import its staging folder; what is left is removed after the next start.
    /// </summary>
    internal async Task CancelTransfersAsync(TimeSpan wait)
    {
        var imports = projects.ListImports().Where(import => import.Status == ProjectImportStatus.Running).Select(import => import.Id).ToArray();
        foreach (var import in imports) projects.CancelImport(import);
        foreach (var (jobId, projectId) in exports)
        {
            try
            {
                if ((await jobs.CancelAsync(projectId, jobId)).Status is not (JobStatus.Queued or JobStatus.Running)) exports.TryRemove(jobId, out _);
            }
            catch (CoreException)
            {
                exports.TryRemove(jobId, out _);
            }
        }
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            var running = projects.ListImports().Any(import => imports.Contains(import.Id) && import.Status == ProjectImportStatus.Running);
            foreach (var (jobId, projectId) in exports)
            {
                try
                {
                    if ((await jobs.GetAsync(projectId, jobId)).Status is JobStatus.Queued or JobStatus.Running) running = true;
                    else exports.TryRemove(jobId, out _);
                }
                catch (CoreException)
                {
                    exports.TryRemove(jobId, out _);
                }
            }
            if (!running) return;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// "&lt;name&gt; &lt;YYYY-MM-DD&gt;.zip": characters a file name cannot have become "_", the whole name fits 100 characters. A name with nothing
    /// left takes the fallback the page passed in its language, otherwise "Project".
    /// </summary>
    internal static string DefaultFileName(string name, DateTime today, string? fallback = null)
    {
        var suffix = " " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip";
        var stem = Stem(name);
        if (stem.Length == 0) stem = Stem(fallback ?? "");
        if (stem.Length == 0) stem = "Project";
        if (stem.Length + suffix.Length > MaxFileNameLength) stem = stem[..(MaxFileNameLength - suffix.Length)].TrimEnd();
        return stem + suffix;

        static string Stem(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(value.Trim().Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        }
    }

    private static InputSize Map(InputSizeDto size) => new(size.Width, size.Height);
}
