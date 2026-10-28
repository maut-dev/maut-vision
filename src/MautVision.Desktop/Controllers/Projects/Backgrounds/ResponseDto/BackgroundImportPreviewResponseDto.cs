using MautVision.Desktop.Controllers.Projects;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

/// <summary>Размытие запуска с выбранным seed.</summary>
public sealed record BackgroundBlurResponseDto(double MinRadius, double MaxRadius, long Seed);

/// <summary>
/// Пример будущего фона. SourceIndex — номер файла в выборе (у видео — номер кадра от начала), TimestampMs — отметка кадра видео.
/// Пропуск: Reason — код (backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable, backgrounds.similar,
/// backgrounds.duplicate, video.frame_unavailable; the page builds «Размер a×b, нужен W×H» and others from it), ImageUrl — null.
/// Иначе ImageUrl — PNG будущего фона ровно размера набора, Crop и BlurRadius — как у будущего фона.
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

/// <summary>«Примеры будущих фонов W × H · размытие a–b px, seed S» и до 6 примеров.</summary>
public sealed record BackgroundImportPreviewResponseDto(InputSizeDto TargetSize, BackgroundBlurResponseDto? Blur, BackgroundSampleResponseDto[] Samples);
