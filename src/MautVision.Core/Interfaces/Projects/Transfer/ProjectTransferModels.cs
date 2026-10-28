using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Transfer;

/// <summary>DestinationPath — выбранный пользователем .zip; ProjectName — подпись задачи.</summary>
public sealed record ProjectExportInput(string ProjectName, string DestinationPath);

/// <summary>
/// Экспорт проекта в один ZIP (project.json, снимок project.db, files/**): задача CPU, не возобновляемая, с прогрессом по файлам
/// (Processed/Total) и байтам (итог). Пока она в очереди или идёт, другие задачи и живые проверки проекта не запускаются
/// (<c>projects.transfer_active</c>); ставится, только когда в проекте нет других активных задач и живой проверки
/// (<c>projects.transfer_busy</c>, Details["section"] — область первой идущей задачи). Архив пишется во временный файл рядом с
/// целью и заменяет её в конце; отмена и ошибка удаляют временный файл. Итог — <see cref="ProjectExportResult"/>.
/// </summary>
[JobKind("projects.export")]
public interface IProjectExportJob : IJobDefinition<ProjectExportInput>
{
}

/// <summary>
/// Итог экспорта (и после отмены или ошибки — сколько успели): Files — записанные файлы, Links — пути жёстких ссылок, записанные в
/// manifest без повторения содержимого, Bytes — объём содержимого, ArchiveBytes — размер архива.
/// </summary>
public sealed record ProjectExportResult(string FileName, long Files, long TotalFiles, long Bytes, long TotalBytes, long Links, long ArchiveBytes,
    long ElapsedMs);

/// <summary>Архив, выбранный пользователем: путь, размер и время изменения на момент выбора (изменённый после выбора файл не читается).</summary>
public sealed record ProjectArchiveSource(string Path, long Length, DateTimeOffset LastWriteTimeUtc);

public enum ProjectImportStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Снимок импорта. Sequence растёт с каждым изменением: из двух снимков новее тот, у кого он больше. Счётчики Processed/Total —
/// файлы и байты распаковки (Total известен после проверки архива, до неё 0). При успехе ProjectId и ProjectName — проект в
/// библиотеке (новый id при совпадении, имя с суффиксом « (импорт)» при совпадении имени); ErrorCode — код отказа при Failed.
/// Импорт живёт в памяти процесса и не переживает перезапуск.
/// </summary>
public sealed record ProjectImportInfo(
    Guid Id,
    string OperationId,
    string FileName,
    ProjectImportStatus Status,
    bool CancelRequested,
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? ProjectId,
    string? ProjectName,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

public sealed class ProjectImportChangedEventArgs(ProjectImportInfo import) : EventArgs
{
    public ProjectImportInfo Import { get; } = import;
}
