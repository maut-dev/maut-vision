using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Backgrounds;
using MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string BackgroundSetSchema = """
        { "type": "object", "description": "Background set: id (setId), name, description, targetSize {width, height} — the size of every background (the project input when the set was created), itemCount active and removedCount removed backgrounds, revision (name, description, archive), contentRevision, archivedAt, matchesProjectInput.",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" },
            "targetSize": { "type": "object", "properties": { "width": { "type": "integer" }, "height": { "type": "integer" } } },
            "revision": { "type": "integer" }, "contentRevision": { "type": "integer" }, "itemCount": { "type": "integer" }, "removedCount": { "type": "integer" },
            "archivedAt": { "type": ["string", "null"] }, "matchesProjectInput": { "type": "boolean" } },
          "required": ["id", "name", "targetSize", "revision", "itemCount"] }
        """;

    private const string StartedJobSchema = """
        "jobId": { "type": "string", "description": "Follow it with wait_job or get_job (projectId, jobId)." },
        "status": { "type": "string", "description": "queued|running|succeeded|failed|cancelled|interrupted|blocked" },
        "job": { "type": "object", "description": "Job snapshot: progress, processed/total, error {code, message}, result of the job kind." }
        """;

    private const string WaitSecondsSchema = """
        "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0, "description": "Wait up to this many seconds for the job to finish before answering (0 — answer at once). Progress goes to notifications/progress when a progressToken is passed." }
        """;

    private const string OperationIdSchema = """
        "operationId": { "type": "string", "description": "Optional key of this action: repeating a call with the same operationId returns the same job." }
        """;

    private const string BlurSchema = """
        "blur": { "type": "object", "description": "Blur of the backgrounds, as \"Blur\" in the window: every background gets a deterministic radius between minRadius and maxRadius (pixels, 0-1000); below 0.3 px it stays sharp. seed 0-4294967295; omitted — derived from the operation.",
          "properties": { "minRadius": { "type": "number", "minimum": 0, "maximum": 1000 }, "maxRadius": { "type": "number", "minimum": 0, "maximum": 1000 },
            "seed": { "type": "integer", "minimum": 0, "maximum": 4294967295 } },
          "required": ["minRadius", "maxRadius"], "additionalProperties": false }
        """;

    private BackgroundsController? backgroundsController;

    private BackgroundsController Backgrounds => backgroundsController ??= new BackgroundsController(Core.Projects, Core.Jobs, importResources, importSelections,
        NoMcpDialogs, Core.Media);

    private IEnumerable<McpToolDefinition> BackgroundTools()
    {
        yield return McpToolDefinition.Create("list_background_sets", "List background sets", McpToolClass.Read, true,
            """
            Background sets of a project with their ids, size and counts. Generation places class images onto the active backgrounds of the sets
            a recipe names (create_recipe backgroundSetIds); a set fits only recipes of its targetSize. Archived sets are included (archivedAt).
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "required": ["projectId"], "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "sets": { "type": "array", "items": {{BackgroundSetSchema}} } }, "required": ["sets"] }""",
            async call =>
            {
                call.Arguments.Only("projectId");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var sets = await Backgrounds.ListSetsAsync(new ListBackgroundSetsRequestDto(projectId), call.CancellationToken);
                return Result(Wrap("sets", sets), sets.Length == 0
                    ? "No background sets."
                    : $"{sets.Length} background sets: " + string.Join("; ", sets.Take(20).Select(set =>
                        $"{set.Name} ({set.Id:D}, {set.TargetSize.Width}×{set.TargetSize.Height}, {set.ItemCount} backgrounds{(set.ArchivedAt is null ? "" : ", archived")})")) + ".");
            });

        yield return McpToolDefinition.Create("create_background_set", "Create background set", McpToolClass.Write, false,
            """
            Creates an empty background set. Its size (targetSize) is the current model input of the project and never changes; every
            background of the set has exactly this size. Fill it with import_backgrounds or import_video_backgrounds.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "name": { "type": "string", "description": "1-120 characters; duplicates are allowed." },
                "description": { "type": "string", "default": "", "description": "Up to 4000 characters." } },
              "required": ["projectId", "name"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "set": {{BackgroundSetSchema}} }, "required": ["set"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "name", "description");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var set = await Backgrounds.CreateSetAsync(new CreateBackgroundSetRequestDto(projectId, call.Arguments.String("name"),
                    call.Arguments.OptionalString("description") ?? ""), call.CancellationToken);
                return Result(Wrap("set", set), $"Background set {set.Name} created with id {set.Id:D}, size {set.TargetSize.Width}×{set.TargetSize.Height}.");
            });

        yield return McpToolDefinition.Create("update_background_set", "Update background set", McpToolClass.Write, true,
            """
            Renames a background set, changes its description, archives it (archived=true) or restores it (archived=false); omitted fields keep
            their values. An archived set is read-only and cannot receive backgrounds; a set with a running import cannot be archived. Pass
            revision from list_background_sets to detect concurrent changes (backgrounds.revision_conflict); without it the current one is used.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "setId": { "type": "string", "description": "Set ID from list_background_sets." },
                "revision": { "type": "integer" }, "name": { "type": "string" }, "description": { "type": "string" }, "archived": { "type": "boolean" } },
              "required": ["projectId", "setId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "set": {{BackgroundSetSchema}} }, "required": ["set"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "setId", "revision", "name", "description", "archived");
                var projectId = arguments.String("projectId");
                var setId = arguments.Guid("setId");
                await PrepareAsync(projectId, call.CancellationToken);
                var set = await Backgrounds.GetSetAsync(new GetBackgroundSetRequestDto(projectId, setId), call.CancellationToken);
                var revision = Revision(arguments, set.Revision);
                var name = arguments.OptionalString("name");
                var description = arguments.OptionalString("description");
                bool? archived = arguments.Has("archived") ? arguments.Bool("archived", false) : null;
                // A restore comes before a change of the text and an archive after it: an archived set is read-only.
                if (archived == false && set.ArchivedAt is not null)
                {
                    set = await Backgrounds.SetSetArchivedAsync(new SetBackgroundSetArchivedRequestDto(projectId, setId, false, revision), call.CancellationToken);
                    revision = set.Revision;
                }
                if ((name is not null && name != set.Name) || (description is not null && description != set.Description))
                {
                    set = await Backgrounds.UpdateSetAsync(new UpdateBackgroundSetRequestDto(projectId, setId, name ?? set.Name,
                        description ?? set.Description, revision), call.CancellationToken);
                    revision = set.Revision;
                }
                else if (revision != set.Revision)
                {
                    throw new CoreException("backgrounds.revision_conflict", "The set changed since the given revision.");
                }
                if (archived == true && set.ArchivedAt is null)
                    set = await Backgrounds.SetSetArchivedAsync(new SetBackgroundSetArchivedRequestDto(projectId, setId, true, revision), call.CancellationToken);
                return Result(Wrap("set", set), $"Background set {set.Name} saved (revision {set.Revision}{(set.ArchivedAt is null ? "" : ", archived")}).");
            });

        yield return McpToolDefinition.Create("delete_background_set", "Delete background set", McpToolClass.Destructive, false,
            """
            Permanently deletes a background set with all its backgrounds and import history. Like the deletion dialog of the window it is
            refused while something depends on the set: a running import or generation recipes that use it (backgrounds.deletion_blocked,
            details.blockers lists them). Archive the set with update_background_set when it may be needed again.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "setId": { "type": "string" } },
              "required": ["projectId", "setId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "setId": { "type": "string" }, "setDeleted": { "type": "boolean" }, "items": { "type": "integer" },
                "cleanupPending": { "type": "boolean" } }, "required": ["setId", "setDeleted", "items", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "setId");
                var projectId = call.Arguments.String("projectId");
                var setId = call.Arguments.Guid("setId");
                await PrepareAsync(projectId, call.CancellationToken);
                var review = await Backgrounds.ReviewSetDeletionAsync(new ReviewBackgroundSetDeletionRequestDto(projectId, setId), call.CancellationToken);
                ThrowIfBlocked("backgrounds", review.SetName, review.Blockers);
                var result = await Backgrounds.DeleteSetAsync(new DeleteBackgroundSetRequestDto(projectId, setId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Background set {review.SetName} deleted with {result.Items} backgrounds.");
            });

        yield return McpToolDefinition.Create("import_backgrounds", "Import backgrounds from images", McpToolClass.Write, false,
            """
            Adds backgrounds to a set from image files (png, jpg, jpeg, webp, bmp) as a job, like "Add backgrounds" in the window. Sources are
            path or paths (files or folders; folders give their images in natural name order, subfolders with recursive=true), then uploadId or
            uploadIds from upload_file; sourceIndex is the position of a file in this combined list. A background always has exactly the set size, nothing is
            scaled: mode "whole" (default) takes files of exactly that size and skips others (backgrounds.wrong_size in the job result);
            mode "crop" cuts a frame of the set size from larger files — at the top-left corner given in crops (pixels of the oriented
            file) or centered when a file has no crop; at most 500 files. skipSimilar skips frames that hardly differ from backgrounds already
            in the set; exact duplicates are always skipped. Returns { jobId, status, job } at once; the job result lists skipped files and
            reasons. New backgrounds appear in list_backgrounds; their ids are view targets { background: id }.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "setId": { "type": "string", "description": "Set ID from list_background_sets." },
                {{FileSourcesSchema}},
                "mode": { "type": "string", "enum": ["whole", "crop"], "default": "whole" },
                "crops": { "type": "array", "description": "mode crop: frame positions per source; files without one are cut in the center.",
                  "items": { "type": "object", "properties": { "sourceIndex": { "type": "integer", "minimum": 0 }, "left": { "type": "integer", "minimum": 0 },
                    "top": { "type": "integer", "minimum": 0 } }, "required": ["sourceIndex", "left", "top"], "additionalProperties": false } },
                "count": { "type": "integer", "minimum": 1, "description": "Stop after this many backgrounds are added." },
                "skipSimilar": { "type": "boolean", "default": false, "description": "\"Skip repeating frames\"." },
                {{BlurSchema}} },
              "required": ["projectId", "setId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}}, "sources": { "type": "integer" }, "cropped": { "type": "integer" } },
              "required": ["jobId", "status", "job"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "setId", "path", "paths", "uploadId", "uploadIds", "recursive", "operationId", "mode", "crops", "count", "skipSimilar", "blur", "waitSeconds");
                var projectId = arguments.String("projectId");
                var setId = arguments.Guid("setId");
                var mode = arguments.OptionalString("mode") ?? "whole";
                if (mode is not ("whole" or "crop")) throw McpToolException.InvalidArgument("mode", "must be whole or crop");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var files = ImportFiles(arguments, expandFolders: true);
                var explicitCrops = new Dictionary<int, BackgroundCropRequestDto>();
                if (arguments.Has("crops"))
                {
                    if (mode != "crop") throw McpToolException.InvalidArgument("crops", "is used only with mode crop");
                    var items = arguments.Objects("crops", 0, BackgroundsControllerMaxCropSources);
                    for (var index = 0; index < items.Count; index++)
                    {
                        var item = items[index];
                        item.Only("sourceIndex", "left", "top");
                        var crop = new BackgroundCropRequestDto(item.Int("sourceIndex", 0, files.Count - 1), item.Int("left", 0, 1_000_000), item.Int("top", 0, 1_000_000));
                        if (!explicitCrops.TryAdd(crop.SourceIndex, crop)) throw McpToolException.InvalidArgument($"crops[{index}]", "repeats a sourceIndex");
                    }
                }
                BackgroundCropRequestDto[]? crops = null;
                if (mode == "crop")
                {
                    if (files.Count > BackgroundsControllerMaxCropSources)
                        throw new CoreException("backgrounds.too_many_sources", $"A crop run takes at most {BackgroundsControllerMaxCropSources} files.");
                    var set = await project.Backgrounds.GetSetAsync(setId, call.CancellationToken);
                    var inspected = await project.Backgrounds.InspectFilesAsync(setId, files, call.CancellationToken);
                    crops = inspected.Select((file, index) => explicitCrops.TryGetValue(index, out var crop)
                            ? crop
                            : file is { Fit: BackgroundSourceFit.Larger, Width: { } width, Height: { } height }
                                ? new BackgroundCropRequestDto(index, Math.Max(0, (width - set.TargetSize.Width) / 2), Math.Max(0, (height - set.TargetSize.Height) / 2))
                                : null)
                        .OfType<BackgroundCropRequestDto>().ToArray();
                }
                var count = arguments.Has("count") ? arguments.Int("count", 1, int.MaxValue) : (int?)null;
                var token = importSelections.Register(files);
                var request = new ImportBackgroundsRequestDto(projectId, setId, token, OperationId(arguments), Crops: crops, Count: count,
                    SkipSimilar: arguments.Bool("skipSimilar", false), Blur: BlurArgument(arguments));
                var job = await Backgrounds.ImportAsync(request, call.CancellationToken);
                var result = await StartedJobAsync(projectId, job.Id, arguments, call, "Background import");
                result.Structured["sources"] = files.Count;
                result.Structured["cropped"] = crops?.Length ?? 0;
                return result;
            });

        yield return McpToolDefinition.Create("import_video_backgrounds", "Import backgrounds from video", McpToolClass.Write, false,
            """
            Cuts backgrounds from a video (mp4, mkv, avi, mov, webm, m4v) as a job, like "Cut from video" in the window: frames from startSeconds
            every stepSeconds, each cropped to the set size at one region (top-left corner in pixels of the video frame; centered when omitted),
            until count backgrounds are added (default — every available frame). The video must be at least the set size
            (backgrounds.video_too_small). Use view { videoFrame: path, timeSeconds } to look at the video first. skipSimilar skips frames that
            hardly differ from earlier ones. Returns { jobId, status, job } at once.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "setId": { "type": "string", "description": "Set ID from list_background_sets." },
                "videoPath": { "type": "string", "description": "Absolute path of the video." },
                "uploadId": { "type": "string", "description": "A video sent with upload_file instead of videoPath." },
                "stepSeconds": { "type": "number", "minimum": 0.001, "default": 1, "description": "Seconds between taken frames." },
                "startSeconds": { "type": "number", "minimum": 0, "default": 0 },
                "count": { "type": "integer", "minimum": 1, "description": "How many backgrounds to cut, at most the number of available frames." },
                "region": { "type": "object", "description": "Top-left corner of the crop in video pixels; the crop has the set size.",
                  "properties": { "left": { "type": "integer", "minimum": 0 }, "top": { "type": "integer", "minimum": 0 } }, "required": ["left", "top"], "additionalProperties": false },
                "skipSimilar": { "type": "boolean", "default": false },
                {{OperationIdSchema}},
                {{BlurSchema}},
                {{WaitSecondsSchema}} },
              "required": ["projectId", "setId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { {{StartedJobSchema}},
                "video": { "type": "object", "properties": { "width": { "type": "integer" }, "height": { "type": "integer" }, "durationMs": { "type": "integer" } } },
                "availableFrames": { "type": "integer" }, "region": { "type": "object" } },
              "required": ["jobId", "status", "job"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "setId", "videoPath", "uploadId", "operationId", "stepSeconds", "startSeconds", "count", "region", "skipSimilar", "blur", "waitSeconds");
                var projectId = arguments.String("projectId");
                var setId = arguments.Guid("setId");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var path = ImportSourceFile(arguments, "videoPath", "uploadId");
                if (!VideoFormats.IsVideo(path)) throw new CoreException("video.unsupported", "The file is not a supported video.");
                var set = await project.Backgrounds.GetSetAsync(setId, call.CancellationToken);
                var video = await Core.Media.ProbeVideoAsync(path, call.CancellationToken);
                var intervalMs = Math.Max(1L, (long)Math.Round(arguments.Double("stepSeconds", 0.001, 1_000_000, 1) * 1000));
                var startMs = (long)Math.Round(arguments.Double("startSeconds", 0, 1_000_000, 0) * 1000);
                var available = BackgroundVideoSource.AvailableFrames(video.DurationMs, startMs, intervalMs);
                var count = arguments.Has("count") ? arguments.Int("count", 1, int.MaxValue) : Math.Max(1, available);
                BackgroundRegionRequestDto region;
                if (arguments.Object("region") is { } given)
                {
                    given.Only("left", "top");
                    region = new BackgroundRegionRequestDto(given.Int("left", 0, 1_000_000), given.Int("top", 0, 1_000_000));
                }
                else
                {
                    region = new BackgroundRegionRequestDto(Math.Max(0, (video.Width - set.TargetSize.Width) / 2), Math.Max(0, (video.Height - set.TargetSize.Height) / 2));
                }
                var token = importSelections.Register([path], FileSelectionKind.Video, video);
                var request = new ImportBackgroundsRequestDto(projectId, setId, token, OperationId(arguments), Region: region, StartMs: startMs, IntervalMs: intervalMs,
                    Count: count, SkipSimilar: arguments.Bool("skipSimilar", false), Blur: BlurArgument(arguments));
                var job = await Backgrounds.ImportAsync(request, call.CancellationToken);
                var result = await StartedJobAsync(projectId, job.Id, arguments, call, "Video background import");
                result.Structured["video"] = new JsonObject { ["width"] = video.Width, ["height"] = video.Height, ["durationMs"] = video.DurationMs };
                result.Structured["availableFrames"] = available;
                result.Structured["region"] = new JsonObject { ["left"] = region.Left, ["top"] = region.Top };
                return result;
            });

        yield return McpToolDefinition.Create("list_backgrounds", "List backgrounds", McpToolClass.Read, true,
            """
            Backgrounds of a set in the order of adding, a page at a time: id, size, origin (uploaded, image-crop with the crop, video-frame with
            the time) and blur radius. Each id is a view target: view { projectId, target: { background: id } } shows the picture. removed
            filters reversibly removed backgrounds (active by default). Pass nextCursor as cursor for the next page.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "setId": { "type": "string" },
                "removed": { "type": "string", "enum": ["active", "removed", "all"], "default": "active" },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
                "cursor": { "type": "string" } },
              "required": ["projectId", "setId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "backgrounds": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "setId": { "type": "string" },
                  "width": { "type": "integer" }, "height": { "type": "integer" }, "origin": { "type": "object" }, "removed": { "type": "boolean" } } } },
                "nextCursor": { "type": ["string", "null"] } },
              "required": ["backgrounds", "nextCursor"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "setId", "removed", "limit", "cursor");
                var projectId = arguments.String("projectId");
                var setId = arguments.Guid("setId");
                var removed = arguments.OptionalString("removed") switch
                {
                    null or "active" => (bool?)false,
                    "removed" => true,
                    "all" => null,
                    _ => throw McpToolException.InvalidArgument("removed", "must be active, removed or all"),
                };
                var project = await PrepareAsync(projectId, call.CancellationToken);
                // Listed from the manager: the page DTO carries resource URLs of the window, which mean nothing to a client.
                var page = await project.Backgrounds.ListItemsAsync(setId, new BackgroundFilter(removed), arguments.Int("limit", 1, 200, 50),
                    arguments.OptionalString("cursor"), call.CancellationToken);
                var items = new JsonArray(page.Items.Select(item => (JsonNode?)new JsonObject
                {
                    ["id"] = item.Id.ToString("D"),
                    ["setId"] = item.SetId.ToString("D"),
                    ["width"] = item.Width,
                    ["height"] = item.Height,
                    ["bytes"] = item.ByteSize,
                    ["revision"] = item.Revision,
                    ["createdAt"] = item.CreatedAt,
                    ["removed"] = item.RemovedAt is not null,
                    ["origin"] = new JsonObject
                    {
                        ["kind"] = item.Origin.Kind switch { BackgroundOriginKind.Uploaded => "uploaded", BackgroundOriginKind.ImageCrop => "image-crop", _ => "video-frame" },
                        ["sourceName"] = item.Origin.SourceName,
                        ["timestampMs"] = item.Origin.TimestampMs,
                        ["crop"] = item.Origin.Crop is { } crop
                            ? new JsonObject { ["left"] = crop.Left, ["top"] = crop.Top, ["width"] = crop.Width, ["height"] = crop.Height }
                            : null,
                        ["blurRadius"] = item.Origin.BlurRadius,
                    },
                }).ToArray());
                var structured = new JsonObject { ["backgrounds"] = items, ["nextCursor"] = page.NextCursor };
                return Result(structured, $"{page.Items.Count} backgrounds" + (page.NextCursor is null ? "." : "; more with cursor.")
                    + (page.Items.Count > 0 ? $" View one with target {{ background: \"{page.Items[0].Id:D}\" }}." : ""));
            });

        yield return McpToolDefinition.Create("delete_backgrounds", "Delete backgrounds", McpToolClass.Destructive, false,
            """
            Permanently deletes up to 400 backgrounds of one set (ids from list_backgrounds). Refused like in the window while something
            depends on them — a running import into the set or generation recipes that would lose all active backgrounds of a set
            (backgrounds.deletion_blocked, details.blockers).
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "setId": { "type": "string" },
                "backgroundIds": { "type": "array", "minItems": 1, "maxItems": 400, "items": { "type": "string" } } },
              "required": ["projectId", "setId", "backgroundIds"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "setId": { "type": "string" }, "setDeleted": { "type": "boolean" }, "items": { "type": "integer" },
                "cleanupPending": { "type": "boolean" } }, "required": ["setId", "items", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "setId", "backgroundIds");
                var projectId = call.Arguments.String("projectId");
                var setId = call.Arguments.Guid("setId");
                var ids = GuidList(call.Arguments, "backgroundIds", 1, 400);
                await PrepareAsync(projectId, call.CancellationToken);
                var review = await Backgrounds.ReviewDeletionAsync(new ReviewBackgroundsDeletionRequestDto(projectId, setId, ids), call.CancellationToken);
                ThrowIfBlocked("backgrounds", review.SetName, review.Blockers);
                var result = await Backgrounds.DeleteAsync(new DeleteBackgroundsRequestDto(projectId, setId, ids, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"{result.Items} backgrounds deleted from {review.SetName}.");
            });
    }

    private const int BackgroundsControllerMaxCropSources = BackgroundsController.MaxCropSources;

    private static BackgroundBlurRequestDto? BlurArgument(McpArguments arguments)
    {
        if (arguments.Object("blur") is not { } blur) return null;
        blur.Only("minRadius", "maxRadius", "seed");
        return new BackgroundBlurRequestDto(blur.Double("minRadius", 0, 1000), blur.Double("maxRadius", 0, 1000),
            blur.Has("seed") ? (long)blur.Double("seed", 0, uint.MaxValue) : null);
    }

    /// <summary>The answer of a tool that started a job: <c>{ jobId, status, job }</c>, after waitSeconds when given.</summary>
    private async Task<McpToolResult> StartedJobAsync(string projectId, Guid jobId, McpArguments arguments, McpToolCall call, string what)
    {
        var job = await Core.Jobs.GetAsync(projectId, jobId, call.CancellationToken);
        return await McpJobWaiter.StartedAsync(Core.Jobs, job, arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0), call, what);
    }

    /// <summary>One file: a path or an upload, exactly one of them.</summary>
    private string ImportSourceFile(McpArguments arguments, string pathName, string uploadName)
    {
        if (arguments.Has(pathName) == arguments.Has(uploadName)) throw McpToolException.InvalidArgument(pathName, $"give either {pathName} or {uploadName}");
        return arguments.Has(pathName)
            ? dependencies.Paths.ResolveFile(arguments.String(pathName), pathName)
            : dependencies.Uploads.Get(arguments.Guid(uploadName)).Path;
    }
}
