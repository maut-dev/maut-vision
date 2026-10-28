namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Why deletion is unavailable: Code — the stable code, Count — how many, Examples — names that illustrate it (at most a few). The page
/// builds the line of the dialog from them.
/// </summary>
public sealed record DeletionBlockerResponseDto(string Code, int Count, string[] Examples)
{
    internal static DeletionBlockerResponseDto From(MautVision.Core.Interfaces.Projects.Catalog.DeletionBlocker blocker)
        => new(blocker.Code, blocker.Count, blocker.Examples.ToArray());
}

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
