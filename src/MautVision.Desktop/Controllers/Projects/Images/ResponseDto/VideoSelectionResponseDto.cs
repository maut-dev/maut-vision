namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Видео, выбранное в нативном диалоге. SelectionToken — null, если пользователь закрыл диалог (тогда остальные поля null);
/// действует 30 минут и только на этой странице. DisplayName — имя файла без пути, ByteSize — размер файла,
/// Width/Height — размер кадров с учётом поворота, DurationMs — длительность. Видео при выборе не хешируется.
/// </summary>
public sealed record VideoSelectionResponseDto(string? SelectionToken, string? DisplayName, long? ByteSize, int? Width, int? Height, long? DurationMs);
