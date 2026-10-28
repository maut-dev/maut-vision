namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>Причина, по которой удаление недоступно: Code — стабильный код, Message — строка списка в диалоге.</summary>
public sealed record DeletionBlockerResponseDto(string Code, int Count, string Message);

/// <summary>
/// Состав удаления. TargetKind: variant, class, classVariant, asset. TargetName — название цели (у изображения пусто).
/// Revision передаётся в удаление; непустой Blockers — удаление сейчас недоступно.
/// </summary>
public sealed record DeletionReviewResponseDto(
    string TargetKind,
    Guid TargetId,
    string TargetName,
    long Classes,
    long ClassVariants,
    long Assets,
    DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Удалено строк; CleanupPending — файлы ещё удаляются в фоне, очистка продолжится при следующем открытии проекта.</summary>
public sealed record DeletionResultResponseDto(long Classes, long ClassVariants, long Assets, bool CleanupPending);
