namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record ReviewVariantDeletionRequestDto(
    string ProjectId,
    Guid VariantId);
