using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Desktop.Mcp;
using Microsoft.Web.WebView2.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Checks through MCP: an evaluation and the comparison matrix, an image check with its picture (also from an upload), a video check
    /// with frames viewable by view, monitors, the screenshot and the screen check confirmed (and denied) in the window, a busy GPU, stop_check
    /// and the stop of a check when its MCP session ends.
    /// </summary>
    private static async Task CheckMcpChecksAsync(CoreWebView2 web, McpClient client, McpService mcp, string endpoint, string secret, ICore core, string root,
        McpModelChain chain, JsonArray checks, CancellationToken token)
    {
        var projectId = chain.ProjectId;
        var versionId = chain.NewId;

        Error(await CallAsync(client, "evaluate_model", new { projectId, versionId, datasetVersionId = chain.DatasetVersionId, split = "train" }, token),
            "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "evaluate_model", new { projectId, versionId, datasetVersionId = chain.DatasetVersionId, split = "test", device = "cpu" }, token),
            "checks.evaluation_split_empty", null);
        var evaluation = Ok(await CallAsync(client, "evaluate_model", new
        {
            projectId, versionId, datasetVersionId = chain.DatasetVersionId, split = "val", device = "cpu", batch = 4, waitSeconds = 60,
        }, token), "evaluate_model");
        var evaluationId = evaluation.GetProperty("evaluationId").GetString()!;
        var evaluationJob = evaluation.GetProperty("status").GetString() == "succeeded" ? evaluation.GetProperty("job")
            : await WaitMcpJobAsync(client, projectId, evaluation.GetProperty("jobId").GetString()!, token);
        if (evaluationJob.GetProperty("status").GetString() != "succeeded" || evaluation.GetProperty("jobId").GetString() != evaluationId)
            throw new InvalidOperationException("evaluate_model: " + evaluationJob);
        var evaluations = Ok(await CallAsync(client, "list_evaluations", new { projectId, versionId, split = "val" }, token), "list_evaluations");
        var listed = evaluations.GetProperty("evaluations").EnumerateArray().First(item => item.GetProperty("id").GetString() == evaluationId);
        if (listed.GetProperty("metrics").GetProperty("mAP50").GetDouble() <= 0 || listed.TryGetProperty("environment", out _)
            || !evaluations.GetProperty("latest").EnumerateArray().Any(item => item.GetProperty("evaluationId").GetString() == evaluationId))
            throw new InvalidOperationException("list_evaluations: " + evaluations);
        checks.Add("evaluate_model refuses an unknown split and an empty one (checks.evaluation_split_empty), evaluates on val as a job; list_evaluations has its metrics and the latest-per-version matrix");

        var image = Path.Combine(root, "smoke-checks", "проверка.jpg");
        Error(await CallAsync(client, "predict_image", new { projectId, versionId }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "predict_image", new { projectId, versionId, path = image, confidence = 2 }, token), "mcp.invalid_arguments", null);
        var predicted = await CallAsync(client, "predict_image", new { projectId, versionId, path = image, maxDetections = 2, device = "cpu", maxSize = 512 }, token);
        var prediction = Ok(predicted, "predict_image");
        var picture = DecodeMcpImage(predicted);
        if (!prediction.GetProperty("finished").GetBoolean() || prediction.GetProperty("state").GetString() != "succeeded"
            || prediction.GetProperty("detections").GetArrayLength() != 2 || Math.Max(picture.Width, picture.Height) > 512)
            throw new InvalidOperationException("predict_image: " + prediction);
        var imageCheckId = prediction.GetProperty("checkId").GetString()!;
        var latest = Ok(await CallAsync(client, "view", new { projectId, target = new { check = imageCheckId }, maxSize = 256 }, token), "view");
        if (latest.GetProperty("kind").GetString() != "check" || latest.GetProperty("boxes").GetArrayLength() != 2) throw new InvalidOperationException("view check: " + latest);
        var upload = Ok(await CallAsync(client, "upload_file", new { name = "кадр.jpg", base64 = Convert.ToBase64String(File.ReadAllBytes(image)) }, token), "upload_file");
        var uploaded = await CallAsync(client, "predict_image", new { projectId, versionId, uploadId = upload.GetProperty("uploadId").GetString(), device = "cpu" }, token);
        if (!Ok(uploaded, "predict_image").GetProperty("finished").GetBoolean()) throw new InvalidOperationException("predict_image of an upload did not finish.");
        DecodeMcpImage(uploaded);
        checks.Add("predict_image returns the detections and the picture with boxes as image content (path or uploadId); view { check } shows the same check with its boxes");

        var video = Path.Combine(root, "smoke-video", "улица.mp4");
        var width = chain.Input.GetProperty("width").GetInt32();
        var height = chain.Input.GetProperty("height").GetInt32();
        Error(await CallAsync(client, "predict_video", new { projectId, versionId, path = video, region = new { x = 700, y = 0, width, height }, device = "cpu" }, token),
            "checks.region_invalid", null);
        Error(await CallAsync(client, "predict_video", new { projectId, versionId, path = image, device = "cpu" }, token), "video.unsupported", null);
        var videoCheck = Ok(await CallAsync(client, "predict_video", new
        {
            projectId, versionId, path = video, stepSeconds = 0.5, region = new { x = 0, y = 0, width, height }, maxDetections = 2, device = "cpu", waitSeconds = 60,
        }, token), "predict_video");
        var videoId = videoCheck.GetProperty("checkId").GetString()!;
        var followed = Ok(await CallAsync(client, "get_check", new { projectId, checkId = videoId, waitSeconds = 60, frameLimit = 3 }, token), "get_check");
        var frames = followed.GetProperty("frames").EnumerateArray().ToArray();
        if (followed.GetProperty("state").GetString() != "succeeded" || followed.GetProperty("check").GetProperty("summary").GetProperty("framesProcessed").GetInt64() != 12
            || frames.Length != 3 || frames[0].GetProperty("detections").GetArrayLength() != 2)
            throw new InvalidOperationException("predict_video / get_check: " + followed);
        var frameView = frames[0].GetProperty("view");
        var shown = Ok(await CallAsync(client, "view", new
        {
            projectId, target = JsonNode.Parse(frameView.GetProperty("target").GetRawText()), region = JsonNode.Parse(frameView.GetProperty("region").GetRawText()), maxSize = 256,
        }, token), "view");
        if (shown.GetProperty("kind").GetString() != "videoFrame") throw new InvalidOperationException("view of a video check frame: " + shown);
        Error(await CallAsync(client, "get_check", new { projectId, checkId = Guid.NewGuid() }, token), "checks.check_not_found", null);
        checks.Add("predict_video checks a video with a region (outside the frame — checks.region_invalid); get_check waits for it and gives the summary and frames with detections whose view arguments open the analyzed crop");

        var monitors = Ok(await CallAsync(client, "list_monitors", null, token), "list_monitors").GetProperty("monitors");
        var monitor = monitors.EnumerateArray().First(item => item.GetProperty("isPrimary").GetBoolean()).GetProperty("index").GetInt32();
        Error(await CallAsync(client, "capture_screen", new { monitor = 99 }, token), "screen.monitor_not_found", null);
        Error(await ConfirmMcpAsync(web, CallAsync(client, "capture_screen", new { monitor }, token), "capture_screen", "mcp-confirmation-deny", checks, token),
            "mcp.confirmation_denied", null);
        var captured = await ConfirmMcpAsync(web, CallAsync(client, "capture_screen", new { monitor, maxSize = 512 }, token), "capture_screen", "mcp-confirmation-allow",
            checks, token);
        var shot = Ok(captured, "capture_screen");
        var screenPicture = DecodeMcpImage(captured);
        if (screenPicture.Width != shot.GetProperty("picture").GetProperty("width").GetInt32() || shot.GetProperty("scale").GetDouble() <= 0
            || Math.Max(screenPicture.Width, screenPicture.Height) > 512)
            throw new InvalidOperationException("capture_screen: " + shot);
        checks.Add("list_monitors lists the monitors; capture_screen asks in the window every time («Отклонить» — mcp.confirmation_denied) and returns the screenshot with its scale");

        Error(await CallAsync(client, "start_screen_check", new { projectId, versionId, monitor, durationSeconds = 301 }, token), "mcp.invalid_arguments", null);
        Error(await ConfirmMcpAsync(web, CallAsync(client, "start_screen_check", new { projectId, versionId, monitor, device = "cpu" }, token), "start_screen_check",
            "mcp-confirmation-deny", checks, token), "mcp.confirmation_denied", null);
        var screen = Ok(await ConfirmMcpAsync(web, CallAsync(client, "start_screen_check", new { projectId, versionId, monitor, fps = 5, durationSeconds = 120, device = "cpu" }, token),
            "start_screen_check", "mcp-confirmation-allow", checks, token), "start_screen_check");
        var screenId = screen.GetProperty("checkId").GetString()!;
        var busy = Error(await CallAsync(client, "predict_image", new { projectId, versionId, path = image, device = "cpu" }, token), "checks.gpu_busy_prediction", null);
        if (busy.GetProperty("message").GetString() is not { Length: > 0 }) throw new InvalidOperationException("gpu_busy: " + busy);
        for (var attempt = 0; ; attempt++)
        {
            var running = Ok(await CallAsync(client, "get_check", new { projectId, checkId = screenId, frameLimit = 0 }, token), "get_check");
            if (running.GetProperty("check").GetProperty("preview").ValueKind == JsonValueKind.Object) break;
            if (attempt > 100) throw new TimeoutException("The screen check shows no frame: " + running);
            await Task.Delay(100, token);
        }
        Ok(await CallAsync(client, "view", new { projectId, target = new { check = screenId }, maxSize = 256 }, token), "view");
        var stopped = Ok(await CallAsync(client, "stop_check", new { projectId, checkId = screenId, waitSeconds = 30 }, token), "stop_check");
        if (stopped.GetProperty("state").GetString() != "cancelled" || stopped.GetProperty("check").GetProperty("stopReason").GetString() != "user")
            throw new InvalidOperationException("stop_check: " + stopped);
        Ok(await CallAsync(client, "stop_check", new { projectId, checkId = screenId, waitSeconds = 0 }, token), "stop_check");
        checks.Add("start_screen_check is confirmed in the window («Отклонить» — denied, more than 300 s refused); while it runs another check gets checks.gpu_busy_prediction; view shows its frame and stop_check stops it (twice is safe)");

        // A check of a session that ends stops with it. Clients of the 2026-07-28 revision have no session (their checks end by the time
        // limit of at most 300 s); a client pinned to 2025-11-25 opens one and ends it with DELETE when disposed.
        string sessionCheckId;
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret },
            Name = "maut-smoke-session",
        });
        await using (var second = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "maut-smoke", Version = "1.0" }, ProtocolVersion = "2025-11-25",
            }, cancellationToken: token))
        {
            var started = Ok(await ConfirmMcpAsync(web, CallAsync(second, "start_screen_check", new { projectId, versionId, monitor, fps = 2, durationSeconds = 120, device = "cpu" },
                token), "start_screen_check", "mcp-confirmation-allow", checks, token), "start_screen_check");
            sessionCheckId = started.GetProperty("checkId").GetString()!;
        }
        var project = await core.Projects.GetAsync(projectId, token);
        for (var attempt = 0; ; attempt++)
        {
            var check = await project.Checks.GetCheckAsync(Guid.Parse(sessionCheckId), 0, token);
            if (check is { State: LiveCheckState.Cancelled, StopReason: LiveCheckStopReason.SessionEnded }) break;
            if (attempt > 300) throw new TimeoutException($"The check of a closed MCP session is {check.State} ({check.StopReason}).");
            await Task.Delay(100, token);
        }
        checks.Add("A live check started by an MCP session stops (session_ended) when that session ends");
    }

    /// <summary>Answers the confirmation of a tool call in the window with the button (mcp-confirmation-allow or mcp-confirmation-deny).</summary>
    private static async Task<CallToolResult> ConfirmMcpAsync(CoreWebView2 web, Task<CallToolResult> call, string action, string button, JsonArray checks,
        CancellationToken token)
    {
        await RunLifecycleScriptAsync(web, """
            const dialog = await until(() => document.querySelector('[data-testid="mcp-confirmation"]'), 'Confirmation');
            assert(dialog.dataset.action === input.action && dialog.textContent.includes('монитор') && dialog.textContent.includes(input.action),
              'The confirmation of ' + input.action + ' names the monitor and the tool in Russian');
            document.querySelector('[data-testid="' + input.button + '"]').click();
            await until(() => !document.querySelector('[data-testid="mcp-confirmation"]'), 'Confirmation closed');
            return {checks};
            """, new { action, button }, checks, token);
        return await call;
    }
}
