namespace MautVision.Core.Interfaces.BaseModels;

public interface IBaseModelsManager
{
    /// <summary>
    /// Сообщает об изменении Status, Progress или ErrorCode модели. Прогресс установки
    /// прореживается; событие может прийти из фонового потока.
    /// </summary>
    event EventHandler<BaseModelChangedEventArgs>? ModelChanged;

    Task<IReadOnlyList<IBaseModel>> ListAsync(CancellationToken cancellationToken = default);
    Task<IBaseModel?> GetOrNullAsync(string id, CancellationToken cancellationToken = default);
}
