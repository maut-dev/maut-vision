using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Desktop.Controllers.Projects.Training;
using MautVision.Desktop.Controllers.Projects.Training.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    /// <summary>
    /// Training parameters for the agent: the worker protocol keys of Core (TrainingParameterRules) with the ranges of the training form
    /// (ui/src/api/projects/training/training-parameters.ts). Defaults come from Core at runtime, so they never drift; the smoke checks that
    /// every Core key has a schema here.
    /// </summary>
    private static readonly (string Key, string Schema, string Description)[] TrainingParameterSpecs =
    [
        ("epochs", """{ "type": "integer", "minimum": 1, "maximum": 100000 }""", "Training epochs."),
        ("batch", """{ "type": "number" }""", "Images per batch: a whole number ≥ 1, -1 for AutoBatch or a share (0, 1) of GPU memory."),
        ("device", """{ "type": "string", "pattern": "^(cpu|-1|[0-9]{1,3})$" }""", "\"cpu\" or a GPU index from devices of get_training_defaults (\"0\" is the first GPU)."),
        ("time", """{ "type": ["number", "null"], "exclusiveMinimum": 0 }""", "Hours of training; overrides epochs when set. null — off."),
        ("patience", """{ "type": ["integer", "null"], "minimum": 0 }""", "Epochs without improvement before early stopping; null — the worker computes max(10, min(40, epochs))."),
        ("amp", """{ "type": ["boolean", "string"], "enum": [true, false, "fp16", "bf16", "fp32"] }""", "Mixed precision: true, false or a precision."),
        ("pretrained", """{ "type": "boolean" }""", "Start from the pretrained weights of the base model."),
        ("clsRemap", """{ "type": "boolean" }""", "Remap the classification head of the pretrained model to the project classes."),
        ("freeze", """{ "type": ["integer", "array", "null"], "items": { "type": ["integer", "string"] } }""", "Freeze the first N layers, or a list of layer indexes and module names like 23.cv2; null — none."),
        ("channelsLast", """{ "type": ["boolean", "null"] }""", "channels_last memory format; null — chosen by the worker."),
        ("verbose", """{ "type": "boolean" }""", "Detailed training console."),
        ("optimizer", """{ "type": "string", "enum": ["auto", "SGD", "MuSGD", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp"] }""", "Optimizer; with auto lr0 is chosen by the optimizer."),
        ("lr0", """{ "type": ["number", "null"], "exclusiveMinimum": 0 }""", "Initial learning rate; null — the worker computes 0.001 · batch / 32."),
        ("lrf", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Final learning rate as a share of lr0."),
        ("momentum", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Momentum (beta1 for Adam)."),
        ("weightDecay", """{ "type": "number", "minimum": 0 }""", "L2 weight decay."),
        ("warmupEpochs", """{ "type": ["number", "null"], "minimum": 0 }""", "Warm-up epochs; null — the worker computes them from epochs and batch."),
        ("warmupMomentum", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Momentum at the start of the warm-up."),
        ("warmupBiasLr", """{ "type": "number", "minimum": 0 }""", "Bias learning rate during the warm-up."),
        ("cosLr", """{ "type": "boolean" }""", "Cosine learning rate schedule."),
        ("box", """{ "type": "number", "minimum": 0 }""", "Box loss gain."),
        ("cls", """{ "type": "number", "minimum": 0 }""", "Classification loss gain."),
        ("clsPw", """{ "type": "number", "minimum": 0 }""", "Positive weight of the classification loss (class imbalance)."),
        ("dfl", """{ "type": "number", "minimum": 0 }""", "Distribution focal loss gain."),
        ("nbs", """{ "type": "integer", "minimum": 1 }""", "Nominal batch size for loss normalization."),
        ("cache", """{ "type": ["boolean", "string"], "enum": [true, false, "ram", "disk"] }""", "Cache images: false, true (RAM), ram or disk."),
        ("workers", """{ "type": ["integer", "null"], "minimum": 0, "maximum": 256 }""", "Data loader workers; null — the worker decides (0 on Windows)."),
        ("singleCls", """{ "type": "boolean" }""", "Train all classes as one class."),
        ("rect", """{ "type": "boolean" }""", "Rectangular batches."),
        ("fraction", """{ "type": ["number", "array"], "items": { "type": "number" }, "maxItems": 3 }""", "Share (0, 1] or count of train images; or 1-3 such values per split."),
        ("classes", """{ "type": ["array", "null"], "items": { "type": "integer", "minimum": 0 }, "minItems": 1, "uniqueItems": true }""", "Train only these output class indexes of the dataset; null — all."),
        ("seed", """{ "type": "integer", "minimum": 0 }""", "Random seed."),
        ("deterministic", """{ "type": "boolean" }""", "Deterministic algorithms."),
        ("hsvH", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Hue augmentation."),
        ("hsvS", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Saturation augmentation."),
        ("hsvV", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Brightness augmentation."),
        ("degrees", """{ "type": "number", "minimum": 0, "maximum": 180 }""", "Rotation in degrees (0 — off)."),
        ("translate", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Translation as a share of the image."),
        ("scale", """{ "type": ["number", "array"], "items": { "type": "number", "exclusiveMinimum": 0 }, "minItems": 2, "maxItems": 2 }""", "Scale gain, or a range [min, max] with 0 < min ≤ max."),
        ("shear", """{ "type": "number", "minimum": 0, "maximum": 180 }""", "Shear in degrees."),
        ("perspective", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Perspective distortion."),
        ("flipud", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of a vertical flip."),
        ("fliplr", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of a horizontal flip."),
        ("bgr", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of swapping RGB to BGR."),
        ("mosaic", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of mosaic."),
        ("closeMosaic", """{ "type": "integer", "minimum": 0 }""", "Turn mosaic off for the last N epochs."),
        ("mixup", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of mixup."),
        ("cutmix", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "Probability of cutmix."),
        ("multiScale", """{ "type": "number", "minimum": 0 }""", "Multi-scale training range (0 — off)."),
        ("val", """{ "type": "boolean" }""", "Validate during training."),
        ("split", """{ "type": "string", "enum": ["train", "val", "test"] }""", "Split used for validation."),
        ("conf", """{ "type": ["number", "null"], "minimum": 0, "maximum": 1 }""", "Confidence threshold of validation; null — the Ultralytics default."),
        ("iou", """{ "type": "number", "minimum": 0, "maximum": 1 }""", "IoU threshold of NMS in validation."),
        ("maxDet", """{ "type": "integer", "minimum": 1 }""", "Maximum detections per image in validation."),
        ("saveJson", """{ "type": "boolean" }""", "Save validation results as COCO JSON."),
        ("save", """{ "type": "boolean" }""", "Save checkpoints."),
        ("savePeriod", """{ "type": "integer", "minimum": -1 }""", "Save a checkpoint every N epochs (-1 — off)."),
        ("plots", """{ "type": "boolean" }""", "Draw training plots (viewable with view {trainingPlot, runId})."),
    ];

    private const string TrainingRunSummarySchema = """
        { "type": "object", "description": "Run: id (= jobId and runId), state queued|preparing|training|cancelling|succeeded|failed|cancelled|interrupted, dataset, epoch progress, live metrics, error, resultVersionId (the model version after success).",
          "properties": { "id": { "type": "string" }, "state": { "type": "string" }, "dataset": { "type": "object" }, "architecture": { "type": "string" },
            "imageSize": { "type": "integer" }, "epoch": { "type": ["integer", "null"] }, "epochs": { "type": ["integer", "null"] }, "metrics": { "type": "object" },
            "error": { "type": ["object", "null"] }, "resultVersionId": { "type": ["string", "null"] }, "createdAt": { "type": "string" } },
          "required": ["id", "state"] }
        """;

    /// <summary>The JSON schema of the parameters object: every Core key with its range, description and Core default.</summary>
    private JsonObject TrainingParametersSchema()
    {
        var specs = TrainingParameterSpecs.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
        var properties = new JsonObject();
        foreach (var (key, value) in Core.TrainingPresets.Defaults)
        {
            var property = specs.TryGetValue(key, out var spec)
                ? JsonNode.Parse(spec.Schema)!.AsObject()
                : new JsonObject();
            property["description"] = spec.Description ?? "Ultralytics parameter " + key + ".";
            property["default"] = JsonNode.Parse(value.GetRawText());
            properties[key] = property;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Training parameters (worker protocol keys); a missing key takes its default. Ranges as in the training form of the window.",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private TrainingController? trainingController;

    /// <summary>The training controller of the page over the same Core, with the registries of the MCP tools (no dialogs).</summary>
    private TrainingController TrainingApi => trainingController ??= new TrainingController(Core.Projects, Core.Jobs, Core.TrainingPresets, Core.Hardware, Core.Python,
        importResources, importSelections, NoMcpDialogs);

    private IEnumerable<McpToolDefinition> TrainingTools()
    {
        var parametersSchema = TrainingParametersSchema().ToJsonString();

        yield return McpToolDefinition.Create("get_training_defaults", "Training defaults", McpToolClass.Read, true,
            """
            Everything needed to fill start_training: every parameter with its default and range (parameterSchema, the same rules as the
            training form), the devices (device "cpu" or a GPU index), whether the Python worker is available and, with projectId, the
            architecture (the base model of the project) and imgsz (the larger side of the project input) the application sets itself.
            Defaults null mean the worker computes the value (lr0, warmupEpochs, patience, workers).
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "additionalProperties": false }""",
            """
            { "type": "object", "properties": {
                "parameters": { "type": "object", "description": "key → default" },
                "parameterSchema": { "type": "object" },
                "devices": { "type": "array", "items": { "type": "object", "properties": { "device": { "type": "string" }, "label": { "type": "string" } } } },
                "driverAvailable": { "type": "boolean" },
                "runtime": { "type": "object" },
                "project": { "type": ["object", "null"] } },
              "required": ["parameters", "parameterSchema", "devices", "runtime"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId");
                JsonObject? project = null;
                if (call.Arguments.OptionalString("projectId") is { } projectId)
                {
                    var prepared = await PrepareAsync(projectId, call.CancellationToken);
                    project = new JsonObject
                    {
                        ["projectId"] = prepared.Id,
                        ["architecture"] = prepared.BaseModelId,
                        ["imageSize"] = Math.Max(prepared.InputSize.Width, prepared.InputSize.Height),
                        ["input"] = new JsonObject { ["width"] = prepared.InputSize.Width, ["height"] = prepared.InputSize.Height },
                    };
                }
                var gpus = await TrainingApi.ListGpusAsync(new ListGpusRequestDto(), call.CancellationToken);
                var runtime = await TrainingApi.GetRuntimeAsync(new GetTrainingRuntimeRequestDto(), call.CancellationToken);
                var devices = new JsonArray(gpus.Gpus.Select(gpu => (JsonNode?)new JsonObject
                {
                    ["device"] = gpu.Index.ToString(CultureInfo.InvariantCulture),
                    ["label"] = string.Create(CultureInfo.InvariantCulture, $"GPU {gpu.Index} — {gpu.Name}, {gpu.MemoryTotalMb / 1024.0:0.#} GB"),
                }).Append(new JsonObject { ["device"] = "cpu", ["label"] = "CPU" }).ToArray());
                var structured = new JsonObject
                {
                    ["parameters"] = McpToolResult.Node(Core.TrainingPresets.Defaults),
                    ["parameterSchema"] = JsonNode.Parse(parametersSchema),
                    ["devices"] = devices,
                    ["driverAvailable"] = gpus.DriverAvailable,
                    ["runtime"] = McpToolResult.Node(runtime),
                    ["project"] = project,
                };
                return Result(structured, $"{Core.TrainingPresets.Defaults.Count} parameters; devices: {string.Join(", ", devices.Select(item => item!["device"]!.GetValue<string>()))}; "
                    + (runtime.Available ? "the worker is available." : $"the worker is unavailable ({runtime.Code})."));
            });

        yield return McpToolDefinition.Create("list_training_presets", "List training presets", McpToolClass.Read, true,
            "Saved training parameter presets (shared by all projects). Pass a presetId to start_training to use one.",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            """{ "type": "object", "properties": { "presets": { "type": "array", "items": { "type": "object" } } }, "required": ["presets"] }""",
            async call =>
            {
                call.Arguments.Only();
                var presets = await TrainingApi.ListPresetsAsync(new ListTrainingPresetsRequestDto(), call.CancellationToken);
                return Result(Wrap("presets", presets), presets.Length == 0 ? "No presets." : $"{presets.Length} presets: " + string.Join(", ", presets.Select(item => $"{item.Name} ({item.Id})")) + ".");
            });

        yield return McpToolDefinition.Create("save_training_preset", "Save training preset", McpToolClass.Write, true,
            """
            Saves training parameters under a name (1-80 characters). A preset with the same name (case-insensitive) is overwritten and keeps its
            id. Only the given keys are stored; the rest take their defaults when a run starts.
            """,
            $$"""
            { "type": "object", "properties": { "name": { "type": "string" }, "parameters": {{parametersSchema}} },
              "required": ["name", "parameters"], "additionalProperties": false }
            """,
            """{ "type": "object", "properties": { "preset": { "type": "object" } }, "required": ["preset"] }""",
            async call =>
            {
                call.Arguments.Only("name", "parameters");
                var preset = await TrainingApi.SavePresetAsync(new SaveTrainingPresetRequestDto(call.Arguments.String("name"), TrainingParameters(call.Arguments)),
                    call.CancellationToken);
                return Result(Wrap("preset", preset), $"Preset {preset.Name} saved with id {preset.Id}.");
            });

        yield return McpToolDefinition.Create("delete_training_preset", "Delete training preset", McpToolClass.Destructive, true,
            "Deletes a training preset by presetId (from list_training_presets). Runs started with it keep their parameters.",
            """{ "type": "object", "properties": { "presetId": { "type": "string" } }, "required": ["presetId"], "additionalProperties": false }""",
            """{ "type": "object", "properties": { "presetId": { "type": "string" } }, "required": ["presetId"] }""",
            async call =>
            {
                call.Arguments.Only("presetId");
                var deleted = await TrainingApi.DeletePresetAsync(new DeleteTrainingPresetRequestDto(call.Arguments.Guid("presetId")), call.CancellationToken);
                return Result(McpToolResult.Object(deleted), $"Preset {deleted.PresetId} deleted.");
            });

        yield return McpToolDefinition.Create("start_training", "Start training", McpToolClass.Write, false,
            $$"""
            Starts training a model version on a dataset version (datasetVersionId from the dataset tools; the dataset must match the project
            input). parameters are the fields of the training form: a missing key takes its default (see get_training_defaults); presetId takes
            a saved preset first, then parameters override it. Training is a GPU job: it waits in the queue while another GPU job or a live check
            runs. Returns { jobId, runId, status, job } at once; follow it with wait_job, read epochs with get_training_run and the log with
            get_training_log. After success job.result.versionId and the run's resultVersionId are the new model version; plots are viewable with
            view { trainingPlot: "results.png", runId }. Refusals: training.parameter_invalid (details.field), training.runtime_*, datasets.*.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "datasetVersionId": { "type": "string" },
                "parameters": {{parametersSchema}},
                "presetId": { "type": "string" },
                {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "datasetVersionId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "runId": { "type": "string" } },
              "required": ["jobId", "runId", "status", "job"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "datasetVersionId", "parameters", "presetId", "operationId", "waitSeconds");
                var projectId = arguments.String("projectId");
                var datasetVersionId = arguments.Guid("datasetVersionId");
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var given = TrainingParameters(arguments);
                var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                string? presetName = null;
                if (arguments.OptionalGuid("presetId") is { } presetId)
                {
                    var presets = await TrainingApi.ListPresetsAsync(new ListTrainingPresetsRequestDto(), call.CancellationToken);
                    var preset = presets.FirstOrDefault(item => item.Id == presetId)
                        ?? throw new CoreException("training.preset_not_found", "The preset was not found.");
                    foreach (var (key, value) in preset.Parameters) parameters[key] = value;
                    // As in the window: a preset changed before the start is not named in the passport.
                    if (given.Count == 0) presetName = preset.Name;
                }
                foreach (var (key, value) in given) parameters[key] = value;
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var run = await TrainingApi.StartRunAsync(new StartTrainingRunRequestDto(project.Id, datasetVersionId, parameters, presetName,
                    OperationId(arguments)), call.CancellationToken);
                var job = await Core.Jobs.GetAsync(project.Id, run.Id, call.CancellationToken);
                var result = await McpJobWaiter.StartedAsync(Core.Jobs, job, waitSeconds, call, $"Training run {run.Id:D} on {run.Dataset.Name}");
                result.Structured["runId"] = run.Id.ToString("D");
                return result;
            });

        yield return McpToolDefinition.Create("list_training_runs", "List training runs", McpToolClass.Read, true,
            """
            Training runs of a project, newest first: state, dataset, epoch progress, live metrics and resultVersionId (the model version a
            successful run created). runId equals the jobId of the run. Page with cursor = nextCursor.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 30 },
                "cursor": { "type": "string" } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "runs": { "type": "array", "items": {{TrainingRunSummarySchema}} }, "total": { "type": "integer" },
                "nextCursor": { "type": ["string", "null"] } },
              "required": ["runs", "total"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "limit", "cursor");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var limit = call.Arguments.Int("limit", 1, 200, 30);
                var page = await TrainingApi.ListRunsAsync(new ListTrainingRunsRequestDto(project.Id, limit, call.Arguments.OptionalGuid("cursor")),
                    call.CancellationToken);
                var structured = new JsonObject
                {
                    ["runs"] = new JsonArray(page.Items.Select(run => (JsonNode?)RunSummary(run)).ToArray()),
                    ["total"] = page.Total,
                    ["nextCursor"] = page.Items.Length == limit ? page.Items[^1].Id.ToString("D") : null,
                };
                return Result(structured, $"{page.Items.Length} of {page.Total} runs" + (page.Items.Length > 0
                    ? ": " + string.Join("; ", page.Items.Take(10).Select(run => $"{run.Id} {run.State}")) + "."
                    : "."));
            });

        yield return McpToolDefinition.Create("get_training_run", "Get training run", McpToolClass.Read, true,
            """
            A training run in full: requested and effective parameters, environment, progress, error and the epoch history thinned to at most
            maxEpochs rows (the first, the last and the best epoch are always kept; each row has losses, validation metrics, lr and fitness).
            plots lists picture names of the resulting version; show one with view { trainingPlot: name, runId }.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "runId": { "type": "string" },
                "maxEpochs": { "type": "integer", "minimum": 2, "maximum": 1000, "default": 40 } },
              "required": ["projectId", "runId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "run": { "type": "object" },
                "epochs": { "type": "object", "properties": { "total": { "type": "integer" }, "shown": { "type": "integer" }, "bestEpoch": { "type": ["integer", "null"] },
                  "items": { "type": "array", "items": { "type": "object" } } } },
                "plots": { "type": "array", "items": { "type": "string" } } },
              "required": ["run", "epochs", "plots"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "runId", "maxEpochs");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var runId = call.Arguments.Guid("runId");
                var maxEpochs = call.Arguments.Int("maxEpochs", 2, 1000, 40);
                var run = await TrainingApi.GetRunAsync(new GetTrainingRunRequestDto(project.Id, runId), call.CancellationToken);
                var epochs = new List<TrainingEpochDto>();
                int? after = null;
                while (true)
                {
                    var page = await TrainingApi.ListEpochsAsync(new ListTrainingEpochsRequestDto(project.Id, runId, after, 1000), call.CancellationToken);
                    epochs.AddRange(page.Items);
                    if (page.NextAfter is not { } next || page.Items.Length == 0) break;
                    after = next;
                }
                var best = epochs.Where(epoch => epoch.Fitness is not null).MaxBy(epoch => epoch.Fitness)?.Epoch;
                var shown = Thin(epochs, maxEpochs, best);
                var plots = new JsonArray();
                if (run.ResultVersionId is { } versionId)
                {
                    try
                    {
                        var version = await project.Training.GetVersionAsync(versionId, call.CancellationToken);
                        foreach (var file in version.Files.Where(file => file.Kind == ModelFileKind.Plot)) plots.Add(Path.GetFileName(file.Name));
                    }
                    catch (CoreException error) when (error.Code == "training.version_not_found")
                    {
                        // The version was deleted; the run keeps its history.
                    }
                }
                var structured = new JsonObject
                {
                    ["run"] = McpToolResult.Node(run),
                    ["epochs"] = new JsonObject
                    {
                        ["total"] = epochs.Count,
                        ["shown"] = shown.Count,
                        ["bestEpoch"] = best,
                        ["items"] = new JsonArray(shown.Select(epoch => (JsonNode?)EpochRow(epoch)).ToArray()),
                    },
                    ["plots"] = plots,
                };
                var last = epochs.LastOrDefault();
                return Result(structured, $"Run {run.Id:D} is {run.State}, {epochs.Count} epochs" + (last is not null && last.Metrics.TryGetValue("mAP50", out var map)
                    ? string.Create(CultureInfo.InvariantCulture, $", last mAP50 {map:0.###}") : "")
                    + (run.ResultVersionId is { } created ? $", model version {created:D}." : "."));
            });

        yield return McpToolDefinition.Create("get_training_log", "Get training log", McpToolClass.Read, true,
            """
            The last lines of a training run log (tail, 1-1000, default 100): console lines of Ultralytics, stages, application lines and
            worker warnings (code with args and an English text). The full log is the resource maut://projects/{projectId}/training/runs/{runId}/log.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "runId": { "type": "string" },
                "tail": { "type": "integer", "minimum": 1, "maximum": 1000, "default": 100 } },
              "required": ["projectId", "runId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "lines": { "type": "array", "items": { "type": "object" } }, "total": { "type": "integer" }, "resource": { "type": "string" } },
              "required": ["lines", "total"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "runId", "tail");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var runId = call.Arguments.Guid("runId");
                var page = await TrainingApi.TailLogAsync(new TailTrainingLogRequestDto(project.Id, runId, call.Arguments.Int("tail", 1, 1000, 100)),
                    call.CancellationToken);
                var structured = new JsonObject
                {
                    ["lines"] = new JsonArray(page.Items.Select(entry => (JsonNode?)LogLine(entry)).ToArray()),
                    ["total"] = page.Total,
                    ["resource"] = TrainingLogUri(project.Id, runId),
                };
                return Result(structured, $"{page.Items.Length} of {page.Total} log lines" + (page.Items.Length > 0 ? ": " + Shorten(LogText(page.Items[^1]), 200) : "."));
            });
    }

    /// <summary>The parameters argument as worker protocol values; their ranges are checked by Core (training.parameter_invalid).</summary>
    private static Dictionary<string, JsonElement> TrainingParameters(McpArguments arguments)
    {
        if (arguments.Element("parameters") is not { } value) return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) throw McpToolException.InvalidArgument("parameters", "must be an object");
        return value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonObject RunSummary(TrainingRunResponseDto run) => new()
    {
        ["id"] = run.Id.ToString("D"),
        ["state"] = run.State,
        ["dataset"] = new JsonObject { ["versionId"] = run.Dataset.VersionId.ToString("D"), ["name"] = run.Dataset.Name },
        ["architecture"] = run.Architecture,
        ["imageSize"] = run.ImageSize,
        ["presetName"] = run.PresetName,
        ["epoch"] = run.Progress.Epoch,
        ["epochs"] = run.Progress.Epochs ?? (run.Parameters.TryGetValue("epochs", out var epochs) && epochs.TryGetInt32(out var count) ? count : null),
        ["progress"] = run.Job.Progress,
        ["metrics"] = McpToolResult.Node(run.Progress.Metrics),
        ["error"] = McpToolResult.Node(run.Error),
        ["resultVersionId"] = run.ResultVersionId?.ToString("D"),
        ["createdAt"] = run.Job.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>At most maxRows epochs evenly over the run; the first, the last and the best stay.</summary>
    private static List<TrainingEpochDto> Thin(IReadOnlyList<TrainingEpochDto> epochs, int maxRows, int? best)
    {
        if (epochs.Count <= maxRows) return epochs.ToList();
        var keep = new SortedSet<int> { 0, epochs.Count - 1 };
        if (best is { } bestEpoch && epochs.ToList().FindIndex(epoch => epoch.Epoch == bestEpoch) is >= 0 and var index) keep.Add(index);
        var slots = maxRows - keep.Count;
        for (var slot = 1; slot <= slots && keep.Count < maxRows; slot++) keep.Add((int)Math.Round(slot * (epochs.Count - 1) / (double)(slots + 1)));
        return keep.Select(item => epochs[item]).ToList();
    }

    private static JsonObject EpochRow(TrainingEpochDto epoch)
    {
        var row = new JsonObject { ["epoch"] = epoch.Epoch };
        foreach (var (key, value) in epoch.Metrics) row[key] = Math.Round(value, 5);
        foreach (var (key, value) in epoch.Losses) row[key] = Math.Round(value, 5);
        if (epoch.Lr is { } lr) row["lr"] = lr;
        if (epoch.Fitness is { } fitness) row["fitness"] = Math.Round(fitness, 5);
        return row;
    }

    private static JsonObject LogLine(TrainingLogEntryDto entry) => new()
    {
        ["sequence"] = entry.Sequence,
        ["at"] = entry.At.ToString("O", CultureInfo.InvariantCulture),
        ["kind"] = entry.Kind,
        ["text"] = LogText(entry),
        ["code"] = entry.Message.Code,
        ["args"] = McpToolResult.Node(entry.Message.Args),
    };

    /// <summary>A log line as text: the console text, or the code with its args when the line is coded.</summary>
    private static string LogText(TrainingLogEntryDto entry)
    {
        var message = entry.Message;
        if (message.Text is { Length: > 0 } text) return text;
        return (message.Code ?? "") + (message.Args is { Count: > 0 } args ? " " + string.Join(", ", args.Select(pair => $"{pair.Key}={pair.Value}")) : "");
    }

    /// <summary>Prompt train_and_compare: train on a dataset, evaluate on its Test split and compare with the working version.</summary>
    private static IEnumerable<McpPromptDefinition> TrainingPrompts()
    {
        yield return new McpPromptDefinition("train_and_compare", "Train and compare",
            "Train a new model version on a dataset version, evaluate it on the Test split and compare it with the working version.",
            [
                new McpPromptArgument("datasetVersionId", "Dataset version to train on (from list_datasets).", true),
                new McpPromptArgument("projectId", "Project of the dataset; when omitted, find it with list_projects and list_datasets.", false),
            ],
            (values, _) =>
            {
                var dataset = PromptArgument(values, "datasetVersionId");
                var project = values.TryGetValue("projectId", out var id) && !string.IsNullOrWhiteSpace(id)
                    ? $"in project {id}"
                    : "(find its project with list_projects and list_datasets)";
                return Task.FromResult($$"""
                    Train a new YOLO model version in Maut Vision on dataset version {{dataset}} {{project}} and compare it with the current working version.

                    1. Call list_model_versions and remember the working version (isWorking) and its dataset.
                    2. Call get_training_defaults with the projectId; keep the defaults unless I asked otherwise, choose the device from devices.
                    3. Call start_training with projectId and datasetVersionId {{dataset}}; follow the job with wait_job until it finishes
                       (call it again while timedOut is true). On failure read get_training_log and explain the error.
                    4. Call get_training_run for the run: report the best epoch and how mAP50 and the losses developed; look at the plot
                       results.png with view { trainingPlot: "results.png", runId }.
                    5. Call evaluate_model for the new version and for the working version on dataset {{dataset}}, split test (val when test is
                       empty), and wait for both jobs with wait_job.
                    6. Call compare_model_versions with both versionIds and datasetVersionId {{dataset}}, split test, and summarize which version is
                       better and by how much. Do not change the working version unless I confirm it; then use set_working_version.
                    """);
            });
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    internal static string TrainingLogUri(string projectId, Guid runId) => $"{McpResources.ProjectsUri}/{projectId}/training/runs/{runId:D}/log";

    internal static string PassportUri(string projectId, Guid versionId) => $"{McpResources.ProjectsUri}/{projectId}/models/{versionId:D}/passport";

    /// <summary>The whole log of a run (resource maut://projects/{id}/training/runs/{runId}/log): every line in order, at most 20,000.</summary>
    internal async Task<JsonObject> TrainingLogAsync(string projectId, Guid runId, CancellationToken cancellationToken)
    {
        var project = await PrepareAsync(projectId, cancellationToken);
        var lines = new JsonArray();
        long? after = null;
        long total = 0;
        while (lines.Count < 20_000)
        {
            var page = await TrainingApi.ListLogAsync(new ListTrainingLogRequestDto(project.Id, runId, after, 1000), cancellationToken);
            total = page.Total;
            foreach (var entry in page.Items) lines.Add(LogLine(entry));
            if (page.NextAfter is not { } next || page.Items.Length == 0) break;
            after = next;
        }
        return new JsonObject { ["runId"] = runId.ToString("D"), ["total"] = total, ["truncated"] = lines.Count < total, ["lines"] = lines };
    }
}
