using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Python;

namespace MautVision.Core.Projects.Checks;

/// <summary>
/// State of one live check in memory: summary of every frame, the bounded journal of frames with detections (a ring of the last
/// <see cref="ScreenJournal"/> frames for the screen, the first <see cref="VideoJournal"/> frames of a video with a truncation flag),
/// the last frame and the last preview. Every change increases <see cref="Sequence"/>; changes are announced at most every
/// <see cref="EventInterval"/>, state transitions at once.
/// </summary>
internal sealed class LiveCheckSession
{
    internal const int ScreenJournal = 2000;
    internal const int VideoJournal = 5000;
    internal const int MaxWarnings = 20;
    internal static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(200);

    private readonly Lock gate = new();
    private readonly Action<LiveCheck, LiveCheckPreview?> announce;
    private readonly CancellationTokenSource stop = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<int, ClassTotals> classes = new();
    private readonly List<LiveCheckFrame> journal = [];
    private readonly Queue<long> frameTimes = new();
    private readonly List<LiveCheckWarning> warnings = [];
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LiveCheckState state = LiveCheckState.Preparing;
    private LiveCheckStopReason? stopReason;
    private LiveCheckError? error;
    private LiveCheckSource source;
    private long sequence = 1;
    private long framesSeen;
    private long framesWithDetections;
    private long detections;
    private long? workerFrames;
    private long? framesTotal;
    private long? positionMs;
    private long? durationMs;
    private long journalTotal;
    private bool truncated;
    private LiveCheckFrame? lastFrame;
    private LiveCheckPreview? preview;
    private long? workerStartedAt;
    private long? firstFrameMs;
    private DateTimeOffset? finishedAt;
    private bool scheduled;
    private long lastAnnounced = long.MinValue / 2;

    internal LiveCheckSession(Guid id, string projectId, LiveCheckMode mode, ModelVersion version, LiveCheckSettings settings, LiveCheckSource source,
        Action<LiveCheck, LiveCheckPreview?> announce)
    {
        Id = id;
        ProjectId = projectId;
        Mode = mode;
        Version = version;
        Settings = settings;
        this.source = source;
        this.announce = announce;
        StartedAt = Now();
    }

    internal Guid Id { get; }

    internal string ProjectId { get; }

    internal LiveCheckMode Mode { get; }

    internal ModelVersion Version { get; }

    internal LiveCheckSettings Settings { get; }

    internal DateTimeOffset StartedAt { get; }

    /// <summary>Cancelled by "Stop", a project switch or the end of the page session.</summary>
    internal CancellationToken StopToken => stop.Token;

    /// <summary>Completes when the worker ended and the outcome is recorded.</summary>
    internal Task Completion => completion.Task;

    internal bool IsFinished
    {
        get
        {
            lock (gate) return state is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled;
        }
    }

    internal long Sequence
    {
        get
        {
            lock (gate) return sequence;
        }
    }

    /// <summary>"Stop": the first reason wins; the worker tree ends and the check becomes Cancelled.</summary>
    internal void Stop(LiveCheckStopReason reason)
    {
        lock (gate)
        {
            if (state is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled or LiveCheckState.Cancelling) return;
            state = LiveCheckState.Cancelling;
            stopReason = reason;
            sequence++;
        }
        AnnounceNow();
        stop.Cancel();
    }

    internal void WorkerStarting()
    {
        lock (gate) workerStartedAt = clock.ElapsedMilliseconds;
    }

    /// <summary>The worker passed loading: predicting or capturing.</summary>
    internal void Running()
    {
        lock (gate)
        {
            if (state != LiveCheckState.Preparing) return;
            state = LiveCheckState.Running;
            sequence++;
        }
        AnnounceNow();
    }

