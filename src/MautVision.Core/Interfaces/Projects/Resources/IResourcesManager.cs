namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// Файлы материалов проекта для показа в UI. Пути строятся только из вида и ID материала; миниатюры создаются
/// при первом запросе и кешируются в папке проекта. Кеш можно удалить в любой момент.
/// </summary>
public interface IResourcesManager
{
    /// <summary>
    /// Открывает файл ресурса; открывает хранилище проекта, если оно ещё не открыто. Ошибки: <c>resources.kind_unknown</c>,
    /// <c>resources.not_found</c>, <c>resources.thumbnail_unsupported</c> (миниатюра не изображения), <c>resources.decode_failed</c>.
    /// Отмена прекращает ожидание; начатая генерация миниатюры завершается и остаётся в кеше.
    /// </summary>
    Task<ResourceContent> OpenAsync(ResourceReference reference, ResourceVariant variant, CancellationToken cancellationToken = default);
}
