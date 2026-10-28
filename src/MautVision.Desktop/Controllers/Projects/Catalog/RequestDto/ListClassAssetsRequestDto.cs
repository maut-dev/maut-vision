namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Limit — 1–500, по умолчанию 100. After — nextCursor предыдущей страницы.</summary>
public sealed record ListClassAssetsRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    int? Limit = null,
    string? After = null);
