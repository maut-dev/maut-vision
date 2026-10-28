namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

public sealed record ReviewClassDeletionRequestDto(
    string ProjectId,
    Guid ClassId);
