using System.Diagnostics;
using System.Text.Json;
using MautVision.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Jobs;

/// <summary>Progress fields of one report; null means unknown.</summary>
internal sealed record JobProgress(double? Percent, CodedText? Message, long? Processed, long? Total);

/// <summary>
/// What a handler gets for one run: cancellation, progress, checkpoint and step receipts of its job.
/// Progress is a notification: reports are coalesced and saved at most every <see cref="JobsManager.ProgressInterval"/>.
/// A checkpoint and a step receipt are durable: they survive a restart of a resumable job.
/// </summary>
internal sealed class JobContext
{
    internal const int MaxProgressTextLength = 200;
    internal const int MaxProgressArgLength = 200;
    internal const int MaxStepKeyLength = 200;

    private readonly JobsManager owner;
    private readonly ActiveJob job;
    private readonly object gate = new();
    private string? checkpoint;
    private JobProgress? pendingProgress;
    private bool flushScheduled;
    private long lastFlush;
    private bool closed;
    private JsonElement? result;

    internal JobContext(JobsManager owner, ActiveJob job, ILogger logger)
    {
        this.owner = owner;
        this.job = job;
        checkpoint = job.Checkpoint;
        Logger = logger;
    }

    public Guid JobId => job.Id;
    public string ProjectId => job.ProjectId;
    public string OperationId => job.Info.OperationId;

    /// <summary>Cancelled when the user cancels the job; OperationCanceledException then makes it Cancelled.</summary>
    public CancellationToken CancellationToken => job.Cancellation.Token;

    public ILogger Logger { get; }

    /// <summary>
    /// Never blocks on storage; the latest report wins when reports come faster than they are saved. message — what the job is doing as a
    /// code of the UI catalog progress with values (<c>CodedText.Of("progress.epoch", ("epoch", 3), ("total", 10))</c>), never a text.
    /// </summary>
    public void ReportProgress(double? percent, CodedText? message = null, long? processed = null, long? total = null)
    {
        if (percent is { } value && (double.IsNaN(value) || value < 0 || value > 100))
            throw new ArgumentOutOfRangeException(nameof(percent), "Progress must be between 0 and 100.");
        if (processed < 0 || total < 0)
            throw new ArgumentOutOfRangeException(nameof(processed), "Counters must not be negative.");
        if (message?.Args is { } args && args.Values.Any(value => value.Length > MaxProgressArgLength))
            message = message with { Args = args.ToDictionary(pair => pair.Key, pair => pair.Value.Length > MaxProgressArgLength ? pair.Value[..MaxProgressArgLength] : pair.Value) };
        if (message?.Text is { Length: > MaxProgressTextLength } text) message = message with { Text = text[..MaxProgressTextLength] };
        TimeSpan delay;
        lock (gate)
        {
            if (closed) return;
            pendingProgress = new JobProgress(percent, message, processed, total);
            if (flushScheduled) return;
            flushScheduled = true;
            delay = lastFlush == 0 ? TimeSpan.Zero : JobsManager.ProgressInterval - Stopwatch.GetElapsedTime(lastFlush);
        }
        _ = FlushAsync(delay);
    }

    /// <summary>The checkpoint saved by an earlier run or this one; default when there is none.</summary>
    public T? Checkpoint<T>()
    {
        string? json;
        lock (gate) json = checkpoint;
        if (json is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, JobsManager.Json);
        }
        catch (JsonException error)
        {
            throw new JobDataException("jobs.checkpoint_invalid", $"Job {JobId} checkpoint cannot be read as {typeof(T).Name}.", error);
        }
    }

    /// <summary>Replaces the checkpoint durably. It holds logical positions and IDs, never large data.</summary>
    public async Task SaveCheckpointAsync<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JobsManager.Json);
        await job.Store.WriteAsync((connection, transaction, token) =>
            JobsTable.SaveCheckpointAsync(connection, transaction, job.Id, json, token), CancellationToken.None).ConfigureAwait(false);
        lock (gate) checkpoint = json;
    }

    /// <summary>
    /// Runs the step unless its receipt exists, then writes the receipt; returns whether the step ran.
    /// A crash between the action and the receipt repeats the step, so the action must be safe to repeat.
    /// </summary>
    public async Task<bool> RunStepAsync(string stepKey, Func<CancellationToken, Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stepKey.Length, MaxStepKeyLength, nameof(stepKey));
        ArgumentNullException.ThrowIfNull(action);
        var done = await job.Store.ReadAsync((connection, token) => JobsTable.HasStepAsync(connection, job.Id, stepKey, token),
            CancellationToken).ConfigureAwait(false);
        if (done) return false;
        CancellationToken.ThrowIfCancellationRequested();
        await action(CancellationToken).ConfigureAwait(false);
        await job.Store.WriteAsync((connection, transaction, token) =>
            JobsTable.AddStepAsync(connection, transaction, job.Id, stepKey, token), CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Sets the summary saved with the final state of this run, whether it succeeds, fails or is cancelled.
    /// A small JSON value: counts and short lists, never large data.
    /// </summary>
    public void SetResult<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, JobsManager.Json);
        lock (gate) result = element;
    }

    internal JsonElement? Result
    {
        get
        {
            lock (gate) return result;
        }
    }

    /// <summary>Stops further progress saves and returns the report that was not saved yet.</summary>
    internal JobProgress? Close()
    {
        lock (gate)
        {
            closed = true;
            var last = pendingProgress;
            pendingProgress = null;
            return last;
        }
    }

    private async Task FlushAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            JobProgress? progress;
            lock (gate)
            {
                progress = pendingProgress;
                pendingProgress = null;
                flushScheduled = false;
                lastFlush = Stopwatch.GetTimestamp();
                if (closed || progress is null) return;
            }
            await owner.SaveProgressAsync(job, progress).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Progress is only a notification; the job goes on and its final state is saved separately.
            Logger.LogWarning(error, "Job {JobId} progress was not saved.", JobId);
        }
    }
}
