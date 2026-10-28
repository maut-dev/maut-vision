namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ReviewRevision — revision из ответа просмотра состава удаления.</summary>
public sealed record DeleteClassVariantRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    string ReviewRevision);
