using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// Состав удаления навсегда: весь набор (WholeSet) или выбранные фоны. Items — «Будет удалено фонов: N.»; непустой Blockers —
/// «Завершите задачи или измените зависимости перед удалением:» + строки Message. Revision передаётся в удаление.
/// </summary>
public sealed record BackgroundDeletionReviewResponseDto(
    Guid SetId,
    string SetName,
    bool WholeSet,
    long Items,
    DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Строки удалены; CleanupPending — файлы ещё удаляются в фоне, очистка продолжится при следующем открытии проекта.</summary>
public sealed record BackgroundDeletionResultResponseDto(Guid SetId, bool SetDeleted, long Items, bool CleanupPending);

/// <summary>Изменённый или удалённый (Revision = null) набор или фон.</summary>
public sealed record BackgroundsEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// Событие backgrounds.changed. Sets — наборы с новыми ревизиями или удалённые; Items — фоны, изменённые явной операцией;
/// ItemsChangedIn — наборы, у которых изменился состав или счётчики: UI перечитывает их набор и страницы.
/// </summary>
public sealed record BackgroundsChangedEventDto(string ProjectId, BackgroundsEntityChangeDto[] Sets, BackgroundsEntityChangeDto[] Items, Guid[] ItemsChangedIn);
