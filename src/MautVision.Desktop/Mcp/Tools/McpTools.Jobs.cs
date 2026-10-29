using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Jobs.RequestDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string JobSchema = """
        { "type": "object", "description": "Job snapshot: status queued|running|succeeded|failed|cancelled|interrupted|blocked, progress 0-100 or null, error {code, message}, result of the job kind.",
          "properties": { "id": { "type": "string" }, "kind": { "type": "string" }, "status": { "type": "string" }, "progress": { "type": ["number", "null"] },
            "error": { "type": ["object", "null"] }, "result": {} } }
        """;

    private IEnumerable<McpToolDefinition> JobTools()
    {
        yield return McpToolDefinition.Create("list_jobs", "List jobs", McpToolClass.Read, true,
            """
            Jobs of a project (imports, generation, dataset builds, training, evaluations, exports), the same list the window shows. active=true
            returns queued and running jobs in queue order with counts; otherwise the history, newest first. Use get_job or wait_job with a jobId.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "active": { "type": "boolean", "default": false, "description": "Only queued and running jobs." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 30 },
                "cursor": { "type": "string", "description": "nextCursor of the previous page." } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": {
                "jobs": { "type": "array", "items": {{JobSchema}} },
                "total": { "type": ["integer", "null"], "description": "Active jobs in total (active=true)." },
                "nextCursor": { "type": ["string", "null"] } },
              "required": ["jobs"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "active", "limit", "cursor");
                var projectId = call.Arguments.String("projectId");
                var limit = call.Arguments.Int("limit", 1, 200, 30);
                var cursor = call.Arguments.OptionalGuid("cursor");
                await PrepareAsync(projectId, call.CancellationToken);
                if (call.Arguments.Bool("active", false))
                {
                    var page = await dependencies.Jobs.ListActiveAsync(new ListActiveJobsRequestDto(projectId, limit, cursor), call.CancellationToken);
                    var active = new JsonObject
                    {
                        ["jobs"] = McpToolResult.Node(page.Items.Select(item => item.Job).ToArray()),
                        ["waitingForGpu"] = McpToolResult.Node(page.Items.Where(item => item.WaitingForGpu).Select(item => item.Job.Id).ToArray()),
                        ["total"] = page.Total,
                        ["countsByKind"] = McpToolResult.Node(page.CountsByKind),
                        ["nextCursor"] = page.NextCursor?.ToString("D"),
                    };
                    return Result(active, $"{page.Total} active jobs.");
                }
                var jobs = await dependencies.Jobs.ListAsync(new ListJobsRequestDto(projectId, limit, cursor), call.CancellationToken);
                var history = new JsonObject
                {
                    ["jobs"] = McpToolResult.Node(jobs),
                    ["nextCursor"] = jobs.Length == limit ? jobs[^1].Id.ToString("D") : null,
                };
                return Result(history, $"{jobs.Length} jobs.");
            });

        yield return McpToolDefinition.Create("get_job", "Get job", McpToolClass.Read, true,
            "The current snapshot of a job without waiting: status, progress, error and result. jobId comes from the tool that started the job or from list_jobs.",
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "jobId": { "type": "string" } },
              "required": ["projectId", "jobId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "job": {{JobSchema}} }, "required": ["job"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "jobId");
                var projectId = call.Arguments.String("projectId");
                var jobId = call.Arguments.Guid("jobId");
                await PrepareAsync(projectId, call.CancellationToken);
                var job = await dependencies.Jobs.GetAsync(new GetJobRequestDto(projectId, jobId), call.CancellationToken);
                return Result(Wrap("job", job), $"Job {job.Id:D} ({job.Kind}) is {job.Status}" + (job.Progress is { } progress ? $", {progress:0.#}%." : "."));
            });

        yield return McpToolDefinition.Create("wait_job", "Wait for job", McpToolClass.Read, true,
            """
            Waits until a job finishes or timeoutSeconds (1-120, default 30) pass and returns its snapshot; timedOut=true means it still runs —
            call wait_job again. Pass a progressToken to receive notifications/progress while waiting. Waiting never cancels the job.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "jobId": { "type": "string" },
                "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 120, "default": 30 } },
              "required": ["projectId", "jobId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "job": {{JobSchema}}, "finished": { "type": "boolean" }, "timedOut": { "type": "boolean" } },
              "required": ["job", "finished", "timedOut"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "jobId", "timeoutSeconds");
                var projectId = call.Arguments.String("projectId");
                var jobId = call.Arguments.Guid("jobId");
                var timeout = call.Arguments.Int("timeoutSeconds", 1, McpJobWaiter.MaxWaitSeconds, 30);
                await PrepareAsync(projectId, call.CancellationToken);
                var (job, timedOut) = await McpJobWaiter.WaitAsync(Core.Jobs, projectId, jobId, TimeSpan.FromSeconds(timeout), call);
                var finished = McpJobWaiter.IsFinished(job.Status);
                var structured = new JsonObject
                {
                    ["job"] = McpToolResult.Node(McpJobWaiter.Map(job)),
                    ["finished"] = finished,
                    ["timedOut"] = timedOut,
                };
                var status = McpJobWaiter.Status(job.Status);
                return Result(structured, timedOut
                    ? $"Job {job.Id:D} is still {status} after {timeout} s" + (job.Progress is { } progress ? $" ({progress:0.#}%)." : ".")
                    : $"Job {job.Id:D} finished: {status}" + (job.Error is { } error ? $" ({error.Code})." : "."));
            });

        yield return McpToolDefinition.Create("cancel_job", "Cancel job", McpToolClass.Write, true,
            """
            Cancels a job: a queued job is cancelled at once, a running one gets cancelRequested and stops soon (follow it with wait_job). A
            finished job does not change. Cancelling twice is safe.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "jobId": { "type": "string" } },
              "required": ["projectId", "jobId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "job": {{JobSchema}} }, "required": ["job"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "jobId");
                var projectId = call.Arguments.String("projectId");
                var jobId = call.Arguments.Guid("jobId");
                await PrepareAsync(projectId, call.CancellationToken);
                var job = await dependencies.Jobs.CancelAsync(new CancelJobRequestDto(projectId, jobId), call.CancellationToken);
                return Result(Wrap("job", job), job.CancelRequested && job.Status == "running"
                    ? $"Cancellation of job {job.Id:D} requested."
                    : $"Job {job.Id:D} is {job.Status}.");
            });
    }
}
