using System.Collections.Concurrent;
using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

[JobKind("smoke.cpu")]
internal interface ICpuSmokeJob : IJobDefinition<string>;

[JobKind("smoke.gpu")]
internal interface IGpuSmokeJob : IJobDefinition<string>;

[JobKind("smoke.resumable")]
internal interface IResumableSmokeJob : IJobDefinition<string>;

[JobKind("smoke.removed")]
internal interface IRemovedSmokeJob : IJobDefinition<string>;

[JobKind("smoke.unregistered")]
internal interface IUnregisteredSmokeJob : IJobDefinition<string>;

internal interface IUnkeyedSmokeJob : IJobDefinition<string>;

internal sealed record ResumeCheckpoint(int Step);

/// <summary>
/// Persistent jobs: queue, scheduler budgets, progress, cancellation, failures, and recovery when a project is opened
/// after the process died. Job kinds here exist only in the smoke; each input names a behavior of the test.
/// </summary>
internal static class JobsSmoke
{
    internal const double OpenBoundMilliseconds = 500;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    internal static async Task<int> RunAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task<T> Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        async Task RejectsCode(string code, Func<Task> action)
        {
            var error = await Rejects<CoreException>(action);
            if (error.Code != code) throw new InvalidOperationException($"Expected {code}, got {error.Code}.");
        }

