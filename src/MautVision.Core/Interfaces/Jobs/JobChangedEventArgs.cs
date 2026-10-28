namespace MautVision.Core.Interfaces.Jobs;

public sealed class JobChangedEventArgs(JobInfo job) : EventArgs
{
    public JobInfo Job { get; } = job;
}
