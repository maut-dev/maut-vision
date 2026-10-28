namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Limit — 1–500, default 100. After — nextCursor of the previous page.</summary>
public sealed record ListClassAssetsRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    int? Limit = null,
    string? After = null);