        var root = Path.Combine(fixture, "jobs-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var settings = new ProjectSettings("Задачи", "", "yolo26n", new InputSize(640, 640));
        string Database(IProject value) => Path.Combine(root, "projects", value.Id, ProjectStore.DatabaseFileName);

        var log = new LogCapture();
        var core = await createCore(root, log);
        var jobs = (JobsManager)core.Jobs;
        var behaviors = new ConcurrentDictionary<string, Func<JobContext, Task>>();
        Register(jobs, behaviors, includeRemoved: true);
        jobs.CpuSlots = 2;
        var events = new ConcurrentQueue<(JobInfo Job, long Timestamp)>();
        jobs.JobChanged += (_, _) => throw new InvalidOperationException("Broken observer.");
        jobs.JobChanged += (_, args) => events.Enqueue((args.Job, Stopwatch.GetTimestamp()));
        JobInfo[] EventsOf(Guid id) => events.Select(item => item.Job).Where(job => job.Id == id).ToArray();

        var project = await core.Projects.CreateAsync(settings);
        var id = project.Id;
        await RejectsCode("jobs.project_not_open", () => jobs.ListAsync(id));
        await RejectsCode("jobs.project_not_open", () => jobs.EnqueueAsync<ICpuSmokeJob, string>("quick", id, "op-closed"));
        Check(!File.Exists(Database(project)), "Jobs of a project that was not opened neither run nor open project.db.");
        await core.Projects.OpenAsync(id);
        Check(await ScalarAsync(Database(project), "SELECT count(*) FROM sqlite_master WHERE name IN ('jobs', 'job_steps', 'jobs_unfinished');") == 3,
            "Schema step 2 adds jobs, job_steps and the unfinished index.");
        Check((await jobs.ListAsync(id)).Count == 0, "An opened project has no invented jobs.");
        await Rejects<InvalidOperationException>(() => Task.Run(() => jobs.Register<IUnregisteredSmokeJob, string>(new Handler(behaviors, JobResource.Cpu, false))));
        await Rejects<NotSupportedException>(() => jobs.EnqueueAsync<IUnregisteredSmokeJob, string>("quick", id, "op-unregistered"));
        await Rejects<ArgumentException>(() => jobs.EnqueueAsync<IUnkeyedSmokeJob, string>("quick", id, "op-unkeyed"));
        await Rejects<ArgumentException>(() => jobs.EnqueueAsync<ICpuSmokeJob, string>("quick", id, " "));
        await RejectsCode("jobs.not_found", () => jobs.GetAsync(id, Guid.NewGuid()));
        await RejectsCode("jobs.not_found", () => jobs.CancelAsync(id, Guid.NewGuid()));
        await Rejects<ArgumentOutOfRangeException>(() => jobs.ListAsync(id, limit: 0));
        await Rejects<ArgumentOutOfRangeException>(() => jobs.ListAsync(id, limit: JobsManager.MaxListLimit + 1));

        // Enqueue answers before the handler finishes; the job runs in the background.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRuns = 0;
        behaviors["gated"] = async context =>
        {
            Interlocked.Increment(ref gatedRuns);
            started.TrySetResult();
            await release.Task;
        };
        behaviors["quick"] = _ => Task.CompletedTask;
        var queued = await jobs.EnqueueAsync<ICpuSmokeJob, string>("gated", id, "op-gated");
        Check(queued.Status == JobStatus.Queued && queued.Sequence == 1 && queued.Kind == "smoke.cpu" && queued.ProjectId == id
            && queued.OperationId == "op-gated" && queued.Progress is null && !queued.CancelRequested, "Enqueue returns a queued snapshot at once.");
        await started.Task.WaitAsync(WaitLimit);
        Check((await jobs.GetAsync(id, queued.Id)).Status == JobStatus.Running, "The job runs while enqueue has already returned.");
        release.SetResult();
        var succeeded = await WaitAsync(jobs, id, queued.Id, JobStatus.Succeeded);
        Check(succeeded.Progress == 100 && succeeded.Error is null && succeeded.StartedAt is not null && succeeded.FinishedAt >= succeeded.StartedAt,
            "A finished job keeps progress 100 and its times.");
        Check(EventsOf(queued.Id).Select(job => (job.Status, job.Sequence)).SequenceEqual(
            [(JobStatus.Queued, 1L), (JobStatus.Running, 2L), (JobStatus.Succeeded, 3L)]), "Events follow every saved change in order despite a failing observer.");

        var duplicate = await jobs.EnqueueAsync<ICpuSmokeJob, string>("gated", id, "op-gated");
        Check(duplicate.Id == queued.Id && duplicate.Status == JobStatus.Succeeded && gatedRuns == 1, "A repeated operationId returns the existing job and runs nothing.");
        await Rejects<ArgumentException>(() => jobs.EnqueueAsync<IGpuSmokeJob, string>("gated", id, "op-gated"));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => jobs.EnqueueAsync<ICpuSmokeJob, string>("quick", id, "op-concurrent")));
        Check(concurrent.Select(job => job.Id).Distinct().Count() == 1, "Concurrent enqueues of one operation create one job.");
        await WaitAsync(jobs, id, concurrent[0].Id, JobStatus.Succeeded);

        // Progress is coalesced: far fewer events than reports, spaced by the interval, in order.
        behaviors["progress"] = async context =>
        {
            for (var step = 0; step < 120; step++)
            {
                context.ReportProgress(step * 100.0 / 120, CodedText.Of("progress.smoke_step", ("step", step)), step, 120);
                await Task.Delay(10, context.CancellationToken);
            }
        };
        var progressWatch = Stopwatch.StartNew();
        var progressJob = await jobs.EnqueueAsync<ICpuSmokeJob, string>("progress", id, "op-progress");
        await WaitAsync(jobs, id, progressJob.Id, JobStatus.Succeeded);
        var progressMilliseconds = progressWatch.Elapsed.TotalMilliseconds;
        var progressEvents = events.Where(item => item.Job.Id == progressJob.Id && item.Job.Status == JobStatus.Running && item.Job.Progress is not null).ToArray();
        var allProgress = EventsOf(progressJob.Id);
        Check(allProgress.Select(job => job.Sequence).SequenceEqual(Enumerable.Range(1, allProgress.Length).Select(value => (long)value))
            && allProgress[^1].Status == JobStatus.Succeeded && allProgress[^1].Progress == 100, "Progress events are ordered by sequence and end with the final state.");
        Check(progressEvents.Length >= 2 && progressEvents.Length <= progressMilliseconds / JobsManager.ProgressInterval.TotalMilliseconds + 2,
            $"Progress is throttled: {progressEvents.Length} events in {progressMilliseconds:F0} ms for 120 reports.");
        Check(progressEvents.Zip(progressEvents.Skip(1)).All(pair => pair.First.Job.Progress <= pair.Second.Job.Progress
            && Stopwatch.GetElapsedTime(pair.First.Timestamp, pair.Second.Timestamp).TotalMilliseconds >= JobsManager.ProgressInterval.TotalMilliseconds * 0.8),
            "Progress events never go backwards and are at most about four per second.");
        Check(progressEvents[^1].Job is { Total: 120, ProgressMessage.Code: "progress.smoke_step", Processed: > 0 }, "Progress carries text and counters.");
        Console.WriteLine($"Job progress: {progressEvents.Length} events for 120 reports in {progressMilliseconds:F0} ms.");

        // Cancel of a queued job: it never starts. Cancel of a running job: the handler stops through the token.
        var holdGpu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gpuHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRan = true;
        behaviors["gpu-hold"] = async _ =>
        {
            gpuHeld.TrySetResult();
            await holdGpu.Task;
        };
        behaviors["gpu-never"] = _ =>
        {
            neverRan = false;
            return Task.CompletedTask;
        };
        var holder = await jobs.EnqueueAsync<IGpuSmokeJob, string>("gpu-hold", id, "op-gpu-hold");
        await gpuHeld.Task.WaitAsync(WaitLimit);
        var waiting = await jobs.EnqueueAsync<IGpuSmokeJob, string>("gpu-never", id, "op-gpu-never");
        await Task.Delay(100);
        Check((await jobs.GetAsync(id, waiting.Id)).Status == JobStatus.Queued, "A second GPU job waits while the GPU is busy.");
        var cancelledQueued = await jobs.CancelAsync(id, waiting.Id);
        Check(cancelledQueued.Status == JobStatus.Cancelled && cancelledQueued.CancelRequested && cancelledQueued.FinishedAt is not null, "A queued job is cancelled at once.");
        var cancelledAgain = await jobs.CancelAsync(id, waiting.Id);
        Check(cancelledAgain.Sequence == cancelledQueued.Sequence && cancelledAgain.Status == JobStatus.Cancelled, "Repeated cancel changes nothing.");
        holdGpu.SetResult();
        await WaitAsync(jobs, id, holder.Id, JobStatus.Succeeded);
        await Task.Delay(100);
        Check(neverRan, "A cancelled queued job never runs.");

        var endlessStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behaviors["endless"] = async context =>
        {
            endlessStarted.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, context.CancellationToken);
        };
        var endless = await jobs.EnqueueAsync<ICpuSmokeJob, string>("endless", id, "op-endless");
        await endlessStarted.Task.WaitAsync(WaitLimit);
        var requested = await jobs.CancelAsync(id, endless.Id);
        Check(requested.CancelRequested && requested.Status is JobStatus.Running or JobStatus.Cancelled, "Cancel of a running job is saved first.");
        var cancelledRunning = await WaitAsync(jobs, id, endless.Id, JobStatus.Cancelled);
        Check((await jobs.CancelAsync(id, endless.Id)).Sequence == cancelledRunning.Sequence, "Cancel after the end is idempotent.");
        var succeededAfterCancel = await jobs.CancelAsync(id, queued.Id);
        Check(succeededAfterCancel.Status == JobStatus.Succeeded && !succeededAfterCancel.CancelRequested, "A finished job is not changed by cancel.");

        // Failures keep their code; unexpected exceptions become jobs.failed and are logged in full.
        behaviors["core-fail"] = _ => throw new CoreException("smoke.custom_failure", "Injected domain failure.");
        behaviors["crash"] = _ => throw new InvalidOperationException("Injected crash.");
        behaviors["self-cancel"] = _ => throw new OperationCanceledException("Cancelled without a request.");
        var coreFailed = await WaitAsync(jobs, id, (await jobs.EnqueueAsync<ICpuSmokeJob, string>("core-fail", id, "op-core-fail")).Id, JobStatus.Failed);
        Check(coreFailed.Error is { Code: "smoke.custom_failure" }, "CoreException keeps its code in the failed job.");
        var crashed = await WaitAsync(jobs, id, (await jobs.EnqueueAsync<ICpuSmokeJob, string>("crash", id, "op-crash")).Id, JobStatus.Failed);
        Check(crashed.Error is { Code: "jobs.failed" }, "An unexpected exception fails the job with jobs.failed.");
        Check(log.Records.Any(record => record.Level == LogLevel.Error && record.Exception is InvalidOperationException { Message: "Injected crash." }
            && record.Message.Contains(crashed.Id.ToString()) && record.Message.Contains("op-crash")), "A crash is logged with the exception, job and operation.");
        var selfCancelled = await WaitAsync(jobs, id, (await jobs.EnqueueAsync<ICpuSmokeJob, string>("self-cancel", id, "op-self-cancel")).Id, JobStatus.Failed);
        Check(selfCancelled.Error is { Code: "jobs.failed" }, "Cancellation without a request is a failure, not a cancel.");
        var missingBehavior = await WaitAsync(jobs, id, (await jobs.EnqueueAsync<ICpuSmokeJob, string>("unknown-behavior", id, "op-missing")).Id, JobStatus.Failed);
        Check(missingBehavior.Error is { Code: "jobs.failed" }, "A handler exception never escapes the scheduler.");

        // Budgets: GPU jobs one at a time, CPU jobs up to CpuSlots.
        var gpuActive = 0;
        var gpuMax = 0;
        var cpuActive = 0;
        var cpuMax = 0;
        behaviors["gpu-track"] = async _ =>
        {
            InterlockedMax(ref gpuMax, Interlocked.Increment(ref gpuActive));
            await Task.Delay(40);
            Interlocked.Decrement(ref gpuActive);
        };
        behaviors["cpu-track"] = async _ =>
        {
            InterlockedMax(ref cpuMax, Interlocked.Increment(ref cpuActive));
            await Task.Delay(60);
            Interlocked.Decrement(ref cpuActive);
        };
        var budget = new List<JobInfo>();
        for (var index = 0; index < 4; index++) budget.Add(await jobs.EnqueueAsync<IGpuSmokeJob, string>("gpu-track", id, "op-gpu-" + index));
        for (var index = 0; index < 6; index++) budget.Add(await jobs.EnqueueAsync<ICpuSmokeJob, string>("cpu-track", id, "op-cpu-" + index));
        foreach (var job in budget) await WaitAsync(jobs, id, job.Id, JobStatus.Succeeded);
        Check(gpuMax == 1, "GPU jobs never run concurrently.");
        Check(cpuMax == 2, $"CPU jobs use exactly the CPU limit: {cpuMax}.");

        var page = await jobs.ListAsync(id, limit: 3);
        var next = await jobs.ListAsync(id, limit: 3, before: page[^1].Id);
        Check(page.Count == 3 && page[0].OperationId == "op-cpu-5" && next.Count == 3 && next[0].OperationId == "op-cpu-2", "List pages go from newest to oldest.");

        // Labels of the enqueuer and the active list: queue order, exact totals by kind, the GPU queue, pages after a cursor.
        Check((await jobs.ListActiveAsync(id)) is { Total: 0, Items.Count: 0, CountsByKind.Count: 0, NextCursor: null }, "Without active jobs the active list is empty.");
        await Rejects<ArgumentOutOfRangeException>(() => jobs.ListActiveAsync(id, limit: 0));
        await Rejects<ArgumentOutOfRangeException>(() => jobs.ListActiveAsync(id, limit: JobsManager.MaxListLimit + 1));
        var holdActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeGpuStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCpuStarted = 0;
        behaviors["active-gpu"] = async _ =>
        {
            activeGpuStarted.TrySetResult();
            await holdActive.Task;
        };
        // Ignores the token, so a cancel request keeps it running (cancelling) until it is released.
        behaviors["active-cpu"] = async _ =>
        {
            Interlocked.Increment(ref activeCpuStarted);
            await holdActive.Task;
        };
        var gpuRunner = await jobs.EnqueueAsync<IGpuSmokeJob, string>("active-gpu", id, "op-active-gpu", CodedText.Plain("  Датасет «Номера»  "));
        await activeGpuStarted.Task.WaitAsync(WaitLimit);
        var longLabel = new string('я', JobInfo.MaxLabelLength + 50);
        var gpuWaiter = await jobs.EnqueueAsync<IGpuSmokeJob, string>("active-gpu", id, "op-active-gpu-2", CodedText.Plain(longLabel));
        var activeCpu = new List<JobInfo>();
        for (var index = 0; index < 5; index++)
            activeCpu.Add(await jobs.EnqueueAsync<ICpuSmokeJob, string>("active-cpu", id, "op-active-cpu-" + index, CodedText.Plain(index == 4 ? " " : "Группа " + index)));
        await WaitUntilAsync(() => Volatile.Read(ref activeCpuStarted) == 2, "two CPU jobs of the active list started");
        Check(gpuRunner.Label?.Text == "Датасет «Номера»" && gpuWaiter.Label?.Text == longLabel[..JobInfo.MaxLabelLength] && activeCpu[4].Label is null
            && activeCpu[0].Label?.Text == "Группа 0", "A label is trimmed, cut to its limit, and empty becomes null.");
        Check((await jobs.GetAsync(id, gpuRunner.Id)).Label == gpuRunner.Label && (await jobs.ListAsync(id, limit: 1))[0].Label is null
            && (await jobs.ListAsync(id, limit: 2))[1].Label?.Text == "Группа 3", "Get and list return the stored label.");
        Check(EventsOf(gpuRunner.Id).Length >= 2 && EventsOf(gpuRunner.Id).All(job => job.Label == gpuRunner.Label), "Every job event carries the label.");
        var duplicateLabel = await jobs.EnqueueAsync<IGpuSmokeJob, string>("active-gpu", id, "op-active-gpu", CodedText.Plain("Другая подпись"));
        Check(duplicateLabel.Id == gpuRunner.Id && duplicateLabel.Label == gpuRunner.Label, "A repeated operation keeps the label of its job.");
        Check(CodedTextJson.Parse("Эпоха 1 из 5") is { Code: null, Text: "Эпоха 1 из 5" }
            && CodedTextJson.Parse(CodedTextJson.Serialize(CodedText.Plain("{\"code\":\"x\"}"))) is { Code: null, Text: "{\"code\":\"x\"}" }
            && CodedTextJson.Parse(CodedTextJson.Serialize(CodedText.Of("progress.epoch", ("epoch", 1), ("total", 5)))) is { Code: "progress.epoch", Args: { } epochArgs }
            && epochArgs["epoch"] == "1" && epochArgs["total"] == "5", "Stored texts of version 1.0.0 read back as texts, codes as codes.");
        var cancelling = await jobs.CancelAsync(id, activeCpu[0].Id);
        var active = await jobs.ListActiveAsync(id, limit: JobsManager.MaxListLimit);
        var queueOrder = new[] { gpuRunner.Id, gpuWaiter.Id }.Concat(activeCpu.Select(job => job.Id)).ToArray();
        Check(active.Total == 7 && active.Items.Select(item => item.Job.Id).SequenceEqual(queueOrder) && active.NextCursor is null
            && active.CountsByKind.Count == 2 && active.CountsByKind["smoke.gpu"] == 2 && active.CountsByKind["smoke.cpu"] == 5,
            $"Active jobs are listed in queue order with exact counts by kind ({active.Total}).");
        Check(active.Items.Single(item => item.Job.Id == gpuWaiter.Id).WaitingForGpu
            && !active.Items.Single(item => item.Job.Id == gpuRunner.Id).WaitingForGpu
            && active.Items.Where(item => item.Job.Kind == "smoke.cpu").All(item => !item.WaitingForGpu),
            "Only a queued GPU job waits for the GPU.");
        Check(cancelling.CancelRequested && active.Items.Single(item => item.Job.Id == activeCpu[0].Id).Job is { Status: JobStatus.Running, CancelRequested: true },
            "A cancelling job stays in the active list.");
        var first = await jobs.ListActiveAsync(id, limit: 3);
        var second = await jobs.ListActiveAsync(id, limit: 3, after: first.NextCursor);
        var third = await jobs.ListActiveAsync(id, limit: 3, after: second.NextCursor);
        Check(first.NextCursor == queueOrder[2] && second.NextCursor == queueOrder[5] && third.NextCursor is null
            && first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Job.Id).SequenceEqual(queueOrder)
            && first.Total == 7 && third.Total == 7, "Active pages continue after the cursor and every page has the exact total.");
        foreach (var sql in new[]
        {
            "SELECT id FROM jobs WHERE status IN ('queued', 'running') ORDER BY seq LIMIT 31;",
            "SELECT id FROM jobs WHERE status IN ('queued', 'running') AND seq > (SELECT seq FROM jobs WHERE id = 'x') ORDER BY seq LIMIT 31;",
            "SELECT kind, count(*) FROM jobs WHERE status IN ('queued', 'running') GROUP BY kind;",
        })
        {
            var activePlan = await PlanAsync(Database(project), sql);
            Check(activePlan.Contains("jobs_active", StringComparison.Ordinal), $"The active list reads the partial index jobs_active: {activePlan}");
        }
        holdActive.SetResult();
        var drained = Stopwatch.StartNew();
        while ((await jobs.ListActiveAsync(id)).Total > 0)
        {
            if (drained.Elapsed > WaitLimit) throw new TimeoutException("Active jobs did not finish.");
            await Task.Delay(20);
        }
        Check((await jobs.ListActiveAsync(id)).CountsByKind.Count == 0, "Finished jobs leave the active list and its counts.");

        // Restart: the process "dies" with running jobs. Handlers below hang forever and never write again.
        jobs.CpuSlots = 4;
        var hang = new TaskCompletionSource();
        var stepAActions = 0;
        var resumableSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        behaviors["resume"] = async context =>
        {
            await context.RunStepAsync("a", _ =>
            {
                Interlocked.Increment(ref stepAActions);
                return Task.CompletedTask;
            });
            await context.SaveCheckpointAsync(new ResumeCheckpoint(1));
            resumableSaved.TrySetResult();
            await hang.Task;
        };
        var hangingStarted = new ConcurrentDictionary<string, bool>();
        behaviors["hang"] = async context =>
        {
            hangingStarted[context.OperationId] = true;
            await hang.Task;
        };
        var resumable = await jobs.EnqueueAsync<IResumableSmokeJob, string>("resume", id, "op-resume", CodedText.Plain("Возобновляемая"));
        var interrupted = await jobs.EnqueueAsync<ICpuSmokeJob, string>("hang", id, "op-plain");
        var removed = await jobs.EnqueueAsync<IRemovedSmokeJob, string>("hang", id, "op-removed");
        var cancelPending = await jobs.EnqueueAsync<ICpuSmokeJob, string>("hang", id, "op-cancel-pending");
        var neverStarted = await jobs.EnqueueAsync<IGpuSmokeJob, string>("after-restart", id, "op-after-restart");
        await resumableSaved.Task.WaitAsync(WaitLimit);
        await WaitUntilAsync(() => hangingStarted.Count == 3, "hanging jobs started");
        var cancelSaved = await jobs.CancelAsync(id, cancelPending.Id);
        Check(cancelSaved.CancelRequested && cancelSaved.Status == JobStatus.Running, "A handler that ignores cancel keeps the job running with the request saved.");
        Check((await jobs.GetAsync(id, neverStarted.Id)).Status == JobStatus.Queued, "A GPU job waits behind a hanging GPU job.");
        // Let the last scheduled progress or event writes settle before the next process takes over.
        await Task.Delay(300);

        var restartLog = new LogCapture();
        var restarted = await createCore(root, restartLog);
        var restartedJobs = (JobsManager)restarted.Jobs;
        var restartedBehaviors = new ConcurrentDictionary<string, Func<JobContext, Task>>();
        Register(restartedJobs, restartedBehaviors, includeRemoved: false);
        ResumeCheckpoint? resumedFrom = null;
        bool? stepARan = null;
        bool? stepBRan = null;
        var restartedRuns = 0;
        restartedBehaviors["resume"] = async context =>
        {
            Interlocked.Increment(ref restartedRuns);
            resumedFrom = context.Checkpoint<ResumeCheckpoint>();
            stepARan = await context.RunStepAsync("a", _ =>
            {
                Interlocked.Increment(ref stepAActions);
                return Task.CompletedTask;
            });
            stepBRan = await context.RunStepAsync("b", _ => Task.CompletedTask);
        };
        restartedBehaviors["hang"] = _ =>
        {
            Interlocked.Increment(ref restartedRuns);
            return Task.CompletedTask;
        };
        restartedBehaviors["after-restart"] = _ =>
        {
            Interlocked.Increment(ref restartedRuns);
            return Task.CompletedTask;
        };
        var restartedEvents = new ConcurrentQueue<JobInfo>();
        restartedJobs.JobChanged += (_, args) => restartedEvents.Enqueue(args.Job);
        var listed = await restarted.Projects.ListAsync();
        await restarted.Projects.GetAsync(id);
        await Task.Delay(300);
        Check(listed.Count == 1 && restartedRuns == 0 && restartedEvents.IsEmpty, "Starting and listing projects recovers and runs no jobs.");
        await RejectsCode("jobs.project_not_open", () => restartedJobs.GetAsync(id, resumable.Id));
        Check(await ScalarAsync(Database(project), "SELECT count(*) FROM jobs WHERE status = 'running';") == 4, "Until the project is opened its jobs stay as the dead process left them.");

        await restarted.Projects.OpenAsync(id);
        var resumed = await WaitAsync(restartedJobs, id, resumable.Id, JobStatus.Succeeded);
        Check(resumed.Label?.Text == "Возобновляемая", "The label survives the restart.");
        Check(resumedFrom == new ResumeCheckpoint(1) && stepARan == false && stepBRan == true && stepAActions == 1,
            "A resumable job continues from its checkpoint and skips completed steps.");
        Check(await ScalarAsync(Database(project), $"SELECT count(*) FROM job_steps WHERE job_id = '{resumable.Id:D}';") == 0, "A finished job drops its step receipts.");
        var interruptedNow = await restartedJobs.GetAsync(id, interrupted.Id);
        Check(interruptedNow.Status == JobStatus.Interrupted && interruptedNow.Error is { Code: "jobs.interrupted" } && interruptedNow.FinishedAt is not null,
            "A running non-resumable job becomes interrupted.");
        var blocked = await restartedJobs.GetAsync(id, removed.Id);
        Check(blocked.Status == JobStatus.Blocked && blocked.Error is { Code: "jobs.kind_unknown" } && blocked.Kind == "smoke.removed"
            && await TextAsync(Database(project), $"SELECT input FROM jobs WHERE id = '{removed.Id:D}';") == "\"hang\"", "An unknown kind becomes blocked and keeps its data.");
        Check((await restartedJobs.GetAsync(id, cancelPending.Id)).Status == JobStatus.Cancelled, "A saved cancel request ends as cancelled after restart.");
        await WaitAsync(restartedJobs, id, neverStarted.Id, JobStatus.Succeeded);
        Check(restartedRuns == 2, "After restart only the resumable and the never-started jobs run.");
        Check(restartedEvents.Where(job => job.Id == interrupted.Id).Select(job => job.Status).SequenceEqual([JobStatus.Interrupted])
            && restartedEvents.Where(job => job.Id == resumable.Id).Select(job => job.Sequence).Order().SequenceEqual(
                restartedEvents.Where(job => job.Id == resumable.Id).Select(job => job.Sequence)), "Recovery reports changed jobs as events in order.");
        var cancelledBlocked = await restartedJobs.CancelAsync(id, removed.Id);
        Check(cancelledBlocked.Status == JobStatus.Cancelled && (await restartedJobs.GetAsync(id, removed.Id)).Status == JobStatus.Cancelled, "A blocked job can be cancelled.");
        await restarted.Projects.OpenAsync(id);
        Check(restartedRuns == 2, "Opening the project again does not recover again.");

        // Opening a project with a long history is one indexed query, independent of the number of finished jobs.
        var history = await restarted.Projects.CreateAsync(settings with { Name = "История" });
        await restarted.Projects.OpenAsync(history.Id);
        const int HistoryRows = 10_000;
        var stuckId = Guid.NewGuid();
        await SeedHistoryAsync(Database(history), HistoryRows, stuckId);
        var plan = await PlanAsync(Database(history), "SELECT id FROM jobs WHERE status IN ('queued', 'running', 'blocked') ORDER BY seq;");
        Check(plan.Contains("jobs_unfinished", StringComparison.Ordinal), $"Recovery reads the partial index: {plan}");
        SqliteConnection.ClearAllPools();
        var historyCore = await createCore(root, null);
        Register((JobsManager)historyCore.Jobs, new ConcurrentDictionary<string, Func<JobContext, Task>>(), includeRemoved: false);
        var other = await historyCore.Projects.CreateAsync(settings with { Name = "Без базы" });
        await historyCore.Projects.ListAsync();
        await historyCore.Projects.GetAsync(history.Id);
        var openWatch = Stopwatch.StartNew();
        await historyCore.Projects.OpenAsync(history.Id);
        var openMilliseconds = openWatch.Elapsed.TotalMilliseconds;
        Check(openMilliseconds < OpenBoundMilliseconds, $"Opening a project with {HistoryRows} jobs is fast: {openMilliseconds:F1} ms.");
        Check((await historyCore.Jobs.GetAsync(history.Id, stuckId)).Status == JobStatus.Interrupted, "The one unfinished job among the history is recovered.");
        var listWatch = Stopwatch.StartNew();
        var firstPage = await historyCore.Jobs.ListAsync(history.Id);
        var listMilliseconds = listWatch.Elapsed.TotalMilliseconds;
        Check(firstPage.Count == 50 && firstPage[0].Id == stuckId, "The first page holds the newest jobs.");
        Check(firstPage.All(job => job.Label is null), "Jobs written without a label (before schema step 10) have none.");
        var activeWatch = Stopwatch.StartNew();
        var historyActive = await historyCore.Jobs.ListActiveAsync(history.Id);
        var activeMilliseconds = activeWatch.Elapsed.TotalMilliseconds;
        Check(historyActive.Total == 0 && historyActive.Items.Count == 0, "Finished history is not active.");
        Check(!File.Exists(Database(other)), "Listing projects never opens or creates project.db.");
        Console.WriteLine($"Project open with {HistoryRows} jobs: {openMilliseconds:F1} ms (cold), first page of jobs {listMilliseconds:F1} ms, "
            + $"active jobs {activeMilliseconds:F1} ms.");
        return checks;
    }

    private static void Register(JobsManager jobs, ConcurrentDictionary<string, Func<JobContext, Task>> behaviors, bool includeRemoved)
    {
        jobs.Register<ICpuSmokeJob, string>(new Handler(behaviors, JobResource.Cpu, resumable: false));
        jobs.Register<IGpuSmokeJob, string>(new Handler(behaviors, JobResource.Gpu, resumable: false));
        jobs.Register<IResumableSmokeJob, string>(new Handler(behaviors, JobResource.Cpu, resumable: true));
        if (includeRemoved) jobs.Register<IRemovedSmokeJob, string>(new Handler(behaviors, JobResource.Gpu, resumable: false));
    }

    private sealed class Handler(ConcurrentDictionary<string, Func<JobContext, Task>> behaviors, JobResource resource, bool resumable) : IJobHandler<string>
    {
        public JobResource Resource { get; } = resource;
        public bool Resumable { get; } = resumable;

        public Task ExecuteAsync(string input, JobContext context) => behaviors[input](context);
    }

    private static async Task<JobInfo> WaitAsync(IJobsManager jobs, string projectId, Guid jobId, JobStatus status)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = await jobs.GetAsync(projectId, jobId);
            if (job.Status == status) return job;
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"Job {job.OperationId} is {job.Status}, expected {status}.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > WaitLimit) throw new TimeoutException("Timed out: " + label);
            await Task.Delay(20);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>Finished history plus one job left running by a dead process, written as one transaction.</summary>
    private static async Task SeedHistoryAsync(string database, int rows, Guid stuckId)
    {
        await using var connection = Raw(database);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs (id, kind, operation_id, input, status, progress, revision, created_at, updated_at, started_at, finished_at)
            VALUES ($id, 'smoke.cpu', $operation, '"quick"', $status, $progress, 3, $time, $time, $time, $finished);
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        var status = command.Parameters.Add("$status", SqliteType.Text);
        var progress = command.Parameters.Add("$progress", SqliteType.Real);
        var time = command.Parameters.Add("$time", SqliteType.Integer);
        var finished = command.Parameters.Add("$finished", SqliteType.Integer);
        var start = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        for (var index = 0; index <= rows; index++)
        {
            var stuck = index == rows;
            id.Value = (stuck ? stuckId : Guid.NewGuid()).ToString("D");
            operation.Value = "history-" + index;
            status.Value = stuck ? "running" : index % 7 == 0 ? "failed" : "succeeded";
            progress.Value = stuck ? 40.0 : 100.0;
            time.Value = start + index * 1000L;
            finished.Value = stuck ? DBNull.Value : start + index * 1000L + 500;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private static async Task<string> PlanAsync(string database, string sql)
    {
        await using var connection = Raw(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return string.Join("; ", plan);
    }

    // Fixture connections bypass the pool so that they never hold the file after the check.
    private static SqliteConnection Raw(string database) => new($"Data Source={database};Pooling=False");

    private static async Task<long> ScalarAsync(string database, string sql) => Convert.ToInt64(await QueryAsync(database, sql));

    private static async Task<string?> TextAsync(string database, string sql) => Convert.ToString(await QueryAsync(database, sql));

    private static async Task<object?> QueryAsync(string database, string sql)
    {
        await using var connection = Raw(database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
