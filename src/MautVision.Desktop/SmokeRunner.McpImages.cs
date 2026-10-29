using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    private static async Task CheckMcpImagesAsync(McpClient client, ICore core, string root, string projectId, McpCatalogIds catalog, JsonArray checks,
        CancellationToken token)
    {
        // Groups.
        var group = Ok(await CallAsync(client, "create_image_group", new { projectId, name = "Улица", splitHint = "train" }, token), "create_image_group").GetProperty("group");
        var groupId = group.GetProperty("id").GetString()!;
        var other = Ok(await CallAsync(client, "create_image_group", new { projectId, name = "Отбор" }, token), "create_image_group").GetProperty("group");
        var otherId = other.GetProperty("id").GetString()!;
        Error(await CallAsync(client, "create_image_group", new { projectId, name = "Плохая", splitHint = "everything" }, token), "mcp.invalid_arguments", null);
        var updated = Ok(await CallAsync(client, "update_image_group", new { projectId, groupId = otherId, name = "Отбор ИИ", splitHint = "test" }, token),
            "update_image_group").GetProperty("group");
        if (updated.GetProperty("splitHint").GetString() != "test" || updated.GetProperty("name").GetString() != "Отбор ИИ")
            throw new InvalidOperationException("update_image_group: " + updated);
        Error(await CallAsync(client, "update_image_group", new { projectId, groupId = otherId, name = "Устарело", revision = other.GetProperty("revision").GetInt64() }, token),
            "images.revision_conflict", null);
        Error(await CallAsync(client, "update_image_group", new { projectId, groupId = Guid.NewGuid(), name = "Нет" }, token), "images.group_not_found", null);
        var groups = Ok(await CallAsync(client, "list_image_groups", new { projectId }, token), "list_image_groups").GetProperty("groups");
        if (groups.GetArrayLength() != 2) throw new InvalidOperationException("list_image_groups: " + groups);
        checks.Add("create_image_group, update_image_group and list_image_groups; a bad split hint, a stale revision and an unknown group are refused");

        // Import: a folder by path, a file by uploadId and video frames of a clip made with the shipped ffmpeg.
        var folder = Path.Combine(root, "mcp-images");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "кадр-2.png"), McpPicture(320, 240, SKColors.SteelBlue));
        File.WriteAllBytes(Path.Combine(folder, "кадр-10.png"), McpPicture(320, 240, SKColors.SeaGreen));
        File.WriteAllBytes(Path.Combine(folder, "кадр-3.png"), McpPicture(320, 240, SKColors.SteelBlue));
        var started = Ok(await CallAsync(client, "import_images", new { projectId, groupId, path = folder }, token), "import_images");
        var job = await WaitMcpJobAsync(client, projectId, started.GetProperty("jobId").GetString()!, "import_images", token);
        var result = job.GetProperty("result");
        if (result.GetProperty("added").GetInt32() != 2 || result.GetProperty("duplicates").GetInt32() != 1) throw new InvalidOperationException("import_images folder: " + job);
        var upload = Ok(await CallAsync(client, "upload_file", new { name = "кадр-u.png", base64 = Convert.ToBase64String(McpPicture(320, 240, SKColors.Plum)) }, token),
            "upload_file").GetProperty("uploadId").GetString()!;
        var uploaded = Ok(await CallAsync(client, "import_images", new { projectId, groupId, uploadId = upload, waitSeconds = 60 }, token), "import_images");
        if (uploaded.GetProperty("status").GetString() != "succeeded") throw new InvalidOperationException("import_images uploadId: " + uploaded);
        Error(await CallAsync(client, "import_images", new { projectId, groupId, path = "relative\\path" }, token), "mcp.path_invalid", null);
        var video = Path.Combine(root, "mcp-video", "клип.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=10:duration=3",
            "-c:v", "libopenh264", "-g", "10", video })
            start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("MCP smoke video was not created: " + await errors);
        }
        var frames = Ok(await CallAsync(client, "import_video_frames", new { projectId, groupId = otherId, path = video, intervalSeconds = 1 }, token), "import_video_frames");
        if (frames.GetProperty("video").GetProperty("frames").GetInt32() != 3) throw new InvalidOperationException("import_video_frames: " + frames);
        var framesJob = await WaitMcpJobAsync(client, projectId, frames.GetProperty("jobId").GetString()!, "import_video_frames", token);
        if (framesJob.GetProperty("result").GetProperty("added").GetInt32() != 3) throw new InvalidOperationException("import_video_frames job: " + framesJob);
        Error(await CallAsync(client, "import_video_frames", new { projectId, groupId = otherId, path = video, startSeconds = 10, count = 2 }, token), "video.range_invalid", null);
        Error(await CallAsync(client, "import_video_frames", new { projectId, groupId = otherId, path = Path.Combine(folder, "кадр-2.png") }, token), "video.unsupported", null);
        checks.Add("import_images by folder path (duplicates skipped) and by uploadId, import_video_frames of an ffmpeg clip (3 frames at 1 s) run as jobs followed with wait_job and progress; a relative path, a range past the end and a non-video are refused");

        // Images and annotation.
        var page = Ok(await CallAsync(client, "list_images", new { projectId, groupId, reviewState = "unreviewed", limit = 2 }, token), "list_images");
        var items = page.GetProperty("items");
        if (items.GetArrayLength() != 2 || page.GetProperty("nextCursor").ValueKind != JsonValueKind.String || items[0].TryGetProperty("thumbnailUrl", out _)
            || items[0].GetProperty("displayName").GetString() != "кадр-2.png")
            throw new InvalidOperationException("list_images: " + page);
        var imageId = items[0].GetProperty("id").GetString()!;
        var secondId = items[1].GetProperty("id").GetString()!;
        var empty = Ok(await CallAsync(client, "get_annotations", new { projectId, imageId }, token), "get_annotations").GetProperty("annotation");
        if (empty.GetProperty("boxes").GetArrayLength() != 0 || empty.GetProperty("reviewState").GetString() != "unreviewed") throw new InvalidOperationException("get_annotations: " + empty);
        var revision = empty.GetProperty("revision").GetInt64();
        var box = new { classId = catalog.CupId, classVariantId = catalog.CupDayId, x = 0.25, y = 0.25, width = 0.5, height = 0.5 };
        var saved = Ok(await CallAsync(client, "set_annotations", new { projectId, imageId, boxes = new[] { box }, reviewed = true, annotationRevision = revision }, token),
            "set_annotations");
        var annotation = saved.GetProperty("annotation");
        var savedBox = annotation.GetProperty("boxes")[0];
        if (annotation.GetProperty("reviewState").GetString() != "reviewed" || savedBox.GetProperty("className").GetString() != "Кружка"
            || savedBox.GetProperty("variantName").GetString() != "День" || savedBox.GetProperty("x").GetDouble() != 0.25
            || saved.GetProperty("image").GetProperty("objectCount").GetInt32() != 1)
            throw new InvalidOperationException("set_annotations: " + saved);
        var read = Ok(await CallAsync(client, "get_annotations", new { projectId, imageIds = new[] { imageId, secondId } }, token), "get_annotations").GetProperty("annotations");
        if (read.GetArrayLength() != 2 || read[0].GetProperty("boxes")[0].GetProperty("id").GetString() != savedBox.GetProperty("id").GetString())
            throw new InvalidOperationException("get_annotations round trip: " + read);
        Error(await CallAsync(client, "set_annotations", new { projectId, imageId, boxes = Array.Empty<object>(), annotationRevision = revision }, token),
            "images.revision_conflict", null);
        Error(await CallAsync(client, "set_annotations", new { projectId, imageId, boxes = new[] { box with { x = 0.7 } } }, token), "images.box_invalid", null);
        Error(await CallAsync(client, "set_annotations", new { projectId, imageId, boxes = new[] { box with { width = 0.001 } } }, token), "images.box_invalid", null);
        Error(await CallAsync(client, "set_annotations", new { projectId, imageId, boxes = new[] { new { classId = catalog.CupId, x = 0.1, y = 0.1 } } }, token),
            "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "set_annotations", new { projectId, imageId = Guid.NewGuid(), boxes = Array.Empty<object>() }, token), "images.image_not_found", null);
        // The overlay of view draws the saved box: the frame with boxes differs from the bare one.
        var overlay = DecodeMcpImage(await CallAsync(client, "view", new { projectId, target = new { image = imageId }, overlay = true, format = "png" }, token), "image/png");
        var bare = DecodeMcpImage(await CallAsync(client, "view", new { projectId, target = new { image = imageId }, overlay = false, format = "png" }, token), "image/png");
        var viewed = Ok(await CallAsync(client, "view", new { projectId, target = new { image = imageId }, overlay = true }, token), "view");
        if (!Differs(overlay, bare) || viewed.GetProperty("boxes").GetArrayLength() != 1 || viewed.GetProperty("boxes")[0].GetProperty("x").GetDouble() != 0.25)
            throw new InvalidOperationException("view does not show the box set through MCP: " + viewed);
        checks.Add("set_annotations round trip: boxes in fractions with classId and classVariantId come back from get_annotations with names and object IDs, view with overlay draws them; a stale revision, a box beyond the image or smaller than the editor allows and an unknown image are refused");

        var reviewed = Ok(await CallAsync(client, "set_image_reviewed", new { projectId, imageIds = new[] { imageId, secondId } }, token), "set_image_reviewed");
        if (reviewed.GetProperty("changed").GetInt32() != 1 || reviewed.GetProperty("unchanged").GetInt32() != 1) throw new InvalidOperationException("set_image_reviewed: " + reviewed);
        var kept = Ok(await CallAsync(client, "get_annotations", new { projectId, imageId }, token), "get_annotations").GetProperty("annotation");
        if (kept.GetProperty("boxes").GetArrayLength() != 1) throw new InvalidOperationException("set_image_reviewed lost boxes: " + kept);
        var unreviewed = Ok(await CallAsync(client, "list_images", new { projectId, groupId, reviewState = "unreviewed" }, token), "list_images").GetProperty("items");
        if (unreviewed.EnumerateArray().Any(item => item.GetProperty("id").GetString() is { } id && (id == imageId || id == secondId)))
            throw new InvalidOperationException("list_images still lists reviewed images: " + unreviewed);
        checks.Add("set_image_reviewed marks images reviewed and keeps their boxes; list_images filters by review state");

        // Deletion blockers come from the review, like the dialog of the window.
        var blocked = Error(await CallAsync(client, "delete_class", new { projectId, classId = catalog.CupId }, token), "classes.deletion_blocked", null);
        if (!blocked.GetProperty("details").GetProperty("blockers").GetString()!.Contains("images.annotations", StringComparison.Ordinal))
            throw new InvalidOperationException("delete_class blockers: " + blocked);
        Error(await CallAsync(client, "unbind_class_variant", new { projectId, classVariantId = catalog.CupDayId }, token), "classes.deletion_blocked", null);
        Error(await CallAsync(client, "delete_variant", new { projectId, variantId = catalog.DayId }, token), "variants.deletion_blocked", null);
        var spoon = Ok(await CallAsync(client, "delete_class", new { projectId, classId = catalog.SpoonId }, token), "delete_class");
        if (spoon.GetProperty("classVariants").GetInt64() != 1) throw new InvalidOperationException("delete_class: " + spoon);
        checks.Add("delete_class, unbind_class_variant and delete_variant are refused while annotation refers to them (deletion_blocked with details.blockers images.annotations); an unused class is deleted with its binding");

        // Moving and deleting images, deleting a group.
        var moved = Ok(await CallAsync(client, "move_images", new { projectId, imageIds = new[] { secondId }, targetGroupId = otherId }, token), "move_images");
        if (moved.GetProperty("moved").GetInt32() != 1 || moved.GetProperty("target").GetProperty("imageCount").GetInt64() != 4) throw new InvalidOperationException("move_images: " + moved);
        Error(await CallAsync(client, "move_images", new { projectId, imageIds = new[] { imageId, secondId }, targetGroupId = otherId }, token), "images.selection_invalid", null);
        var deleted = Ok(await CallAsync(client, "delete_images", new { projectId, imageIds = new[] { secondId } }, token), "delete_images");
        if (deleted.GetProperty("images").GetInt64() != 1) throw new InvalidOperationException("delete_images: " + deleted);
        Error(await CallAsync(client, "delete_images", new { projectId, imageIds = new[] { secondId } }, token), "images.image_not_found", null);
        var archived = Ok(await CallAsync(client, "update_image_group", new { projectId, groupId = otherId, archived = true }, token), "update_image_group").GetProperty("group");
        if (archived.GetProperty("archivedAt").ValueKind == JsonValueKind.Null) throw new InvalidOperationException("update_image_group archived: " + archived);
        Error(await CallAsync(client, "import_images", new { projectId, groupId = otherId, uploadId = upload }, token), "images.group_archived", null);
        var removed = Ok(await CallAsync(client, "delete_image_group", new { projectId, groupId = otherId }, token), "delete_image_group");
        if (!removed.GetProperty("groupDeleted").GetBoolean() || removed.GetProperty("images").GetInt64() != 3) throw new InvalidOperationException("delete_image_group: " + removed);
        checks.Add("move_images, delete_images and delete_image_group review and change like the window; images of two groups and an archived group are refused");

        // The prompt that guides an AI through the review.
        var prompts = await client.ListPromptsAsync(cancellationToken: token);
        if (!prompts.Any(prompt => prompt.Name == "review_annotations")) throw new InvalidOperationException("prompts/list: " + string.Join(", ", prompts.Select(prompt => prompt.Name)));
        var guide = await client.GetPromptAsync("review_annotations", new Dictionary<string, object?> { ["projectId"] = projectId, ["groupId"] = groupId }, cancellationToken: token);
        var text = string.Join(" ", guide.Messages.Select(message => message.Content).OfType<TextContentBlock>().Select(block => block.Text));
        if (!text.Contains(groupId, StringComparison.Ordinal) || !text.Contains("set_annotations", StringComparison.Ordinal) || !text.Contains("Кружка", StringComparison.Ordinal))
            throw new InvalidOperationException("review_annotations: " + text);
        try
        {
            await client.GetPromptAsync("review_annotations", new Dictionary<string, object?> { ["projectId"] = projectId, ["groupId"] = Guid.NewGuid().ToString() },
                cancellationToken: token);
            throw new InvalidOperationException("review_annotations accepted an unknown group.");
        }
        catch (McpException error) when (error.Message.Contains("images.group_not_found", StringComparison.Ordinal))
        {
        }
        checks.Add("The prompt review_annotations is listed and renders the steps (list_images, view with overlay, set_annotations) for the group with its classes; an unknown group is an error");
    }
}
