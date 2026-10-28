using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Desktop.Controllers.Projects.Generation;

namespace MautVision.Desktop.Events;

/// <summary>Forwards generation changes as generation.changed: which recipes and runs changed, never the lists themselves.</summary>
internal sealed class GenerationEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal GenerationEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.GenerationChanged += OnGenerationChanged;
    }

    private void OnGenerationChanged(object? sender, GenerationChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("generation.changed", GenerationResponseMapper.Map(args));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) projects.GenerationChanged -= OnGenerationChanged;
    }
}
