namespace MautVision.Core.Interfaces.Jobs;

/// <summary>Активная задача: WaitingForGpu — задача стоит в очереди за видеокартой (обучение, проверка модели).</summary>
public sealed record ActiveJobEntry(JobInfo Job, bool WaitingForGpu);

/// <summary>
/// Страница активных задач в порядке очереди. Total — все активные задачи проекта, CountsByKind — их число по видам;
/// NextCursor — ID последней задачи страницы, если за ней есть ещё, иначе null.
/// </summary>
public sealed record ActiveJobsPage(IReadOnlyList<ActiveJobEntry> Items, int Total, IReadOnlyDictionary<string, int> CountsByKind, Guid? NextCursor);
