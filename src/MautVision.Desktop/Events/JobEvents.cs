using MautVision.Core.Interfaces.Jobs;
using MautVision.Desktop.Controllers.Jobs;

namespace MautVision.Desktop.Events;

/// <summary>Core throttles progress, so every snapshot is forwarded as job.changed.</summary>
internal sealed class JobEvents : IDisposable
{
    private readonly IJobsManager jobs;
    private readonly Action<string, object> publish;
    private readonly Func<string, string> messageFor;
    private int disposed;

    internal JobEvents(IJobsManager jobs, Action<string, object> publish, Func<string, string> messageFor)
    {
        this.jobs = jobs;
        this.publish = publish;
        this.messageFor = messageFor;
        jobs.JobChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("job.changed", JobResponseMapper.Map(args.Job, messageFor));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) jobs.JobChanged -= OnJobChanged;
    }
}
