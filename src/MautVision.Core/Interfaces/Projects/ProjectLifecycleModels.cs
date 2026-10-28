namespace MautVision.Core.Interfaces.Projects;

/// <summary>
/// Что сейчас работает в проекте: активная задача (Kind — её вид, JobId, Label — подпись) или живая проверка модели
/// (Kind <see cref="LiveCheckKind"/>, JobId null).
/// </summary>
public sealed record ProjectActivity(string Kind, Guid? JobId, string? Label)
{
    public const string LiveCheckKind = "checks.live";
}

/// <summary>
/// Подтверждение удаления: Revision — ревизия, которую видит пользователь (передаётся в DeleteAsync); Activities — идущие задачи и
/// живая проверка, которые удаление отменит; TransferActive — идёт экспорт проекта, удаление сейчас откажет.
/// </summary>
public sealed record ProjectDeletionReview(string ProjectId, string Name, long Revision, IReadOnlyList<ProjectActivity> Activities, bool TransferActive);

/// <summary>CleanupPending — проект удалён, но часть его файлов занята; очистка повторится при следующем запуске.</summary>
public sealed record ProjectDeletionResult(string ProjectId, bool CleanupPending);

public sealed class ProjectRemovedEventArgs(string projectId) : EventArgs
{
    public string ProjectId { get; } = projectId;
}
