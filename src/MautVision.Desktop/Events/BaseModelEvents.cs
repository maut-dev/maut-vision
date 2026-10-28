using MautVision.Core.Interfaces.BaseModels;
using MautVision.Desktop.Controllers.BaseModels;

namespace MautVision.Desktop.Events;

internal sealed class BaseModelEvents : IDisposable
{
    private readonly IBaseModelsManager baseModels;
    private readonly Action<string, object> publish;
    private int disposed;

    internal BaseModelEvents(IBaseModelsManager baseModels, Action<string, object> publish)
    {
        this.baseModels = baseModels;
        this.publish = publish;
        baseModels.ModelChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, BaseModelChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("baseModel.changed", BaseModelResponseMapper.Map(args.Model));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) baseModels.ModelChanged -= OnModelChanged;
    }
}
