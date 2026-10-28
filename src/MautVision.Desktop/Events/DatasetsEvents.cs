using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Desktop.Controllers.Projects.Datasets;

namespace MautVision.Desktop.Events;

/// <summary>Forwards dataset changes as datasets.changed: which versions changed, never the lists themselves.</summary>
internal sealed class DatasetsEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal DatasetsEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.DatasetsChanged += OnDatasetsChanged;
    }

    private void OnDatasetsChanged(object? sender, DatasetsChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("datasets.changed", DatasetsResponseMapper.Map(args));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) projects.DatasetsChanged -= OnDatasetsChanged;
    }
}
