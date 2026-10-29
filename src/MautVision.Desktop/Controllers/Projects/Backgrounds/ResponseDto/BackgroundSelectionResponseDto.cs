namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>
/// Selection file: Index — index in the selection (passed to previewSource, sourceIndices and crops), Name — name without path, Width/Height —
/// size with orientation applied (null if unreadable). Fit: exact (exactly the set size), larger (can be cut with a crop),
/// smaller (will be skipped), unreadable.
/// </summary>
public sealed record BackgroundSourceFileResponseDto(int Index, string Name, int? Width, int? Height, string Fit);

/// <summary>
/// File selection in a native dialog. SelectionToken — null if the dialog was closed (then Files is empty); valid for 30 minutes and only
/// on this page. Files are in natural name order; Exact, Larger, Smaller, Unreadable — counters by Fit for the size
/// of the set TargetWidth × TargetHeight.
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
/// Frame for the crop: SourceIndex — selection file (null for a video frame), TimestampMs — video frame mark (null for a file), Width/Height —
/// source frame size (crop coordinates), PreviewUrl — a reduced copy PreviewWidth × PreviewHeight (no larger than 1600 px).
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
