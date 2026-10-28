using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Resources;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Controllers.Projects.Catalog;

/// <summary>
/// Classes, class variants and their images of an opened project. Image files are chosen in a native dialog
/// (<see cref="NativeDialogs.PickImageFiles"/>); the page gets a selection token and starts the import job with it.
/// </summary>
internal sealed class ClassesController(
    IProjectsManager projects,
    IJobsManager jobs,
    ResourceHandles resources,
    FileSelections selections,
    NativeDialogs dialogs)
{
    public async Task<CatalogWorkspaceResponseDto> GetWorkspaceAsync(GetCatalogWorkspaceRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.GetWorkspaceAsync(cancellationToken));

    public async Task<ClassResponseDto> CreateAsync(CreateClassRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.CreateAsync(request.Name, request.Description,
            cancellationToken));

    public async Task<ClassResponseDto> UpdateAsync(UpdateClassRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.UpdateAsync(request.ClassId, request.Name,
            request.Description, request.Revision, cancellationToken));

    public async Task<ClassResponseDto> SetArchivedAsync(SetClassArchivedRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.SetArchivedAsync(request.ClassId, request.Archived,
            request.Revision, cancellationToken));

    public async Task<ClassOrderResponseDto> ReorderAsync(ReorderClassesRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.ReorderAsync(request.ClassIds, request.OrderRevision,
            cancellationToken));

    public async Task<DeletionReviewResponseDto> ReviewDeletionAsync(ReviewClassDeletionRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.ReviewDeletionAsync(request.ClassId, cancellationToken));

    public async Task<DeletionResultResponseDto> DeleteAsync(DeleteClassRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.DeleteAsync(request.ClassId, request.ReviewRevision,
            cancellationToken));

    public async Task<ClassVariantResponseDto> AddVariantAsync(AddClassVariantRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.AddVariantAsync(request.ClassId, request.VariantId,
            cancellationToken));

    public async Task<ClassVariantResponseDto> SetVariantColorAsync(SetClassVariantColorRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.SetVariantColorAsync(request.ClassVariantId,
            request.Color, request.Revision, cancellationToken));

    public async Task<ClassVariantResponseDto> SetVariantArchivedAsync(SetClassVariantArchivedRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.SetVariantArchivedAsync(request.ClassVariantId,
            request.Archived, request.Revision, cancellationToken));

    public async Task<DeletionReviewResponseDto> ReviewVariantDeletionAsync(ReviewClassVariantDeletionRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.ReviewVariantDeletionAsync(request.ClassVariantId,
            cancellationToken));

    public async Task<DeletionResultResponseDto> DeleteVariantAsync(DeleteClassVariantRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.DeleteVariantAsync(request.ClassVariantId,
            request.ReviewRevision, cancellationToken));

    public async Task<ClassAssetPageResponseDto> ListAssetsAsync(ListClassAssetsRequestDto request, CancellationToken cancellationToken)
    {
        var project = await Project(request.ProjectId, cancellationToken);
        var page = await project.Classes.ListAssetsAsync(request.ClassVariantId, request.Limit ?? 100, request.After, cancellationToken);
        return CatalogResponseMapper.Map(project.Id, page, resources);
    }

    public async Task<DeletionReviewResponseDto> ReviewAssetDeletionAsync(ReviewClassAssetDeletionRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.ReviewAssetDeletionAsync(request.AssetId,
            cancellationToken));

    public async Task<DeletionResultResponseDto> DeleteAssetAsync(DeleteClassAssetRequestDto request, CancellationToken cancellationToken)
        => CatalogResponseMapper.Map(await (await Project(request.ProjectId, cancellationToken)).Classes.DeleteAssetAsync(request.AssetId,
            request.ReviewRevision, cancellationToken));

    /// <summary>Shows the native multi-select dialog; the answer comes when the user closes it.</summary>
    public async Task<ClassAssetFileSelectionResponseDto> SelectAssetFilesAsync(SelectClassAssetFilesRequestDto request, CancellationToken cancellationToken)
    {
        var paths = await dialogs.PickImageFiles(Strings.DialogAddImages, cancellationToken);
        return paths is null || paths.Count == 0
            ? new ClassAssetFileSelectionResponseDto(null, 0)
            : new ClassAssetFileSelectionResponseDto(selections.Register(paths), paths.Count);
    }

    /// <summary>Starts the import job and returns its snapshot at once; progress and the result come through job.changed.</summary>
    public async Task<JobResponseDto> ImportAssetsAsync(ImportClassAssetsRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var paths = selections.Resolve(request.SelectionToken, request.OperationId) is { Kind: FileSelectionKind.Files } selection
            ? selection.Paths
            : throw new CoreException("classes.selection_expired", "File selection is unknown, expired, a folder or belongs to another operation.");
        var project = await Project(request.ProjectId, cancellationToken);
        // Checked here for an immediate answer; the job checks again before it writes.
        var binding = await project.Classes.GetVariantAsync(request.ClassVariantId, cancellationToken);
        if (!binding.Available)
            throw new CoreException("classes.variant_unavailable", $"Class variant {binding.Id} is archived or its class or project variant is.");
        var workspace = await project.Classes.GetWorkspaceAsync(cancellationToken);
        var label = $"{workspace.Classes.FirstOrDefault(item => item.Id == binding.ClassId)?.Name} · {workspace.Variants.FirstOrDefault(item => item.Id == binding.VariantId)?.Name}";
        var job = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id, paths),
            project.Id, request.OperationId, label, cancellationToken);
        return JobResponseMapper.Map(job);
    }

    private Task<IProject> Project(string projectId, CancellationToken cancellationToken) => projects.GetAsync(projectId, cancellationToken);
}
