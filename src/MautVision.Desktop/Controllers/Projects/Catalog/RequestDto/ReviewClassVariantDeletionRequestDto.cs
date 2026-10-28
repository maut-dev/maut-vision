namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record ReviewClassVariantDeletionRequestDto(
    string ProjectId,
    Guid ClassVariantId);
