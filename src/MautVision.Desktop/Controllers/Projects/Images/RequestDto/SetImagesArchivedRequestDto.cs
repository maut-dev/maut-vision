namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Image with the revision the user saw.</summary>
public sealed record ImageRevisionRequestDto(Guid Id, long Revision);

/// <summary>Up to 400 images; all or nothing.</summary>
public sealed record SetImagesArchivedRequestDto(string ProjectId, ImageRevisionRequestDto[] Images, bool Archived);
