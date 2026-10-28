using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>Левый верхний угол рамки размера набора в пикселях исходного кадра (с учётом EXIF-ориентации).</summary>
public sealed record BackgroundCrop(int Left, int Top);

/// <summary>
/// Файл изображения (PNG, JPEG, WebP, BMP). Path — абсолютный путь; хранится во входе задачи и наружу не выходит.
/// Crop — рамка «Нарезать из изображений»; без неё файл берётся целиком и должен быть ровно размера набора.
/// </summary>
public sealed record BackgroundFileSource(string Path, BackgroundCrop? Crop = null);

/// <summary>
/// Видео «Нарезать из видео»: кадры на отметках Frames, из каждого вырезается одна рамка Region. Frames обычно — все доступные
/// кадры с выбранным шагом (<see cref="AvailableFrames"/>), а останов задаёт <see cref="ImportBackgroundsInput.RequestedCount"/>.
/// </summary>
public sealed record BackgroundVideoSource(string Path, VideoFrameRange Frames, BackgroundCrop Region)
{
    /// <summary>
    /// Сколько кадров даёт видео длительностью durationMs с отметками startMs + i × intervalMs:
    /// max(0, ⌊(durationMs − 1 − startMs) / intervalMs⌋ + 1). Последняя отметка строго раньше конца видео.
    /// </summary>
    public static int AvailableFrames(long durationMs, long startMs, long intervalMs)
    {
        if (intervalMs < 1 || startMs < 0 || startMs >= durationMs) return 0;
        return (int)Math.Min(int.MaxValue, (durationMs - 1 - startMs) / intervalMs + 1);
    }
}

/// <summary>
/// Размытие по Гауссу (sigma = радиус): радиус кадра выбирается детерминированно от Seed и номера источника в диапазоне
/// MinRadius–MaxRadius; меньше 0,3 px — фон остаётся резким. Seed null — выводится из ID проекта и операции, поэтому примеры
/// и запуск одной операции совпадают; итог запуска хранит выбранный seed.
/// </summary>
public sealed record BackgroundBlur(double MinRadius, double MaxRadius, uint? Seed = null);

/// <summary>
/// Вход задачи подготовки фонов в набор. Ровно один способ: Files (готовые кадры и нарезка из изображений) или Video.
/// RequestedCount — сколько фонов добавить (дальше источники не читаются; null — все источники). SkipSimilar — пропускать
/// кадры, подпись которых почти не отличается (≤ 3/255) от фонов набора и уже взятых этим запуском. Точные повторы
/// (те же пиксели в наборе) пропускаются всегда.
/// </summary>
public sealed record ImportBackgroundsInput(
    Guid SetId,
    IReadOnlyList<BackgroundFileSource> Files,
    BackgroundVideoSource? Video = null,
    int? RequestedCount = null,
    bool SkipSimilar = false,
    BackgroundBlur? Blur = null)
{
    public const double MaxBlurRadius = 1000;

    /// <summary>
    /// Нет источников — <c>backgrounds.sources_empty</c>; радиус вне 0–1000 или «от» больше «до» — <c>backgrounds.blur_invalid</c>;
    /// отрицательная рамка — <c>backgrounds.crop_invalid</c>; RequestedCount меньше 1 или больше числа источников —
    /// <c>backgrounds.count_invalid</c>; диапазон видео — <see cref="VideoFrameRange.Validate"/> (без длительности: только
    /// <c>video.frames_invalid</c>).
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Files);
        if (Files.Count > 0 && Video is not null) throw new ArgumentException("A run takes either files or one video.");
        if (Files.Count == 0 && Video is null) throw new CoreException("backgrounds.sources_empty", "A background run has no sources.");
        if (Blur is { } blur && !(double.IsFinite(blur.MinRadius) && double.IsFinite(blur.MaxRadius)
            && blur.MinRadius >= 0 && blur.MaxRadius <= MaxBlurRadius && blur.MinRadius <= blur.MaxRadius))
        {
            throw new CoreException("backgrounds.blur_invalid", $"Blur radius {blur.MinRadius}–{blur.MaxRadius} is outside 0–{MaxBlurRadius} or reversed.");
        }
        foreach (var file in Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path)) throw new ArgumentException("A background source needs a path.");
            if (file.Crop is { } crop && (crop.Left < 0 || crop.Top < 0)) throw CropInvalid();
        }
        var sources = Files.Count;
        if (Video is { } video)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(video.Path);
            ArgumentNullException.ThrowIfNull(video.Frames);
            if (video.Region is null || video.Region.Left < 0 || video.Region.Top < 0) throw CropInvalid();
            if (video.Frames.StartMs < 0 || video.Frames.IntervalMs < 1 || video.Frames.Count < 1)
                throw new CoreException("video.frames_invalid", "Video frames of a background run are invalid.");
            sources = video.Frames.Count;
        }
        if (RequestedCount is { } count && (count < 1 || count > sources))
            throw new CoreException("backgrounds.count_invalid", $"Requested {count} backgrounds of {sources} sources.");
    }

    private static CoreException CropInvalid() => new("backgrounds.crop_invalid", "A crop frame has a negative position.");
}

/// <summary>
/// Причина пропуска источника. SourceIndex — номер источника (у видео — номер кадра), Name — имя файла или видео,
/// Reason — код: <c>backgrounds.wrong_size</c> и <c>backgrounds.crop_out_of_bounds</c> (Width/Height — размер файла или кадра),
/// <c>backgrounds.image_unreadable</c>, <c>backgrounds.similar</c>, <c>backgrounds.duplicate</c>, <c>video.frame_unavailable</c>.
/// </summary>
public sealed record BackgroundImportIssue(int SourceIndex, string Name, string Reason, int? Width = null, int? Height = null, long? TimestampMs = null);

/// <summary>
/// Итог запуска (в <see cref="JobInfo.Result"/>, также после отмены и сбоя): Total — источников (файлов или кадров видео),
/// Processed — рассмотрено, Added, Duplicates (точные повторы), Similar (почти одинаковые), Rejected; RequestedCount — цель
/// запуска; TargetSize — размер набора; Blur — размытие с выбранным seed; Issues — первые <see cref="MaxIssues"/> пропусков.
/// </summary>
public sealed record ImportBackgroundsResult(
    int Total,
    int Processed,
    int Added,
    int Duplicates,
    int Similar,
    int Rejected,
    int? RequestedCount,
    InputSize TargetSize,
    BackgroundBlur? Blur,
    IReadOnlyList<BackgroundImportIssue> Issues)
{
    public const int MaxIssues = 100;
}

/// <summary>
/// Подготовка фонов в набор (CPU, возобновляемая). Кадр: декодирование с ориентацией → проверка размера или рамки → вырез →
/// подпись 32×32 → размытие → PNG RGBA ровно размера набора. Кадры обрабатываются параллельно, решения о повторах принимаются
/// по порядку источников. Набор не найден — <c>backgrounds.set_not_found</c>, в архиве — <c>backgrounds.set_archived</c>;
/// видео меньше набора — <c>backgrounds.video_too_small</c>, рамка вне кадра — <c>backgrounds.region_invalid</c>, ошибки
/// видео — коды <c>video.*</c>; источники изменились до продолжения — <c>backgrounds.import_sources_changed</c>; ни один
/// источник не подошёл — <c>backgrounds.no_usable_media</c>. Отмена и сбой оставляют добавленное.
/// </summary>
[JobKind("backgrounds.import")]
public interface IImportBackgroundsJob : IJobDefinition<ImportBackgroundsInput>
{
}
