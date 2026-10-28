namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record ReviewClassAssetDeletionRequestDto(
    string ProjectId,
    Guid AssetId);
