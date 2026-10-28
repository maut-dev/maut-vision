using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.Backgrounds;

namespace MautVision.Desktop.Events;

/// <summary>Forwards background changes as backgrounds.changed: what changed and the new revisions, never the lists themselves.</summary>
internal sealed class BackgroundsEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal BackgroundsEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.BackgroundsChanged += OnBackgroundsChanged;
    }

    private void OnBackgroundsChanged(object? sender, BackgroundsChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("backgrounds.changed", BackgroundsResponseMapper.Map(args));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) projects.BackgroundsChanged -= OnBackgroundsChanged;
    }
}
