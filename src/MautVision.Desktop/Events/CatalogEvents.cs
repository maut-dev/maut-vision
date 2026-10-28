using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.Catalog;

namespace MautVision.Desktop.Events;

/// <summary>Forwards catalog changes as catalog.changed: what changed and the new revisions, never the lists themselves.</summary>
internal sealed class CatalogEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal CatalogEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.CatalogChanged += OnCatalogChanged;
    }

    private void OnCatalogChanged(object? sender, CatalogChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("catalog.changed", CatalogResponseMapper.Map(args));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) projects.CatalogChanged -= OnCatalogChanged;
    }
}
