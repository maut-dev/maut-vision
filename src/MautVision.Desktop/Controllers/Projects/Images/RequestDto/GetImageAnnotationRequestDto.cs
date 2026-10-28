namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Revision — ревизия истории разметки; null — текущая.</summary>
public sealed record GetImageAnnotationRequestDto(string ProjectId, Guid ImageId, long? Revision = null);
