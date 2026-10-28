namespace MautVision.Core.Jobs;

/// <summary>Resource a job waits for in the queue: GPU jobs run one at a time, CPU jobs up to the CPU slot count.</summary>
internal enum JobResource
{
    Cpu,
    Gpu,
}

/// <summary>Algorithm of one job kind. Handlers are internal and registered inside Core; consumers enqueue by contract.</summary>
internal interface IJobHandler<TInput>
{
    JobResource Resource => JobResource.Cpu;

    /// <summary>
    /// True: after the process died the job returns to the queue and continues from its checkpoint and step receipts.
    /// False: such a job becomes Interrupted and the user decides whether to start it again.
    /// </summary>
    bool Resumable => false;

    Task ExecuteAsync(TInput input, JobContext context);
}

/// <summary>Kind data bound at registration; Execute deserializes the stored input for the handler.</summary>
internal sealed record JobRegistration(string Kind, JobResource Resource, bool Resumable, Func<string, JobContext, Task> Execute);

/// <summary>Stored input or checkpoint cannot be read by this version; the job becomes Blocked and keeps its data.</summary>
internal sealed class JobDataException(string code, string message, Exception innerException)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
