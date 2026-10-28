namespace MautVision.Core.Interfaces.BaseModels;

public sealed class BaseModelChangedEventArgs(IBaseModel model) : EventArgs
{
    public IBaseModel Model { get; } = model;
}
