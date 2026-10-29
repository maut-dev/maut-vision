namespace MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;

/// <summary>
/// Source image of a class variant. Format: png, jpeg, webp, bmp. Width/Height — with EXIF orientation applied.
/// OriginalUrl and ThumbnailUrl — resource channel URLs (JPEG thumbnail up to 256 px), valid for a limited time.
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

/// <summary>NextCursor — the after value for the next page; null on the last one.</summary>
public sealed record ClassAssetPageResponseDto(ClassAssetResponseDto[] Items, string? NextCursor);

/// <summary>
/// File selection in a native dialog. SelectionToken — null if the user closed the dialog; paths are not passed to the UI.
/// The token is valid for 30 minutes and only on the page that received it.
/// </summary>
public sealed record ClassAssetFileSelectionResponseDto(string? SelectionToken, int FileCount);
