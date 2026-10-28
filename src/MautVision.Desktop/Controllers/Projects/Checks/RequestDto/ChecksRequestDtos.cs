namespace MautVision.Desktop.Controllers.Projects.Checks.RequestDto;

/// <summary>
/// «Проверить на датасете»: версия, версия датасета, Split — val («Validation») | test («Test»), Device — «cpu» или номер видеокарты,
/// Batch 1–1024 (по умолчанию 16); OperationId — новый на действие (повтор вернёт ту же оценку).
/// </summary>
public sealed record StartModelEvaluationRequestDto(string ProjectId, Guid VersionId, Guid DatasetVersionId, string Split, string Device, int? Batch,
    string OperationId);

/// <summary>«Отменить»: идемпотентно; оценка в очереди отменяется сразу, идущая — после остановки worker.</summary>
public sealed record CancelModelEvaluationRequestDto(string ProjectId, Guid EvaluationId);

public sealed record GetModelEvaluationRequestDto(string ProjectId, Guid EvaluationId);

/// <summary>«Все проверки», новые первыми; фильтры по версии, датасету и части (val | test); Limit 1–200 (по умолчанию 50).</summary>
public sealed record ListModelEvaluationsRequestDto(string ProjectId, Guid? VersionId = null, Guid? DatasetVersionId = null, string? Split = null, int? Limit = null,
    Guid? Before = null);

/// <summary>Матрица сравнения: последняя успешная оценка каждой версии на паре «датасет · часть» (null — все пары).</summary>
public sealed record ListLatestModelEvaluationsRequestDto(string ProjectId, Guid? DatasetVersionId = null, string? Split = null);

/// <summary>«Выбрать изображение и проверить…»: одно изображение PNG, JPEG, WebP или BMP; путь остаётся в host.</summary>
public sealed record SelectCheckImageRequestDto(string ProjectId);

/// <summary>Выбор видео (MP4, MKV, AVI, MOV, WebM, M4V); ответ несёт кадр из середины для рамки.</summary>
public sealed record SelectCheckVideoRequestDto(string ProjectId);

/// <summary>«Показать другой кадр»: кадр выбранного видео в TimestampMs, уменьшенный до MaxSide (по умолчанию 1600).</summary>
public sealed record PreviewCheckVideoFrameRequestDto(string ProjectId, string SelectionToken, long TimestampMs, int? MaxSide = null);

/// <summary>Мониторы в физических пикселях; Index — номер для проверки с экрана.</summary>
public sealed record ListMonitorsRequestDto;

/// <summary>«Снять экран»: PNG монитора для рамки (канал ресурсов).</summary>
public sealed record CaptureScreenRequestDto(string ProjectId, int Monitor);

/// <summary>Общие поля: Confidence — «Порог уверенности» (null — 0.25), MaxDetections — null «Автоматически», Device — «cpu» или номер видеокарты.</summary>
public sealed record CheckParametersDto(double? Confidence, int? MaxDetections, string Device);

/// <summary>Рамка в пикселях кадра (у экрана — от угла монитора), размером со вход версии.</summary>
public sealed record CheckRegionDto(int X, int Y, int Width, int Height);

/// <summary>Проверка по изображению: SelectionToken — из selectImage; OperationId — новый на действие (повтор вернёт ту же проверку).</summary>
public sealed record StartImageCheckRequestDto(string ProjectId, Guid VersionId, string SelectionToken, CheckParametersDto Parameters, string OperationId);

/// <summary>Проверка по видео: StepMs — «Шаг» (null — 1000 мс), Region — null, если видео не больше входа.</summary>
public sealed record StartVideoCheckRequestDto(string ProjectId, Guid VersionId, string SelectionToken, CheckParametersDto Parameters, int? StepMs,
    CheckRegionDto? Region, string OperationId);

/// <summary>Проверка с экрана: Fps — «Кадров в секунду» (null — 3), MaxDurationSec — «Остановить через» (null — 30 мин), Region — null, если экран не больше входа.</summary>
public sealed record StartScreenCheckRequestDto(string ProjectId, Guid VersionId, int Monitor, CheckParametersDto Parameters, double? Fps, int? MaxDurationSec,
    CheckRegionDto? Region, string OperationId);

/// <summary>«Остановить»: идемпотентно.</summary>
public sealed record StopCheckRequestDto(string ProjectId, Guid CheckId);

/// <summary>Снимок проверки с последней страницей журнала (JournalLimit 0–200, по умолчанию 40).</summary>
public sealed record GetCheckRequestDto(string ProjectId, Guid CheckId, int? JournalLimit = null);

/// <summary>Живые проверки проекта в памяти: идущая и последние завершённые.</summary>
public sealed record ListChecksRequestDto(string ProjectId);

/// <summary>Журнал кадров с обнаружениями: After — дочитать новые после Ordinal, Before — листать назад; Limit 1–200 (по умолчанию 100).</summary>
public sealed record ListCheckJournalRequestDto(string ProjectId, Guid CheckId, long? After = null, long? Before = null, int? Limit = null);
