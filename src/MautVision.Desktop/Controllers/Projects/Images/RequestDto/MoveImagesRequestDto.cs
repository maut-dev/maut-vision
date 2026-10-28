namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>До 400 активных изображений из SourceGroupId в группу того же происхождения; всё или ничего.</summary>
public sealed record MoveImagesRequestDto(
    string ProjectId,
    Guid SourceGroupId,
    long SourceRevision,
    Guid TargetGroupId,
    long TargetRevision,
    ImageRevisionRequestDto[] Images);
