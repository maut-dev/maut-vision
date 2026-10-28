namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Исходное изображение варианта класса. Format: png, jpeg, webp, bmp. Width/Height — с учётом EXIF-ориентации.
/// OriginalUrl и ThumbnailUrl — URL канала ресурсов (миниатюра JPEG до 256 px), действуют ограниченное время.
/// </summary>
public sealed record ClassAssetResponseDto(
    Guid Id,
    Guid ClassVariantId,
    string Format,
    int Width,
    int Height,
    bool HasAlpha,
    long ByteSize,
    DateTimeOffset CreatedAt,
    string OriginalUrl,
    string ThumbnailUrl);

/// <summary>NextCursor — значение after для следующей страницы; null на последней.</summary>
public sealed record ClassAssetPageResponseDto(ClassAssetResponseDto[] Items, string? NextCursor);

/// <summary>
/// Выбор файлов в нативном диалоге. SelectionToken — null, если пользователь закрыл диалог; пути в UI не передаются.
/// Токен действует 30 минут и только на странице, которая его получила.
/// </summary>
public sealed record ClassAssetFileSelectionResponseDto(string? SelectionToken, int FileCount);
