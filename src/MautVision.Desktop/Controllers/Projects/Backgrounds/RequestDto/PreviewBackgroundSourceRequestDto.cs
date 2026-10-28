namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>
/// Кадр файла выбора (SelectionToken из backgrounds.selectFiles, SourceIndex — номер файла в выборе) для рамки.
/// MaxSide — 64–4096, по умолчанию 1600.
/// </summary>
public sealed record PreviewBackgroundSourceRequestDto(string ProjectId, string SelectionToken, int SourceIndex, int? MaxSide = null);
