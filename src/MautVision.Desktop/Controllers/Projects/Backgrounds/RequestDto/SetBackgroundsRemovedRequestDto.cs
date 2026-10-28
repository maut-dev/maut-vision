namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Фон с ревизией, которую видел пользователь.</summary>
public sealed record BackgroundRevisionRequestDto(Guid Id, long Revision);

/// <summary>«Убрать» (Removed = true) или «Восстановить» до 400 фонов; всё или ничего, ревизия набора не нужна.</summary>
public sealed record SetBackgroundsRemovedRequestDto(string ProjectId, BackgroundRevisionRequestDto[] Items, bool Removed);
