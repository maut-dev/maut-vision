namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>ReviewRevision — revision из ответа просмотра состава удаления.</summary>
public sealed record DeleteClassAssetRequestDto(
    string ProjectId,
    Guid AssetId,
    string ReviewRevision);
