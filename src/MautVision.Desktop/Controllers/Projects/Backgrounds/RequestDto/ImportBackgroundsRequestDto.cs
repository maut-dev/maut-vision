namespace MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

/// <summary>Crop for a selection file: SourceIndex — index of the file in the selection, Left/Top — top-left corner in file pixels.</summary>
public sealed record BackgroundCropRequestDto(int SourceIndex, int Left, int Top);

/// <summary>One crop for the whole video: top-left corner in frame pixels.</summary>
public sealed record BackgroundRegionRequestDto(int Left, int Top);

/// <summary>Blur: radius from/to 0–1000 px; Seed — 0–4 294 967 295 or null ("From operation").</summary>
public sealed record BackgroundBlurRequestDto(double MinRadius, double MaxRadius, long? Seed = null);

/// <summary>
/// Preparation run (backgrounds.import) and its samples (backgrounds.previewImport) — one form, so that the samples match
/// the future backgrounds (the same OperationId). SelectionToken — from backgrounds.selectFiles or backgrounds.selectVideo; the run
/// binds the token to the operation, the samples do not.
/// Files: SourceIndices — indexes of the included selection files in order (null — all; for "Cut from images" up to 500);
/// Crops — crops for files larger than the set (a file without a crop is used whole and must be exactly the set size); Count —
/// an optional limit. Video: Region, StartMs, IntervalMs (step ≥ 1 ms) are required; Count — "How many backgrounds to cut",
/// from 1 to the number of available frames. SkipSimilar — "Skip repeating frames". Blur — null or a radius up to 0 means no blur.
/// </summary>
public sealed record ImportBackgroundsRequestDto(
    string ProjectId,
    Guid SetId,
    string SelectionToken,
    string OperationId,
    int[]? SourceIndices = null,
    BackgroundCropRequestDto[]? Crops = null,
    BackgroundRegionRequestDto? Region = null,
    long? StartMs = null,
    long? IntervalMs = null,
    int? Count = null,
    bool SkipSimilar = false,
    BackgroundBlurRequestDto? Blur = null);
