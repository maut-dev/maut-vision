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
/// What a deletion removes. TargetKind: variant, class, classVariant, asset. TargetName — the target name (empty for an image).
/// Revision is passed to the deletion; non-empty Blockers — the deletion is unavailable now.
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

/// <summary>Rows deleted; CleanupPending — files are still being deleted in the background, the cleanup continues the next time the project is opened.</summary>
public sealed record DeletionResultResponseDto(long Classes, long ClassVariants, long Assets, bool CleanupPending);
