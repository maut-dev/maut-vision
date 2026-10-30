using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Datasets;
using MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string DatasetVersionSchema = """
        { "type": "object", "description": "Dataset version: id (versionId), name, revision, archivedAt, integrityState verified|corrupt|unverified, width×height, inputSizeMatches (training needs true), specification (how it was built), classMap [{index, classId, name}] (model outputs), counts {train, val, test, total}, composition (boxes per class, groups).",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] },
            "integrityState": { "type": "string" }, "width": { "type": "integer" }, "height": { "type": "integer" }, "inputSizeMatches": { "type": "boolean" },
            "specification": { "type": "object" }, "classMap": { "type": "array" }, "counts": { "type": "object" }, "composition": { "type": "object" } },
          "required": ["id", "name", "revision", "integrityState", "counts"] }
        """;

    private const string DatasetSpecificationSchema = """
        "name": { "type": "string", "description": "Name of the new dataset version, 1-120 characters." },
        "description": { "type": "string", "default": "" },
        "groupIds": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "Image groups to take frames from (image group tools; a generation run gives outputGroupId). Only reviewed frames are taken." },
        "classIds": { "type": "array", "items": { "type": "string" }, "description": "Classes the model learns, indexes 0..n-1 in catalog order; default — every active class." },
        "targetVariantIds": { "type": "array", "items": { "type": "string" }, "description": "Class-variant bindings whose objects are targets; default — the available bindings of the chosen classes." },
        "negativeVariantIds": { "type": "array", "items": { "type": "string" }, "description": "Bindings whose objects become negatives; default — the archived bindings of the chosen classes." },
        "unassignedVariantRole": { "type": "string", "enum": ["target", "negative", "reject"], "default": "target", "description": "Objects without a variant." },
        "unselectedClassRole": { "type": "string", "enum": ["negative", "reject"], "default": "negative", "description": "Objects of classes not chosen." },
        "includeReviewedEmpty": { "type": "boolean", "default": true, "description": "Take reviewed frames without objects." },
        "allowPartialGeneration": { "type": "boolean", "default": false, "description": "Take frames of unfinished generation runs." },
        "seed": { "type": "integer", "minimum": 0, "maximum": 4294967295, "description": "Seed of the split; random by default." },
        "valRatio": { "type": "number", "minimum": 0, "maximum": 1, "default": 0.2, "description": "Validation share, above 0 (a fraction, not percent)." },
        "testRatio": { "type": "number", "minimum": 0, "maximum": 1, "default": 0, "description": "Test share; valRatio + testRatio below 1. Train gets the rest." }
        """;

    private static readonly string[] DatasetSpecificationFields =
    [
        "name", "description", "groupIds", "classIds", "targetVariantIds", "negativeVariantIds", "unassignedVariantRole", "unselectedClassRole",
        "includeReviewedEmpty", "allowPartialGeneration", "seed", "valRatio", "testRatio",
    ];

    private DatasetsController? datasetsController;

    private DatasetsController Datasets => datasetsController ??= new DatasetsController(Core.Projects, Core.Jobs, importResources, importSelections, NoMcpDialogs);

    private IEnumerable<McpToolDefinition> DatasetTools()
    {
        yield return McpToolDefinition.Create("list_datasets", "List datasets", McpToolClass.Read, true,
            """
            Ready dataset versions of a project, newest first, archived ones included unless includeArchived=false. A versionId is used by
            get_dataset_version, verify_dataset, export_dataset, training and as a view target: view { target: { datasetSample: versionId,
            split: train|val|test, index } } shows a sample with its boxes. The full composition is the resource
            maut://projects/{projectId}/datasets/{versionId}/manifest.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "includeArchived": { "type": "boolean", "default": true } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "datasets": { "type": "array", "items": {{DatasetVersionSchema}} } }, "required": ["datasets"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "includeArchived");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var versions = await Datasets.ListAsync(new ListDatasetsRequestDto(projectId, call.Arguments.Bool("includeArchived", true)), call.CancellationToken);
                return Result(Wrap("datasets", versions), versions.Length == 0
                    ? "No datasets."
                    : $"{versions.Length} datasets: " + string.Join("; ", versions.Take(20).Select(version =>
                        $"{version.Name} ({version.Id:D}, {version.Counts.Train}/{version.Counts.Val}/{version.Counts.Test}, {version.IntegrityState})")) + ".");
            });

        yield return McpToolDefinition.Create("get_dataset_version", "Get dataset version", McpToolClass.Read, true,
            """
            A dataset version with its specification, classes, counts and composition, and the first samplesPerSplit samples of every split:
            sampleId (the source image ID), groupId and boxes. View a sample with view { target: { datasetSample: versionId, sampleId } } or
            { datasetSample: versionId, split, index }.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" },
                "samplesPerSplit": { "type": "integer", "minimum": 0, "maximum": 50, "default": 5 } },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "dataset": {{DatasetVersionSchema}},
                "samples": { "type": "object", "properties": { "train": { "type": "array" }, "val": { "type": "array" }, "test": { "type": "array" } } } },
              "required": ["dataset", "samples"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "versionId", "samplesPerSplit");
                var projectId = call.Arguments.String("projectId");
                var versionId = call.Arguments.Guid("versionId");
                var limit = call.Arguments.Int("samplesPerSplit", 0, 50, 5);
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var version = await Datasets.GetAsync(new GetDatasetRequestDto(projectId, versionId), call.CancellationToken);
                var samples = new JsonObject();
                foreach (var split in new[] { DatasetSplit.Train, DatasetSplit.Validation, DatasetSplit.Test })
                {
                    var items = limit == 0 ? [] : (await project.Datasets.ListSamplesAsync(versionId, split, limit, null, call.CancellationToken)).Items;
                    samples[DatasetsResponseMapper.Key(split)] = new JsonArray(items.Select(sample => (JsonNode?)SampleJson(sample, version.ClassMap)).ToArray());
                }
                var structured = new JsonObject { ["dataset"] = McpToolResult.Node(version), ["samples"] = samples };
                return Result(structured, $"Dataset {version.Name}: {version.Width}×{version.Height}, train {version.Counts.Train}, val {version.Counts.Val}, "
                    + $"test {version.Counts.Test}, {version.ClassMap.Length} classes, integrity {version.IntegrityState}.");
            });

        yield return McpToolDefinition.Create("plan_dataset", "Plan dataset", McpToolClass.Read, true,
            """
            "Check before build": selects the frames of the specification, assigns train/val/test and counts them per split and class without
            building anything. Returns the plan with its id and hash, skipped frames by reason, warnings with English texts and buildable
            (frames exist and no identical pixels fall into different splits). Build it with build_dataset { planId }. Only reviewed frames
            count; a generation run's frames are reviewed already. Errors name the wrong field (datasets.*_invalid, details.field).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, {{DatasetSpecificationSchema}} },
              "required": ["projectId", "name", "groupIds"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "plan": { "type": "object", "description": "id (planId), specification, width, height, classes, counts {train, val, test, total}, composition, skipped, warnings, sufficientSamples, buildable, hash." },
                "warnings": { "type": "array", "items": { "type": "string" } } },
              "required": ["plan", "warnings"] }
            """,
            async call =>
            {
                call.Arguments.Only(["projectId", .. DatasetSpecificationFields]);
                var projectId = call.Arguments.String("projectId");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var plan = await PlanDatasetAsync(project, call.Arguments, call.CancellationToken);
                var warnings = PlanWarnings(plan);
                var structured = new JsonObject { ["plan"] = McpToolResult.Node(plan), ["warnings"] = new JsonArray(warnings.Select(text => (JsonNode?)text).ToArray()) };
                return Result(structured, $"Plan {plan.Id:D}: train {plan.Counts.Train}, val {plan.Counts.Val}, test {plan.Counts.Test}, skipped {plan.Skipped.Total}; "
                    + (plan.Buildable ? "buildable." : "not buildable.") + (warnings.Length > 0 ? " Warnings: " + string.Join(" ", warnings) : ""));
            });

        yield return McpToolDefinition.Create("build_dataset", "Build dataset", McpToolClass.Write, false,
            """
            Builds an immutable dataset version (YOLO images and labels) as a job, like "Build dataset" in the window. Pass planId from
            plan_dataset (planHash optional), or the specification fields of plan_dataset to plan and build in one call. A plan whose sources
            changed is datasets.plan_stale — plan again. The versionId is the jobId; the version is verified when the job succeeds.
            Returns { jobId, status, job, operation, versionId } at once.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "planId": { "type": "string", "description": "Plan from plan_dataset; without it the specification fields below are planned first." },
                "planHash": { "type": "string" },
                {{DatasetSpecificationSchema}},
                {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "versionId": { "type": "string" }, "operation": { "type": "object" }, "plan": { "type": ["object", "null"] } },
              "required": ["jobId", "status", "job", "versionId", "operation"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only(["projectId", "planId", "planHash", "operationId", "waitSeconds", .. DatasetSpecificationFields]);
                var projectId = arguments.String("projectId");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                DatasetPlanResponseDto? planned = null;
                Guid planId;
                string hash;
                if (arguments.OptionalGuid("planId") is { } given)
                {
                    if (DatasetSpecificationFields.Any(arguments.Has)) throw McpToolException.InvalidArgument("planId", "give either planId or the specification fields");
                    planId = given;
                    hash = arguments.OptionalString("planHash")
                        ?? (await Datasets.GetPlanAsync(new GetDatasetPlanRequestDto(projectId, planId), call.CancellationToken)).Hash;
                }
                else
                {
                    if (arguments.Has("planHash")) throw McpToolException.InvalidArgument("planHash", "is used only with planId");
                    planned = await PlanDatasetAsync(project, arguments, call.CancellationToken);
                    (planId, hash) = (planned.Id, planned.Hash);
                }
                var operation = await Datasets.StartBuildAsync(new StartDatasetBuildRequestDto(projectId, planId, hash, OperationId(arguments)), call.CancellationToken);
                return await DatasetOperationResultAsync(projectId, operation, arguments, call, "Dataset build", planned);
            });

        yield return McpToolDefinition.Create("verify_dataset", "Verify dataset", McpToolClass.Write, false,
            """
            Checks the integrity of a dataset version as a job: every image, label, data.yaml and manifest.json is read and compared with its
            SHA-256. The version becomes verified or corrupt; the job result lists the problems. Returns { jobId, status, job, operation } at once.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" }, {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "versionId": { "type": "string" }, "operation": { "type": "object" } },
              "required": ["jobId", "status", "job", "operation"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "versionId", "operationId", "waitSeconds");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var operation = await Datasets.StartVerifyAsync(new StartDatasetVerifyRequestDto(projectId, call.Arguments.Guid("versionId"), OperationId(call.Arguments)),
                    call.CancellationToken);
                return await DatasetOperationResultAsync(projectId, operation, call.Arguments, call, "Dataset integrity check", null);
            });

        yield return McpToolDefinition.Create("export_dataset", "Export dataset", McpToolClass.Write, false,
            """
            Exports a dataset version as a ZIP in YOLO format (images/ and labels/ by split, data.yaml, manifest.json) as a job, like "Export ZIP"
            in the window. destinationPath is an absolute path of a .zip file in an existing folder, or an existing folder (the file is then
            "<dataset name>.zip"). An existing file is not replaced unless overwrite=true (mcp.destination_exists); replacing needs the full
            access level. A corrupt or archived version is not exported. Returns { jobId, status, job, operation, path } at once.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" },
                "destinationPath": { "type": "string", "description": "For example D:\\export\\street.zip or D:\\export." },
                "overwrite": { "type": "boolean", "default": false },
                {{OperationIdSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "versionId", "destinationPath"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "versionId": { "type": "string" }, "operation": { "type": "object" }, "path": { "type": "string" } },
              "required": ["jobId", "status", "job", "operation", "path"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "versionId", "destinationPath", "overwrite", "operationId", "waitSeconds");
                var projectId = arguments.String("projectId");
                var versionId = arguments.Guid("versionId");
                await PrepareAsync(projectId, call.CancellationToken);
                var version = await Datasets.GetAsync(new GetDatasetRequestDto(projectId, versionId), call.CancellationToken);
                var path = ExportDestination(arguments.String("destinationPath"), DatasetsController.DefaultFileName(version.Name));
                if (File.Exists(path) || Directory.Exists(path))
                {
                    if (!arguments.Bool("overwrite", false) || Directory.Exists(path))
                        throw new McpToolException("mcp.destination_exists", "The destination file exists.", new Dictionary<string, string> { ["path"] = path });
                    var level = dependencies.Settings().AccessLevel;
                    if (!McpAccessLevels.Allows(level, McpToolClass.Destructive))
                        throw new McpToolException("mcp.access_denied", "Replacing a file needs the full access level.",
                            new Dictionary<string, string> { ["level"] = level, ["required"] = McpAccessLevels.Full });
                }
                var token = importSelections.Register([path], FileSelectionKind.SaveFile);
                var operation = await Datasets.StartExportAsync(new StartDatasetExportRequestDto(projectId, versionId, token, OperationId(arguments)),
                    call.CancellationToken);
                var result = await DatasetOperationResultAsync(projectId, operation, arguments, call, "Dataset export", null);
                result.Structured["path"] = path;
                return result with { Text = result.Text + $" Destination {path}." };
            });

        yield return McpToolDefinition.Create("archive_dataset", "Archive dataset", McpToolClass.Write, true,
            """
            Moves a dataset version to the archive (archived=true, the default) or restores it. An archived version is read-only: it cannot be
            verified, exported or trained on; nothing is deleted. Pass revision from list_datasets or omit it for the current one.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" },
                "archived": { "type": "boolean", "default": true }, "revision": { "type": "integer" } },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "dataset": {{DatasetVersionSchema}} }, "required": ["dataset"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "versionId", "archived", "revision");
                var projectId = call.Arguments.String("projectId");
                var versionId = call.Arguments.Guid("versionId");
                await PrepareAsync(projectId, call.CancellationToken);
                var revision = call.Arguments.Has("revision")
                    ? (long)call.Arguments.Double("revision", 0, long.MaxValue)
                    : (await Datasets.GetAsync(new GetDatasetRequestDto(projectId, versionId), call.CancellationToken)).Revision;
                var archived = call.Arguments.Bool("archived", true);
                var version = await Datasets.SetArchivedAsync(new SetDatasetArchivedRequestDto(projectId, versionId, archived, revision), call.CancellationToken);
                return Result(Wrap("dataset", version), archived ? $"Dataset {version.Name} is archived." : $"Dataset {version.Name} is restored from the archive.");
            });

        yield return McpToolDefinition.Create("delete_dataset_version", "Delete dataset version", McpToolClass.Destructive, false,
            """
            Permanently deletes the files and samples of a dataset version; its passport stays for the models trained on it. Refused like in
            the window while an integrity check, an export, training or a model check uses it (datasets.deletion_blocked, details.blockers).
            Prefer archive_dataset when the data may be needed.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "versionId": { "type": "string" } },
              "required": ["projectId", "versionId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "versionId": { "type": "string" }, "samples": { "type": "integer" }, "operations": { "type": "integer" },
                "cleanupPending": { "type": "boolean" } }, "required": ["versionId", "samples", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "versionId");
                var projectId = call.Arguments.String("projectId");
                var versionId = call.Arguments.Guid("versionId");
                await PrepareAsync(projectId, call.CancellationToken);
                var review = await Datasets.ReviewDeletionAsync(new ReviewDatasetDeletionRequestDto(projectId, versionId), call.CancellationToken);
                ThrowIfBlocked("datasets", review.Name, review.Blockers);
                var result = await Datasets.DeleteAsync(new DeleteDatasetRequestDto(projectId, versionId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Dataset {review.Name} deleted ({result.Samples} samples).");
            });
    }

    /// <summary>A .zip path in an existing folder, or a folder that gets the default file name; the folder passes the path rules.</summary>
    private string ExportDestination(string destination, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new McpToolException("mcp.path_invalid", "The path is not an absolute Windows path.", new Dictionary<string, string> { ["argument"] = "destinationPath" });
        if (Directory.Exists(destination)) return Path.Combine(dependencies.Paths.ResolveDirectory(destination, "destinationPath"), defaultName);
        var name = Path.GetFileName(destination);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.Length <= 4)
            throw McpToolException.InvalidArgument("destinationPath", "must be a .zip file or an existing folder");
        var folder = dependencies.Paths.ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)), "destinationPath");
        return Path.Combine(folder, name);
    }

    private async Task<McpToolResult> DatasetOperationResultAsync(string projectId, DatasetOperationResponseDto operation, McpArguments arguments, McpToolCall call,
        string what, DatasetPlanResponseDto? plan)
    {
        var result = await StartedJobAsync(projectId, operation.Job.Id, arguments, call, what);
        operation = await Datasets.GetOperationAsync(new GetDatasetOperationRequestDto(projectId, operation.Job.Id), call.CancellationToken);
        result.Structured["versionId"] = operation.VersionId.ToString("D");
        result.Structured["operation"] = McpToolResult.Node(operation);
        if (plan is not null) result.Structured["plan"] = McpToolResult.Node(plan);
        return result with { Text = result.Text + $" Dataset version {operation.VersionId:D} ({operation.VersionName})." };
    }

    private async Task<DatasetPlanResponseDto> PlanDatasetAsync(IProject project, McpArguments arguments, CancellationToken cancellationToken)
    {
        var specification = await DatasetSpecificationAsync(project, arguments, cancellationToken);
        return await Datasets.PlanAsync(new PlanDatasetRequestDto(project.Id, specification, "mcp:" + Guid.NewGuid().ToString("N")), cancellationToken);
    }

    /// <summary>
    /// The specification of the build form from the arguments, with the defaults of the window: every active class, targets — available
    /// bindings of the chosen classes, negatives — the others, Train 80 % / Validation 20 %, empty frames included, a random seed.
    /// </summary>
    private static async Task<DatasetSpecificationDto> DatasetSpecificationAsync(IProject project, McpArguments arguments, CancellationToken cancellationToken)
    {
        var name = arguments.String("name");
        var groupIds = GuidList(arguments, "groupIds", 1, 10_000);
        var classIds = (arguments.Has("classIds") ? GuidList(arguments, "classIds", 0, 100_000) : null);
        var targets = (arguments.Has("targetVariantIds") ? GuidList(arguments, "targetVariantIds", 0, 100_000) : null);
        var negatives = (arguments.Has("negativeVariantIds") ? GuidList(arguments, "negativeVariantIds", 0, 100_000) : null);
        if (classIds is null || targets is null || negatives is null)
        {
            var workspace = await project.Classes.GetWorkspaceAsync(cancellationToken);
            classIds ??= workspace.Classes.Where(item => item.ArchivedAt is null).Select(item => item.Id).ToArray();
            var selected = classIds.ToHashSet();
            var bindings = workspace.ClassVariants.Where(binding => selected.Contains(binding.ClassId)).ToArray();
            targets ??= bindings.Where(binding => binding.Available).Select(binding => binding.Id).ToArray();
            negatives ??= bindings.Where(binding => !binding.Available).Select(binding => binding.Id).ToArray();
        }
        var seed = arguments.Has("seed") ? (long)arguments.Double("seed", 0, uint.MaxValue) : RandomNumberGenerator.GetInt32(int.MaxValue);
        return new DatasetSpecificationDto(name, arguments.OptionalString("description") ?? "", groupIds, classIds, targets, negatives,
            arguments.OptionalString("unassignedVariantRole") ?? "target", arguments.OptionalString("unselectedClassRole") ?? "negative",
            arguments.Bool("includeReviewedEmpty", true), arguments.Bool("allowPartialGeneration", false), seed,
            arguments.Double("valRatio", 0, 1, 0.2), arguments.Double("testRatio", 0, 1, 0));
    }

    private static string[] PlanWarnings(DatasetPlanResponseDto plan) => plan.Warnings.Select(warning =>
    {
        var values = new Dictionary<string, string> { ["count"] = warning.Count.ToString(CultureInfo.InvariantCulture) };
        var key = "planWarning." + warning.Code;
        var text = McpErrors.Text(key, values);
        return text == McpErrors.Text("generic", null) ? McpErrors.Text("planWarning.default", values) : text;
    }).ToArray();

    /// <summary>A sample for the AI: its ID (the source image), split, group, size and boxes with class indexes and names (fractions of the frame).</summary>
    private static JsonObject SampleJson(DatasetSample sample, IReadOnlyList<DatasetClassEntryDto> classes)
    {
        var names = classes.ToDictionary(item => item.ClassId, item => item.Name);
        return new JsonObject
        {
            ["sampleId"] = sample.Id.ToString("D"),
            ["split"] = DatasetsResponseMapper.Key(sample.Split),
            ["groupId"] = sample.GroupId.ToString("D"),
            ["width"] = sample.Width,
            ["height"] = sample.Height,
            ["format"] = sample.Format == MautVision.Core.Interfaces.Projects.Images.ImageFileFormat.Jpeg ? "jpeg" : "png",
            ["boxes"] = new JsonArray(sample.Boxes.Select(box => (JsonNode?)new JsonObject
            {
                ["classIndex"] = box.Index, ["classId"] = box.ClassId.ToString("D"), ["class"] = names.GetValueOrDefault(box.ClassId),
                ["classVariantId"] = box.ClassVariantId?.ToString("D"),
                ["centerX"] = Math.Round(box.CenterX, 6), ["centerY"] = Math.Round(box.CenterY, 6), ["width"] = Math.Round(box.Width, 6), ["height"] = Math.Round(box.Height, 6),
            }).ToArray()),
            ["negatives"] = sample.Negatives.Count,
        };
    }

    /// <summary>
    /// The resource maut://projects/{projectId}/datasets/{versionId}/manifest: the version (specification, classes, counts) and every
    /// sample with its split, group, hashes and boxes, read page by page from the samples of the version.
    /// </summary>
    internal async Task<string> DatasetManifestAsync(string projectId, Guid versionId, CancellationToken cancellationToken)
    {
        var project = await PrepareAsync(projectId, cancellationToken);
        var version = await Datasets.GetAsync(new GetDatasetRequestDto(projectId, versionId), cancellationToken);
        var samples = new JsonArray();
        foreach (var split in new[] { DatasetSplit.Train, DatasetSplit.Validation, DatasetSplit.Test })
        {
            string? cursor = null;
            do
            {
                var page = await project.Datasets.ListSamplesAsync(versionId, split, 200, cursor, cancellationToken);
                foreach (var sample in page.Items)
                {
                    var item = SampleJson(sample, version.ClassMap);
                    item["imageHash"] = sample.ImageHash;
                    item["labelHash"] = sample.LabelHash;
                    samples.Add(item);
                }
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        return new JsonObject { ["dataset"] = McpToolResult.Node(version), ["samples"] = samples }.ToJsonString();
    }

    private static IEnumerable<McpPromptDefinition> DatasetPrompts()
    {
        yield return new McpPromptDefinition("prepare_dataset_from_folder", "Dataset from a folder of frames",
            "Imports the frames of a folder into an image group, reviews their annotation and builds a dataset 80/10/10.",
            [new McpPromptArgument("folder", "Absolute path of the folder with the frames.", true)],
            (values, _) => Task.FromResult($$"""
                Prepare a YOLO dataset in Maut Vision from the frames in the folder {{PromptArgument(values, "folder")}}.

                1. Call get_app_info, then preview_folder { path: "{{PromptArgument(values, "folder")}}" } to see how many images it holds.
                2. Pick the project with list_projects (ask me which one when it is not clear) or create one with create_project.
                3. Create an image group with create_image_group and import the folder with import_images; follow the job with wait_job.
                4. Look at the frames with list_images and view / view_many (overlay shows the boxes). Annotate the frames that need it with
                   set_annotations, using the classes of list_classes, and mark checked frames with set_image_reviewed: only reviewed frames go
                   into a dataset.
                5. Call plan_dataset with the group, valRatio 0.1 and testRatio 0.1 (Train 80 %). Show me the counts per split and class and the
                   warnings before building.
                6. Build it with build_dataset { planId }, wait for the job, then check a few samples with view { datasetSample: versionId, split, index }.
                Report the dataset name, versionId and the counts.
                """));

        yield return new McpPromptDefinition("synthetic_dataset", "Synthetic dataset from class images and backgrounds",
            "Builds a synthetic dataset: classes from subfolders of transparent PNG cut-outs, backgrounds from a folder, a generation recipe, generation and a dataset.",
            [
                new McpPromptArgument("classesFolder", "Absolute path of a folder with one subfolder per class holding transparent PNG cut-outs.", true),
                new McpPromptArgument("backgroundsFolder", "Absolute path of a folder with background images.", true),
            ],
            (values, _) => Task.FromResult($$"""
                Build a synthetic YOLO dataset in Maut Vision. Class images are in {{PromptArgument(values, "classesFolder")}} (one subfolder per class, transparent PNG
                cut-outs); backgrounds are in {{PromptArgument(values, "backgroundsFolder")}}.

                1. Call get_app_info and list_projects; use the project I name or create one with create_project (the model input, for example
                   640×640, is also the size of every background and generated frame).
                2. Look into the folders with preview_folder (recursive for the classes). For every subfolder create a class (create_class), make sure
                   a project variant exists (list_variants / create_variant), bind it with bind_class_variant and import the cut-outs with
                   import_class_assets; wait for the jobs.
                3. Create a background set with create_background_set and import the backgrounds with import_backgrounds: mode "whole" for images of
                   exactly the input size, mode "crop" to cut a frame of that size from larger images. Wait for the job and check a few with
                   list_backgrounds and view { background: id }.
                4. Create a recipe with create_recipe (backgroundSetIds, objectsPerImage, scale, emptyProbability) and look at its samples with
                   view { generationSample: recipeId, sampleIndex: 0..3 }; adjust it with update_recipe until objects look right.
                5. Start start_generation with a count I agree to (default 500) and follow it with wait_job; run.outputGroupId is the new image group.
                6. Plan the dataset with plan_dataset { groupIds: [outputGroupId] }, show me the plan, then build_dataset, verify_dataset and view a few
                   samples with view { datasetSample: versionId, split: "val" }.
                Report the recipe, the run and the dataset version.
                """));

        yield return new McpPromptDefinition("generate_from_video", "Backgrounds from a video and generation",
            "Cuts backgrounds from a video, generates annotated frames onto them with the existing class images and builds a dataset.",
            [new McpPromptArgument("videoPath", "Absolute path of the video (mp4, mkv, avi, mov, webm, m4v).", true)],
            (values, _) => Task.FromResult($$"""
                Use the video {{PromptArgument(values, "videoPath")}} as backgrounds for synthetic training data in Maut Vision.

                1. Call get_app_info (ffmpegAvailable must be true) and pick the project with list_projects; ask me when it is not clear.
                2. Look at the video with view { videoFrame: "{{PromptArgument(values, "videoPath")}}", timeSeconds } at a few moments, with the projectId.
                3. Create a background set with create_background_set and cut frames with import_video_backgrounds (stepSeconds about 1-2, a region
                   that keeps the interesting part; it is centered by default, skipSimilar true). Wait for the job and view some backgrounds with
                   list_backgrounds and view { background: id }.
                4. Create a recipe with create_recipe using this set and the classes that have images (list_classes), check
                   view { generationSample: recipeId } and start start_generation; follow it with wait_job.
                5. Plan and build a dataset from run.outputGroupId with plan_dataset and build_dataset, then show me a few samples with
                   view { datasetSample: versionId, split: "train" }.
                Report how many backgrounds, frames and samples were made.
                """));
    }

    /// <summary>A required argument of a prompt; a missing one is mcp.invalid_arguments.</summary>
    private static string PromptArgument(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw McpToolException.InvalidArgument(name, "required");
}
