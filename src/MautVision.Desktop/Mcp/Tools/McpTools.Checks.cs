using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Checks;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Checks.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Training.RequestDto;
using MautVision.Desktop.Mcp.View;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    internal const int MaxScreenCheckSeconds = 300;

    /// <summary>
    /// Live checks started through MCP: the session that started them, the project, the video path (for frame views) and the last request that
    /// touched them. They stop with stop_check, when another project is opened (Core), when their MCP session ends, when no MCP request touched
    /// them for <see cref="McpCleanup.CheckIdle"/> (clients served per request have no session end) and when the server stops.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, McpCheckOwner> mcpChecks = new();

    private sealed class McpCheckOwner(string session, string projectId, string? path)
    {
        private long touched = Environment.TickCount64;

        internal string Session { get; } = session;

        internal string ProjectId { get; } = projectId;

        internal string? Path { get; } = path;

        internal long Touched => Volatile.Read(ref touched);

        internal void Touch() => Volatile.Write(ref touched, Environment.TickCount64);
    }

    private ChecksController? checksController;

    /// <summary>The checks controller of the page over the same Core, with the registries of the MCP tools (no dialogs).</summary>
    private ChecksController ChecksApi => checksController ??= new ChecksController(Core.Projects, Core.Jobs, Core.Screen, importResources, importSelections,
        NoMcpDialogs, Core.Media);

    private const string CheckParametersSchema = """
        "confidence": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.25, "description": "Confidence threshold." },
        "maxDetections": { "type": "integer", "minimum": 1, "description": "Detections per frame; omit for all model candidates." },
        "device": { "type": "string", "description": "\"cpu\" or a GPU index (devices of get_training_defaults); default — the first GPU, or cpu without one." }
        """;

    private const string LiveCheckSchema = """
        { "type": "object", "description": "Live check: id (checkId), mode image|video|screen, state preparing|running|cancelling|succeeded|failed|cancelled, settings, source, progress, summary by class, lastFrame, journal, warnings, error, stopReason user|project_switched|session_ended|time_limit. The latest frame with boxes: view { check: id }.",
          "properties": { "id": { "type": "string" }, "mode": { "type": "string" }, "state": { "type": "string" }, "summary": { "type": "object" } },
          "required": ["id", "mode", "state", "summary"] }
        """;

    private const string RegionPixelsSchema = """
        { "type": "object", "description": "Crop in pixels of the frame (for the screen — from the top-left corner of the monitor); width and height must equal the model input of the version. Omit when the frame is not larger than the input: the whole frame is letterboxed.",
          "properties": { "x": { "type": "integer", "minimum": 0 }, "y": { "type": "integer", "minimum": 0 }, "width": { "type": "integer", "minimum": 1 }, "height": { "type": "integer", "minimum": 1 } },
          "required": ["x", "y", "width", "height"] }
        """;

    private IEnumerable<McpToolDefinition> CheckTools()
    {
        yield return McpToolDefinition.Create("evaluate_model", "Evaluate model on dataset", McpToolClass.Write, false,
            """
            Evaluates a model version on a split of a dataset version (split val or test, default test): mAP50, mAP50-95, precision and recall,
            also per class. The dataset must have the classes of the version in the same order and its input size. A GPU job that waits for the
            GPU in the queue: returns { jobId, evaluationId, status, job } at once; after wait_job the metrics are in list_evaluations.
            Refusals: checks.evaluation_split_empty, checks.evaluation_classes_mismatch, checks.evaluation_input_mismatch, checks.runtime_*.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}}, "datasetVersionId": { "type": "string" },
                "split": { "type": "string", "enum": ["val", "test"], "default": "test" },
                "device": { "type": "string" }, "batch": { "type": "integer", "minimum": 1, "maximum": 1024, "default": 16 },
                {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "versionId", "datasetVersionId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "evaluationId": { "type": "string" } },
              "required": ["jobId", "evaluationId", "status", "job"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "datasetVersionId", "split", "device", "batch", "operationId", "waitSeconds");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var split = arguments.OptionalString("split") ?? "test";
                if (split is not ("val" or "test")) throw McpToolException.InvalidArgument("split", "must be val or test");
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var device = arguments.OptionalString("device") ?? await DefaultDeviceAsync(call.CancellationToken);
                var evaluation = await ChecksApi.StartEvaluationAsync(new StartModelEvaluationRequestDto(project.Id, arguments.Guid("versionId"),
                    arguments.Guid("datasetVersionId"), split, device, arguments.Has("batch") ? arguments.Int("batch", 1, 1024) : null, OperationId(arguments)),
                    call.CancellationToken);
                var job = await Core.Jobs.GetAsync(project.Id, evaluation.Id, call.CancellationToken);
                var result = await McpJobWaiter.StartedAsync(Core.Jobs, job, waitSeconds, call,
                    $"Evaluation of version {evaluation.VersionNumber} on {evaluation.Dataset.Name} ({split})");
                result.Structured["evaluationId"] = evaluation.Id.ToString("D");
                return result;
            });

        yield return McpToolDefinition.Create("list_evaluations", "List evaluations", McpToolClass.Read, true,
            """
            Dataset evaluations of a project, newest first, filtered by versionId, datasetVersionId and split; each has its state, metrics and
            per-class metrics. latest is the comparison matrix of the Checks section: the latest successful evaluation of every version on each
            "dataset · split" pair (narrowed by datasetVersionId and split when given).
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" }, "datasetVersionId": { "type": "string" },
                "split": { "type": "string", "enum": ["val", "test"] },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 30 }, "cursor": { "type": "string" } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "evaluations": { "type": "array", "items": { "type": "object" } }, "total": { "type": "integer" },
                "latest": { "type": "array", "items": { "type": "object" } }, "nextCursor": { "type": ["string", "null"] } },
              "required": ["evaluations", "total", "latest"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "datasetVersionId", "split", "limit", "cursor");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var split = arguments.OptionalString("split");
                if (split is not (null or "val" or "test")) throw McpToolException.InvalidArgument("split", "must be val or test");
                var limit = arguments.Int("limit", 1, 200, 30);
                var datasetVersionId = arguments.OptionalGuid("datasetVersionId");
                var page = await ChecksApi.ListEvaluationsAsync(new ListModelEvaluationsRequestDto(project.Id, arguments.OptionalGuid("versionId"), datasetVersionId,
                    split, limit, arguments.OptionalGuid("cursor")), call.CancellationToken);
                var latest = await ChecksApi.ListLatestEvaluationsAsync(new ListLatestModelEvaluationsRequestDto(project.Id, datasetVersionId, split),
                    call.CancellationToken);
                var structured = new JsonObject
                {
                    ["evaluations"] = new JsonArray(page.Items.Select(item => (JsonNode?)EvaluationNode(item)).ToArray()),
                    ["total"] = page.Total,
                    ["latest"] = new JsonArray(latest.OrderBy(item => item.Dataset.Name, StringComparer.CurrentCulture).ThenBy(item => item.Split)
                        .ThenByDescending(item => item.VersionNumber).Select(item => (JsonNode?)new JsonObject
                        {
                            ["versionId"] = item.VersionId.ToString("D"), ["versionNumber"] = item.VersionNumber,
                            ["datasetVersionId"] = item.Dataset.VersionId.ToString("D"), ["datasetName"] = item.Dataset.Name, ["split"] = item.Split,
                            ["evaluationId"] = item.Id.ToString("D"), ["metrics"] = McpToolResult.Node(item.Metrics),
                        }).ToArray()),
                    ["nextCursor"] = page.Items.Length == limit ? page.Items[^1].Id.ToString("D") : null,
                };
                return Result(structured, $"{page.Items.Length} of {page.Total} evaluations; {latest.Length} latest results in the comparison matrix.");
            });

        yield return McpToolDefinition.Create("predict_image", "Check image", McpToolClass.Write, false,
            """
            Runs a model version on one image (path — an absolute PNG, JPEG, WebP or BMP path, or uploadId from upload_file) and returns the
            detections (class, confidence, box as fractions 0-1 of the image) and the image with the boxes drawn as image content. It is a live
            check: it needs the GPU slot and is refused at once while training, an evaluation or another check holds it
            (checks.gpu_busy_training, checks.gpu_busy_evaluation, checks.gpu_busy_prediction). If it does not finish within waitSeconds,
            finished=false: read it later with get_check (checkId).
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}},
                "path": { "type": "string" }, "uploadId": { "type": "string" },
                {{CheckParametersSchema}},
                "maxSize": { "type": "integer", "minimum": 128, "maximum": 2048, "default": 1024 },
                {{OperationIdSchema}},
                "waitSeconds": { "type": "integer", "minimum": 1, "maximum": 120, "default": 60 } },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "checkId": { "type": "string" }, "state": { "type": "string" }, "finished": { "type": "boolean" },
                "detections": { "type": "array", "items": { "type": "object", "properties": { "class": { "type": "string" }, "classIndex": { "type": "integer" },
                  "confidence": { "type": "number" }, "x": { "type": "number" }, "y": { "type": "number" }, "width": { "type": "number" }, "height": { "type": "number" } } } },
                "frame": { "type": ["object", "null"] }, "picture": { "type": ["object", "null"] }, "error": { "type": ["object", "null"] } },
              "required": ["checkId", "state", "finished", "detections"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "path", "uploadId", "confidence", "maxDetections", "device", "maxSize", "operationId", "waitSeconds");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var path = SourceFile(arguments);
                var maxSize = arguments.Int("maxSize", McpViewRenderer.MinMaxSize, McpViewRenderer.MaxMaxSize, McpViewRenderer.DefaultMaxSize);
                var waitSeconds = arguments.Int("waitSeconds", 1, McpJobWaiter.MaxWaitSeconds, 60);
                var parameters = await CheckParametersAsync(arguments, call.CancellationToken);
                var token = importSelections.Register([path], FileSelectionKind.Files, null, [FileStamp.Of(path)]);
                var started = await ChecksApi.StartImageAsync(new StartImageCheckRequestDto(project.Id, arguments.Guid("versionId"), token, parameters,
                    OperationId(arguments)), call.CancellationToken);
                mcpChecks[started.Id] = new McpCheckOwner(call.SessionKey, project.Id, path);
                var (check, finished) = await WaitCheckAsync(project, started.Id, TimeSpan.FromSeconds(waitSeconds), call);
                var detections = check.LastFrame?.Detections ?? [];
                var structured = new JsonObject
                {
                    ["checkId"] = check.Id.ToString("D"),
                    ["state"] = StateKey(check.State),
                    ["finished"] = finished,
                    ["detections"] = new JsonArray(detections.Select(item => (JsonNode?)DetectionNode(item)).ToArray()),
                    ["frame"] = check.LastFrame is { } frame ? new JsonObject { ["width"] = frame.Width, ["height"] = frame.Height, ["inferenceMs"] = frame.InferenceMs } : null,
                    ["error"] = check.Error is { } error ? new JsonObject { ["code"] = error.Code, ["message"] = McpErrors.Text(error.Code, null) } : null,
                };
                if (!finished) return Result(structured, $"Check {check.Id:D} is still {StateKey(check.State)}; read it with get_check.");
                if (check.State == LiveCheckState.Failed && check.Error is { } failure) throw new CoreException(failure.Code, failure.Message);
                IReadOnlyList<McpImage>? images = null;
                if (project.Checks.GetPreview(check.Id) is { } preview)
                {
                    var rendered = await Task.Run(() => McpViewRenderer.Render(preview.Content, null, Boxes(detections), maxSize, false), call.CancellationToken);
                    structured["picture"] = new JsonObject { ["width"] = rendered.Width, ["height"] = rendered.Height, ["mimeType"] = rendered.MimeType };
                    images = [new McpImage(rendered.Content, rendered.MimeType)];
                }
                return Result(structured, $"{detections.Count} detections" + (detections.Count > 0
                    ? ": " + string.Join(", ", detections.Take(20).Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Name} {item.Confidence:0.00}"))) + "."
                    : "."), images);
            });

        yield return McpToolDefinition.Create("predict_video", "Check video", McpToolClass.Write, false,
            """
            Runs a model version over a video file (absolute path; MP4, MKV, AVI, MOV, WebM, M4V), reading a frame every stepSeconds. region
            crops the input-sized part of the frame in video pixels; without it the whole frame is letterboxed. A live check that holds the GPU
            slot (refused at once while it is busy). Returns { checkId, check } after waitSeconds (default 0); follow it with get_check, which
            gives the summary by class and the frames with detections, each with view arguments for view { videoFrame, timeSeconds } (with
            region the view shows the analyzed crop). view { check: checkId } shows the latest analyzed frame with boxes. stop_check stops it.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}},
                "path": { "type": "string", "description": "Absolute path of the video." },
                "stepSeconds": { "type": "number", "minimum": 0.001, "maximum": 3600, "default": 1 },
                "region": {{RegionPixelsSchema}},
                {{CheckParametersSchema}},
                {{OperationIdSchema}},
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0 } },
              "required": ["projectId", "versionId", "path"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "checkId": { "type": "string" }, "state": { "type": "string" }, "finished": { "type": "boolean" }, "check": {{LiveCheckSchema}},
                "frames": { "type": "array", "items": { "type": "object" } } },
              "required": ["checkId", "state", "finished", "check"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "path", "stepSeconds", "region", "confidence", "maxDetections", "device", "operationId", "waitSeconds");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var path = dependencies.Paths.ResolveFile(arguments.String("path"));
                if (!VideoFormats.IsVideo(path)) throw new CoreException("video.unsupported", "The file is not a supported video.");
                var stepMs = (int)Math.Max(1, Math.Round(arguments.Double("stepSeconds", 0.001, 3600, 1) * 1000));
                var region = RegionPixels(arguments);
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var parameters = await CheckParametersAsync(arguments, call.CancellationToken);
                var video = await Core.Media.ProbeVideoAsync(path, call.CancellationToken);
                var token = importSelections.Register([path], FileSelectionKind.Video, video, [FileStamp.Of(path)]);
                var started = await ChecksApi.StartVideoAsync(new StartVideoCheckRequestDto(project.Id, arguments.Guid("versionId"), token, parameters, stepMs,
                    region, OperationId(arguments)), call.CancellationToken);
                mcpChecks[started.Id] = new McpCheckOwner(call.SessionKey, project.Id, path);
                return await CheckResultAsync(project, started.Id, waitSeconds, 50, call);
            });

        yield return McpToolDefinition.Create("get_check", "Get live check", McpToolClass.Read, true,
            """
            A live check (predict_image, predict_video, start_screen_check or one the user started) by checkId: state, progress, summary by
            class, warnings, error and up to frameLimit frames with detections (for a video the first ones, for the screen the latest). waitSeconds
            waits until the check finishes first. Live checks live in memory until the application exits.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "checkId": { "type": "string" },
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0 },
                "frameLimit": { "type": "integer", "minimum": 0, "maximum": 200, "default": 50 } },
              "required": ["projectId", "checkId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "checkId": { "type": "string" }, "state": { "type": "string" }, "finished": { "type": "boolean" }, "check": {{LiveCheckSchema}},
                "frames": { "type": "array", "items": { "type": "object" } } },
              "required": ["checkId", "state", "finished", "check"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "checkId", "waitSeconds", "frameLimit");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                return await CheckResultAsync(project, call.Arguments.Guid("checkId"), call.Arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0),
                    call.Arguments.Int("frameLimit", 0, 200, 50), call);
            });

        yield return McpToolDefinition.Create("list_monitors", "List monitors", McpToolClass.Read, true,
            "Monitors in physical pixels: index (for capture_screen and start_screen_check), position, size, primary. Nothing is captured.",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            """{ "type": "object", "properties": { "monitors": { "type": "array", "items": { "type": "object" } } }, "required": ["monitors"] }""",
            async call =>
            {
                call.Arguments.Only();
                var monitors = await ChecksApi.ListMonitorsAsync(new ListMonitorsRequestDto(), call.CancellationToken);
                return Result(Wrap("monitors", monitors), string.Join("; ", monitors.Select(item => $"monitor {item.Index}: {item.Width}×{item.Height}{(item.IsPrimary ? " primary" : "")}")) + ".");
            });

        yield return McpToolDefinition.Create("capture_screen", "Capture screen", McpToolClass.Read, false,
            """
            Takes a screenshot of a monitor (default — the primary one) and returns it as image content. The user confirms every capture in the
            application window at any access level (mcp.confirmation_denied, mcp.confirmation_timeout). The picture is reduced to maxSize; scale
            is picture pixels per monitor pixel, so a region for start_screen_check is pictureCoordinate / scale in monitor pixels.
            """,
            """
            { "type": "object", "properties": {
                "monitor": { "type": "integer", "minimum": 1 },
                "maxSize": { "type": "integer", "minimum": 128, "maximum": 2048, "default": 1024 },
                "format": { "type": "string", "enum": ["jpeg", "png"], "default": "jpeg" } },
              "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "monitor": { "type": "object" }, "monitors": { "type": "array", "items": { "type": "object" } },
                "picture": { "type": "object" }, "scale": { "type": "number" } },
              "required": ["monitor", "picture", "scale"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("monitor", "maxSize", "format");
                var format = arguments.OptionalString("format") ?? "jpeg";
                if (format is not ("jpeg" or "png")) throw McpToolException.InvalidArgument("format", "must be jpeg or png");
                var maxSize = arguments.Int("maxSize", McpViewRenderer.MinMaxSize, McpViewRenderer.MaxMaxSize, McpViewRenderer.DefaultMaxSize);
                var monitors = Core.Screen.ListMonitors();
                var index = arguments.Has("monitor") ? arguments.Int("monitor", 1, 1000) : (monitors.FirstOrDefault(item => item.IsPrimary) ?? monitors.FirstOrDefault())?.Index ?? 1;
                if (monitors.All(item => item.Index != index)) throw new CoreException("screen.monitor_not_found", "The monitor was not found.");
                await call.ConfirmAsync("capture_screen", new Dictionary<string, string> { ["monitor"] = index.ToString(CultureInfo.InvariantCulture) });
                var shot = await Core.Screen.CaptureAsync(index, call.CancellationToken);
                var rendered = await Task.Run(() => McpViewRenderer.Render(shot.Content, null, [], maxSize, format == "png"), call.CancellationToken);
                var structured = new JsonObject
                {
                    ["monitor"] = McpToolResult.Node(ChecksResponseMapperMonitor(shot.Monitor)),
                    ["monitors"] = McpToolResult.Node(shot.Monitors.Select(ChecksResponseMapperMonitor).ToArray()),
                    ["picture"] = new JsonObject { ["width"] = rendered.Width, ["height"] = rendered.Height, ["mimeType"] = rendered.MimeType },
                    ["scale"] = Math.Round(rendered.Width / (double)shot.Monitor.Width, 6),
                };
                return Result(structured, $"Monitor {shot.Monitor.Index} {shot.Monitor.Width}×{shot.Monitor.Height}; picture {rendered.Width}×{rendered.Height}.",
                    [new McpImage(rendered.Content, rendered.MimeType)]);
            });

        yield return McpToolDefinition.Create("start_screen_check", "Start screen check", McpToolClass.Write, false,
            $$"""
            Checks the screen live with a model version: fps frames per second of a monitor (list_monitors), for at most durationSeconds
            (1-{{MaxScreenCheckSeconds}}). region is the input-sized crop in monitor pixels (see capture_screen); omit it when the monitor is
            not larger than the input. The user confirms it in the application window every time. A live check that holds the GPU slot (refused
            at once while it is busy). It stops by stop_check, at its time limit (succeeded with the warning live_time_limit), when the user opens
            another project, when this MCP session ends or the server stops. Follow it with get_check; view { check: checkId } shows the latest frame.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}},
                "monitor": { "type": "integer", "minimum": 1 },
                "region": {{RegionPixelsSchema}},
                "fps": { "type": "number", "exclusiveMinimum": 0, "maximum": 60, "default": 3 },
                "durationSeconds": { "type": "integer", "minimum": 1, "maximum": {{MaxScreenCheckSeconds}}, "default": 60 },
                {{CheckParametersSchema}},
                {{OperationIdSchema}},
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0 } },
              "required": ["projectId", "versionId", "monitor"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "checkId": { "type": "string" }, "state": { "type": "string" }, "finished": { "type": "boolean" }, "check": {{LiveCheckSchema}},
                "frames": { "type": "array", "items": { "type": "object" } } },
              "required": ["checkId", "state", "finished", "check"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "monitor", "region", "fps", "durationSeconds", "confidence", "maxDetections", "device", "operationId",
                    "waitSeconds");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var versionId = arguments.Guid("versionId");
                var monitor = arguments.Int("monitor", 1, 1000);
                var fps = arguments.Double("fps", 0, 60, 3);
                if (fps <= 0) throw McpToolException.InvalidArgument("fps", "must be greater than 0");
                var duration = arguments.Int("durationSeconds", 1, MaxScreenCheckSeconds, 60);
                var region = RegionPixels(arguments);
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var parameters = await CheckParametersAsync(arguments, call.CancellationToken);
                var version = await project.Training.GetVersionAsync(versionId, call.CancellationToken);
                if (Core.Screen.ListMonitors().All(item => item.Index != monitor)) throw new CoreException("screen.monitor_not_found", "The monitor was not found.");
                await call.ConfirmAsync("start_screen_check", new Dictionary<string, string>
                {
                    ["name"] = project.Name,
                    ["version"] = version.Number.ToString(CultureInfo.InvariantCulture),
                    ["monitor"] = monitor.ToString(CultureInfo.InvariantCulture),
                    ["seconds"] = duration.ToString(CultureInfo.InvariantCulture),
                });
                var started = await ChecksApi.StartScreenAsync(new StartScreenCheckRequestDto(project.Id, versionId, monitor, parameters, fps, duration, region,
                    OperationId(arguments)), call.CancellationToken);
                mcpChecks[started.Id] = new McpCheckOwner(call.SessionKey, project.Id, null);
                return await CheckResultAsync(project, started.Id, waitSeconds, 20, call);
            });

        yield return McpToolDefinition.Create("stop_check", "Stop live check", McpToolClass.Write, true,
            """
            Stops a live check (as its "Stop" button): it goes to cancelling and then cancelled with stopReason user; a finished check does not
            change. Waits up to waitSeconds (default 10) for it to end and returns its summary.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "checkId": { "type": "string" },
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 10 } },
              "required": ["projectId", "checkId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "checkId": { "type": "string" }, "state": { "type": "string" }, "finished": { "type": "boolean" }, "check": {{LiveCheckSchema}},
                "frames": { "type": "array", "items": { "type": "object" } } },
              "required": ["checkId", "state", "finished", "check"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "checkId", "waitSeconds");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var checkId = call.Arguments.Guid("checkId");
                await ChecksApi.StopAsync(new StopCheckRequestDto(project.Id, checkId), call.CancellationToken);
                return await CheckResultAsync(project, checkId, call.Arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 10), 0, call);
            });
    }

    /// <summary>The MCP session ended: the live checks it started stop (session_ended).</summary>
    internal void EndSession(string session)
    {
        foreach (var (checkId, owner) in mcpChecks.ToArray())
        {
            if (owner.Session != session || !mcpChecks.TryRemove(checkId, out _)) continue;
            _ = StopMcpCheckAsync(checkId, owner.ProjectId);
        }
    }

    /// <summary>An MCP request named the check (get_check, stop_check, view): it is still followed.</summary>
    internal void TouchCheck(Guid checkId)
    {
        if (mcpChecks.TryGetValue(checkId, out var owner)) owner.Touch();
    }

    /// <summary>
    /// The sweep of the server: a running check no MCP request touched for <paramref name="idle"/> stops (session_ended) — its client is gone
    /// or forgot it. Returns how many were stopped.
    /// </summary>
    internal int StopIdleChecks(TimeSpan idle)
    {
        var limit = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        var stopped = 0;
        foreach (var (checkId, owner) in mcpChecks.ToArray())
        {
            if (owner.Touched > limit || !mcpChecks.TryRemove(checkId, out _)) continue;
            _ = StopMcpCheckAsync(checkId, owner.ProjectId);
            stopped++;
        }
        return stopped;
    }

    /// <summary>The server stops: every live check started through it stops.</summary>
    internal void EndAllSessions()
    {
        foreach (var (checkId, owner) in mcpChecks.ToArray())
        {
            if (mcpChecks.TryRemove(checkId, out _)) _ = StopMcpCheckAsync(checkId, owner.ProjectId);
        }
    }

    private async Task StopMcpCheckAsync(Guid checkId, string projectId)
    {
        try
        {
            var project = await Core.Projects.GetAsync(projectId);
            var check = await project.Checks.GetCheckAsync(checkId, 0);
            if (check.State is LiveCheckState.Preparing or LiveCheckState.Running) await project.Checks.StopCheckAsync(checkId, LiveCheckStopReason.SessionEnded);
        }
        catch (Exception error) when (error is CoreException or ObjectDisposedException)
        {
            // The check finished and was forgotten, or the project went away.
        }
    }

    private static MonitorResponseDto ChecksResponseMapperMonitor(Core.Interfaces.Screen.MonitorInfo monitor)
        => ChecksResponseMapper.Map(monitor);

    /// <summary>The source of predict_image: an existing file path or an upload of this server.</summary>
    private string SourceFile(McpArguments arguments)
    {
        var hasPath = arguments.Has("path");
        if (hasPath == arguments.Has("uploadId")) throw McpToolException.InvalidArgument("path", "pass exactly one of path and uploadId");
        return hasPath ? dependencies.Paths.ResolveFile(arguments.String("path")) : dependencies.Uploads.Get(arguments.Guid("uploadId")).Path;
    }

    private async Task<CheckParametersDto> CheckParametersAsync(McpArguments arguments, CancellationToken cancellationToken)
        => new(arguments.Has("confidence") ? arguments.Double("confidence", 0, 1) : null,
            arguments.Has("maxDetections") ? arguments.Int("maxDetections", 1, 100_000) : null,
            arguments.OptionalString("device") ?? await DefaultDeviceAsync(cancellationToken));

    /// <summary>As the device field of the window: the first GPU, or the processor without a driver or GPUs.</summary>
    private async Task<string> DefaultDeviceAsync(CancellationToken cancellationToken)
    {
        var gpus = await TrainingApi.ListGpusAsync(new ListGpusRequestDto(), cancellationToken);
        return gpus.Gpus.FirstOrDefault() is { } gpu ? gpu.Index.ToString(CultureInfo.InvariantCulture) : "cpu";
    }

    private static CheckRegionDto? RegionPixels(McpArguments arguments)
    {
        if (arguments.Object("region") is not { } region) return null;
        region.Only("x", "y", "width", "height");
        return new CheckRegionDto(region.Int("x", 0, 100_000), region.Int("y", 0, 100_000), region.Int("width", 1, 100_000), region.Int("height", 1, 100_000));
    }

    private static string StateKey(LiveCheckState state) => state.ToString().ToLowerInvariant();

    private static bool IsFinished(LiveCheckState state) => state is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled;

    private static JsonObject DetectionNode(LiveDetection item) => new()
    {
        ["class"] = item.Name, ["classIndex"] = item.ClassIndex, ["classId"] = item.ClassId?.ToString("D"), ["confidence"] = Math.Round(item.Confidence, 4),
        ["x"] = Math.Round(item.X, 5), ["y"] = Math.Round(item.Y, 5), ["width"] = Math.Round(item.Width, 5), ["height"] = Math.Round(item.Height, 5),
    };

    private static McpViewBox[] Boxes(IReadOnlyList<LiveDetection> detections) => detections.Select(item => new McpViewBox(item.X, item.Y, item.Width, item.Height,
        string.Create(CultureInfo.InvariantCulture, $"{item.Name} {item.Confidence:0.00}"), VariantColors.Default(item.ClassId ?? Guid.Empty))).ToArray();

    private static JsonObject EvaluationNode(ModelEvaluationResponseDto evaluation)
    {
        var node = McpToolResult.Object(evaluation);
        node.Remove("environment");
        return node;
    }

    /// <summary>
    /// Waits until a live check finishes or the timeout passes; its snapshots go to the client as progress (frames processed). The check
    /// keeps running after a timeout.
    /// </summary>
    private async Task<(LiveCheck Check, bool Finished)> WaitCheckAsync(IProject project, Guid checkId, TimeSpan timeout, McpToolCall call)
    {
        var changes = System.Threading.Channels.Channel.CreateUnbounded<LiveCheck>(new() { SingleReader = true });
        void OnChanged(object? sender, ChecksChangedEventArgs args)
        {
            if (args.ProjectId == project.Id && args.Check is { } check && check.Id == checkId) changes.Writer.TryWrite(check);
        }
        var reported = -1.0;
        async Task ReportAsync(LiveCheck check)
        {
            var value = IsFinished(check.State) ? 100 : check.Progress.FramesTotal is > 0 and var total
                ? Math.Min(99, check.Progress.FramesProcessed * 100.0 / total)
                : 0;
            if (!call.WantsProgress || value <= reported) return;
            reported = value;
            try
            {
                await call.ReportProgressAsync(value, 100, $"{StateKey(check.State)} {check.Progress.FramesProcessed} frames");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A client that went away does not stop the wait.
            }
        }
        Core.Projects.ChecksChanged += OnChanged;
        try
        {
            var current = await project.Checks.GetCheckAsync(checkId, 0, call.CancellationToken);
            if (timeout <= TimeSpan.Zero) return (current, IsFinished(current.State));
            await ReportAsync(current);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(call.CancellationToken);
            deadline.CancelAfter(timeout);
            while (!IsFinished(current.State))
            {
                LiveCheck next;
                try
                {
                    next = await changes.Reader.ReadAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!call.CancellationToken.IsCancellationRequested)
                {
                    current = await project.Checks.GetCheckAsync(checkId, 0, call.CancellationToken);
                    return (current, IsFinished(current.State));
                }
                if (next.Sequence <= current.Sequence) continue;
                current = next;
                await ReportAsync(current);
            }
            // Events carry no journal: the final snapshot is read again.
            return (await project.Checks.GetCheckAsync(checkId, 0, call.CancellationToken), true);
        }
        finally
        {
            Core.Projects.ChecksChanged -= OnChanged;
        }
    }

    /// <summary>The answer of the live check tools: the snapshot (without window URLs) and frames with detections after waiting.</summary>
    private async Task<McpToolResult> CheckResultAsync(IProject project, Guid checkId, int waitSeconds, int frameLimit, McpToolCall call)
    {
        TouchCheck(checkId);
        var (check, finished) = await WaitCheckAsync(project, checkId, TimeSpan.FromSeconds(waitSeconds), call);
        // Finished checks stay registered (a few bytes) so get_check can still name the video of their frames; stopping skips them.
        var path = mcpChecks.TryGetValue(checkId, out var owner) ? owner.Path : null;
        owner?.Touch();
        var snapshot = McpToolResult.Object(ChecksResponseMapper.Map(check with { RecentFrames = [] }, null));
        snapshot.Remove("recentFrames");
        snapshot["preview"] = check.Preview is { } preview
            ? new JsonObject { ["frameIndex"] = preview.FrameIndex, ["timeMs"] = preview.TimeMs, ["view"] = new JsonObject { ["check"] = check.Id.ToString("D") } }
            : null;
        snapshot["error"] = check.Error is { } error ? new JsonObject { ["code"] = error.Code, ["message"] = McpErrors.Text(error.Code, null) } : null;
        var frames = new JsonArray();
        if (frameLimit > 0 && check.Journal.Total > 0)
        {
            var page = await project.Checks.ListJournalAsync(checkId, check.Mode == LiveCheckMode.Screen ? null : 0, null, frameLimit, call.CancellationToken);
            foreach (var frame in page.Items)
            {
                var item = new JsonObject
                {
                    ["ordinal"] = frame.Ordinal, ["index"] = frame.Index, ["timeSeconds"] = frame.TimeMs / 1000.0,
                    ["detections"] = new JsonArray(frame.Detections.Select(detection => (JsonNode?)DetectionNode(detection)).ToArray()),
                };
                if (check.Mode == LiveCheckMode.Video && path is not null) item["view"] = VideoFrameView(project.Id, path, frame, check);
                frames.Add(item);
            }
        }
        var structured = new JsonObject
        {
            ["checkId"] = check.Id.ToString("D"),
            ["state"] = StateKey(check.State),
            ["finished"] = finished,
            ["check"] = snapshot,
            ["frames"] = frames,
        };
        var summary = check.Summary;
        var classes = summary.Classes.Count == 0 ? "no detections"
            : string.Join(", ", summary.Classes.Select(item => string.Create(CultureInfo.InvariantCulture, $"{item.Name} in {item.Frames} frames")));
        return Result(structured, $"Check {check.Id:D} ({check.Mode.ToString().ToLowerInvariant()}) is {StateKey(check.State)}: {summary.FramesProcessed} frames, {classes}"
            + (check.Error is { } failure ? $"; error {failure.Code}." : "."));
    }

    /// <summary>The view arguments that show the analyzed frame of a video check: the video frame at its time, cropped to the region.</summary>
    private static JsonObject VideoFrameView(string projectId, string path, LiveCheckFrame frame, LiveCheck check)
    {
        var view = new JsonObject
        {
            ["projectId"] = projectId,
            ["target"] = new JsonObject { ["videoFrame"] = path, ["timeSeconds"] = frame.TimeMs / 1000.0 },
        };
        if (check.Settings.Region is { } region && check.Source is { Width: > 0, Height: > 0 } source)
        {
            view["region"] = new JsonObject
            {
                ["x"] = Math.Round(region.X / (double)source.Width!.Value, 6), ["y"] = Math.Round(region.Y / (double)source.Height!.Value, 6),
                ["width"] = Math.Round(region.Width / (double)source.Width.Value, 6), ["height"] = Math.Round(region.Height / (double)source.Height.Value, 6),
            };
        }
        return view;
    }
}
