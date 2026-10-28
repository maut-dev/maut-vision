using System.Text.Json;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Checks.ResponseDto;

/// <summary>Метрики класса (выход Index): precision, recall, mAP50, mAP50_95.</summary>
public sealed record EvaluationClassMetricsDto(int Index, Guid? ClassId, string Name, Dictionary<string, double> Metrics);

/// <summary>Error of a check: Code — what the page translates; WorkerMessage — the worker's own message for its checks.worker_* codes, null otherwise.</summary>
public sealed record CheckErrorDto(string Code, string? WorkerMessage);

/// <summary>
/// Оценка на датасете: State — queued «Ожидает видеокарту» | preparing «Подготовка» | running «Идёт проверка» | cancelling «Остановка…» |
/// succeeded «Готово» | failed «Ошибка» | cancelled «Остановлено» | interrupted «Прервано»; Job — снимок задачи checks.evaluate;
/// Split — val | test; Metrics — precision, recall, mAP50, mAP50_95; ImageCount — кадров в части; Revision — для сверки с checks.changed.
/// </summary>
public sealed record ModelEvaluationResponseDto(
    Guid Id,
    JobResponseDto Job,
    string State,
    Guid VersionId,
    int VersionNumber,
    TrainingDatasetDto Dataset,
    string Split,
    string Device,
    int Batch,
    int? ImageCount,
    Dictionary<string, double> Metrics,
    EvaluationClassMetricsDto[] ClassMetrics,
    JsonElement? Environment,
    CheckErrorDto? Error,
    long Revision);

public sealed record ModelEvaluationPageResponseDto(ModelEvaluationResponseDto[] Items, long Total);

/// <summary>Token — выбор (null — окно закрыто); путь в UI не попадает.</summary>
public sealed record CheckFileSelectionResponseDto(string? Token, string? Name, long? ByteSize);

/// <summary>Кадр выбранного видео: размер кадра видео и уменьшенного превью (рамка ставится в координатах кадра), Url — канал ресурсов.</summary>
public sealed record CheckVideoFrameResponseDto(long TimestampMs, int Width, int Height, int PreviewWidth, int PreviewHeight, string Url);

/// <summary>Выбранное видео: размер кадра, длительность, частота и кадр из середины (null у закрытого окна).</summary>
public sealed record CheckVideoSelectionResponseDto(string? Token, string? Name, long? ByteSize, int? Width, int? Height, long? DurationMs, double? Fps,
    CheckVideoFrameResponseDto? Frame);

/// <summary>Монитор в физических пикселях: «Монитор {Index}» ({Width} × {Height}), IsPrimary — основной.</summary>
public sealed record MonitorResponseDto(int Index, int Left, int Top, int Width, int Height, bool IsPrimary, string Name);

/// <summary>Снимок монитора (PNG по Url) и все мониторы на момент снимка.</summary>
public sealed record ScreenShotResponseDto(MonitorResponseDto Monitor, string Url, MonitorResponseDto[] Monitors);

/// <summary>Параметры, как они переданы worker: StepMs — у видео; Monitor, Fps, MaxDurationSec — у экрана.</summary>
public sealed record LiveCheckSettingsDto(double Confidence, int? MaxDetections, string Device, int ImageSize, CheckRegionDto? Region, int? StepMs, int? Monitor,
    double? Fps, int? MaxDurationSec);

/// <summary>Имя файла (null у экрана), размер кадра, длительность и частота видео, монитор.</summary>
public sealed record LiveCheckSourceDto(string? Name, int? Width, int? Height, long? DurationMs, double? Fps, int? Monitor);

/// <summary>
/// «m:ss.s из m:ss.s · проверено кадров X из ~Y» (видео), «Кадров N · X к/с · m:ss.s от начала» (экран); FirstFrameMs — от запуска
/// worker до первого кадра.
/// </summary>
public sealed record LiveCheckProgressDto(long FramesProcessed, long? FramesTotal, long? PositionMs, long? DurationMs, double? Fps, long ElapsedMs, long? FirstFrameMs);

/// <summary>Строка «Итог по классам»: Класс / Кадров / Рамок / Макс. уверенность.</summary>
public sealed record LiveCheckClassSummaryDto(int ClassIndex, Guid? ClassId, string Name, long Frames, long Detections, double MaxConfidence);

public sealed record LiveCheckSummaryDto(long FramesProcessed, long FramesWithDetections, long Detections, LiveCheckClassSummaryDto[] Classes);

/// <summary>Обнаружение: рамка в долях анализируемого кадра, подпись «имя NN%».</summary>
public sealed record LiveDetectionDto(int ClassIndex, Guid? ClassId, string Name, double Confidence, double X, double Y, double Width, double Height);

/// <summary>
/// Кадр: Ordinal — запись журнала (0 у последнего кадра без обнаружений), Index — номер кадра, TimeMs — его время; Detections в
/// событии — не больше 100 первых по уверенности, DetectionCount — все.
/// </summary>
public sealed record LiveCheckFrameDto(long Ordinal, long Index, long TimeMs, int Width, int Height, int InferenceMs, LiveDetectionDto[] Detections, int DetectionCount);

/// <summary>Последнее превью кадра (JPEG по Url); Number растёт с каждым новым превью.</summary>
public sealed record LiveCheckPreviewDto(long Number, long FrameIndex, long TimeMs, int Width, int Height, string Url);

/// <summary>Журнал: Total — кадров с обнаружениями всего, Kept — хранится, Truncated — журнал видео обрезан (5 000 первых).</summary>
public sealed record LiveCheckJournalInfoDto(long Total, int Kept, bool Truncated, long FirstOrdinal, long LastOrdinal);

public sealed record LiveCheckJournalPageResponseDto(LiveCheckFrameDto[] Items, LiveCheckJournalInfoDto Journal);

public sealed record LiveCheckWarningDto(string Code, string Message);

/// <summary>
/// Живая проверка: Mode — image | video | screen; State — preparing «Подготовка» | running «Идёт проверка» | cancelling «Остановка…» |
/// succeeded «Готово» | failed «Ошибка» | cancelled «Остановлено»; StopReason — user | project_switched | session_ended | time_limit;
/// LastFrame — «Сейчас на экране»; RecentFrames — последняя страница журнала (в событиях пусто); Sequence — применять снимок, только
/// если он больше известного.
/// </summary>
public sealed record LiveCheckResponseDto(
    Guid Id,
    string Mode,
    string State,
    Guid VersionId,
    int VersionNumber,
    LiveCheckSettingsDto Settings,
    LiveCheckSourceDto Source,
    LiveCheckProgressDto Progress,
    LiveCheckSummaryDto Summary,
    LiveCheckFrameDto? LastFrame,
    LiveCheckPreviewDto? Preview,
    LiveCheckJournalInfoDto Journal,
    LiveCheckFrameDto[] RecentFrames,
    LiveCheckWarningDto[] Warnings,
    CheckErrorDto? Error,
    string? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

public sealed record EvaluationChangeDto(Guid Id, long? Revision);

/// <summary>checks.changed: оценки с новой ревизией (перечитать getEvaluation) и/или снимок живой проверки (не чаще 5 раз в секунду).</summary>
public sealed record ChecksChangedEventDto(string ProjectId, EvaluationChangeDto[] Evaluations, LiveCheckResponseDto? Check);
