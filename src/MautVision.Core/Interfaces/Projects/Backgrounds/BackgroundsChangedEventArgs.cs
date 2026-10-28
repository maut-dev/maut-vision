namespace MautVision.Core.Interfaces.Projects.Backgrounds;

/// <summary>Изменённый или удалённый (Revision = null) объект: набор или фон.</summary>
public sealed record BackgroundsEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в фонах проекта. Sets — наборы с новыми ревизиями или удалённые; Items — фоны, изменённые явной операцией
/// («Убрать», «Восстановить», удаление выбранных); ItemsChangedIn — наборы, у которых изменился состав или счётчики (подготовка,
/// «Убрать», удаление): UI перечитывает их набор и страницы. Подготовка сообщает об изменениях не чаще раза в 500 мс на набор.
/// </summary>
public sealed class BackgroundsChangedEventArgs(
    string projectId,
    IReadOnlyList<BackgroundsEntityChange> sets,
    IReadOnlyList<BackgroundsEntityChange> items,
    IReadOnlyList<Guid> itemsChangedIn) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<BackgroundsEntityChange> Sets { get; } = sets;
    public IReadOnlyList<BackgroundsEntityChange> Items { get; } = items;
    public IReadOnlyList<Guid> ItemsChangedIn { get; } = itemsChangedIn;
}
