namespace MautVision.Core.Interfaces.BaseModels;

public interface IBaseModelsManager
{
    /// <summary>
    /// Reports a change of Status, Progress or ErrorCode of a model. Installation progress
    /// is throttled; the event may arrive from a background thread.
    /// </summary>
    event EventHandler<BaseModelChangedEventArgs>? ModelChanged;

    Task<IReadOnlyList<IBaseModel>> ListAsync(CancellationToken cancellationToken = default);
    Task<IBaseModel?> GetOrNullAsync(string id, CancellationToken cancellationToken = default);
}
