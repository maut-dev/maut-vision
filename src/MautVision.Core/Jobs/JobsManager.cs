using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Jobs;

/// <summary>Runtime state of a queued or running job of an opened project. Info is changed only under Gate.</summary>
internal sealed class ActiveJob(ProjectStore store, JobRegistration registration, JobInfo info, string input, string? checkpoint)
{
    internal ProjectStore Store { get; } = store;
    internal JobRegistration Registration { get; } = registration;
    internal string Input { get; } = input;
    internal string? Checkpoint { get; } = checkpoint;
    internal JobInfo Info { get; set; } = info;
    internal Guid Id => Info.Id;
    internal string ProjectId => Info.ProjectId;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    // Not disposed: it has no timer or linked registration, and a late Cancel must not throw.
    internal CancellationTokenSource Cancellation { get; } = new();
}

/// <summary>
/// Persistent per-project queue and in-process scheduler. Job rows live in project.db; a project's jobs are recovered
/// and run only after it is opened. Handlers hold the algorithms; this class holds no scenario logic.
/// </summary>
internal sealed partial class JobsManager : IJobsManager
{
    internal const int MaxListLimit = 200;
    internal const int MaxOperationIdLength = 200;
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger logger;
    private readonly Dictionary<Type, JobRegistration> contracts = new();
    private readonly Dictionary<string, JobRegistration> kinds = new(StringComparer.Ordinal);
    // Guards everything below: attached projects, the FIFO of queued jobs, active jobs and busy slots.
    private readonly object scheduler = new();
    private readonly Dictionary<string, Task> attaching = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectStore> attached = new(StringComparer.Ordinal);
    // Attached projects whose opening finished: only their queued jobs are dispatched.
    private readonly HashSet<string> started = new(StringComparer.Ordinal);
    private readonly List<ActiveJob> pending = [];
    private readonly Dictionary<Guid, ActiveJob> active = new();
    // Jobs admitted by Admission whose row is being written; they count as active for the next admission of their project.
    private readonly List<JobInfo> admitting = [];
    private readonly GpuSlot gpu;
    private int busyCpu;

    internal JobsManager(IProjectsManager projects, IBaseModelsManager baseModels, ILogger logger, GpuSlot? gpu = null)
    {
        Projects = projects;
        BaseModels = baseModels;
        this.logger = logger;
        this.gpu = gpu ?? new GpuSlot();
        // A live check that ends frees the slot outside the queue: waiting GPU jobs start then.
        this.gpu.Released += Dispatch;
    }

    public event EventHandler<JobChangedEventArgs>? JobChanged;

    /// <summary>Managers that handlers registered inside Core receive through their constructors.</summary>
    internal IProjectsManager Projects { get; }
    internal IBaseModelsManager BaseModels { get; }

