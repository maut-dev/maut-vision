using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Состав удаления навсегда: вся группа (WholeGroup) или выбранные изображения. Revision передаётся в удаление;
/// непустой Blockers — удаление сейчас недоступно (строки списка — Message).
/// </summary>
public sealed record ImageDeletionReviewResponseDto(
    Guid GroupId,
    string GroupName,
    bool WholeGroup,
    long Images,
    DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Строки удалены; CleanupPending — файлы ещё удаляются в фоне, очистка продолжится при следующем открытии проекта.</summary>
public sealed record ImageDeletionResultResponseDto(Guid GroupId, bool GroupDeleted, long Images, bool CleanupPending);

/// <summary>Изменённая или удалённая (Revision = null) группа или изображение.</summary>
public sealed record ImagesEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// Событие images.changed. Groups — группы с новыми ревизиями или удалённые; Images — изображения, изменённые явной
/// операцией; ImagesChangedIn — группы, у которых изменился состав или счётчики: UI перечитывает их группу и страницы.
/// </summary>
public sealed record ImagesChangedEventDto(
    string ProjectId,
    ImagesEntityChangeDto[] Groups,
    ImagesEntityChangeDto[] Images,
    Guid[] ImagesChangedIn);
