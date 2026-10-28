namespace MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;

/// <summary>Revision — ревизия объекта, которую видел пользователь; при расхождении — *.revision_conflict. Color — #rrggbb или null — цвет по умолчанию.</summary>
public sealed record SetClassVariantColorRequestDto(
    string ProjectId,
    Guid ClassVariantId,
    long Revision,
    string? Color);
