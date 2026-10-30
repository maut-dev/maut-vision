using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Training.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string VersionIdSchema = """{ "type": "string", "description": "Model version ID (from list_model_versions or the resultVersionId of a training run)." }""";

    private const string ModelVersionSummarySchema = """
        { "type": "object", "description": "Model version: id, number, isWorking, runId, dataset, architecture, input, epochs, bestEpoch, metrics of best.pt (mAP50, mAP50_95, precision, recall), createdAt.",
          "properties": { "id": { "type": "string" }, "number": { "type": "integer" }, "isWorking": { "type": "boolean" }, "runId": { "type": "string" },
            "dataset": { "type": "object" }, "architecture": { "type": "string" }, "metrics": { "type": "object" }, "createdAt": { "type": "string" } },
          "required": ["id", "number", "isWorking", "metrics"] }
        """;

    private IEnumerable<McpToolDefinition> ModelTools()
    {
        yield return McpToolDefinition.Create("list_model_versions", "List model versions", McpToolClass.Read, true,
            """
            Model versions of a project, newest first, with their best.pt metrics, the dataset they were trained on and which one is the working
            version (the one the application uses by default). Use the id as versionId in the model and check tools.
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "required": ["projectId"], "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "versions": { "type": "array", "items": {{ModelVersionSummarySchema}} } }, "required": ["versions"] }""",
            async call =>
            {
                call.Arguments.Only("projectId");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var versions = await TrainingApi.ListVersionsAsync(new ListModelVersionsRequestDto(project.Id), call.CancellationToken);
                var structured = new JsonObject { ["versions"] = new JsonArray(versions.Select(version => (JsonNode?)VersionSummary(version)).ToArray()) };
                return Result(structured, versions.Length == 0 ? "No model versions." : $"{versions.Length} model versions: "
                    + string.Join("; ", versions.Take(10).Select(version => $"v{version.Number}{(version.IsWorking ? " (working)" : "")} {version.Id} mAP50 {Metric(version.Metrics, "mAP50")}")) + ".");
            });

        yield return McpToolDefinition.Create("get_model_version", "Get model version", McpToolClass.Read, true,
            """
            The passport of a model version: dataset and classes, architecture and input, requested and effective parameters, epochs and early
            stopping, best epoch, metrics, files (weights, ONNX, plots, log), notes (worker warnings) and the environment. Plots are shown with
            view { trainingPlot: name, runId }. The same JSON is the resource maut://projects/{projectId}/models/{versionId}/passport.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}} },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            """{ "type": "object", "properties": { "version": { "type": "object" } }, "required": ["version"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "versionId");
                var version = await PassportAsync(call.Arguments.String("projectId"), call.Arguments.Guid("versionId"), call.CancellationToken);
                return Result(new JsonObject { ["version"] = version }, $"Model version {version["number"]} ({version["id"]}).");
            });

        yield return McpToolDefinition.Create("set_working_version", "Make version working", McpToolClass.Write, true,
            "Makes a model version the working one of the project (exactly one version is working). Repeating it changes nothing.",
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}} },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "version": {{ModelVersionSummarySchema}} }, "required": ["version"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "versionId");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var version = await TrainingApi.SetWorkingVersionAsync(new SetWorkingModelVersionRequestDto(project.Id, call.Arguments.Guid("versionId")),
                    call.CancellationToken);
                return Result(new JsonObject { ["version"] = VersionSummary(version) }, $"Version {version.Number} is the working version.");
            });

        yield return McpToolDefinition.Create("export_model", "Export model ZIP", McpToolClass.Write, false,
            """
            Exports a model version as a ZIP (weights/best.pt, weights/best.onnx when present, manifest.json, classes.json, README.md) to
            destinationPath, an absolute path of a .zip file in an existing folder, or an existing folder (the file is then "model-vN.zip"). An
            existing file is not replaced unless overwrite=true (mcp.destination_exists); replacing needs the full access level. A CPU job:
            returns { jobId, status, job, versionId, path } at once; job.result has the file name, size and SHA-256 after wait_job.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}},
                "destinationPath": { "type": "string", "description": "For example D:\\models\\model-v3.zip or D:\\models." },
                "overwrite": { "type": "boolean", "default": false },
                {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "versionId", "destinationPath"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "versionId": { "type": "string" }, "path": { "type": "string" } },
              "required": ["jobId", "status", "job", "versionId", "path"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "destinationPath", "overwrite", "operationId", "waitSeconds");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var versionId = arguments.Guid("versionId");
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var version = await project.Training.GetVersionAsync(versionId, call.CancellationToken);
                var path = ExportDestination(arguments.String("destinationPath"), $"model-v{version.Number}.zip");
                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (!arguments.Bool("overwrite", false) || Directory.Exists(path))
                        throw new McpToolException("mcp.destination_exists", "The destination file exists.", new Dictionary<string, string> { ["path"] = path });
                    var level = dependencies.Settings().AccessLevel;
                    if (!McpAccessLevels.Allows(level, McpToolClass.Destructive))
                        throw new McpToolException("mcp.access_denied", "Replacing a file needs the full access level.",
                            new Dictionary<string, string> { ["level"] = level, ["required"] = McpAccessLevels.Full });
                }
                // The "Save as" selection the window passes after its dialog.
                var token = importSelections.Register([path], FileSelectionKind.SaveFile);
                var started = await TrainingApi.StartExportAsync(new StartModelExportRequestDto(project.Id, versionId, token, OperationId(arguments)),
                    call.CancellationToken);
                var job = await Core.Jobs.GetAsync(project.Id, started.Id, call.CancellationToken);
                var result = await McpJobWaiter.StartedAsync(Core.Jobs, job, waitSeconds, call, $"Export of version {version.Number}");
                result.Structured["versionId"] = versionId.ToString("D");
                result.Structured["path"] = path;
                return result with { Text = result.Text + $" Destination {path}." };
            });

        yield return McpToolDefinition.Create("delete_model_version", "Delete model version", McpToolClass.Destructive, false,
            """
            Deletes a model version with its files and its dataset evaluations. The working version and a version being evaluated, checked live
            or exported cannot be deleted: training.version_in_use with details.reason working | evaluation | check | export. Make another
            version working first. Version numbers are never reused.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "versionId": {{VersionIdSchema}} },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "versionId": { "type": "string" }, "number": { "type": "integer" }, "evaluations": { "type": "integer" },
                "cleanupPending": { "type": "boolean" } },
              "required": ["versionId", "number", "evaluations", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "versionId");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var deleted = await TrainingApi.DeleteVersionAsync(new DeleteModelVersionRequestDto(project.Id, call.Arguments.Guid("versionId")),
                    call.CancellationToken);
                return Result(McpToolResult.Object(deleted), $"Version {deleted.Number} deleted with {deleted.Evaluations} evaluations.");
            });
    }

    private static JsonObject VersionSummary(ModelVersionResponseDto version) => new()
    {
        ["id"] = version.Id.ToString("D"),
        ["number"] = version.Number,
        ["isWorking"] = version.IsWorking,
        ["runId"] = version.RunId.ToString("D"),
        ["dataset"] = new JsonObject { ["versionId"] = version.Dataset.VersionId.ToString("D"), ["name"] = version.Dataset.Name, ["counts"] = McpToolResult.Node(version.Dataset.Counts) },
        ["architecture"] = version.Architecture,
        ["input"] = McpToolResult.Node(version.Input),
        ["epochsCompleted"] = version.EpochsCompleted,
        ["bestEpoch"] = version.BestEpoch,
        ["classes"] = new JsonArray(version.Classes.Select(item => (JsonNode?)item.Name).ToArray()),
        ["metrics"] = McpToolResult.Node(version.Metrics),
        ["createdAt"] = version.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
    };

    private static string Metric(IReadOnlyDictionary<string, double> metrics, string key)
        => metrics.TryGetValue(key, out var value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "—";

    /// <summary>The passport of a version as get_model_version and the passport resource return it; resource channel URLs are of the window only.</summary>
    internal async Task<JsonObject> PassportAsync(string projectId, Guid versionId, CancellationToken cancellationToken)
    {
        var project = await PrepareAsync(projectId, cancellationToken);
        var version = await TrainingApi.GetVersionAsync(new GetModelVersionRequestDto(project.Id, versionId), cancellationToken);
        var passport = McpToolResult.Object(version);
        foreach (var file in passport["files"]!.AsArray())
        {
            var item = file!.AsObject();
            item.Remove("url");
            if (item["kind"]?.GetValue<string>() == "plot") item["view"] = new JsonObject { ["trainingPlot"] = Path.GetFileName(item["name"]!.GetValue<string>()), ["runId"] = version.RunId.ToString("D") };
        }
        return passport;
    }
}
