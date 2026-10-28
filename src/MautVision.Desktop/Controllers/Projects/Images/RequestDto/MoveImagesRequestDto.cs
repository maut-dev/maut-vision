namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>Up to 400 active images from SourceGroupId to a group of the same origin; all or nothing.</summary>
public sealed record MoveImagesRequestDto(
    string ProjectId,
    Guid SourceGroupId,
    long SourceRevision,
    Guid TargetGroupId,
    long TargetRevision,
    ImageRevisionRequestDto[] Images);
