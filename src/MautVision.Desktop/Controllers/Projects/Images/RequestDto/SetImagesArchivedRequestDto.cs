namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Изображение с ревизией, которую видел пользователь.</summary>
public sealed record ImageRevisionRequestDto(Guid Id, long Revision);

/// <summary>До 400 изображений; всё или ничего.</summary>
public sealed record SetImagesArchivedRequestDto(string ProjectId, ImageRevisionRequestDto[] Images, bool Archived);
