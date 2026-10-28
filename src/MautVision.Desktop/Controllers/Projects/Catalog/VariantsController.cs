using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Catalog;

/// <summary>Project variants of an opened project; the full catalog snapshot is classes.getWorkspace.</summary>
public sealed class VariantsController(IProjectsManager projects)
{
    public async Task<VariantResponseDto> CreateAsync(CreateVariantRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return CatalogResponseMapper.Map(await project.Variants.CreateAsync(request.Name, request.Description, cancellationToken));
    }

    public async Task<VariantResponseDto> UpdateAsync(UpdateVariantRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return CatalogResponseMapper.Map(await project.Variants.UpdateAsync(request.VariantId, request.Name, request.Description, request.Revision,
            cancellationToken));
    }

    public async Task<VariantResponseDto> SetArchivedAsync(SetVariantArchivedRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return CatalogResponseMapper.Map(await project.Variants.SetArchivedAsync(request.VariantId, request.Archived, request.Revision, cancellationToken));
    }

    public async Task<DeletionReviewResponseDto> ReviewDeletionAsync(ReviewVariantDeletionRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return CatalogResponseMapper.Map(await project.Variants.ReviewDeletionAsync(request.VariantId, cancellationToken));
    }

    public async Task<DeletionResultResponseDto> DeleteAsync(DeleteVariantRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        return CatalogResponseMapper.Map(await project.Variants.DeleteAsync(request.VariantId, request.ReviewRevision, cancellationToken));
    }
}