    /// <summary>Concurrent CPU jobs; GPU jobs always run one at a time.</summary>
    internal int CpuSlots { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Binds a contract to its handler. Kinds are registered during composition, before any project is opened.</summary>
    internal void Register<TJob, TInput>(IJobHandler<TInput> handler) where TJob : IJobDefinition<TInput>
    {
        ArgumentNullException.ThrowIfNull(handler);
        var kind = KindOf(typeof(TJob));
        var registration = new JobRegistration(kind, handler.Resource, handler.Resumable, (json, context) =>
        {
            TInput input;
            try
            {
                input = JsonSerializer.Deserialize<TInput>(json, Json)!;
            }
            catch (JsonException error)
            {
                throw new JobDataException("jobs.input_invalid", $"Job {context.JobId} input cannot be read as {typeof(TInput).Name}.", error);
            }
            return handler.ExecuteAsync(input, context);
        });
        lock (scheduler)
        {
            if (attaching.Count > 0)
                throw new InvalidOperationException("Job kinds must be registered before projects are opened.");
            if (contracts.ContainsKey(typeof(TJob)) || kinds.ContainsKey(kind))
                throw new InvalidOperationException($"Job kind {kind} is already registered.");
            contracts.Add(typeof(TJob), registration);
            kinds.Add(kind, registration);
        }
    }

    public async Task<JobInfo> EnqueueAsync<TJob, TInput>(TInput input, string projectId, string operationId, CodedText? label = null,
        CancellationToken cancellationToken = default) where TJob : IJobDefinition<TInput>
    {
        var kind = KindOf(typeof(TJob));
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, MaxOperationIdLength, nameof(operationId));
        JobRegistration registration;
        lock (scheduler)
        {
            registration = contracts.GetValueOrDefault(typeof(TJob))
                ?? throw new NotSupportedException($"No handler is registered for job kind {kind}; no job was queued.");
        }
        var store = RequireAttached(projectId);
        var inputJson = JsonSerializer.Serialize(input, Json);
        var now = Now();
        var candidate = new JobInfo(Guid.NewGuid(), kind, projectId, operationId, JobStatus.Queued, null, null, null, null, null,
            CancelRequested: false, now, now, StartedAt: null, FinishedAt: null, Sequence: 1, Label: CleanLabel(label));
        var admitted = false;
        try
        {
            var (info, created) = await store.WriteAsync(async (connection, transaction, token) =>
            {
                if (await JobsTable.FindByOperationAsync(connection, transaction, projectId, operationId, token).ConfigureAwait(false) is { } existing)
                    return (existing, false);
                // Writes of a project are serialized, so the admission sees every job admitted before this one, even one not yet active.
                Admit(candidate);
                admitted = true;
                await JobsTable.InsertAsync(connection, transaction, candidate, inputJson, token).ConfigureAwait(false);
                return (candidate, true);
            }, cancellationToken).ConfigureAwait(false);
            if (!created)
            {
                if (info.Kind != kind)
                    throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {info.Kind}.", nameof(operationId));
                return info;
            }

            var job = new ActiveJob(store, registration, info, inputJson, checkpoint: null);
            // The queued event must precede the running one, so it is raised before the job becomes visible to Dispatch.
            await job.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (scheduler)
                {
                    active.Add(job.Id, job);
                    pending.Add(job);
                    admitting.Remove(candidate);
                    admitted = false;
                }
                LogTransition(info);
                Raise(info);
            }
            finally
            {
                job.Gate.Release();
            }
            Dispatch();
            return info;
        }
        finally
        {
            if (admitted)
            {
                lock (scheduler) admitting.Remove(candidate);
            }
        }
    }

    /// <summary>
    /// Asks the composition whether a job of this kind may join the project's active jobs (for example, nothing starts while the project
    /// is exported); an admitted job is counted until it becomes active or its write fails.
    /// </summary>
    private void Admit(JobInfo candidate)
    {
        lock (scheduler)
        {
            if (Admission is { } admission)
            {
                var kinds = active.Values.Where(job => job.ProjectId == candidate.ProjectId && job.Info.Status is JobStatus.Queued or JobStatus.Running)
                    .Select(job => job.Info.Kind)
                    .Concat(admitting.Where(job => job.ProjectId == candidate.ProjectId).Select(job => job.Kind))
                    .ToArray();
                admission(candidate.ProjectId, candidate.Kind, kinds);
            }
            admitting.Add(candidate);
        }
    }

    /// <summary>
    /// Set once by the Core composition: refuses a new job with a CoreException given the project, the kind and the kinds of its active
    /// jobs (queued, running, being admitted). Called under the scheduler lock: it must be quick and must not call back into JobsManager.
    /// </summary>
    internal Action<string, string, IReadOnlyList<string>>? Admission { get; set; }

    public int CountActive()
    {
        lock (scheduler) return active.Values.Count(job => job.Info.Status is JobStatus.Queued or JobStatus.Running);
    }

    /// <summary>
    /// Cancels every active job of a project that is being deleted, waits until their handlers stopped and forgets the project: its
    /// jobs are not loaded any more (<c>jobs.project_not_open</c>). False — some job did not stop within the wait; the project stays attached.
    /// </summary>
    internal async Task<bool> DetachAsync(string projectId, TimeSpan wait, CancellationToken cancellationToken)
    {
        lock (scheduler)
        {
            if (!attached.ContainsKey(projectId))
            {
                attaching.Remove(projectId);
                return true;
            }
        }
        foreach (var job in ActiveJobs(projectId))
        {
            try
            {
                await CancelAsync(projectId, job.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (CoreException error) when (error.Code == "jobs.not_found")
            {
                // Finished and removed meanwhile.
            }
        }
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            lock (scheduler)
            {
                if (!active.Values.Any(job => job.ProjectId == projectId) && !admitting.Any(job => job.ProjectId == projectId))
                {
                    attached.Remove(projectId);
                    started.Remove(projectId);
                    attaching.Remove(projectId);
                    return true;
                }
            }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<JobInfo> GetAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var store = RequireAttached(projectId);
        return await store.ReadAsync((connection, token) => JobsTable.GetAsync(connection, null, projectId, jobId, token), cancellationToken).ConfigureAwait(false)
            ?? throw NotFound(projectId, jobId);
    }

    public async Task<IReadOnlyList<JobInfo>> ListAsync(string projectId, int limit = 50, Guid? before = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxListLimit);
        var store = RequireAttached(projectId);
        return await store.ReadAsync((connection, token) => JobsTable.ListAsync(connection, projectId, limit, before, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActiveJobsPage> ListActiveAsync(string projectId, int limit = 30, Guid? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxListLimit);
        var store = RequireAttached(projectId);
        // One more row than asked tells whether a next page exists.
        var (rows, counts) = await store.ReadAsync((connection, token) => JobsTable.ListActiveAsync(connection, projectId, limit + 1, after, token),
            cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).Select(job => new ActiveJobEntry(job, job.Status == JobStatus.Queued && UsesGpu(job.Kind))).ToArray();
        return new ActiveJobsPage(page, counts.Values.Sum(), counts, rows.Count > limit ? page[^1].Job.Id : null);
    }

    public async Task<JobInfo> CancelAsync(string projectId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var store = RequireAttached(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        ActiveJob? job;
        bool wasQueued;
        lock (scheduler)
        {
            if (active.TryGetValue(jobId, out job) && job.ProjectId != projectId) job = null;
            wasQueued = job is not null && pending.Remove(job);
        }
        if (job is null)
        {
            return await CancelStoredAsync(store, projectId, jobId).ConfigureAwait(false);
        }
        if (wasQueued)
        {
            JobInfo cancelled;
            try
            {
                cancelled = await ChangeAsync(job, info => info with
                {
                    Status = JobStatus.Cancelled,
                    CancelRequested = true,
                    FinishedAt = Now(),
                }).ConfigureAwait(false);
            }
            catch
            {
                lock (scheduler) pending.Insert(0, job);
                throw;
            }
            lock (scheduler) active.Remove(job.Id);
            return cancelled;
        }
        // Already dispatched: the request is saved first, so a crash before the handler stops still ends as Cancelled.
        var requested = await ChangeAsync(job, info => info.Status is JobStatus.Queued or JobStatus.Running && !info.CancelRequested
            ? info with { CancelRequested = true }
            : null).ConfigureAwait(false);
        await job.Cancellation.CancelAsync().ConfigureAwait(false);
        return requested;
    }

    /// <summary>
    /// Recovers the jobs of a project when it is opened; repeated opens reuse the result, a failed one is retried.
    /// </summary>
    internal Task AttachAsync(Project project, CancellationToken cancellationToken)
    {
        Task task;
        lock (scheduler)
        {
            if (!attaching.TryGetValue(project.Id, out task!) || task.IsFaulted || task.IsCanceled)
            {
                // Shared by concurrent opens, so it does not depend on one caller's token.
                task = Task.Run(() => RecoverAsync(project.Id, project.Store));
                attaching[project.Id] = task;
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Lets the queued jobs of an attached project run. Called when the project is open, after the recovery of its scenarios
    /// (accepted training results, orphaned builds), so that a recovered job never runs before them or against a closed project.
    /// </summary>
    internal void Start(string projectId)
    {
        lock (scheduler)
        {
            if (!attached.ContainsKey(projectId) || !started.Add(projectId)) return;
        }
        Dispatch();
    }

    /// <summary>Queued and running jobs of a project, from memory; blocked and finished jobs are not active.</summary>
    internal IReadOnlyList<JobInfo> ActiveJobs(string projectId)
    {
        lock (scheduler)
        {
            // A job that just saved its final state stays in the table until its slot is released.
            return active.Values.Select(job => job.Info)
                .Where(info => info.ProjectId == projectId && info.Status is JobStatus.Queued or JobStatus.Running).ToArray();
        }
    }

    /// <summary>Stored inputs of the queued and running jobs of one kind in a project, from memory (for example, the target of an import).</summary>
    internal IReadOnlyList<string> ActiveInputs(string projectId, string kind)
    {
        lock (scheduler)
        {
            return active.Values
                .Where(job => job.ProjectId == projectId && job.Registration.Kind == kind && job.Info.Status is JobStatus.Queued or JobStatus.Running)
                .Select(job => job.Input).ToArray();
        }
    }

    /// <summary>
    /// Recovery of a kind that can confirm its own outcome after a restart (a training run whose worker published a valid result
    /// before the application ended): its Interrupted job becomes Succeeded with full progress. Any other state stays as it is.
    /// </summary>
    internal async Task<JobInfo> CompleteInterruptedAsync(string projectId, Guid jobId)
    {
        var store = RequireAttached(projectId);
        var (info, changed) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var stored = await JobsTable.GetAsync(connection, transaction, projectId, jobId, token).ConfigureAwait(false) ?? throw NotFound(projectId, jobId);
            if (stored.Status != JobStatus.Interrupted) return (stored, false);
            var now = Now();
            var next = stored with
            {
                Status = JobStatus.Succeeded,
                Error = null,
                Progress = 100,
                FinishedAt = stored.FinishedAt ?? now,
                UpdatedAt = now,
                Sequence = stored.Sequence + 1,
            };
            await JobsTable.UpdateAsync(connection, transaction, next, token).ConfigureAwait(false);
            return (next, true);
        }).ConfigureAwait(false);
        if (changed)
        {
            LogTransition(info);
            Raise(info);
        }
        return info;
    }

    internal Task SaveProgressAsync(ActiveJob job, JobProgress progress) => ChangeAsync(job, info => info.Status == JobStatus.Running
        ? info with { Progress = progress.Percent, ProgressMessage = progress.Message, Processed = progress.Processed, Total = progress.Total }
        : null);

    /// <summary>
    /// One indexed query for unfinished jobs, then one write for the rows whose state the dead process left behind.
    /// Running jobs are not assumed alive: the process that ran them is gone.
    /// </summary>
    private async Task RecoverAsync(string projectId, ProjectStore store)
    {
        var rows = await store.ReadAsync((connection, token) => JobsTable.ReadUnfinishedAsync(connection, projectId, token)).ConfigureAwait(false);
        var now = Now();
        var changed = new List<JobInfo>();
        var runnable = new List<ActiveJob>();
        foreach (var row in rows)
        {
            var info = row.Info;
            JobRegistration? registration;
            lock (scheduler) registration = kinds.GetValueOrDefault(info.Kind);
            JobInfo? next;
            if (info.CancelRequested)
            {
                next = info with { Status = JobStatus.Cancelled, FinishedAt = now };
            }
            else if (registration is null)
            {
                next = info.Status == JobStatus.Blocked && info.Error?.Code == "jobs.kind_unknown"
                    ? null
                    : info with { Status = JobStatus.Blocked, Error = new JobError("jobs.kind_unknown", $"Job kind {info.Kind} is not known to this version.") };
            }
            else if (info.Status == JobStatus.Queued
                || (info.Status == JobStatus.Blocked && info.Error?.Code != "jobs.kind_unknown"))
            {
                // Unreadable input or checkpoint stays Blocked: the same version would fail on it again.
                next = null;
            }
            else if (info.StartedAt is null || registration.Resumable)
            {
                // Running or Blocked with a known kind: start again, continuing from the checkpoint and step receipts.
                next = info with { Status = JobStatus.Queued, Error = null };
            }
            else
            {
                next = info with
                {
                    Status = JobStatus.Interrupted,
                    Error = new JobError("jobs.interrupted", "The job was interrupted when the application stopped."),
                    FinishedAt = now,
                };
            }
            if (next is not null)
            {
                next = next with { Sequence = info.Sequence + 1, UpdatedAt = now };
                changed.Add(next);
            }
            var current = next ?? info;
            if (current.Status == JobStatus.Queued)
            {
                runnable.Add(new ActiveJob(store, registration!, current, row.Input, row.Checkpoint));
            }
        }
        if (changed.Count > 0)
        {
            await store.WriteAsync(async (connection, transaction, token) =>
            {
                foreach (var info in changed) await JobsTable.UpdateAsync(connection, transaction, info, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        if (rows.Count > 0)
        {
            logger.LogInformation("Project {ProjectId} jobs recovered: {Unfinished} unfinished, {Changed} changed, {Queued} queued.",
                projectId, rows.Count, changed.Count, runnable.Count);
        }
        // Raised before the jobs can be dispatched, so that a recovered snapshot never follows a newer one.
        foreach (var info in changed)
        {
            LogTransition(info);
            Raise(info);
        }
        lock (scheduler)
        {
            attached[projectId] = store;
            foreach (var job in runnable)
            {
                active.Add(job.Id, job);
                pending.Add(job);
            }
        }
        Dispatch();
    }

    private async Task<JobInfo> CancelStoredAsync(ProjectStore store, string projectId, Guid jobId)
    {
        // Only Blocked jobs are not active; other inactive jobs are finished and stay as they are.
        var (info, changed) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var stored = await JobsTable.GetAsync(connection, transaction, projectId, jobId, token).ConfigureAwait(false) ?? throw NotFound(projectId, jobId);
            if (stored.Status != JobStatus.Blocked) return (stored, false);
            var now = Now();
            var next = stored with { Status = JobStatus.Cancelled, CancelRequested = true, FinishedAt = now, UpdatedAt = now, Sequence = stored.Sequence + 1 };
            await JobsTable.UpdateAsync(connection, transaction, next, token).ConfigureAwait(false);
            return (next, true);
        }).ConfigureAwait(false);
        if (changed)
        {
            LogTransition(info);
            Raise(info);
        }
        return info;
    }

    /// <summary>Starts queued jobs in FIFO order while their resource has a free slot.</summary>
    private void Dispatch()
    {
        var start = new List<ActiveJob>();
        lock (scheduler)
        {
            for (var index = 0; index < pending.Count;)
            {
                var job = pending[index];
                if (!started.Contains(job.ProjectId))
                {
                    index++;
                    continue;
                }
                bool takes;
                if (job.Registration.Resource == JobResource.Gpu)
                {
                    // The slot may be held by a live check as well as by another GPU job.
                    takes = gpu.TryAcquire(job.Registration.Kind);
                }
                else
                {
                    takes = busyCpu < CpuSlots;
                    if (takes) busyCpu++;
                }
                if (takes)
                {
                    pending.RemoveAt(index);
                    start.Add(job);
                }
                else
                {
                    index++;
                }
            }
        }
        foreach (var job in start)
        {
            _ = Task.Run(() => RunAsync(job));
        }
    }

    private async Task RunAsync(ActiveJob job)
    {
        JobContext? context = null;
        Func<JobInfo, JobInfo> finish;
        try
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            var running = await ChangeAsync(job, info => info with { Status = JobStatus.Running, StartedAt = Now(), Error = null }).ConfigureAwait(false);
            LogTransition(running);
            context = new JobContext(this, job, logger);
            await job.Registration.Execute(job.Input, context).ConfigureAwait(false);
            finish = info => info with { Status = JobStatus.Succeeded, Progress = 100, Error = null };
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            finish = info => info with { Status = JobStatus.Cancelled };
        }
        catch (JobDataException error)
        {
            logger.LogWarning(error, "Job {JobId} {Kind} of project {ProjectId} is blocked: {Code}.", job.Id, job.Registration.Kind, job.ProjectId, error.Code);
            finish = info => info with { Status = JobStatus.Blocked, Error = new JobError(error.Code, error.Message) };
        }
        catch (CoreException error)
        {
            logger.LogWarning(error, "Job {JobId} {Kind} of project {ProjectId} operation {OperationId} failed with {Code}.",
                job.Id, job.Registration.Kind, job.ProjectId, job.Info.OperationId, error.Code);
            finish = info => info with { Status = JobStatus.Failed, Error = new JobError(error.Code, error.Message) };
        }
        catch (Exception error)
        {
            logger.LogError(error, "Job {JobId} {Kind} of project {ProjectId} operation {OperationId} failed.",
                job.Id, job.Registration.Kind, job.ProjectId, job.Info.OperationId);
            finish = info => info with { Status = JobStatus.Failed, Error = new JobError("jobs.failed", error.Message) };
        }

        var last = context?.Close();
        try
        {
            var final = await ChangeAsync(job, info =>
            {
                var withProgress = last is null ? info : info with
                {
                    Progress = last.Percent,
                    ProgressMessage = last.Message,
                    Processed = last.Processed,
                    Total = last.Total,
                };
                var result = context?.Result ?? info.Result;
                return finish(withProgress) with { FinishedAt = Now(), Result = result };
            }).ConfigureAwait(false);
            LogTransition(final);
        }
        catch (Exception error)
        {
            // The row stays unfinished; the next open of the project recovers it.
            logger.LogError(error, "Job {JobId} of project {ProjectId} final state was not saved.", job.Id, job.ProjectId);
        }
        finally
        {
            var releaseGpu = false;
            lock (scheduler)
            {
                active.Remove(job.Id);
                if (job.Registration.Resource == JobResource.Gpu) releaseGpu = true;
                else busyCpu--;
            }
            // Releasing the GPU slot dispatches the queue (GpuSlot.Released); a CPU slot is given out here.
            if (releaseGpu) gpu.Release();
            else Dispatch();
        }
    }

    /// <summary>Saves a change of an active job and raises its event; changes of one job are ordered by its gate.</summary>
    private async Task<JobInfo> ChangeAsync(ActiveJob job, Func<JobInfo, JobInfo?> change)
    {
        await job.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = job.Info;
            if (change(current) is not { } changed) return current;
            var next = changed with { Sequence = current.Sequence + 1, UpdatedAt = Now() };
            await job.Store.WriteAsync((connection, transaction, token) => JobsTable.UpdateAsync(connection, transaction, next, token),
                CancellationToken.None).ConfigureAwait(false);
            job.Info = next;
            Raise(next);
            return next;
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private bool UsesGpu(string kind)
    {
        lock (scheduler) return kinds.GetValueOrDefault(kind)?.Resource == JobResource.Gpu;
    }

    /// <summary>The text is trimmed and cut, so are the values of a code; a label without a code and text is null.</summary>
    private static CodedText? CleanLabel(CodedText? label)
    {
        if (label is null) return null;
        var text = Cut(label.Text);
        var args = label.Args?.ToDictionary(pair => pair.Key, pair => Cut(pair.Value) ?? "", StringComparer.Ordinal);
        if (label.Code is null && text is null) return null;
        return label with { Text = text, Args = args };

        static string? Cut(string? value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            return trimmed.Length <= JobInfo.MaxLabelLength ? trimmed : trimmed[..JobInfo.MaxLabelLength].TrimEnd();
        }
    }

    private ProjectStore RequireAttached(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (scheduler)
        {
            return attached.GetValueOrDefault(projectId)
                ?? throw new CoreException("jobs.project_not_open", $"Project {projectId} is not open; its jobs are not loaded.");
        }
    }

    private void Raise(JobInfo info)
    {
        if (JobChanged is not { } handlers) return;
        var args = new JobChangedEventArgs(info);
        foreach (EventHandler<JobChangedEventArgs> subscriber in handlers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception error)
            {
                // Observers cannot change the outcome of a job.
                logger.LogError(error, "Job event observer failed.");
            }
        }
    }

    private void LogTransition(JobInfo info)
    {
        if (info.Status is JobStatus.Failed or JobStatus.Interrupted or JobStatus.Blocked)
        {
            logger.LogWarning("Job {JobId} {Kind} of project {ProjectId} operation {OperationId} is {Status} with {Code}.",
                info.Id, info.Kind, info.ProjectId, info.OperationId, info.Status, info.Error?.Code);
        }
        else
        {
            logger.LogInformation("Job {JobId} {Kind} of project {ProjectId} operation {OperationId} is {Status}.",
                info.Id, info.Kind, info.ProjectId, info.OperationId, info.Status);
        }
    }

    private static CoreException NotFound(string projectId, Guid jobId) => new("jobs.not_found", $"Job {jobId} was not found in project {projectId}.");

    // Stored times have millisecond precision; snapshots use the same so that memory and project.db agree.
    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static string KindOf(Type contract)
    {
        if (!contract.IsInterface)
            throw new ArgumentException("A job key must be a contract interface.", nameof(contract));
        var key = contract.GetCustomAttribute<JobKindAttribute>()?.Key;
        if (key is null || key.Length > 100 || !KindPattern().IsMatch(key))
            throw new ArgumentException($"Job contract {contract.FullName} needs [JobKind] with a key like 'images.import'.", nameof(contract));
        return key;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();
}
