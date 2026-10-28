namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>Изменённый или удалённый (Revision = null) объект: группа или изображение.</summary>
public sealed record ImagesEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в изображениях проекта. Groups — группы с новыми ревизиями или удалённые; Images — изображения,
/// изменённые явной операцией (разметка, архив, перемещение, удаление выбранных); ImagesChangedIn — группы, у которых
/// изменился состав или счётчики (импорт, перемещение, архив, удаление): UI перечитывает их группу и страницы.
/// Импорт сообщает об изменениях не чаще раза в 500 мс на группу.
/// </summary>
public sealed class ImagesChangedEventArgs(
    string projectId,
    IReadOnlyList<ImagesEntityChange> groups,
    IReadOnlyList<ImagesEntityChange> images,
    IReadOnlyList<Guid> imagesChangedIn) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<ImagesEntityChange> Groups { get; } = groups;
    public IReadOnlyList<ImagesEntityChange> Images { get; } = images;
    public IReadOnlyList<Guid> ImagesChangedIn { get; } = imagesChangedIn;
}
