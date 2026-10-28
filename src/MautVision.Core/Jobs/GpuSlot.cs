namespace MautVision.Core.Jobs;

/// <summary>
/// The one GPU operation of the application: a GPU job of the queue (training, evaluation on a dataset) or a live check. A job
/// waits for the slot in the queue; a live check takes it at once or is refused with the kind that holds it. Releasing the slot
/// lets the queue dispatch its next GPU job.
/// </summary>
internal sealed class GpuSlot
{
    /// <summary>The holder kind of a live check (image, video or screen).</summary>
    internal const string LiveCheckKind = "checks.live";

    private readonly Lock gate = new();
    private string? holder;

    /// <summary>Raised after a release, outside the lock: the queue dispatches waiting GPU jobs.</summary>
    internal event Action? Released;

    /// <summary>The kind that holds the slot (a job kind or <see cref="LiveCheckKind"/>); null when it is free.</summary>
    internal string? Holder
    {
        get
        {
            lock (gate) return holder;
        }
    }

    internal bool TryAcquire(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (gate)
        {
            if (holder is not null) return false;
            holder = kind;
            return true;
        }
    }

    internal void Release()
    {
        lock (gate)
        {
            if (holder is null) throw new InvalidOperationException("The GPU slot is not held.");
            holder = null;
        }
        Released?.Invoke();
    }
}
