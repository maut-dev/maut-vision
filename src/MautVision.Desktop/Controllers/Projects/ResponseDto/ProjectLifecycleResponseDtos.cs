using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.ResponseDto;

/// <summary>Библиотека: все проекты и последний открытый (null, если его нет или он удалён).</summary>
public sealed record ProjectLibraryResponseDto(ProjectResponseDto[] Items, string? LastProjectId);

/// <summary>Место сохранения экспорта; null — окно закрыто без выбора.</summary>
public sealed record ProjectExportSelectionResponseDto(string? SelectionToken, string? FileName);

/// <summary>Выбранный архив; null — окно закрыто без выбора.</summary>
public sealed record ProjectArchiveSelectionResponseDto(string? SelectionToken, string? FileName, long? ByteSize);

/// <summary>
/// Импорт: Status — running | succeeded | failed | cancelled; счётчики файлов и байтов распаковки (Total — 0 до проверки архива);
/// ProjectId и ProjectName — добавленный проект; Error — код и текст отказа. Sequence растёт с каждым снимком.
/// </summary>
public sealed record ProjectImportResponseDto(
    Guid Id,
    string OperationId,
    string FileName,
    string Status,
    bool CancelRequested,
    long ProcessedFiles,
    long TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? ProjectId,
    string? ProjectName,
    JobErrorDto? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

/// <summary>Идущая работа проекта: задача (Kind — её вид, JobId, Label) или живая проверка (Kind checks.live).</summary>
public sealed record ProjectActivityResponseDto(string Kind, Guid? JobId, string? Label);

/// <summary>Activities — что удаление отменит; TransferActive — идёт экспорт, удаление откажет.</summary>
public sealed record ProjectDeletionReviewResponseDto(string ProjectId, string Name, long Revision, ProjectActivityResponseDto[] Activities, bool TransferActive);

/// <summary>CleanupPending — часть файлов занята, очистка повторится при следующем запуске.</summary>
public sealed record ProjectDeletionResponseDto(string ProjectId, bool CleanupPending);

/// <summary>Событие project.removed.</summary>
public sealed record ProjectRemovedEventDto(string ProjectId);
