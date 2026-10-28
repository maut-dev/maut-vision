using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Jobs;
using MautVision.Core.Python;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Training;

/// <summary>
/// Live state of one running training: the latest stage, epoch, batch and metrics, effective parameters, environment, new epochs
/// and log lines. Events are collected in memory and written in batches — one transaction at most every <see cref="FlushInterval"/>,
/// never a write per console line — and each write raises one TrainingChanged with the new revision of the run.
/// </summary>
internal sealed class TrainingRunRecorder : IAsyncDisposable
{
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly TrainingManager owner;
    private readonly Guid runId;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly List<TrainingEpoch> epochs = [];
    private readonly List<(DateTimeOffset At, TrainingLogKind Kind, CodedText Message)> lines = [];
    private string? stage;
    private int? epoch;
    private int? totalEpochs;
    private int? batch;
    private int? batches;
    private IReadOnlyDictionary<string, double>? metrics;
    private JsonElement? effective;
    private JsonElement? environment;
    private bool dirty;
    private Task? scheduled;
    private bool closed;
    private bool pumping;

    internal TrainingRunRecorder(TrainingManager owner, Guid runId, ILogger logger)
    {
        this.owner = owner;
        this.runId = runId;
        this.logger = logger;
    }

    internal int? TotalEpochs
    {
        get
        {
            lock (gate) return totalEpochs;
        }
    }

    internal void Stage(string value) => Change(() =>
    {
        stage = value;
        lines.Add((Now(), TrainingLogKind.Stage, CodedText.Plain(value)));
    });

    internal void Batch(WorkerBatchEvent value) => Change(() =>
    {
        epoch = value.Epoch;
        batch = value.Batch;
        batches = value.Batches;
    });

    internal void Epoch(WorkerEpochEvent value) => Change(() =>
    {
        var record = new TrainingEpoch(value.Epoch.Epoch, value.Epoch.Losses, value.Epoch.Metrics, value.Epoch.Lr, value.Epoch.Fitness);
        epochs.Add(record);
        epoch = record.Epoch;
        totalEpochs = value.Epochs;
        batch = null;
        batches = null;
        metrics = record.Losses.Concat(record.Metrics).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
    });

    internal void EffectiveParams(JsonElement value) => Change(() => effective = value);

    internal void Environment(JsonElement value) => Change(() => environment = value);

    /// <summary>A line of the log: a console line as text, a system line or a warning as a code with values.</summary>
    internal void Log(TrainingLogKind kind, CodedText message) => Change(() => lines.Add((Now(), kind, message)));

    /// <summary>Writes everything collected so far; waits for a write already in progress.</summary>
    internal async Task FlushAsync()
    {
        await writer.WaitAsync().ConfigureAwait(false);
        try
        {
            TrainingRunSnapshot snapshot;
            lock (gate)
            {
                if (!dirty) return;
                snapshot = new TrainingRunSnapshot(stage, epoch, totalEpochs, batch, batches, metrics, effective, environment, [.. epochs], [.. lines]);
                epochs.Clear();
                lines.Clear();
                dirty = false;
            }
            try
            {
                await owner.WriteSnapshotAsync(runId, snapshot).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Kept for the next write: history and log must not be lost because one write failed.
                logger.LogWarning(error, "Training run {RunId} progress was not saved.", runId);
                lock (gate)
                {
                    epochs.InsertRange(0, snapshot.NewEpochs);
                    lines.InsertRange(0, snapshot.Lines);
                    dirty = true;
                }
            }
        }
        finally
        {
            writer.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (gate)
        {
            closed = true;
            pending = scheduled;
        }
        if (pending is not null) await pending.ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    private void Change(Action change)
    {
        lock (gate)
        {
            change();
            dirty = true;
            if (closed || pumping) return;
            pumping = true;
            scheduled = Task.Run(PumpAsync);
        }
    }

    /// <summary>Writes while changes keep coming, at most once per interval; stops when nothing is left or the recorder closes.</summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            await Task.Delay(FlushInterval).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (!dirty || closed)
                {
                    pumping = false;
                    return;
                }
            }
        }
    }
}

/// <summary>What one write of a running training stores: the latest live fields and the epochs and lines collected since the previous write.</summary>
internal sealed record TrainingRunSnapshot(
    string? Stage,
    int? Epoch,
    int? Epochs,
    int? Batch,
    int? Batches,
    IReadOnlyDictionary<string, double>? Metrics,
    JsonElement? EffectiveParams,
    JsonElement? Environment,
    IReadOnlyList<TrainingEpoch> NewEpochs,
    IReadOnlyList<(DateTimeOffset At, TrainingLogKind Kind, CodedText Message)> Lines);
