namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ReviewRevision — revision из ответа просмотра состава удаления.</summary>
public sealed record DeleteClassRequestDto(
    string ProjectId,
    Guid ClassId,
    string ReviewRevision);
