namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>Cut-out area of the source frame in its pixels.</summary>
public sealed record BackgroundCropAreaDto(int Left, int Top, int Width, int Height);

/// <summary>
/// Background origin. Kind: uploaded ("Ready image"), image-crop ("Image fragment"), video-frame ("Video frame").
/// SourceName — file or video name; frame fields (VideoName, TimestampMs, FrameIndex) — only for video-frame. Crop — the area
/// ("cut from x L, y T"), null — the whole frame. BlurRadius — blur radius, 0 — sharp, null — a run without blur.
/// </summary>
public sealed record BackgroundOriginResponseDto(
    string Kind,
    string OperationId,
    string SourceName,
    string? VideoName,
    long? TimestampMs,
    long? FrameIndex,
    BackgroundCropAreaDto? Crop,
    double? BlurRadius,
    string ProcessorVersion);

/// <summary>
/// Background: PNG of exactly the set size. Revision is passed to "Remove"/"Restore"; RemovedAt — removed. OriginalUrl (PNG) and
/// ThumbnailUrl (JPEG up to 256 px) — resource channel URLs valid for a limited time.
/// </summary>
public sealed record BackgroundResponseDto(
    Guid Id,
    Guid SetId,
    int Width,
    int Height,
    long ByteSize,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt,
    BackgroundOriginResponseDto Origin,
    string OriginalUrl,
    string ThumbnailUrl);

/// <summary>Page in the order of adding; NextCursor — the after value for the next page of the same filter, null on the last one.</summary>
public sealed record BackgroundPageResponseDto(BackgroundResponseDto[] Items, string? NextCursor);
