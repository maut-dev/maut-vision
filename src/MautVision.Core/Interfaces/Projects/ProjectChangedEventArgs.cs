namespace MautVision.Core.Interfaces.Projects;

public sealed class ProjectChangedEventArgs(IProject project) : EventArgs
{
    public IProject Project { get; } = project;
}
