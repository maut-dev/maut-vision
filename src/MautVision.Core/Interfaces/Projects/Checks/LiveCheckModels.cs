using System.Text.Json.Serialization;

namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>Источник живой проверки: изображение, видео или экран.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckMode>))]
public enum LiveCheckMode
{
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("video")] Video,
    [JsonStringEnumMemberName("screen")] Screen,
}

/// <summary>
/// Состояние живой проверки: Preparing «Подготовка», Running «Идёт проверка», Cancelling «Остановка…», Succeeded «Готово»,
/// Failed «Ошибка», Cancelled «Остановлено». Живая проверка не ждёт видеокарту: занятая видеокарта — сразу отказ.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckState>))]
public enum LiveCheckState
{
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>
/// Почему проверка остановлена: User — «Остановить»; ProjectSwitched — открыт другой проект; SessionEnded — страница UI
/// перезагружена или окно закрыто; TimeLimit — предел «Остановить через, мин» (проверка экрана завершается «Готово» с
/// предупреждением).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckStopReason>))]
public enum LiveCheckStopReason
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("project_switched")] ProjectSwitched,
    [JsonStringEnumMemberName("session_ended")] SessionEnded,
    [JsonStringEnumMemberName("time_limit")] TimeLimit,
}

/// <summary>Рамка в пикселях кадра (у экрана — от левого верхнего угла монитора); размер равен входу версии.</summary>
public sealed record CheckRegion(int X, int Y, int Width, int Height);

/// <summary>
/// Файл-источник, выбранный в нативном окне: абсолютный путь, размер и время изменения на момент выбора (изменённый после выбора
/// файл — <c>checks.source_changed</c>); у видео — размер кадра, длительность и частота по ffprobe.
/// </summary>
public sealed record CheckSourceFile(string Path, long Length, DateTimeOffset LastWriteTimeUtc, int? Width = null, int? Height = null, long? DurationMs = null,
    double? Fps = null);

/// <summary>
/// Общие поля: «Порог уверенности (0–1)» (по умолчанию 0.25); «Обнаружения в кадре» (null — все кандидаты модели, иначе ≥ 1);
/// «Устройство» — «cpu» или номер видеокарты из списка обучения. IoU 0.7 и imgsz берутся из версии.
/// </summary>
public sealed record LiveCheckParameters(double Confidence, int? MaxDetections, string Device);

/// <summary>«Выбрать изображение и проверить…»: изображение подаётся целиком (letterbox).</summary>
public sealed record ImageCheckRequest(Guid VersionId, CheckSourceFile Source, LiveCheckParameters Parameters);

/// <summary>
/// Проверка по видео: StepMs — «Шаг» (≥ 1 мс, по умолчанию 1000), кадры читаются подряд; Region — рамка размера входа в кадре
/// или null, если видео не больше входа (кадр целиком).
/// </summary>
public sealed record VideoCheckRequest(Guid VersionId, CheckSourceFile Source, LiveCheckParameters Parameters, int StepMs, CheckRegion? Region);

/// <summary>
/// Проверка с экрана: Monitor — номер из <c>IScreenManager.ListMonitors</c>; Fps — «Кадров в секунду» (0 &lt; fps ≤ 60, по
/// умолчанию 3); MaxDurationSec — «Остановить через» (1 с – 24 ч, по умолчанию 30 мин); Region — рамка размера входа внутри
/// монитора или null, если экран не больше входа.
/// </summary>
public sealed record ScreenCheckRequest(Guid VersionId, int Monitor, LiveCheckParameters Parameters, double Fps, int MaxDurationSec, CheckRegion? Region);

/// <summary>Обнаружение: класс (выход модели и класс проекта), уверенность и рамка в долях анализируемого кадра (вырез рамки, если задан).</summary>
public sealed record LiveDetection(int ClassIndex, Guid? ClassId, string Name, double Confidence, double X, double Y, double Width, double Height);

/// <summary>
/// Кадр проверки: Ordinal — номер записи журнала (растёт), Index — номер кадра видео (порядковый у экрана, 0 у изображения),
/// TimeMs — время кадра (от начала видео или проверки экрана), Width × Height — анализируемый кадр, InferenceMs — время прогона.
/// </summary>
public sealed record LiveCheckFrame(long Ordinal, long Index, long TimeMs, int Width, int Height, int InferenceMs, IReadOnlyList<LiveDetection> Detections);

/// <summary>«Итог по классам»: в скольких кадрах встретился класс, сколько рамок, наибольшая уверенность.</summary>
public sealed record LiveCheckClassSummary(int ClassIndex, Guid? ClassId, string Name, long Frames, long Detections, double MaxConfidence);

