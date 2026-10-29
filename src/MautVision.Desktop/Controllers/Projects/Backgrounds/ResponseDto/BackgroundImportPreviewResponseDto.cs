using MautVision.Desktop.Controllers.Projects;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>Run blur with the chosen seed.</summary>
public sealed record BackgroundBlurResponseDto(double MinRadius, double MaxRadius, long Seed);

/// <summary>
/// Sample of a future background. SourceIndex — index of the file in the selection (for video — frame index from the start), TimestampMs — video frame mark.
/// Skip: Reason — code (backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable, backgrounds.similar,
/// backgrounds.duplicate, video.frame_unavailable; the page builds "Size a×b, W×H needed" and others from it), ImageUrl — null.
/// Otherwise ImageUrl — PNG of the future background of exactly the set size, Crop and BlurRadius — as in the future background.
/// </summary>
public sealed record BackgroundSampleResponseDto(
    int SourceIndex,
    string SourceName,
    long? TimestampMs,
    string? Reason,
    int? ActualWidth,
    int? ActualHeight,
    BackgroundCropAreaDto? Crop,
    double? BlurRadius,
    string? ImageUrl);

/// <summary>"Samples of future backgrounds W × H · blur a–b px, seed S" and up to 6 samples.</summary>
public sealed record BackgroundImportPreviewResponseDto(InputSizeDto TargetSize, BackgroundBlurResponseDto? Blur, BackgroundSampleResponseDto[] Samples);
