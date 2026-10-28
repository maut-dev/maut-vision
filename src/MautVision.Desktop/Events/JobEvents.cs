using MautVision.Core.Interfaces.Jobs;
using MautVision.Desktop.Controllers.Jobs;

namespace MautVision.Desktop.Events;

/// <summary>Core throttles progress, so every snapshot is forwarded as job.changed.</summary>
internal sealed class JobEvents : IDisposable
{
    private readonly IJobsManager jobs;
    private readonly Action<string, object> publish;
    private int disposed;

    internal JobEvents(IJobsManager jobs, Action<string, object> publish)
    {
        this.jobs = jobs;
        this.publish = publish;
        jobs.JobChanged += OnJobChanged;
    }

    private void OnJobChanged(object? sender, JobChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) publish("job.changed", JobResponseMapper.Map(args.Job));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) jobs.JobChanged -= OnJobChanged;
    }
}
