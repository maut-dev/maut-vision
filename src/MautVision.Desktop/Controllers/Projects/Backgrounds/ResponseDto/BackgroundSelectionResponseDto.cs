namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// Файл выбора: Index — номер в выборе (его передают в previewSource, sourceIndices и crops), Name — имя без пути, Width/Height —
/// размер с учётом ориентации (null у нечитаемого). Fit: exact (ровно размер набора), larger (можно вырезать рамкой),
/// smaller (будет пропущен), unreadable.
/// </summary>
public sealed record BackgroundSourceFileResponseDto(int Index, string Name, int? Width, int? Height, string Fit);

/// <summary>
/// Выбор файлов в нативном диалоге. SelectionToken — null, если диалог закрыт (тогда Files пусты); действует 30 минут и только
/// на этой странице. Файлы — в естественном порядке имён; Exact, Larger, Smaller, Unreadable — счётчики по Fit для размера
/// набора TargetWidth × TargetHeight.
/// </summary>
public sealed record BackgroundFileSelectionResponseDto(
    string? SelectionToken,
    int TargetWidth,
    int TargetHeight,
    BackgroundSourceFileResponseDto[] Files,
    int Exact,
    int Larger,
    int Smaller,
    int Unreadable);

/// <summary>
/// Кадр для рамки: SourceIndex — файл выбора (у кадра видео null), TimestampMs — отметка кадра видео (у файла null), Width/Height —
/// размер исходного кадра (координаты рамки), PreviewUrl — уменьшенная копия PreviewWidth × PreviewHeight (не больше 1600 px).
/// </summary>
public sealed record BackgroundSourcePreviewResponseDto(
    int? SourceIndex,
    long? TimestampMs,
    string Name,
    int Width,
    int Height,
    string PreviewUrl,
    int PreviewWidth,
    int PreviewHeight);
