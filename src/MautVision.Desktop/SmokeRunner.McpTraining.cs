using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Desktop.Mcp;
using Microsoft.Web.WebView2.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>The tools of training, models, checks and analytics, listed by tools/list next to the first set.</summary>
    private static readonly string[] McpTrainingTools =
    [
        "get_training_defaults", "list_training_presets", "save_training_preset", "delete_training_preset", "start_training", "list_training_runs",
        "get_training_run", "get_training_log",
        "list_model_versions", "get_model_version", "set_working_version", "export_model", "delete_model_version",
        "evaluate_model", "list_evaluations", "predict_image", "predict_video", "get_check", "list_monitors", "capture_screen", "start_screen_check", "stop_check",
        "compare_model_versions",
    ];

    /// <summary>A model version and its project as the MCP training chain made them.</summary>
    private sealed record McpModelChain(string ProjectId, string DatasetVersionId, string WorkingId, string NewId, string RunId, JsonElement Input);

    /// <summary>
    /// Training, models, checks and analytics through MCP with the fake worker: defaults and the parameter schema cover every Core key, a
    /// preset, a run that waits with wait_job, its epochs, log, plot and resources, the new version made working, a ZIP export on disk (with
    /// the existing-file rule), an evaluation, an image check with its picture, a video check with frame views, the screen with
    /// confirmations, a busy GPU, stop_check and the end of a session, the comparison table and the prompt; error paths of each.
    /// </summary>
    private static async Task CheckMcpTrainingAsync(CoreWebView2 web, McpClient client, McpService mcp, string endpoint, string secret, ICore core, string root,
        string projectId, JsonArray checks, CancellationToken token)
    {
        var tools = (await client.ListToolsAsync(cancellationToken: token)).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);
        if (McpTrainingTools.Except(tools.Keys).ToArray() is { Length: > 0 } absent) throw new InvalidOperationException("MCP tools missing: " + string.Join(", ", absent));
        string[] reads = ["get_training_defaults", "list_training_presets", "list_training_runs", "get_training_run", "get_training_log", "list_model_versions",
            "get_model_version", "list_evaluations", "get_check", "list_monitors", "capture_screen", "compare_model_versions"];
        foreach (var name in McpTrainingTools)
        {
            var annotations = tools[name].Annotations!;
            if (annotations.ReadOnlyHint != reads.Contains(name) || annotations.DestructiveHint != name.StartsWith("delete_", StringComparison.Ordinal)
                || tools[name].OutputSchema is null)
                throw new InvalidOperationException("Annotations of " + name);
        }
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: token);
        if (!templates.Any(item => item.UriTemplate == "maut://projects/{projectId}/training/runs/{runId}/log")
            || !templates.Any(item => item.UriTemplate == "maut://projects/{projectId}/models/{versionId}/passport"))
            throw new InvalidOperationException("The training log and passport templates are not listed.");
        checks.Add($"tools/list has the {McpTrainingTools.Length} tools of training, models, checks and analytics with schemas; reads are read-only, deletions destructive; the log and passport templates are listed");

        var chain = await CheckMcpTrainingRunAsync(client, core, projectId, checks, token);
        await CheckMcpModelsAsync(client, root, mcp, chain, checks, token);
        await CheckMcpChecksAsync(web, client, mcp, endpoint, secret, core, root, chain, checks, token);
        await CheckMcpAnalyticsAsync(client, chain, checks, token);
    }

    private static async Task<JsonElement> WaitMcpJobAsync(McpClient client, string projectId, string jobId, CancellationToken token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var waited = Ok(await CallAsync(client, "wait_job", new { projectId, jobId, timeoutSeconds = 60 }, token), "wait_job");
            if (waited.GetProperty("finished").GetBoolean()) return waited.GetProperty("job");
        }
        throw new TimeoutException("MCP job " + jobId + " did not finish.");
    }

    private static async Task<McpModelChain> CheckMcpTrainingRunAsync(McpClient client, ICore core, string projectId, JsonArray checks, CancellationToken token)
    {
        // The GPU queue must be free: earlier checks end their jobs.
        for (var attempt = 0; (await core.Jobs.ListAsync(projectId, 200, cancellationToken: token)).Any(job => job.Status is JobStatus.Queued or JobStatus.Running); attempt++)
        {
            if (attempt > 600) throw new TimeoutException("Jobs before the MCP training chain did not end.");
            await Task.Delay(100, token);
        }
        var defaults = Ok(await CallAsync(client, "get_training_defaults", new { projectId }, token), "get_training_defaults");
        var parameters = defaults.GetProperty("parameters").EnumerateObject().Select(item => item.Name).ToArray();
        var schema = defaults.GetProperty("parameterSchema").GetProperty("properties");
        if (parameters.Length < 50 || schema.EnumerateObject().Count() != parameters.Length
            || schema.EnumerateObject().Any(item => !item.Value.TryGetProperty("type", out _) || !item.Value.TryGetProperty("description", out _) || !item.Value.TryGetProperty("default", out _))
            || !defaults.GetProperty("devices").EnumerateArray().Any(item => item.GetProperty("device").GetString() == "cpu")
            || defaults.GetProperty("project").GetProperty("imageSize").GetInt32() <= 0 || !defaults.GetProperty("runtime").GetProperty("available").GetBoolean())
            throw new InvalidOperationException("get_training_defaults: " + defaults);
        var startSchema = (await client.ListToolsAsync(cancellationToken: token)).First(tool => tool.Name == "start_training").ProtocolTool.InputSchema;
        if (startSchema.GetProperty("properties").GetProperty("parameters").GetProperty("properties").EnumerateObject().Count() != parameters.Length)
            throw new InvalidOperationException("The inputSchema of start_training does not list every parameter.");
        checks.Add($"get_training_defaults gives {parameters.Length} parameters with type, range and the Core default, the devices with cpu and the architecture and imgsz of the project; start_training lists the same parameters in its inputSchema");

        var versions = Ok(await CallAsync(client, "list_model_versions", new { projectId }, token), "list_model_versions").GetProperty("versions");
        var working = versions.EnumerateArray().First(item => item.GetProperty("isWorking").GetBoolean());
        var workingId = working.GetProperty("id").GetString()!;
        var datasetVersionId = working.GetProperty("dataset").GetProperty("versionId").GetString()!;
        var input = working.GetProperty("input").Clone();

        var invalid = Error(await CallAsync(client, "start_training", new { projectId, datasetVersionId, parameters = new { epochs = 0 } }, token), "training.parameter_invalid", null);
        if (invalid.GetProperty("details").GetProperty("field").GetString() != "epochs") throw new InvalidOperationException("parameter_invalid: " + invalid);
        Error(await CallAsync(client, "start_training", new { projectId, datasetVersionId, parameters = new { unknownKey = 1 } }, token), "training.parameter_invalid", null);
        Error(await CallAsync(client, "start_training", new { projectId, datasetVersionId = "x" }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "save_training_preset", new { name = "", parameters = new { epochs = 2 } }, token), "training.preset_name_invalid", null);

        var preset = Ok(await CallAsync(client, "save_training_preset", new { name = "MCP preset", parameters = new { epochs = 2, batch = 4, device = "cpu" } }, token),
            "save_training_preset").GetProperty("preset");
        var presetId = preset.GetProperty("id").GetString()!;
        if (!Ok(await CallAsync(client, "list_training_presets", null, token), "list_training_presets").GetProperty("presets").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == presetId))
            throw new InvalidOperationException("list_training_presets misses the preset.");
        Error(await CallAsync(client, "start_training", new { projectId, datasetVersionId, presetId = Guid.NewGuid() }, token), "training.preset_not_found", null);

        var started = Ok(await CallAsync(client, "start_training", new { projectId, datasetVersionId, presetId, operationId = "mcp-smoke-training" }, token), "start_training");
        var runId = started.GetProperty("runId").GetString()!;
        if (started.GetProperty("jobId").GetString() != runId) throw new InvalidOperationException("start_training: " + started);
        var again = Ok(await CallAsync(client, "start_training", new { projectId, datasetVersionId, presetId, operationId = "mcp-smoke-training" }, token), "start_training");
        if (again.GetProperty("runId").GetString() != runId) throw new InvalidOperationException("A repeated operationId started another run.");
        var job = await WaitMcpJobAsync(client, projectId, runId, token);
        if (job.GetProperty("status").GetString() != "succeeded") throw new InvalidOperationException("MCP training: " + job);
        checks.Add("start_training refuses invalid parameters (training.parameter_invalid with the field) and an unknown preset, starts a run from a preset with an operationId (repeated — the same run) and wait_job follows it to the end");

        var runs = Ok(await CallAsync(client, "list_training_runs", new { projectId, limit = 5 }, token), "list_training_runs");
        var listed = runs.GetProperty("runs").EnumerateArray().First(item => item.GetProperty("id").GetString() == runId);
        var newId = listed.GetProperty("resultVersionId").GetString()!;
        if (listed.GetProperty("state").GetString() != "succeeded" || runs.GetProperty("total").GetInt64() < 2) throw new InvalidOperationException("list_training_runs: " + runs);
        var run = Ok(await CallAsync(client, "get_training_run", new { projectId, runId, maxEpochs = 2 }, token), "get_training_run");
        var epochs = run.GetProperty("epochs");
        if (run.GetProperty("run").GetProperty("presetName").GetString() != "MCP preset" || epochs.GetProperty("total").GetInt32() < 1
            || epochs.GetProperty("shown").GetInt32() > Math.Max(2, epochs.GetProperty("total").GetInt32() > 2 ? 3 : 2) || epochs.GetProperty("items").GetArrayLength() < 1)
            throw new InvalidOperationException("get_training_run: " + run);
        var log = Ok(await CallAsync(client, "get_training_log", new { projectId, runId, tail = 5 }, token), "get_training_log");
        if (log.GetProperty("lines").GetArrayLength() is < 1 or > 5 || log.GetProperty("total").GetInt64() < 1) throw new InvalidOperationException("get_training_log: " + log);
        Error(await CallAsync(client, "get_training_log", new { projectId, runId, tail = 1001 }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "get_training_run", new { projectId, runId = Guid.NewGuid() }, token), "training.run_not_found", null);
        var plots = run.GetProperty("plots").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (plots.Length > 0)
        {
            var plot = Ok(await CallAsync(client, "view", new { projectId, target = new { trainingPlot = plots[0], runId }, maxSize = 256 }, token), "view");
            if (plot.GetProperty("kind").GetString() != "trainingPlot") throw new InvalidOperationException("view trainingPlot: " + plot);
        }
        var resource = await client.ReadResourceAsync($"maut://projects/{projectId}/training/runs/{runId}/log", cancellationToken: token);
        using (var document = JsonDocument.Parse(((TextResourceContents)resource.Contents[0]).Text))
        {
            if (document.RootElement.GetProperty("lines").GetArrayLength() != document.RootElement.GetProperty("total").GetInt64())
                throw new InvalidOperationException("The training log resource is not complete.");
        }
        checks.Add($"list_training_runs, get_training_run (epochs thinned, preset in the passport), get_training_log (tail) and the log resource describe the run; its plot opens with view trainingPlot ({plots.Length} plots)");
        return new McpModelChain(projectId, datasetVersionId, workingId, newId, runId, input);
    }

    private static async Task CheckMcpModelsAsync(McpClient client, string root, McpService mcp, McpModelChain chain, JsonArray checks, CancellationToken token)
    {
        var projectId = chain.ProjectId;
        var version = Ok(await CallAsync(client, "get_model_version", new { projectId, versionId = chain.NewId }, token), "get_model_version").GetProperty("version");
        if (version.GetProperty("runId").GetString() != chain.RunId || version.GetProperty("files").EnumerateArray().Any(file => file.TryGetProperty("url", out _)))
            throw new InvalidOperationException("get_model_version: " + version);
        var passport = await client.ReadResourceAsync($"maut://projects/{projectId}/models/{chain.NewId}/passport", cancellationToken: token);
        using (var document = JsonDocument.Parse(((TextResourceContents)passport.Contents[0]).Text))
        {
            if (document.RootElement.GetProperty("id").GetString() != chain.NewId) throw new InvalidOperationException("The passport resource is wrong.");
        }
        Error(await CallAsync(client, "get_model_version", new { projectId, versionId = Guid.NewGuid() }, token), "training.version_not_found", null);

        var made = Ok(await CallAsync(client, "set_working_version", new { projectId, versionId = chain.NewId }, token), "set_working_version").GetProperty("version");
        if (!made.GetProperty("isWorking").GetBoolean()) throw new InvalidOperationException("set_working_version: " + made);
        var blocked = Error(await CallAsync(client, "delete_model_version", new { projectId, versionId = chain.NewId }, token), "training.version_in_use", null);
        if (blocked.GetProperty("details").GetProperty("reason").GetString() != "working") throw new InvalidOperationException("delete working: " + blocked);
        checks.Add("get_model_version and the passport resource give the passport without window URLs; set_working_version makes the new version working, and deleting it is refused (training.version_in_use, working)");

        var folder = Path.Combine(root, "mcp-models");
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, "модель.zip");
        Error(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = "model.zip" }, token), "mcp.path_invalid", null);
        Error(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = Path.Combine(folder, "model.txt") }, token), "mcp.invalid_arguments", null);
        var exported = Ok(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = zip, waitSeconds = 60 }, token), "export_model");
        var exportJob = exported.GetProperty("status").GetString() == "succeeded" ? exported.GetProperty("job")
            : await WaitMcpJobAsync(client, projectId, exported.GetProperty("jobId").GetString()!, token);
        if (exportJob.GetProperty("status").GetString() != "succeeded" || !File.Exists(zip) || new FileInfo(zip).Length == 0)
            throw new InvalidOperationException("export_model: " + exportJob);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            if (!archive.Entries.Any(entry => entry.FullName == "weights/best.pt")) throw new InvalidOperationException("The exported ZIP has no weights.");
        }
        Error(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = zip }, token), "mcp.destination_exists", null);
        await mcp.UpdateAsync(current => current with { AccessLevel = McpAccessLevels.ReadWrite }, token);
        try
        {
            Error(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = zip, overwrite = true }, token), "mcp.access_denied", null);
        }
        finally
        {
            await mcp.UpdateAsync(current => current with { AccessLevel = McpAccessLevels.Full }, token);
        }
        var replaced = Ok(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = folder, overwrite = false, waitSeconds = 60 }, token),
            "export_model");
        if (Path.GetFileName(replaced.GetProperty("path").GetString()) != $"model-v{version.GetProperty("number").GetInt32()}.zip")
            throw new InvalidOperationException("export_model into a folder: " + replaced);
        var overwritten = Ok(await CallAsync(client, "export_model", new { projectId, versionId = chain.NewId, destinationPath = zip, overwrite = true, waitSeconds = 60 }, token),
            "export_model");
        if (overwritten.GetProperty("status").GetString() != "succeeded" && (await WaitMcpJobAsync(client, projectId, overwritten.GetProperty("jobId").GetString()!, token))
            .GetProperty("status").GetString() != "succeeded")
            throw new InvalidOperationException("export_model with overwrite: " + overwritten);
        checks.Add("export_model writes the ZIP to disk (a folder gets model-vN.zip); a relative path, another extension and an existing file are refused (mcp.destination_exists), overwrite needs the full access level");
    }

    private static async Task CheckMcpAnalyticsAsync(McpClient client, McpModelChain chain, JsonArray checks, CancellationToken token)
    {
        var projectId = chain.ProjectId;
        var table = Ok(await CallAsync(client, "compare_model_versions", new
        {
            projectId, versionIds = new[] { chain.WorkingId, chain.NewId }, datasetVersionId = chain.DatasetVersionId, split = "val",
        }, token), "compare_model_versions");
        var rows = table.GetProperty("rows").EnumerateArray().ToDictionary(row => row.GetProperty("key").GetString()!);
        string[] keys = ["mAP50", "mAP50_95", "precision", "recall", "bestEpoch", "epochs", "dataset", "split", "date", "batch", "lr0", "amp", "device", "evaluation.mAP50"];
        if (keys.Except(rows.Keys).ToArray() is { Length: > 0 } missing || rows.Values.Any(row => row.GetProperty("values").GetArrayLength() != 2)
            || rows["evaluation.mAP50"].GetProperty("values")[1].ValueKind != JsonValueKind.Number || rows["device"].GetProperty("values")[1].GetString() != "CPU")
            throw new InvalidOperationException("compare_model_versions: " + table);
        Error(await CallAsync(client, "compare_model_versions", new { projectId, versionIds = Array.Empty<string>() }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "compare_model_versions", new { projectId, versionIds = new[] { Guid.NewGuid() } }, token), "training.version_not_found", null);
        checks.Add("compare_model_versions gives the Analytics table (quality with the best version, epochs, dataset, batch, lr0, AMP, device) and the latest evaluation rows of a dataset");

        var prompts = await client.ListPromptsAsync(cancellationToken: token);
        if (!prompts.Any(prompt => prompt.Name == "train_and_compare")) throw new InvalidOperationException("prompts/list misses train_and_compare.");
        var prompt = await client.GetPromptAsync("train_and_compare", new Dictionary<string, object?> { ["datasetVersionId"] = chain.DatasetVersionId, ["projectId"] = projectId },
            cancellationToken: token);
        var text = ((TextContentBlock)prompt.Messages[0].Content).Text;
        if (!text.Contains(chain.DatasetVersionId, StringComparison.Ordinal) || !text.Contains("compare_model_versions", StringComparison.Ordinal))
            throw new InvalidOperationException("train_and_compare: " + text);
        checks.Add("The prompt train_and_compare names the dataset and leads through training, evaluation and the comparison");

        // Clean up: the old version is working again, the MCP version and preset go.
        Ok(await CallAsync(client, "set_working_version", new { projectId, versionId = chain.WorkingId }, token), "set_working_version");
        var deleted = Ok(await CallAsync(client, "delete_model_version", new { projectId, versionId = chain.NewId }, token), "delete_model_version");
        if (deleted.GetProperty("evaluations").GetInt32() < 1) throw new InvalidOperationException("delete_model_version: " + deleted);
        var presets = Ok(await CallAsync(client, "list_training_presets", null, token), "list_training_presets").GetProperty("presets");
        foreach (var preset in presets.EnumerateArray().Where(item => item.GetProperty("name").GetString() == "MCP preset"))
            Ok(await CallAsync(client, "delete_training_preset", new { presetId = preset.GetProperty("id").GetString() }, token), "delete_training_preset");
        Error(await CallAsync(client, "delete_training_preset", new { presetId = Guid.NewGuid() }, token), "training.preset_not_found", null);
        checks.Add("delete_model_version removes a version that is not working with its evaluations; delete_training_preset removes the preset");
    }
}