    internal void Frame(WorkerFrame frame, byte[]? previewContent)
    {
        var items = frame.Detections.Select(item =>
        {
            var known = item.ClassIndex >= 0 && item.ClassIndex < Version.Classes.Count ? Version.Classes[item.ClassIndex] : null;
            return new LiveDetection(item.ClassIndex, known?.ClassId, known?.Name ?? item.Name, item.Confidence, item.X, item.Y, item.Width, item.Height);
        }).ToArray();
        var becameRunning = false;
        lock (gate)
        {
            if (state == LiveCheckState.Preparing)
            {
                state = LiveCheckState.Running;
                becameRunning = true;
            }
            var now = clock.ElapsedMilliseconds;
            firstFrameMs ??= now - (workerStartedAt ?? 0);
            // A video sends frames without detections only to carry a preview: they are not counted twice with the progress.
            if (Mode != LiveCheckMode.Video || items.Length > 0) framesSeen++;
            var record = new LiveCheckFrame(0, frame.Index, frame.TimeMs, frame.Width, frame.Height, frame.InferenceMs, items);
            if (items.Length > 0)
            {
                framesWithDetections++;
                detections += items.Length;
                foreach (var group in items.GroupBy(item => item.ClassIndex))
                {
                    if (!classes.TryGetValue(group.Key, out var totals)) classes[group.Key] = totals = new ClassTotals(group.First().ClassId, group.First().Name);
                    totals.Frames++;
                    totals.Detections += group.Count();
                    totals.MaxConfidence = Math.Max(totals.MaxConfidence, group.Max(item => item.Confidence));
                }
                record = record with { Ordinal = ++journalTotal };
                if (Mode == LiveCheckMode.Video && journal.Count >= VideoJournal)
                {
                    truncated = true;
                }
                else
                {
                    journal.Add(record);
                    if (Mode == LiveCheckMode.Screen && journal.Count > ScreenJournal) journal.RemoveAt(0);
                }
            }
            lastFrame = record;
            if (Mode == LiveCheckMode.Video) positionMs = Math.Max(positionMs ?? 0, frame.TimeMs);
            else positionMs = frame.TimeMs;
            if (Mode == LiveCheckMode.Screen)
            {
                frameTimes.Enqueue(now);
                while (frameTimes.Count > 10) frameTimes.Dequeue();
            }
            if (previewContent is not null)
            {
                var number = (preview?.Info.Number ?? 0) + 1;
                preview = new LiveCheckPreview(new LiveCheckPreviewInfo(number, frame.Index, frame.TimeMs, frame.Width, frame.Height), previewContent, "image/jpeg");
            }
            sequence++;
        }
        if (becameRunning) AnnounceNow();
        else Changed();
    }

    internal void Progress(WorkerProgress progress)
    {
        lock (gate)
        {
            workerFrames = Math.Max(workerFrames ?? 0, progress.FramesProcessed);
            framesTotal = progress.FramesTotal;
            positionMs = Math.Max(positionMs ?? 0, progress.PositionMs ?? 0);
            durationMs = progress.DurationMs ?? durationMs;
            sequence++;
        }
        Changed();
    }

    internal void Warning(string code, string message, CodedText text)
    {
        lock (gate)
        {
            if (warnings.Count >= MaxWarnings || warnings.Any(item => item.Code == code && item.Message == message)) return;
            warnings.Add(new LiveCheckWarning(code, message, text));
            if (code == "live_time_limit") stopReason ??= LiveCheckStopReason.TimeLimit;
            sequence++;
        }
        Changed();
    }

    /// <summary>
    /// The worker succeeded: its summary over every frame replaces the one collected from events. A stop that came meanwhile still
    /// ends the check as "Stopped" (<see cref="Finish"/>).
    /// </summary>
    internal void Succeeded(WorkerPredictResult result)
    {
        lock (gate)
        {
            if (state is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled) return;
            classes.Clear();
            foreach (var item in result.Summary.Classes)
            {
                var known = item.Index >= 0 && item.Index < Version.Classes.Count ? Version.Classes[item.Index] : null;
                classes[item.Index] = new ClassTotals(known?.ClassId, known?.Name ?? item.Name)
                {
                    Frames = item.Frames,
                    Detections = item.Detections,
                    MaxConfidence = item.MaxConfidence,
                };
            }
            framesSeen = result.Summary.FramesProcessed;
            workerFrames = result.Summary.FramesProcessed;
            framesWithDetections = result.Summary.FramesWithDetections;
            detections = result.Summary.Detections;
            if (Mode == LiveCheckMode.Video) framesTotal = result.Summary.FramesProcessed;
            if (result.Source is { } seen)
            {
                source = source with { Width = seen.Width, Height = seen.Height, DurationMs = seen.DurationMs ?? source.DurationMs, Fps = seen.Fps ?? source.Fps };
                durationMs = seen.DurationMs ?? durationMs;
            }
            foreach (var warning in result.Warnings)
            {
                if (warnings.Count < MaxWarnings && !warnings.Any(item => item.Code == warning.Code && item.Message == warning.Message))
                    warnings.Add(new LiveCheckWarning(warning.Code, warning.Message, warning.Text));
                if (warning.Code == "live_time_limit") stopReason ??= LiveCheckStopReason.TimeLimit;
            }
        }
        Finish(LiveCheckState.Succeeded, null);
    }