/// <summary>Сводка по всем проверенным кадрам (у видео — по всему видео, независимо от журнала).</summary>
public sealed record LiveCheckSummary(long FramesProcessed, long FramesWithDetections, long Detections, IReadOnlyList<LiveCheckClassSummary> Classes);

/// <summary>
/// Ход: FramesProcessed из FramesTotal (у видео — оценка по шагу), PositionMs и DurationMs — позиция в видео, Fps — фактическая
/// частота кадров экрана по последним кадрам, ElapsedMs — от начала проверки, FirstFrameMs — от запуска worker до первого кадра.
/// </summary>
public sealed record LiveCheckProgress(long FramesProcessed, long? FramesTotal, long? PositionMs, long? DurationMs, double? Fps, long ElapsedMs, long? FirstFrameMs);

/// <summary>Что проверяется: имя файла (null у экрана), размер кадра источника (у экрана — захватываемой области), длительность и частота видео, монитор.</summary>
public sealed record LiveCheckSource(string? Name, int? Width, int? Height, long? DurationMs, double? Fps, int? Monitor);

/// <summary>Последнее превью: Number растёт с каждым новым превью; байты — <see cref="IChecksManager.GetPreview"/>.</summary>
public sealed record LiveCheckPreviewInfo(long Number, long FrameIndex, long TimeMs, int Width, int Height);

/// <summary>
/// Журнал «кадры с обнаружениями»: Total — сколько таких кадров было, Kept — сколько хранится (экран — кольцо последних 2 000, видео —
/// первые 5 000), Truncated — журнал видео обрезан, FirstOrdinal и LastOrdinal — границы хранимых записей (0, если пусто).
/// </summary>
public sealed record LiveCheckJournalInfo(long Total, int Kept, bool Truncated, long FirstOrdinal, long LastOrdinal);

/// <summary>Страница журнала по возрастанию Ordinal.</summary>
public sealed record LiveCheckJournalPage(IReadOnlyList<LiveCheckFrame> Items, LiveCheckJournalInfo Journal);

public sealed record LiveCheckWarning(string Code, string Message);

/// <summary>Ошибка проверки: для checks.worker_* — сообщение самого worker, иначе текст приложения по коду.</summary>
public sealed record LiveCheckError(string Code, string Message);

/// <summary>Параметры проверки, как они переданы worker: StepMs — у видео, Monitor, Fps, MaxDurationSec — у экрана.</summary>
public sealed record LiveCheckSettings(double Confidence, int? MaxDetections, string Device, int ImageSize, CheckRegion? Region, int? StepMs, int? Monitor,
    double? Fps, int? MaxDurationSec);

/// <summary>
/// Снимок живой проверки: LastFrame — «Сейчас на экране» (последний кадр, в том числе пустой); RecentFrames — последняя страница
/// журнала (только у <see cref="IChecksManager.GetCheckAsync"/>, в событиях пусто); Sequence растёт с каждым изменением — из двух
/// снимков новее тот, у кого он больше.
/// </summary>
public sealed record LiveCheck(
    Guid Id,
    LiveCheckMode Mode,
    LiveCheckState State,
    Guid VersionId,
    int VersionNumber,
    LiveCheckSettings Settings,
    LiveCheckSource Source,
    LiveCheckProgress Progress,
    LiveCheckSummary Summary,
    LiveCheckFrame? LastFrame,
    LiveCheckPreviewInfo? Preview,
    LiveCheckJournalInfo Journal,
    IReadOnlyList<LiveCheckFrame> RecentFrames,
    IReadOnlyList<LiveCheckWarning> Warnings,
    LiveCheckError? Error,
    LiveCheckStopReason? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

/// <summary>Байты последнего превью (JPEG) и его описание.</summary>
public sealed record LiveCheckPreview(LiveCheckPreviewInfo Info, byte[] Content, string ContentType);

/// <summary>Изменённая или удалённая (Revision = null) оценка.</summary>
public sealed record EvaluationChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в проверках проекта: Evaluations — оценки с новой ревизией (перечитать); Check — снимок живой проверки (без
/// журнала), не чаще 5 раз в секунду, переходы состояний — сразу; Preview — байты последнего превью проверки, чтобы host выдал
/// для него URL канала ресурсов (в UI байты не передаются). Состояние задач оценки — JobChanged.
/// </summary>
public sealed class ChecksChangedEventArgs(string projectId, IReadOnlyList<EvaluationChange> evaluations, LiveCheck? check, LiveCheckPreview? preview = null)
    : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<EvaluationChange> Evaluations { get; } = evaluations;
    public LiveCheck? Check { get; } = check;
    public LiveCheckPreview? Preview { get; } = preview;
}
