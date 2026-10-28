namespace MautVision.Core.Interfaces.Projects;

public sealed class ProjectOpenedEventArgs(IProject project) : EventArgs
{
    public IProject Project { get; } = project;
}
