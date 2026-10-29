using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Desktop.Mcp.View;

namespace MautVision.Desktop.Mcp.Tools;

/// <summary>What a target shows: the encoded picture, the boxes it has (fractions of the frame) and metadata for structuredContent.</summary>
internal sealed record McpViewSource(byte[] Content, IReadOnlyList<McpViewBox> Boxes, JsonObject Metadata);

internal sealed partial class McpTools
{
    internal const int MaxViewTargets = 8;
    private const long MaxViewFileBytes = 64L * 1024 * 1024;

    private const string TargetSchema = """
        { "type": "object", "description": "Exactly one kind: {image: imageId} | {classAsset: assetId} | {background: backgroundId} | {generationSample: recipeId, sampleIndex?: 0-3, seed?} | {datasetSample: datasetVersionId, split: train|val|test, index?: 0-based, sampleId?} | {trainingPlot: file name such as results.png, runId} | {check: live checkId} | {path: absolute image path} | {uploadId} | {videoFrame: absolute video path, timeSeconds}. Project kinds and videoFrame need projectId.",
          "properties": {
            "image": { "type": "string" }, "classAsset": { "type": "string" }, "background": { "type": "string" },
            "generationSample": { "type": "string" }, "sampleIndex": { "type": "integer", "minimum": 0, "maximum": 3 }, "seed": { "type": "integer", "minimum": 0 },
            "datasetSample": { "type": "string" }, "split": { "type": "string", "enum": ["train", "val", "test"] }, "index": { "type": "integer", "minimum": 0 }, "sampleId": { "type": "string" },
            "trainingPlot": { "type": "string" }, "runId": { "type": "string" },
            "check": { "type": "string" },
            "path": { "type": "string" }, "uploadId": { "type": "string" },
            "videoFrame": { "type": "string" }, "timeSeconds": { "type": "number", "minimum": 0 } } }
        """;

    private const string ViewOptionsSchema = """
        "overlay": { "type": "boolean", "default": true, "description": "Draw annotation or detection boxes with class labels." },
        "region": { "type": "object", "description": "Zoom into a part of the frame, fractions 0-1 of the frame from the top-left corner.",
          "properties": { "x": { "type": "number", "minimum": 0, "maximum": 1 }, "y": { "type": "number", "minimum": 0, "maximum": 1 },
            "width": { "type": "number", "exclusiveMinimum": 0, "maximum": 1 }, "height": { "type": "number", "exclusiveMinimum": 0, "maximum": 1 } },
          "required": ["x", "y", "width", "height"] },
        "maxSize": { "type": "integer", "minimum": 128, "maximum": 2048, "default": 1024, "description": "Longer side of the picture in pixels." },
        "format": { "type": "string", "enum": ["jpeg", "png"], "default": "jpeg" }
        """;

