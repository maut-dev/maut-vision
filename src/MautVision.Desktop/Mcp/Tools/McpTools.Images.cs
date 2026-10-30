using System.Globalization;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images;
using MautVision.Desktop.Controllers.Projects.Images.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    /// <summary>The smallest box side the annotation editor creates (a smaller one is an accidental click), as a fraction of the frame.</summary>
    internal const double MinBoxSize = 0.002;

    private const int MaxImagesPerCall = 400;

    private const string ImageGroupSchema = """
        { "type": "object", "description": "Image group: id (groupId), name, description, origin (manual — imported frames; generated — made by a generation run, read-only while producing), splitHint (unassigned, train, validation, test: where datasets put its frames), revision, archivedAt, imageCount (archived included), archivedImageCount.",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" }, "origin": { "type": "string" },
            "splitHint": { "type": "string" }, "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] },
            "imageCount": { "type": "integer" }, "archivedImageCount": { "type": "integer" } },
          "required": ["id", "name", "origin", "splitHint", "revision", "imageCount"] }
        """;

    private const string ImageSchema = """
        { "type": "object", "description": "Image (frame) of a group: id (imageId), groupId, displayName, format, width and height in pixels with EXIF orientation, revision, annotationRevision (current annotation), objectCount (boxes), reviewState (unreviewed or reviewed), archivedAt, lineage (file, video or generated origin).",
          "properties": { "id": { "type": "string" }, "groupId": { "type": "string" }, "displayName": { "type": "string" }, "width": { "type": "integer" },
            "height": { "type": "integer" }, "revision": { "type": "integer" }, "annotationRevision": { "type": "integer" }, "objectCount": { "type": "integer" },
            "reviewState": { "type": "string" }, "archivedAt": { "type": ["string", "null"] } },
          "required": ["id", "groupId", "width", "height", "annotationRevision", "objectCount", "reviewState"] }
        """;

    private const string BoxFieldsSchema = """
        "x": { "type": "number", "minimum": 0, "maximum": 1, "description": "Left edge as a fraction of the image width (0 — left)." },
        "y": { "type": "number", "minimum": 0, "maximum": 1, "description": "Top edge as a fraction of the image height (0 — top)." },
        "width": { "type": "number", "minimum": 0.002, "maximum": 1, "description": "Width as a fraction of the image width; x + width ≤ 1." },
        "height": { "type": "number", "minimum": 0.002, "maximum": 1, "description": "Height as a fraction of the image height; y + height ≤ 1." }
        """;

    private const string AnnotationSchema = $$"""
        { "type": "object", "description": "An annotation revision of an image: revision (pass it as annotationRevision to set_annotations), reviewState, roi (area box or null) and boxes in fractions of the oriented image from the top-left corner, each with its object id, classId, className, classVariantId (or null) and variantName.",
          "properties": { "imageId": { "type": "string" }, "revision": { "type": "integer" }, "reviewState": { "type": "string" },
            "roi": { "type": ["object", "null"] },
            "boxes": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "classId": { "type": "string" },
              "className": { "type": "string" }, "classVariantId": { "type": ["string", "null"] }, "variantName": { "type": ["string", "null"] }, {{BoxFieldsSchema}} },
              "required": ["id", "classId", "x", "y", "width", "height"] } } },
          "required": ["imageId", "revision", "reviewState", "boxes"] }
        """;

    private const string ImageDeletionSchema = """
        { "type": "object", "properties": { "groupId": { "type": "string" }, "groupDeleted": { "type": "boolean" }, "images": { "type": "integer" },
            "cleanupPending": { "type": "boolean" } }, "required": ["groupId", "groupDeleted", "images", "cleanupPending"] }
        """;

    private ImagesController? imagesController;

    private ImagesController Images => imagesController ??= new ImagesController(Core.Projects, Core.Jobs, importResources, importSelections, NoMcpDialogs, Core.Media);

    private IEnumerable<McpToolDefinition> ImageTools()
    {
        // Groups.
        yield return McpToolDefinition.Create("list_image_groups", "List image groups", McpToolClass.Read, true,
            """
            Image groups of a project: imported (origin manual) and generated ones, with image counts and the split hint datasets follow.
            Archived groups are skipped unless includeArchived=true. groupId is what the image tools take.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "includeArchived": { "type": "boolean", "default": false } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "groups": { "type": "array", "items": {{ImageGroupSchema}} } }, "required": ["groups"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "includeArchived");
                var projectId = await PreparedIdAsync(call);
                var includeArchived = call.Arguments.Bool("includeArchived", false);
                var groups = (await Images.ListGroupsAsync(new ListImageGroupsRequestDto(projectId), call.CancellationToken))
                    .Where(group => includeArchived || group.ArchivedAt is null).ToArray();
                return Result(Wrap("groups", groups), groups.Length == 0
                    ? "No image groups."
                    : $"{groups.Length} groups: " + string.Join("; ", groups.Select(group => $"{group.Name} ({group.Id}, {group.Origin}, {group.ImageCount - group.ArchivedImageCount} images)")) + ".");
            });

        yield return McpToolDefinition.Create("create_image_group", "Create image group", McpToolClass.Write, false,
            """
            Creates a group for imported frames (origin manual). name has 1-120 characters (duplicates allowed), description up to 4000.
            splitHint tells datasets where to put its frames: unassigned (split by percentages), train, validation or test.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "name": { "type": "string" }, "description": { "type": "string", "default": "" },
                "splitHint": { "type": "string", "enum": ["unassigned", "train", "validation", "test"], "default": "unassigned" } },
              "required": ["projectId", "name"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "group": {{ImageGroupSchema}} }, "required": ["group"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "name", "description", "splitHint");
                var projectId = await PreparedIdAsync(call);
                var group = await Images.CreateGroupAsync(new CreateImageGroupRequestDto(projectId, call.Arguments.String("name"),
                    call.Arguments.OptionalString("description") ?? "", SplitHint(call.Arguments, "unassigned")), call.CancellationToken);
                return Result(Wrap("group", group), $"Image group {group.Name} created with id {group.Id}.");
            });

        yield return McpToolDefinition.Create("update_image_group", "Update image group", McpToolClass.Write, true,
            """
            Changes the name, description or split hint of a group and/or archives or restores it (archived). Omitted fields keep their values.
            An archived group is read-only except restoring and deleting; a group with a running import cannot be archived
            (images.group_importing); a generated group is read-only while generation fills it (images.group_busy).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "groupId": { "type": "string" }, "revision": {{RevisionSchema}},
                "name": { "type": "string" }, "description": { "type": "string" },
                "splitHint": { "type": "string", "enum": ["unassigned", "train", "validation", "test"] }, "archived": { "type": "boolean" } },
              "required": ["projectId", "groupId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "group": {{ImageGroupSchema}} }, "required": ["group"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "groupId", "revision", "name", "description", "splitHint", "archived");
                var projectId = await PreparedIdAsync(call);
                var group = await Images.GetGroupAsync(new GetImageGroupRequestDto(projectId, arguments.Guid("groupId")), call.CancellationToken);
                var revision = Revision(arguments, group.Revision);
                var archived = arguments.Has("archived") ? arguments.Bool("archived", false) : (bool?)null;
                var fields = arguments.Has("name") || arguments.Has("description") || arguments.Has("splitHint");
                if (!fields && archived is null) throw McpToolException.InvalidArgument("name", "pass a field to change or archived");
                if (archived == false && group.ArchivedAt is not null)
                {
                    group = await Images.SetGroupArchivedAsync(new SetImageGroupArchivedRequestDto(projectId, group.Id, false, revision), call.CancellationToken);
                    revision = group.Revision;
                }
                if (fields)
                {
                    group = await Images.UpdateGroupAsync(new UpdateImageGroupRequestDto(projectId, group.Id, arguments.OptionalString("name") ?? group.Name,
                        arguments.OptionalString("description") ?? group.Description, SplitHint(arguments, group.SplitHint), revision), call.CancellationToken);
                    revision = group.Revision;
                }
                if (archived == true && group.ArchivedAt is null)
                    group = await Images.SetGroupArchivedAsync(new SetImageGroupArchivedRequestDto(projectId, group.Id, true, revision), call.CancellationToken);
                return Result(Wrap("group", group), $"Image group {group.Name} saved (revision {group.Revision}" + (group.ArchivedAt is null ? ")." : ", archived)."));
            });

        yield return McpToolDefinition.Create("delete_image_group", "Delete image group", McpToolClass.Destructive, false,
            """
            Permanently deletes an image group with its images, annotation and import history, like "Delete" in the window: the content is
            reviewed first and blockers refuse it (images.deletion_blocked with details.blockers: an import or generation into the group, a
            dataset being built from it). Prefer update_image_group(archived=true) when the data may be needed.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "groupId": { "type": "string" } }, "required": ["projectId", "groupId"],
              "additionalProperties": false }
            """,
            ImageDeletionSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "groupId");
                var projectId = await PreparedIdAsync(call);
                var groupId = call.Arguments.Guid("groupId");
                var review = await Images.ReviewGroupDeletionAsync(new ReviewImageGroupDeletionRequestDto(projectId, groupId), call.CancellationToken);
                ThrowIfBlocked("images", review.GroupName, review.Blockers);
                var result = await Images.DeleteGroupAsync(new DeleteImageGroupRequestDto(projectId, groupId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Image group {review.GroupName} deleted with {result.Images} images.");
            });

        // Import.
        yield return McpToolDefinition.Create("import_images", "Import images", McpToolClass.Write, false,
            $$"""
            Imports frames (PNG, JPEG, WebP, BMP) into a manual, active image group as a job. Give one folder (path; with recursive also
            subfolders), files or several folders (paths) by absolute path, or files sent with upload_file (uploadId, uploadIds). Frames get
            an empty unreviewed annotation; skipDuplicates (default true) skips frames whose pixels are already in the group. Returns
            { jobId, status, job } at once (or after waitSeconds); follow it with wait_job, the counts are in job.result. Check a folder first
            with preview_folder.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "groupId": { "type": "string" }, {{FileSourcesSchema}},
                "skipDuplicates": { "type": "boolean", "default": true } },
              "required": ["projectId", "groupId"], "additionalProperties": false }
            """,
            JobStartedSchema,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "groupId", "path", "paths", "uploadId", "uploadIds", "recursive", "operationId", "waitSeconds", "skipDuplicates");
                var projectId = await PreparedIdAsync(call);
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var recursive = arguments.Bool("recursive", false);
                string token;
                string what;
                // One folder is imported as a folder, so the job reads it itself (natural order, resumable); anything else as files.
                if (arguments.OptionalString("path") is { } single && !arguments.Has("paths") && !arguments.Has("uploadId") && !arguments.Has("uploadIds")
                    && dependencies.Paths.Resolve(single) is { IsDirectory: true } folder)
                {
                    token = importSelections.Register([folder.Path], FileSelectionKind.Folder);
                    what = $"Import of folder {folder.Path}";
                }
                else
                {
                    var files = ImportFiles(arguments, expandFolders: true);
                    token = importSelections.Register(files);
                    what = $"Import of {files.Count} images";
                }
                var job = await Images.ImportAsync(new ImportImagesRequestDto(projectId, arguments.Guid("groupId"), token, OperationId(arguments), recursive,
                    arguments.Bool("skipDuplicates", true)), call.CancellationToken);
                return await McpJobWaiter.StartedAsync(Core.Jobs, await Core.Jobs.GetAsync(projectId, job.Id, call.CancellationToken), waitSeconds, call, what);
            });

        yield return McpToolDefinition.Create("import_video_frames", "Import video frames", McpToolClass.Write, false,
            """
            Imports frames of a video (MP4, MKV, AVI, MOV, WebM, M4V) into a manual, active image group as a job: frames at startSeconds +
            i × intervalSeconds for i = 0..count-1, each the first frame not earlier than its mark, saved as PNG at the original size. count
            defaults to every mark before the end of the video (at most 10000). Give the video by absolute path or uploadId. Returns
            { jobId, status, job, video } at once (or after waitSeconds); follow it with wait_job. Look at a moment first with
            view({ videoFrame: path, timeSeconds }).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "groupId": { "type": "string" },
                "path": { "type": "string", "description": "Absolute path of the video file." }, "uploadId": { "type": "string" },
                "startSeconds": { "type": "number", "minimum": 0, "default": 0 },
                "intervalSeconds": { "type": "number", "minimum": 0.001, "default": 1 },
                "count": { "type": "integer", "minimum": 1, "maximum": 10000 },
                "skipDuplicates": { "type": "boolean", "default": true },
                "operationId": { "type": "string" },
                "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0 } },
              "required": ["projectId", "groupId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "jobId": { "type": "string" }, "status": { "type": "string" }, "job": { "type": "object" },
                "video": { "type": "object", "properties": { "width": { "type": "integer" }, "height": { "type": "integer" }, "durationSeconds": { "type": "number" },
                  "frameRate": { "type": "number" }, "frames": { "type": "integer" } } } },
              "required": ["jobId", "status", "job", "video"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "groupId", "path", "uploadId", "startSeconds", "intervalSeconds", "count", "skipDuplicates", "operationId", "waitSeconds");
                var projectId = await PreparedIdAsync(call);
                var waitSeconds = arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                string path;
                if (arguments.Has("path") == arguments.Has("uploadId")) throw McpToolException.InvalidArgument("path", "pass either path or uploadId");
                path = arguments.Has("path") ? dependencies.Paths.ResolveFile(arguments.String("path")) : dependencies.Uploads.Get(arguments.Guid("uploadId")).Path;
                if (!VideoFormats.IsVideo(path)) throw new CoreException("video.unsupported", "The file is not a supported video.");
                var video = await Core.Media.ProbeVideoAsync(path, call.CancellationToken);
                var startMs = (long)Math.Round(arguments.Double("startSeconds", 0, 1_000_000, 0) * 1000);
                var intervalMs = Math.Max(1, (long)Math.Round(arguments.Double("intervalSeconds", 0.001, 1_000_000, 1) * 1000));
                var count = arguments.Has("count")
                    ? arguments.Int("count", 1, 10_000)
                    : (int)Math.Clamp((video.DurationMs - 1 - startMs) / intervalMs + 1, 1, 10_000);
                var token = importSelections.Register([path], FileSelectionKind.Video, video);
                var job = await Images.ImportAsync(new ImportImagesRequestDto(projectId, arguments.Guid("groupId"), token, OperationId(arguments), false,
                    arguments.Bool("skipDuplicates", true), startMs, intervalMs, count), call.CancellationToken);
                var result = await McpJobWaiter.StartedAsync(Core.Jobs, await Core.Jobs.GetAsync(projectId, job.Id, call.CancellationToken), waitSeconds, call,
                    $"Import of {count} video frames");
                result.Structured["video"] = new JsonObject
                {
                    ["width"] = video.Width, ["height"] = video.Height, ["durationSeconds"] = video.DurationMs / 1000.0, ["frameRate"] = video.FrameRate, ["frames"] = count,
                };
                return result;
            });

        // Images.
        yield return McpToolDefinition.Create("list_images", "List images", McpToolClass.Read, true,
            """
            Images of a group in import order, a page at a time (pass nextCursor as cursor with the same filters). archive: active (default),
            archived or all; reviewState: unreviewed or reviewed to filter by the review of the annotation. Look at an image with
            view({ image: id }, overlay: true) and read its boxes with get_annotations.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "groupId": { "type": "string" },
                "archive": { "type": "string", "enum": ["active", "archived", "all"], "default": "active" },
                "reviewState": { "type": "string", "enum": ["unreviewed", "reviewed"] },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 100 }, "cursor": { "type": "string" } },
              "required": ["projectId", "groupId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "items": { "type": "array", "items": {{ImageSchema}} }, "nextCursor": { "type": ["string", "null"] } },
              "required": ["items", "nextCursor"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "groupId", "archive", "reviewState", "limit", "cursor");
                var projectId = await PreparedIdAsync(call);
                var archive = arguments.OptionalString("archive") ?? "active";
                if (archive is not ("active" or "archived" or "all")) throw McpToolException.InvalidArgument("archive", "must be active, archived or all");
                var review = arguments.OptionalString("reviewState");
                if (review is not (null or "unreviewed" or "reviewed")) throw McpToolException.InvalidArgument("reviewState", "must be unreviewed or reviewed");
                var page = await Images.ListAsync(new ListImagesRequestDto(projectId, arguments.Guid("groupId"), archive, review, arguments.Int("limit", 1, 200, 100),
                    arguments.OptionalString("cursor")), call.CancellationToken);
                return Result(WithoutUrls(McpToolResult.Object(page)), $"{page.Items.Length} images ("
                    + $"{page.Items.Count(item => item.ReviewState == "reviewed")} reviewed, {page.Items.Sum(item => item.ObjectCount)} boxes)"
                    + (page.NextCursor is null ? "." : "; more with cursor."));
            });

        yield return McpToolDefinition.Create("get_annotations", "Get annotation", McpToolClass.Read, true,
            """
            The annotation of an image: boxes (fractions 0-1 of the oriented image from the top-left corner) with class and variant names,
            the review state and the area box (roi). revision is the annotation revision to pass to set_annotations; give revision to read an
            earlier one (history is immutable). Several images at once: imageIds (up to 200) instead of imageId, current revisions only.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "imageId": { "type": "string" },
                "imageIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 200 },
                "revision": { "type": "integer", "minimum": 1 } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "annotation": {{AnnotationSchema}}, "annotations": { "type": "array", "items": {{AnnotationSchema}} } } }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "imageId", "imageIds", "revision");
                var projectId = await PreparedIdAsync(call);
                var names = new AnnotationNames(await WorkspaceAsync(projectId, call.CancellationToken));
                if (arguments.Has("imageIds"))
                {
                    if (arguments.Has("imageId") || arguments.Has("revision")) throw McpToolException.InvalidArgument("imageIds", "pass imageIds alone or imageId with revision");
                    var list = await Images.ListAnnotationsAsync(new ListImageAnnotationsRequestDto(projectId, GuidList(arguments, "imageIds", 1, 200)), call.CancellationToken);
                    return Result(new JsonObject { ["annotations"] = new JsonArray(list.Select(item => (JsonNode?)names.Describe(item)).ToArray()) },
                        $"{list.Length} annotations, {list.Sum(item => item.Objects.Length)} boxes.");
                }
                var annotation = await Images.GetAnnotationAsync(new GetImageAnnotationRequestDto(projectId, arguments.Guid("imageId"),
                    arguments.Has("revision") ? Long(arguments, "revision") : null), call.CancellationToken);
                return Result(new JsonObject { ["annotation"] = names.Describe(annotation) }, names.Summary(annotation));
            });

        yield return McpToolDefinition.Create("set_annotations", "Set annotation", McpToolClass.Write, false,
            $$"""
            Replaces the boxes of an image with a new annotation revision, as saving in the annotation editor. Each box is x, y, width, height
            in fractions 0-1 of the image (as view shows it, EXIF orientation applied) from the top-left corner, inside the image, each side at
            least {{MinBoxSize.ToString(CultureInfo.InvariantCulture)}}, with classId (list_classes) and optionally classVariantId (a binding of
            that class). Keep an existing box's id to keep the object; boxes without id are new. New or changed references need an active class
            and variant (images.reference_unavailable). reviewed sets the review (omit to keep it; reviewed with no boxes means a deliberately
            empty frame); roi sets the area box (null clears, omit keeps). annotationRevision (from get_annotations or list_images) protects
            against a concurrent edit (images.revision_conflict); omit it to replace the current one. At most 1000 boxes.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "imageId": { "type": "string" },
                "boxes": { "type": "array", "maxItems": 1000, "items": { "type": "object", "properties": {
                  "id": { "type": "string", "description": "Object id of the current revision to keep; omit for a new box." },
                  "classId": { "type": "string" }, "classVariantId": { "type": ["string", "null"] }, {{BoxFieldsSchema}} },
                  "required": ["classId", "x", "y", "width", "height"], "additionalProperties": false } },
                "reviewed": { "type": "boolean" },
                "roi": { "type": ["object", "null"], "properties": { {{BoxFieldsSchema}} }, "required": ["x", "y", "width", "height"] },
                "annotationRevision": { "type": "integer", "minimum": 1 } },
              "required": ["projectId", "imageId", "boxes"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "image": {{ImageSchema}}, "annotation": {{AnnotationSchema}} }, "required": ["image", "annotation"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "imageId", "boxes", "reviewed", "roi", "annotationRevision");
                var projectId = await PreparedIdAsync(call);
                var imageId = arguments.Guid("imageId");
                var boxes = arguments.Has("boxes") ? arguments.Objects("boxes", 0, 1000) : throw McpToolException.InvalidArgument("boxes", "required");
                var objects = boxes.Select((box, index) =>
                {
                    box.Only("id", "classId", "classVariantId", "x", "y", "width", "height");
                    return new AnnotationObjectRequestDto(box.OptionalGuid("id"), box.Guid("classId"), box.OptionalGuid("classVariantId"), Box(box, $"boxes[{index}]"));
                }).ToArray();
                var current = await Images.GetAnnotationAsync(new GetImageAnnotationRequestDto(projectId, imageId), call.CancellationToken);
                var roi = arguments.Raw.ContainsKey("roi")
                    ? arguments.Object("roi") is { } given ? Box(given, "roi") : null
                    : current.Roi is { } kept ? new ImageBoxRequestDto(kept.X, kept.Y, kept.Width, kept.Height) : null;
                var review = arguments.Has("reviewed") ? (arguments.Bool("reviewed", false) ? "reviewed" : "unreviewed") : current.ReviewState;
                var revision = arguments.Has("annotationRevision") ? Long(arguments, "annotationRevision") : current.Revision;
                var saved = await Images.SaveAnnotationAsync(new SaveImageAnnotationRequestDto(projectId, imageId, revision, objects, roi, review), call.CancellationToken);
                var names = new AnnotationNames(await WorkspaceAsync(projectId, call.CancellationToken));
                var structured = new JsonObject
                {
                    ["image"] = WithoutUrls(McpToolResult.Object(saved.Image)), ["annotation"] = names.Describe(saved.Annotation),
                };
                return Result(structured, "Saved. " + names.Summary(saved.Annotation));
            });

        yield return McpToolDefinition.Create("set_image_reviewed", "Mark images reviewed", McpToolClass.Write, true,
            """
            Marks images as reviewed (reviewed=true, the default) or unreviewed without changing their boxes: each changed image gets a new
            annotation revision with the same boxes. Up to 400 images; images already in that state are left as they are. Archived images
            cannot change (images.image_archived).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "imageIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 400 },
                "reviewed": { "type": "boolean", "default": true } },
              "required": ["projectId", "imageIds"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "changed": { "type": "integer" }, "unchanged": { "type": "integer" }, "reviewState": { "type": "string" } },
              "required": ["changed", "unchanged", "reviewState"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "imageIds", "reviewed");
                var projectId = await PreparedIdAsync(call);
                var ids = GuidList(arguments, "imageIds", 1, MaxImagesPerCall).Distinct().ToArray();
                var state = arguments.Bool("reviewed", true) ? "reviewed" : "unreviewed";
                var annotations = new List<ImageAnnotationResponseDto>();
                foreach (var chunk in ids.Chunk(200))
                {
                    annotations.AddRange(await Images.ListAnnotationsAsync(new ListImageAnnotationsRequestDto(projectId, chunk), call.CancellationToken));
                }
                var missing = ids.Except(annotations.Select(item => item.ImageId)).FirstOrDefault();
                if (missing != Guid.Empty) throw new CoreException("images.image_not_found", $"Image {missing} is not in the project.");
                var changed = 0;
                foreach (var annotation in annotations.Where(item => item.ReviewState != state))
                {
                    await Images.SaveAnnotationAsync(new SaveImageAnnotationRequestDto(projectId, annotation.ImageId, annotation.Revision,
                        annotation.Objects.Select(item => new AnnotationObjectRequestDto(item.Id, item.ClassId, item.ClassVariantId,
                            new ImageBoxRequestDto(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height))).ToArray(),
                        annotation.Roi is { } roi ? new ImageBoxRequestDto(roi.X, roi.Y, roi.Width, roi.Height) : null, state), call.CancellationToken);
                    changed++;
                }
                return Result(new JsonObject { ["changed"] = changed, ["unchanged"] = annotations.Count - changed, ["reviewState"] = state },
                    $"{changed} images marked {state}; {annotations.Count - changed} already were.");
            });

        yield return McpToolDefinition.Create("move_images", "Move images", McpToolClass.Write, false,
            """
            Moves up to 400 active images of one group to another group of the same origin (imported to imported, generated to generated),
            all or nothing; they go to the end of the target. Annotation moves with them.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "imageIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 400 },
                "targetGroupId": { "type": "string" } },
              "required": ["projectId", "imageIds", "targetGroupId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "source": {{ImageGroupSchema}}, "target": {{ImageGroupSchema}}, "moved": { "type": "integer" } },
              "required": ["source", "target", "moved"] }
            """,
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "imageIds", "targetGroupId");
                var project = await PrepareAsync(arguments.String("projectId"), call.CancellationToken);
                var images = await ImagesOfOneGroupAsync(project, GuidList(arguments, "imageIds", 1, MaxImagesPerCall), call.CancellationToken);
                var source = await project.Images.GetGroupAsync(images[0].GroupId, call.CancellationToken);
                var target = await project.Images.GetGroupAsync(arguments.Guid("targetGroupId"), call.CancellationToken);
                var moved = await Images.MoveAsync(new MoveImagesRequestDto(project.Id, source.Id, source.Revision, target.Id, target.Revision,
                    images.Select(image => new ImageRevisionRequestDto(image.Id, image.Revision)).ToArray()), call.CancellationToken);
                var structured = new JsonObject
                {
                    ["source"] = McpToolResult.Node(moved.Source), ["target"] = McpToolResult.Node(moved.Target), ["moved"] = moved.Images.Length,
                };
                return Result(structured, $"{moved.Images.Length} images moved from {moved.Source.Name} to {moved.Target.Name}.");
            });

        yield return McpToolDefinition.Create("delete_images", "Delete images", McpToolClass.Destructive, false,
            """
            Permanently deletes up to 400 images of one group with their annotation history, like "Delete" in the window: the content is
            reviewed first and blockers refuse it (images.deletion_blocked with details.blockers: an import or generation into the group).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "imageIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 400 } },
              "required": ["projectId", "imageIds"], "additionalProperties": false }
            """,
            ImageDeletionSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "imageIds");
                var project = await PrepareAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var images = await ImagesOfOneGroupAsync(project, GuidList(call.Arguments, "imageIds", 1, MaxImagesPerCall), call.CancellationToken);
                var ids = images.Select(image => image.Id).ToArray();
                var review = await Images.ReviewDeletionAsync(new ReviewImagesDeletionRequestDto(project.Id, images[0].GroupId, ids), call.CancellationToken);
                ThrowIfBlocked("images", review.GroupName, review.Blockers);
                var result = await Images.DeleteAsync(new DeleteImagesRequestDto(project.Id, images[0].GroupId, ids, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"{result.Images} images deleted from {review.GroupName}.");
            });
    }

    private IEnumerable<McpPromptDefinition> ImagePrompts()
    {
        yield return new McpPromptDefinition("review_annotations", "Review annotation of a group",
            "Walks through the unreviewed images of an image group: looks at each with its boxes, fixes the boxes and marks it reviewed.",
            [
                new McpPromptArgument("projectId", "Project ID (from list_projects).", true),
                new McpPromptArgument("groupId", "Image group ID (from list_image_groups).", true),
            ],
            async (arguments, cancellationToken) =>
            {
                var values = new McpArguments(arguments.ToDictionary(pair => pair.Key, pair => System.Text.Json.JsonSerializer.SerializeToElement(pair.Value)));
                values.Only("projectId", "groupId");
                var project = await PrepareAsync(values.String("projectId"), cancellationToken);
                var group = await project.Images.GetGroupAsync(values.Guid("groupId"), cancellationToken);
                var workspace = await project.Classes.GetWorkspaceAsync(cancellationToken);
                var classes = string.Join(", ", workspace.Classes.Where(item => item.ArchivedAt is null).Select(item => $"{item.Name} ({item.Id})"));
                return $$"""
                    Review the annotation of the image group "{{group.Name}}" (groupId {{group.Id}}) in the Maut Vision project "{{project.Name}}"
                    (projectId {{project.Id}}). Active classes: {{(classes.Length == 0 ? "none yet — ask the user before creating classes" : classes)}}.

                    1. Call list_images with projectId, groupId and reviewState "unreviewed" (follow nextCursor for more pages).
                    2. For each image call view with target { image: <imageId> } and overlay true; zoom with region when objects are small. Read the
                       boxes with get_annotations.
                    3. Decide the correct boxes: every visible object of a class gets one tight box; remove boxes of wrong classes or without an
                       object. Boxes are fractions 0-1 of the image from its top-left corner (x, y, width, height).
                    4. Save with set_annotations (projectId, imageId, boxes, reviewed: true, annotationRevision from get_annotations). Keep the id of
                       a box you do not change. An image with no objects is saved with boxes [] and reviewed true.
                    5. When unsure about an image, leave it unreviewed and list it for the user instead of guessing.
                    6. After the group, report how many images you reviewed, changed and left for the user. Do not delete images or classes.
                    """;
            });
    }

    /// <summary>Images by ID that all belong to one group (the move and deletion of the window work on one group).</summary>
    private static async Task<IReadOnlyList<MautVision.Core.Interfaces.Projects.Images.ProjectImage>> ImagesOfOneGroupAsync(MautVision.Core.Interfaces.Projects.IProject project,
        IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var images = new List<MautVision.Core.Interfaces.Projects.Images.ProjectImage>();
        foreach (var id in ids.Distinct())
        {
            images.Add(await project.Images.GetImageAsync(id, cancellationToken));
        }
        if (images.Select(image => image.GroupId).Distinct().Count() > 1)
            throw new CoreException("images.selection_invalid", "The images belong to different groups.");
        return images;
    }

    private static string SplitHint(McpArguments arguments, string fallback)
    {
        var value = arguments.OptionalString("splitHint") ?? fallback;
        return value is "unassigned" or "train" or "validation" or "test" ? value : throw McpToolException.InvalidArgument("splitHint", "must be unassigned, train, validation or test");
    }

    /// <summary>A box checked like the annotation editor makes it: inside the image and each side at least <see cref="MinBoxSize"/>.</summary>
    private static ImageBoxRequestDto Box(McpArguments box, string argument)
    {
        var x = box.Double("x", 0, 1);
        var y = box.Double("y", 0, 1);
        var width = box.Double("width", 0, 1);
        var height = box.Double("height", 0, 1);
        if (width < MinBoxSize || height < MinBoxSize || x + width > 1 + 1e-9 || y + height > 1 + 1e-9)
        {
            throw new CoreException("images.box_invalid", $"{argument} goes beyond the image or is smaller than {MinBoxSize}.")
            {
                Details = new Dictionary<string, string> { ["argument"] = argument },
            };
        }
        return new ImageBoxRequestDto(x, y, width, height);
    }

    /// <summary>Class and variant names of annotation boxes for the AI.</summary>
    private sealed class AnnotationNames(CatalogWorkspaceResponseDto workspace)
    {
        private readonly Dictionary<Guid, string> classes = workspace.Classes.ToDictionary(item => item.Id, item => item.Name);
        private readonly Dictionary<Guid, string> bindings = workspace.ClassVariants.ToDictionary(item => item.Id,
            item => workspace.Variants.FirstOrDefault(variant => variant.Id == item.VariantId)?.Name ?? "");

        internal JsonObject Describe(ImageAnnotationResponseDto annotation) => new()
        {
            ["imageId"] = annotation.ImageId.ToString("D"),
            ["revision"] = annotation.Revision,
            ["reviewState"] = annotation.ReviewState,
            ["roi"] = annotation.Roi is { } roi ? new JsonObject { ["x"] = roi.X, ["y"] = roi.Y, ["width"] = roi.Width, ["height"] = roi.Height } : null,
            ["boxes"] = new JsonArray(annotation.Objects.Select(item => (JsonNode?)new JsonObject
            {
                ["id"] = item.Id.ToString("D"),
                ["classId"] = item.ClassId.ToString("D"),
                ["className"] = classes.GetValueOrDefault(item.ClassId, ""),
                ["classVariantId"] = item.ClassVariantId?.ToString("D"),
                ["variantName"] = item.ClassVariantId is { } binding ? bindings.GetValueOrDefault(binding) : null,
                ["x"] = item.Box.X, ["y"] = item.Box.Y, ["width"] = item.Box.Width, ["height"] = item.Box.Height,
            }).ToArray()),
        };

        internal string Summary(ImageAnnotationResponseDto annotation)
            => $"Annotation revision {annotation.Revision}, {annotation.ReviewState}, {annotation.Objects.Length} boxes"
                + (annotation.Objects.Length == 0
                    ? "."
                    : ": " + string.Join("; ", annotation.Objects.Take(30).Select(item => string.Create(CultureInfo.InvariantCulture,
                        $"{classes.GetValueOrDefault(item.ClassId, "?")} x={item.Box.X:0.###} y={item.Box.Y:0.###} w={item.Box.Width:0.###} h={item.Box.Height:0.###}")))
                    + (annotation.Objects.Length > 30 ? "; …" : "."));
    }
}
