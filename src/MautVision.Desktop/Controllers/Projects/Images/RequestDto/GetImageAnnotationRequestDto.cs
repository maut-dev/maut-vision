namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Revision — annotation history revision; null — the current one.</summary>
public sealed record GetImageAnnotationRequestDto(string ProjectId, Guid ImageId, long? Revision = null);
