namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Background with the revision the user saw.</summary>
public sealed record BackgroundRevisionRequestDto(Guid Id, long Revision);

/// <summary>"Remove" (Removed = true) or "Restore" up to 400 backgrounds; all or nothing, the set revision is not needed.</summary>
public sealed record SetBackgroundsRemovedRequestDto(string ProjectId, BackgroundRevisionRequestDto[] Items, bool Removed);
