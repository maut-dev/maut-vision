using System.Text.Json;

namespace MautVision.Core.Interfaces.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>Выполнение оборвалось вместе с процессом, а вид задачи не умеет продолжать.</summary>
    Interrupted,

    /// <summary>Эта версия приложения не может выполнить задачу (неизвестный вид, несовместимые данные); данные сохранены.</summary>
    Blocked,
}

/// <summary>Code — стабильный код ошибки; Message — текст для журнала, пользовательский текст выбирает Desktop по коду.</summary>
public sealed record JobError(string Code, string Message);

/// <summary>
/// Снимок задачи. Sequence увеличивается при каждом сохранённом изменении: из двух снимков новее тот,
/// у кого Sequence больше. Progress — 0–100 или null, если объём работы неизвестен.
/// Result — итог задачи в JSON, который задаёт её вид (например, <see cref="ImportClassAssetsResult"/>); null, пока его нет.
/// Label — подпись, которую задал постановщик (имя группы, датасета, номер версии), до <see cref="MaxLabelLength"/> символов;
/// null у задач без подписи и у задач, поставленных до её появления.
/// </summary>
public sealed record JobInfo(
    Guid Id,
    string Kind,
    string ProjectId,
    string OperationId,
    JobStatus Status,
    double? Progress,
    string? ProgressText,
    long? Processed,
    long? Total,
    JobError? Error,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence,
    JsonElement? Result = null,
    string? Label = null)
{
    public const int MaxLabelLength = 200;
}
