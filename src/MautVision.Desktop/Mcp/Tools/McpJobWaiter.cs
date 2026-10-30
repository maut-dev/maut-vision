using System.Globalization;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

/// <summary>
/// Long operations for MCP. A tool that starts a job answers at once with <c>{ jobId, status, job }</c>; the client follows it with
/// wait_job or get_job. While a call waits (wait_job, or a starting tool given waitSeconds), every job.changed snapshot of that job becomes
/// notifications/progress when the client passed a progressToken. A request never waits longer than 120 s.
/// </summary>
internal static class McpJobWaiter
{
    internal const int MaxWaitSeconds = 120;

    internal static bool IsFinished(JobStatus status) => status is not (JobStatus.Queued or JobStatus.Running);

    /// <summary>The answer of a tool that started a job, after waiting up to waitSeconds (0 — at once).</summary>
    internal static async Task<McpToolResult> StartedAsync(IJobsManager jobs, JobInfo job, int waitSeconds, McpToolCall call, string what)
    {
        (JobInfo Job, bool TimedOut) waited = waitSeconds > 0 ? await WaitAsync(jobs, job.ProjectId, job.Id, TimeSpan.FromSeconds(waitSeconds), call) : (job, false);
        var snapshot = waited.Job;
        var structured = new JsonObject
        {
            ["jobId"] = snapshot.Id.ToString("D"),
            ["status"] = Status(snapshot.Status),
            ["job"] = McpToolResult.Node(JobResponseMapper.Map(snapshot)),
        };
        var text = IsFinished(snapshot.Status)
            ? $"{what}: job {snapshot.Id:D} {Status(snapshot.Status)}."
            : $"{what}: job {snapshot.Id:D} is {Status(snapshot.Status)}; follow it with wait_job (projectId, jobId).";
        return new McpToolResult(structured, text);
    }

    /// <summary>
    /// Waits until the job finishes or the timeout passes; snapshots go to the client as progress. Returns the last snapshot and whether it
    /// timed out. The job keeps running after a timeout.
    /// </summary>
    internal static async Task<(JobInfo Job, bool TimedOut)> WaitAsync(IJobsManager jobs, string projectId, Guid jobId, TimeSpan timeout, McpToolCall call)
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<JobInfo>(new() { SingleReader = true });
        void OnChanged(object? sender, JobChangedEventArgs args)
        {
            if (args.Job.Id == jobId && args.Job.ProjectId == projectId) changes.Writer.TryWrite(args.Job);
        }
        // notifications/progress must grow: a snapshot that would not move it forward is not sent.
        var reported = -1.0;
        async Task ReportAsync(JobInfo job)
        {
            var value = job.Progress ?? (IsFinished(job.Status) ? 100 : 0);
            if (!call.WantsProgress || value <= reported) return;
            reported = value;
            var message = Status(job.Status) + (job.Processed is { } processed && job.Total is { } total
                ? string.Create(CultureInfo.InvariantCulture, $" {processed}/{total}")
                : "");
            try
            {
                await call.ReportProgressAsync(value, 100, message);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A client that went away does not stop the wait; the answer reports the state.
            }
        }
        jobs.JobChanged += OnChanged;
        try
        {
            var current = await jobs.GetAsync(projectId, jobId, call.CancellationToken);
            await ReportAsync(current);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(call.CancellationToken);
            deadline.CancelAfter(timeout);
            while (!IsFinished(current.Status))
            {
                JobInfo next;
                try
                {
                    next = await changes.Reader.ReadAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!call.CancellationToken.IsCancellationRequested)
                {
                    // A snapshot may have been missed between the read and the subscription: the final state is read again.
                    current = await jobs.GetAsync(projectId, jobId, call.CancellationToken);
                    return (current, !IsFinished(current.Status));
                }
                if (next.Sequence <= current.Sequence) continue;
                current = next;
                await ReportAsync(current);
            }
            return (current, false);
        }
        finally
        {
            jobs.JobChanged -= OnChanged;
        }
    }

    internal static string Status(JobStatus status) => JobResponseMapper.MapStatus(status);

    internal static JobResponseDto Map(JobInfo job) => JobResponseMapper.Map(job);
}
