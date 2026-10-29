using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Controllers.Projects.ResponseDto;

namespace MautVision.Desktop.Events;

/// <summary>
/// project.opened, project.changed (created, imported, changed), project.removed {projectId} and projectImport.changed (import
/// snapshots, applied by Sequence).
/// </summary>
internal sealed class ProjectEvents : IDisposable
{
    private readonly IProjectsManager projects;
    private readonly Action<string, object> publish;
    private int disposed;

    internal ProjectEvents(IProjectsManager projects, Action<string, object> publish)
    {
        this.projects = projects;
        this.publish = publish;
        projects.ProjectOpened += OnProjectOpened;
        projects.ProjectChanged += OnProjectChanged;
        projects.ProjectRemoved += OnProjectRemoved;
        projects.ImportChanged += OnImportChanged;
    }

    private void OnProjectOpened(object? sender, ProjectOpenedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("project.opened", ProjectResponseMapper.Map(args.Project));
    }

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("project.changed", ProjectResponseMapper.Map(args.Project));
    }

    private void OnProjectRemoved(object? sender, ProjectRemovedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("project.removed", new ProjectRemovedEventDto(args.ProjectId));
    }

    private void OnImportChanged(object? sender, ProjectImportChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("projectImport.changed", ProjectResponseMapper.Map(args.Import));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        projects.ProjectOpened -= OnProjectOpened;
        projects.ProjectChanged -= OnProjectChanged;
        projects.ProjectRemoved -= OnProjectRemoved;
        projects.ImportChanged -= OnImportChanged;
    }
}