    private IEnumerable<McpToolDefinition> ViewTools()
    {
        yield return McpToolDefinition.Create("view", "View image", McpToolClass.Read, true,
            """
            Shows a picture to you (MCP image content) with its metadata in structuredContent: frames of image groups, class images, backgrounds,
            generation samples, dataset samples, training plots, the latest frame of a live check, any image file on disk, an upload or a video
            frame. overlay draws annotation or detection boxes with class labels (boxes are also listed as fractions 0-1 of the frame);
            region zooms into a part of the frame to see small details; maxSize is the longer side (default 1024, at most 2048).
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "target": {{TargetSchema}},
                {{ViewOptionsSchema}} },
              "required": ["target"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "kind": { "type": "string" }, "width": { "type": "integer" }, "height": { "type": "integer" },
                "picture": { "type": "object", "properties": { "width": { "type": "integer" }, "height": { "type": "integer" }, "mimeType": { "type": "string" } } },
                "region": { "type": ["object", "null"] },
                "boxes": { "type": "array", "items": { "type": "object" } } },
              "required": ["kind", "width", "height", "picture", "boxes"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "target", "overlay", "region", "maxSize", "format");
                var options = ViewOptions(arguments);
                var target = arguments.Object("target") ?? throw McpToolException.InvalidArgument("target", "required");
                var source = await ResolveTargetAsync(arguments.OptionalString("projectId"), target, "target", call.CancellationToken);
                var rendered = await Task.Run(() => McpViewRenderer.Render(source.Content, options.Region, options.Overlay ? source.Boxes : [], options.MaxSize, options.Png),
                    call.CancellationToken);
                var metadata = Describe(source, rendered, options);
                var kind = metadata["kind"]!.GetValue<string>();
                return Result(metadata, $"{kind} {rendered.SourceWidth}×{rendered.SourceHeight}, {source.Boxes.Count} boxes; picture {rendered.Width}×{rendered.Height}.",
                    [new McpImage(rendered.Content, rendered.MimeType)]);
            });

        yield return McpToolDefinition.Create("view_many", "View several images", McpToolClass.Read, true,
            """
            Shows up to 8 targets at once as one numbered grid picture: the number in the corner of a cell is the position of the target in
            your list (from 1), use it to refer to them. Targets are the same as in view; a target that fails is a crossed cell and its error is
            in items[n].error. Metadata of every target is in items.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "targets": { "type": "array", "minItems": 1, "maxItems": 8, "items": {{TargetSchema}} },
                {{ViewOptionsSchema}} },
              "required": ["targets"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "items": { "type": "array", "items": { "type": "object", "properties": { "number": { "type": "integer" }, "error": { "type": ["object", "null"] } } } },
                "picture": { "type": "object" } },
              "required": ["items", "picture"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "targets", "overlay", "region", "maxSize", "format");
                var options = ViewOptions(arguments);
                var targets = arguments.Objects("targets", 1, MaxViewTargets);
                var projectId = arguments.OptionalString("projectId");
                var sources = new McpViewSource?[targets.Count];
                var items = new JsonArray();
                for (var index = 0; index < targets.Count; index++)
                {
                    try
                    {
                        sources[index] = await ResolveTargetAsync(projectId, targets[index], $"targets[{index}]", call.CancellationToken);
                        var item = (JsonObject)sources[index]!.Metadata.DeepClone();
                        item["number"] = index + 1;
                        item["boxes"] = Boxes(sources[index]!.Boxes);
                        items.Add(item);
                    }
                    catch (Exception error) when (error is McpToolException or CoreException or IOException or UnauthorizedAccessException)
                    {
                        var failure = McpErrors.From(error, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "view_many");
                        items.Add(new JsonObject
                        {
                            ["number"] = index + 1,
                            ["error"] = new JsonObject { ["code"] = failure.Code, ["message"] = failure.Message },
                        });
                    }
                }
                if (sources.All(source => source is null))
                {
                    var first = items[0]!["error"]!;
                    throw new McpToolException(first["code"]!.GetValue<string>(), "No target could be shown.");
                }
                var rendered = await Task.Run(() => McpViewRenderer.Grid(sources.Select(source => source?.Content).ToArray(),
                    sources.Select(_ => options.Region).ToArray(),
                    sources.Select(source => options.Overlay && source is not null ? source.Boxes : (IReadOnlyList<McpViewBox>)[]).ToArray(),
                    options.MaxSize, options.Png), call.CancellationToken);
                var structured = new JsonObject
                {
                    ["items"] = items,
                    ["picture"] = new JsonObject { ["width"] = rendered.Width, ["height"] = rendered.Height, ["mimeType"] = rendered.MimeType },
                    ["region"] = options.Region is { } region ? RegionNode(region) : null,
                };
                var failed = sources.Count(source => source is null);
                return Result(structured, $"Grid of {targets.Count} targets numbered 1-{targets.Count}" + (failed > 0 ? $"; {failed} failed (see items)." : "."),
                    [new McpImage(rendered.Content, rendered.MimeType)]);
            });
    }

    private sealed record ViewOptionValues(bool Overlay, McpViewRegion? Region, int MaxSize, bool Png);

    private static ViewOptionValues ViewOptions(McpArguments arguments)
    {
        var format = arguments.OptionalString("format") ?? "jpeg";
        if (format is not ("jpeg" or "png")) throw McpToolException.InvalidArgument("format", "must be jpeg or png");
        McpViewRegion? region = null;
        if (arguments.Object("region") is { } value)
        {
            value.Only("x", "y", "width", "height");
            region = new McpViewRegion(value.Double("x", 0, 1), value.Double("y", 0, 1), value.Double("width", 0, 1), value.Double("height", 0, 1));
            if (region.Width <= 0 || region.Height <= 0 || region.X + region.Width > 1.000001 || region.Y + region.Height > 1.000001)
                throw McpToolException.InvalidArgument("region", "must lie inside the frame with a positive width and height");
        }
        return new ViewOptionValues(arguments.Bool("overlay", true), region,
            arguments.Int("maxSize", McpViewRenderer.MinMaxSize, McpViewRenderer.MaxMaxSize, McpViewRenderer.DefaultMaxSize), format == "png");
    }

    private static JsonObject Describe(McpViewSource source, McpRenderedImage rendered, ViewOptionValues options)
    {
        var metadata = (JsonObject)source.Metadata.DeepClone();
        metadata["width"] = rendered.SourceWidth;
        metadata["height"] = rendered.SourceHeight;
        metadata["picture"] = new JsonObject { ["width"] = rendered.Width, ["height"] = rendered.Height, ["mimeType"] = rendered.MimeType };
        metadata["region"] = options.Region is { } region ? RegionNode(region) : null;
        metadata["boxes"] = Boxes(source.Boxes);
        return metadata;
    }

    private static JsonObject RegionNode(McpViewRegion region) => new() { ["x"] = region.X, ["y"] = region.Y, ["width"] = region.Width, ["height"] = region.Height };

    private static JsonArray Boxes(IReadOnlyList<McpViewBox> boxes) => new(boxes.Select(box => (JsonNode?)new JsonObject
    {
        ["label"] = box.Label, ["x"] = Math.Round(box.X, 5), ["y"] = Math.Round(box.Y, 5), ["width"] = Math.Round(box.Width, 5), ["height"] = Math.Round(box.Height, 5),
    }).ToArray());

    /// <summary>Reads a target of view: the bytes of its picture, the boxes it has and what it is.</summary>
    private async Task<McpViewSource> ResolveTargetAsync(string? projectId, McpArguments target, string argument, CancellationToken cancellationToken)
    {
        string[] kinds = ["image", "classAsset", "background", "generationSample", "datasetSample", "trainingPlot", "check", "path", "uploadId", "videoFrame"];
        var present = kinds.Where(target.Has).ToArray();
        if (present.Length != 1) throw McpToolException.InvalidArgument(argument, "must name exactly one kind: " + string.Join(", ", kinds));
        var kind = present[0];
        switch (kind)
        {
            case "path":
                target.Only("path");
                return FileSource("path", dependencies.Paths.ResolveFile(target.String("path"), argument + ".path"));
            case "uploadId":
                target.Only("uploadId");
                var upload = dependencies.Uploads.Get(target.Guid("uploadId"));
                return FileSource("upload", upload.Path, new JsonObject { ["uploadId"] = upload.Id.ToString("D"), ["name"] = upload.Name });
        }
        if (projectId is null) throw McpToolException.InvalidArgument("projectId", $"is required for the target kind {kind}");
        var project = await PrepareAsync(projectId, cancellationToken);
        return kind switch
        {
            "image" => await ImageSourceAsync(project, target, cancellationToken),
            "classAsset" => await ClassAssetSourceAsync(project, target, cancellationToken),
            "background" => await BackgroundSourceAsync(project, target, cancellationToken),
            "generationSample" => await GenerationSampleSourceAsync(project, target, cancellationToken),
            "datasetSample" => await DatasetSampleSourceAsync(project, target, cancellationToken),
            "trainingPlot" => await TrainingPlotSourceAsync(project, target, cancellationToken),
            "check" => await CheckSourceAsync(project, target, cancellationToken),
            _ => await VideoFrameSourceAsync(project, target, argument, cancellationToken),
        };
    }

    private static McpViewSource FileSource(string kind, string path, JsonObject? extra = null)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxViewFileBytes)
            throw new McpToolException("mcp.image_too_large", "The file is larger than 64 MB.", new Dictionary<string, string> { ["limitMb"] = "64" });
        var metadata = extra ?? new JsonObject();
        metadata["kind"] = kind;
        if (kind == "path") metadata["path"] = path;
        metadata["bytes"] = info.Length;
        return new McpViewSource(File.ReadAllBytes(path), [], metadata);
    }

    private static async Task<byte[]> ReadAsync(IProject project, string resourceKind, Guid id, CancellationToken cancellationToken)
    {
        await using var content = await project.Resources.OpenAsync(new ResourceReference(resourceKind, id), ResourceVariant.Original, cancellationToken);
        using var buffer = new MemoryStream();
        await content.Stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>Class and variant names and colors for labels; an unknown reference keeps its ID.</summary>
    private sealed class CatalogNames(CatalogWorkspace workspace)
    {
        private readonly Dictionary<Guid, CatalogClass> classes = workspace.Classes.ToDictionary(item => item.Id);
        private readonly Dictionary<Guid, ClassVariant> bindings = workspace.ClassVariants.ToDictionary(item => item.Id);
        private readonly Dictionary<Guid, ProjectVariant> variants = workspace.Variants.ToDictionary(item => item.Id);

        internal string Label(Guid classId, Guid? classVariantId)
        {
            var name = classes.TryGetValue(classId, out var item) ? item.Name : classId.ToString("D");
            return classVariantId is { } id && bindings.TryGetValue(id, out var binding) && variants.TryGetValue(binding.VariantId, out var variant)
                ? name + " · " + variant.Name
                : name;
        }

        internal string Color(Guid classId, Guid? classVariantId)
            => classVariantId is { } id && bindings.TryGetValue(id, out var binding) ? binding.DisplayColor : VariantColors.Default(classId);
    }

    private async Task<McpViewSource> ImageSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("image");
        var image = await project.Images.GetImageAsync(target.Guid("image"), cancellationToken);
        var annotation = await project.Images.GetAnnotationAsync(image.Id, null, cancellationToken);
        var names = new CatalogNames(await project.Classes.GetWorkspaceAsync(cancellationToken));
        var boxes = annotation.Objects.Select(item => new McpViewBox(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height,
            names.Label(item.ClassId, item.ClassVariantId), names.Color(item.ClassId, item.ClassVariantId))).ToArray();
        var metadata = new JsonObject
        {
            ["kind"] = "image",
            ["imageId"] = image.Id.ToString("D"),
            ["groupId"] = image.GroupId.ToString("D"),
            ["name"] = image.DisplayName,
            ["reviewState"] = image.ReviewState.ToString().ToLowerInvariant(),
            ["annotationRevision"] = annotation.Revision,
            ["roi"] = annotation.Roi is { } roi ? new JsonObject { ["x"] = roi.X, ["y"] = roi.Y, ["width"] = roi.Width, ["height"] = roi.Height } : null,
            ["objects"] = new JsonArray(annotation.Objects.Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id.ToString("D"), ["classId"] = item.ClassId.ToString("D"), ["classVariantId"] = item.ClassVariantId?.ToString("D"),
                ["label"] = names.Label(item.ClassId, item.ClassVariantId),
            }).ToArray()),
        };
        return new McpViewSource(await ReadAsync(project, ProjectImage.ResourceKind, image.Id, cancellationToken), boxes, metadata);
    }

    private static async Task<McpViewSource> ClassAssetSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("classAsset");
        var id = target.Guid("classAsset");
        var metadata = new JsonObject { ["kind"] = "classAsset", ["assetId"] = id.ToString("D") };
        return new McpViewSource(await ReadAsync(project, ClassAsset.ResourceKind, id, cancellationToken), [], metadata);
    }

    private static async Task<McpViewSource> BackgroundSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("background");
        var item = await project.Backgrounds.GetItemAsync(target.Guid("background"), cancellationToken);
        var metadata = new JsonObject
        {
            ["kind"] = "background", ["backgroundId"] = item.Id.ToString("D"), ["setId"] = item.SetId.ToString("D"), ["removed"] = item.RemovedAt is not null,
        };
        return new McpViewSource(await ReadAsync(project, BackgroundItem.ResourceKind, item.Id, cancellationToken), [], metadata);
    }

    private static async Task<McpViewSource> GenerationSampleSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("generationSample", "sampleIndex", "seed");
        var recipe = await project.Generation.GetRecipeAsync(target.Guid("generationSample"), cancellationToken);
        var index = target.Int("sampleIndex", 0, 3, 0);
        var seed = target.Has("seed") ? (uint)target.Double("seed", 0, uint.MaxValue) : 1u;
        var samples = await project.Generation.PreviewSamplesAsync(recipe.ToInput(), seed, index + 1, cancellationToken);
        var sample = samples[index];
        var names = new CatalogNames(await project.Classes.GetWorkspaceAsync(cancellationToken));
        var boxes = sample.Objects.Select(item => new McpViewBox(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height,
            names.Label(item.ClassId, item.ClassVariantId), names.Color(item.ClassId, item.ClassVariantId))).ToArray();
        var metadata = new JsonObject
        {
            ["kind"] = "generationSample", ["recipeId"] = recipe.Id.ToString("D"), ["recipeName"] = recipe.Name, ["seed"] = seed, ["sampleIndex"] = index,
            ["backgroundId"] = sample.BackgroundId.ToString("D"), ["missingObjects"] = sample.MissingObjects,
        };
        return new McpViewSource(sample.Png, boxes, metadata);
    }

    private static async Task<McpViewSource> DatasetSampleSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("datasetSample", "split", "index", "sampleId");
        var version = await project.Datasets.GetAsync(target.Guid("datasetSample"), cancellationToken);
        DatasetSample sample;
        if (target.OptionalGuid("sampleId") is { } sampleId) sample = await project.Datasets.GetSampleAsync(version.Id, sampleId, cancellationToken);
        else
        {
            var split = target.OptionalString("split") switch
            {
                "train" => DatasetSplit.Train,
                "val" => DatasetSplit.Validation,
                "test" => DatasetSplit.Test,
                null => throw McpToolException.InvalidArgument("split", "required without sampleId"),
                _ => throw McpToolException.InvalidArgument("split", "must be train, val or test"),
            };
            var index = target.Int("index", 0, 1_000_000, 0);
            string? cursor = null;
            var skipped = 0;
            while (true)
            {
                var page = await project.Datasets.ListSamplesAsync(version.Id, split, 200, cursor, cancellationToken);
                if (index - skipped < page.Items.Count)
                {
                    sample = page.Items[index - skipped];
                    break;
                }
                skipped += page.Items.Count;
                cursor = page.NextCursor;
                if (cursor is null)
                {
                    throw new McpToolException("mcp.sample_index_out_of_range", "The split has fewer samples.",
                        new Dictionary<string, string> { ["count"] = skipped.ToString(CultureInfo.InvariantCulture) });
                }
            }
        }
        var classes = version.ClassMap.ToDictionary(item => item.ClassId, item => item.Name);
        var variants = version.Variants.ToDictionary(item => item.ClassVariantId, item => item.Name);
        string Label(Guid classId, Guid? variantId, string prefix = "")
            => prefix + (classes.TryGetValue(classId, out var name) ? name : classId.ToString("D"))
                + (variantId is { } id && variants.TryGetValue(id, out var variant) ? " · " + variant : "");
        var boxes = sample.Boxes.Select(box => new McpViewBox(box.CenterX - box.Width / 2, box.CenterY - box.Height / 2, box.Width, box.Height,
                Label(box.ClassId, box.ClassVariantId), box.ClassVariantId is { } id ? VariantColors.Default(id) : VariantColors.Default(box.ClassId)))
            .Concat(sample.Negatives.Select(box => new McpViewBox(box.CenterX - box.Width / 2, box.CenterY - box.Height / 2, box.Width, box.Height,
                Label(box.ClassId, box.ClassVariantId, "negative: "), "#9ca3af")))
            .ToArray();
        var metadata = new JsonObject
        {
            ["kind"] = "datasetSample", ["datasetVersionId"] = version.Id.ToString("D"), ["sampleId"] = sample.Id.ToString("D"),
            ["split"] = sample.Split switch { DatasetSplit.Train => "train", DatasetSplit.Validation => "val", _ => "test" },
            ["groupId"] = sample.GroupId.ToString("D"), ["negatives"] = sample.Negatives.Count,
        };
        return new McpViewSource(await ReadAsync(project, DatasetSample.ResourceKind, sample.ResourceId, cancellationToken), boxes, metadata);
    }

    private static async Task<McpViewSource> TrainingPlotSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("trainingPlot", "runId");
        var name = target.String("trainingPlot");
        var run = await project.Training.GetRunAsync(target.Guid("runId"), cancellationToken);
        if (run.ResultVersionId is not { } versionId)
            throw new McpToolException("mcp.plot_not_found", "The run has no model version with plots yet.", new Dictionary<string, string> { ["name"] = name });
        var version = await project.Training.GetVersionAsync(versionId, cancellationToken);
        var plots = version.Files.Where(file => file.Kind == ModelFileKind.Plot).ToArray();
        var file = plots.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? plots.FirstOrDefault(item => Path.GetFileName(item.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpToolException("mcp.plot_not_found", "The run has no plot with this name.",
                new Dictionary<string, string> { ["name"] = name, ["available"] = string.Join(", ", plots.Select(item => Path.GetFileName(item.Name))) });
        var metadata = new JsonObject
        {
            ["kind"] = "trainingPlot", ["runId"] = run.Id.ToString("D"), ["versionId"] = version.Id.ToString("D"), ["name"] = file.Name,
            ["available"] = new JsonArray(plots.Select(item => (JsonNode?)Path.GetFileName(item.Name)).ToArray()),
        };
        return new McpViewSource(await ReadAsync(project, ModelVersion.ResourceKind, file.ResourceId, cancellationToken), [], metadata);
    }

    private static async Task<McpViewSource> CheckSourceAsync(IProject project, McpArguments target, CancellationToken cancellationToken)
    {
        target.Only("check");
        var check = await project.Checks.GetCheckAsync(target.Guid("check"), 0, cancellationToken);
        var preview = project.Checks.GetPreview(check.Id)
            ?? throw new McpToolException("mcp.check_preview_missing", "The check has no frame to show yet.");
        var frame = check.RecentFrames.Concat(check.LastFrame is { } last ? [last] : []).FirstOrDefault(item => item.Index == preview.Info.FrameIndex);
        var boxes = frame?.Detections.Select(item => new McpViewBox(item.X, item.Y, item.Width, item.Height,
            string.Create(CultureInfo.InvariantCulture, $"{item.Name} {item.Confidence:0.00}"), VariantColors.Default(item.ClassId ?? Guid.Empty))).ToArray() ?? [];
        var metadata = new JsonObject
        {
            ["kind"] = "check", ["checkId"] = check.Id.ToString("D"), ["state"] = check.State.ToString().ToLowerInvariant(), ["frameIndex"] = preview.Info.FrameIndex,
            ["timeMs"] = preview.Info.TimeMs, ["detectionsKnown"] = frame is not null,
            ["detections"] = new JsonArray((frame?.Detections ?? []).Select(item => (JsonNode?)new JsonObject
            {
                ["class"] = item.Name, ["classId"] = item.ClassId?.ToString("D"), ["confidence"] = Math.Round(item.Confidence, 4),
            }).ToArray()),
        };
        return new McpViewSource(preview.Content, boxes, metadata);
    }

    private async Task<McpViewSource> VideoFrameSourceAsync(IProject project, McpArguments target, string argument, CancellationToken cancellationToken)
    {
        target.Only("videoFrame", "timeSeconds");
        var path = dependencies.Paths.ResolveFile(target.String("videoFrame"), argument + ".videoFrame");
        if (!VideoFormats.IsVideo(path)) throw new CoreException("video.unsupported", "The file is not a supported video.");
        var seconds = target.Double("timeSeconds", 0, 1_000_000, 0);
        var frame = await project.Backgrounds.PreviewVideoFrameAsync(path, (long)Math.Round(seconds * 1000), McpViewRenderer.MaxMaxSize, cancellationToken);
        var metadata = new JsonObject
        {
            ["kind"] = "videoFrame", ["path"] = path, ["timeSeconds"] = seconds, ["videoWidth"] = frame.Width, ["videoHeight"] = frame.Height,
        };
        return new McpViewSource(frame.Preview.Content, [], metadata);
    }
}