    internal void Finish(LiveCheckState final, LiveCheckError? failure)
    {
        lock (gate)
        {
            if (state is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled) return;
            // A stop that came while the worker was finishing still ends as "Stopped".
            state = state == LiveCheckState.Cancelling && final != LiveCheckState.Failed ? LiveCheckState.Cancelled : final;
            error = state == LiveCheckState.Failed ? failure : null;
            finishedAt = Now();
            sequence++;
        }
        AnnounceNow();
    }

    internal void Completed() => completion.TrySetResult();

    internal LiveCheckPreview? Preview
    {
        get
        {
            lock (gate) return preview;
        }
    }

    internal LiveCheck Snapshot(int journalLimit)
    {
        lock (gate)
        {
            var recent = journalLimit <= 0 ? [] : journal.Skip(Math.Max(0, journal.Count - journalLimit)).ToArray();
            double? fps = null;
            if (Mode == LiveCheckMode.Screen && frameTimes.Count > 1 && state == LiveCheckState.Running)
            {
                var span = (frameTimes.Last() - frameTimes.Peek()) / 1000.0;
                if (span > 0) fps = Math.Round((frameTimes.Count - 1) / span, 1);
            }
            var processed = Math.Max(framesSeen, workerFrames ?? 0);
            var elapsed = (long)((finishedAt ?? Now()) - StartedAt).TotalMilliseconds;
            return new LiveCheck(
                Id,
                Mode,
                state,
                Version.Id,
                Version.Number,
                Settings,
                source,
                new LiveCheckProgress(processed, framesTotal, Mode == LiveCheckMode.Image ? null : positionMs, durationMs, fps, Math.Max(0, elapsed), firstFrameMs),
                new LiveCheckSummary(processed, framesWithDetections, detections, classes.OrderBy(entry => entry.Key)
                    .Select(entry => new LiveCheckClassSummary(entry.Key, entry.Value.ClassId, entry.Value.Name, entry.Value.Frames, entry.Value.Detections,
                        entry.Value.MaxConfidence)).ToArray()),
                lastFrame,
                preview?.Info,
                JournalInfo(),
                recent,
                warnings.ToArray(),
                error,
                stopReason,
                StartedAt,
                finishedAt,
                sequence);
        }
    }

    internal LiveCheckJournalPage Journal(long? after, long? before, int limit)
    {
        lock (gate)
        {
            var first = journal.Count == 0 ? 0 : journal[0].Ordinal;
            IEnumerable<LiveCheckFrame> items;
            if (after is { } from)
            {
                var start = (int)Math.Clamp(from - first + 1, 0, journal.Count);
                items = journal.Skip(start).Take(limit);
            }
            else
            {
                var end = before is { } until ? (int)Math.Clamp(until - first, 0, journal.Count) : journal.Count;
                items = journal.Skip(Math.Max(0, end - limit)).Take(Math.Min(limit, end));
            }
            return new LiveCheckJournalPage(items.ToArray(), JournalInfo());
        }
    }

    private LiveCheckJournalInfo JournalInfo() => new(journalTotal, journal.Count, truncated, journal.Count == 0 ? 0 : journal[0].Ordinal,
        journal.Count == 0 ? 0 : journal[^1].Ordinal);

    /// <summary>Announces the current snapshot after at most <see cref="EventInterval"/>; frames in between are coalesced.</summary>
    private void Changed()
    {
        long delay;
        lock (gate)
        {
            if (scheduled) return;
            scheduled = true;
            delay = Math.Max(0, (long)EventInterval.TotalMilliseconds - (clock.ElapsedMilliseconds - lastAnnounced));
        }
        _ = Task.Delay(TimeSpan.FromMilliseconds(delay)).ContinueWith(_ =>
        {
            lock (gate) scheduled = false;
            AnnounceNow();
        }, TaskScheduler.Default);
    }

    private void AnnounceNow()
    {
        LiveCheckPreview? current;
        lock (gate)
        {
            lastAnnounced = clock.ElapsedMilliseconds;
            current = preview;
        }
        announce(Snapshot(0), current);
    }

    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class ClassTotals(Guid? classId, string name)
    {
        internal Guid? ClassId { get; } = classId;
        internal string Name { get; } = name;
        internal long Frames { get; set; }
        internal long Detections { get; set; }
        internal double MaxConfidence { get; set; }
    }
}
