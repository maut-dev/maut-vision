namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>
/// SelectionToken — из images.selectFiles, images.selectFolder или images.selectVideo; действует 30 минут и только на этой
/// странице. OperationId — создаётся UI на одно действие; повтор с тем же значением вернёт ту же задачу.
/// Recursive — для папки: включая вложенные папки. SkipDuplicates — пропускать повторы в группе (по умолчанию да).
/// StartMs, IntervalMs, Count — для видео (обязательны): кадры на отметках StartMs + i × IntervalMs; начало ≥ 0,
/// интервал ≥ 1 мс, количество ≥ 1, последняя отметка раньше конца видео. Для файлов и папки не передаются.
/// </summary>
public sealed record ImportImagesRequestDto(
    string ProjectId,
    Guid GroupId,
    string SelectionToken,
    string OperationId,
    bool Recursive = false,
    bool SkipDuplicates = true,
    long? StartMs = null,
    long? IntervalMs = null,
    int? Count = null);
