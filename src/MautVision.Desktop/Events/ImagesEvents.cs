using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.Images;

namespace MautVision.Desktop.Events;

/// <summary>Forwards image changes as images.changed: what changed and the new revisions, never the lists themselves.</summary>
internal sealed class ImagesEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal ImagesEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.ImagesChanged += OnImagesChanged;
    }

    private void OnImagesChanged(object? sender, ImagesChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("images.changed", ImagesResponseMapper.Map(args));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) projects.ImagesChanged -= OnImagesChanged;
    }
}
