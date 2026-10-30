using System.Collections.Concurrent;
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
    private static readonly string[] McpCatalogTools =
    [
        "list_base_models", "install_base_model", "delete_base_model",
        "list_variants", "create_variant", "update_variant", "archive_variant", "delete_variant",
        "list_classes", "create_class", "update_class", "archive_class", "reorder_classes", "delete_class",
        "bind_class_variant", "update_class_variant", "unbind_class_variant", "list_class_assets", "import_class_assets", "delete_class_assets",
        "list_image_groups", "create_image_group", "update_image_group", "delete_image_group", "import_images", "import_video_frames",
        "list_images", "get_annotations", "set_annotations", "set_image_reviewed", "move_images", "delete_images",
    ];

    /// <summary>
    /// Base models, the catalog (variants, classes, bindings, cut-out images) and images with annotation through MCP, in a project of its
    /// own: every tool answers, errors keep the codes of the window (validation, not found, revision conflicts, deletion blockers), imports
    /// by path and by uploadId finish through wait_job with progress, jobs carry the AI label, and the review_annotations prompt is served.
    /// </summary>
    private static async Task CheckMcpCatalogAndImagesAsync(McpClient client, ICore core, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        var tools = (await client.ListToolsAsync(cancellationToken: token)).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);
        if (McpCatalogTools.Except(tools.Keys).ToArray() is { Length: > 0 } missing) throw new InvalidOperationException("MCP tools missing: " + string.Join(", ", missing));
        foreach (var (name, destructive) in new[] { ("delete_variant", true), ("delete_class_assets", true), ("delete_images", true), ("unbind_class_variant", true),
            ("set_annotations", false), ("list_images", false), ("delete_base_model", true) })
        {
            if (tools[name].Annotations!.DestructiveHint != destructive || (name.StartsWith("list_", StringComparison.Ordinal) && tools[name].Annotations!.ReadOnlyHint != true))
                throw new InvalidOperationException("Wrong annotations of " + name);
        }
        checks.Add($"tools/list has the {McpCatalogTools.Length} base model, catalog and image tools with read-only and destructive hints by class");

        var baseModelId = (await core.Projects.GetAsync(projectId, token)).BaseModelId;
        await CheckMcpBaseModelsAsync(client, core, baseModelId, checks, token);
        var created = Ok(await CallAsync(client, "create_project", new { name = "MCP каталог", baseModelId, width = 320, height = 320 }, token), "create_project");
        var id = created.GetProperty("project").GetProperty("id").GetString()!;
        var catalog = await CheckMcpCatalogAsync(client, root, id, checks, token);
        await CheckMcpImagesAsync(client, core, root, id, catalog, checks, token);
        Ok(await CallAsync(client, "archive_project", new { projectId = id }, token), "archive_project");
    }

    private static async Task CheckMcpBaseModelsAsync(McpClient client, ICore core, string installedId, JsonArray checks, CancellationToken token)
    {
        var models = Ok(await CallAsync(client, "list_base_models", null, token), "list_base_models").GetProperty("models").EnumerateArray().ToArray();
        if (!models.Any(model => model.GetProperty("id").GetString() == installedId && model.GetProperty("status").GetString() == "installed"))
            throw new InvalidOperationException("list_base_models: " + string.Join(", ", models.Select(model => model.ToString())));
        var notifications = new ConcurrentQueue<ProgressNotificationValue>();
        var installed = Ok(await CallAsync(client, "install_base_model", new { modelId = installedId, waitSeconds = 5 }, token, new RecordingProgress(notifications)),
            "install_base_model");
        if (!installed.GetProperty("finished").GetBoolean() || installed.GetProperty("model").GetProperty("status").GetString() != "installed"
            || notifications.LastOrDefault()?.Progress != 100)
            throw new InvalidOperationException("install_base_model of an installed model: " + installed);
        Error(await CallAsync(client, "install_base_model", new { modelId = "yolo-missing" }, token), "base_models.not_found", null);
        Error(await CallAsync(client, "delete_base_model", new { modelId = "yolo-missing" }, token), "base_models.not_found", null);
        if (models.FirstOrDefault(model => model.GetProperty("status").GetString() == "not_installed") is { ValueKind: JsonValueKind.Object } absent)
        {
            var deleted = Ok(await CallAsync(client, "delete_base_model", new { modelId = absent.GetProperty("id").GetString() }, token), "delete_base_model");
            if (deleted.GetProperty("model").GetProperty("status").GetString() != "not_installed") throw new InvalidOperationException("delete_base_model: " + deleted);
        }
        checks.Add("list_base_models, install_base_model (an installed model finishes at once with progress 100) and delete_base_model answer; an unknown model is base_models.not_found");
    }

    private sealed record McpCatalogIds(string CupId, string SpoonId, string DayId, string CupDayId, string SpoonDayId);

    private static async Task<McpCatalogIds> CheckMcpCatalogAsync(McpClient client, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        // Variants.
        var day = Ok(await CallAsync(client, "create_variant", new { projectId, name = "День" }, token), "create_variant").GetProperty("variant");
        var night = Ok(await CallAsync(client, "create_variant", new { projectId, name = "Ночь", description = "Тёмные кадры" }, token), "create_variant").GetProperty("variant");
        Error(await CallAsync(client, "create_variant", new { projectId, name = "день" }, token), "variants.name_duplicate", null);
        Error(await CallAsync(client, "create_variant", new { projectId, name = "" }, token), "variants.name_invalid", null);
        var dayId = day.GetProperty("id").GetString()!;
        var nightId = night.GetProperty("id").GetString()!;
        var renamed = Ok(await CallAsync(client, "update_variant", new { projectId, variantId = nightId, name = "Ночь ИК" }, token), "update_variant").GetProperty("variant");
        if (renamed.GetProperty("name").GetString() != "Ночь ИК" || renamed.GetProperty("description").GetString() != "Тёмные кадры")
            throw new InvalidOperationException("update_variant: " + renamed);
        Error(await CallAsync(client, "update_variant", new { projectId, variantId = nightId, name = "Устарело", revision = night.GetProperty("revision").GetInt64() }, token),
            "variants.revision_conflict", null);
        Error(await CallAsync(client, "update_variant", new { projectId, variantId = Guid.NewGuid(), name = "Нет" }, token), "variants.not_found", null);
        Ok(await CallAsync(client, "archive_variant", new { projectId, variantId = nightId }, token), "archive_variant");
        Error(await CallAsync(client, "update_variant", new { projectId, variantId = nightId, name = "Архив" }, token), "variants.archived", null);
        var listed = Ok(await CallAsync(client, "list_variants", new { projectId }, token), "list_variants").GetProperty("variants");
        var all = Ok(await CallAsync(client, "list_variants", new { projectId, includeArchived = true }, token), "list_variants").GetProperty("variants");
        if (listed.GetArrayLength() != 1 || all.GetArrayLength() != 2) throw new InvalidOperationException("list_variants: " + all);
        Ok(await CallAsync(client, "archive_variant", new { projectId, variantId = nightId, archived = false }, token), "archive_variant");
        checks.Add("create_variant, update_variant, archive_variant and list_variants work like the window: duplicate and empty names, revision conflict, not found and archived variants are refused with their codes");

        // Classes and their order.
        var cup = Ok(await CallAsync(client, "create_class", new { projectId, name = "Кружка" }, token), "create_class").GetProperty("class");
        var spoon = Ok(await CallAsync(client, "create_class", new { projectId, name = "Ложка", description = "Столовая" }, token), "create_class").GetProperty("class");
        var cupId = cup.GetProperty("id").GetString()!;
        var spoonId = spoon.GetProperty("id").GetString()!;
        Error(await CallAsync(client, "create_class", new { projectId, name = "КРУЖКА" }, token), "classes.name_duplicate", null);
        Ok(await CallAsync(client, "update_class", new { projectId, classId = cupId, description = "Для чая" }, token), "update_class");
        Error(await CallAsync(client, "update_class", new { projectId, classId = cupId, name = "Старая", revision = cup.GetProperty("revision").GetInt64() }, token),
            "classes.revision_conflict", null);
        Ok(await CallAsync(client, "archive_class", new { projectId, classId = spoonId }, token), "archive_class");
        Ok(await CallAsync(client, "archive_class", new { projectId, classId = spoonId, archived = false }, token), "archive_class");
        var classes = Ok(await CallAsync(client, "list_classes", new { projectId }, token), "list_classes");
        var orderRevision = classes.GetProperty("classOrderRevision").GetInt64();
        var reordered = Ok(await CallAsync(client, "reorder_classes", new { projectId, classIds = new[] { spoonId, cupId }, orderRevision }, token), "reorder_classes");
        if (reordered.GetProperty("classes")[0].GetProperty("id").GetString() != spoonId) throw new InvalidOperationException("reorder_classes: " + reordered);
        Error(await CallAsync(client, "reorder_classes", new { projectId, classIds = new[] { cupId, spoonId }, orderRevision }, token), "classes.order_revision_conflict", null);
        Error(await CallAsync(client, "reorder_classes", new { projectId, classIds = new[] { cupId } }, token), "classes.order_invalid", null);
        Error(await CallAsync(client, "reorder_classes", new { projectId, classIds = new[] { "not-an-id" } }, token), "mcp.invalid_arguments", null);
        checks.Add("create_class, update_class, archive_class, list_classes and reorder_classes (stale order revision and an incomplete order refused) change the catalog");

        // Bindings.
        var cupDay = Ok(await CallAsync(client, "bind_class_variant", new { projectId, classId = cupId, variantId = dayId, color = "#ff0000" }, token),
            "bind_class_variant").GetProperty("classVariant");
        if (cupDay.GetProperty("color").GetString() != "#ff0000") throw new InvalidOperationException("bind_class_variant: " + cupDay);
        var cupDayId = cupDay.GetProperty("id").GetString()!;
        Error(await CallAsync(client, "bind_class_variant", new { projectId, classId = cupId, variantId = dayId }, token), "classes.duplicate_variant_binding", null);
        var spoonDayId = Ok(await CallAsync(client, "bind_class_variant", new { projectId, classId = spoonId, variantId = dayId }, token), "bind_class_variant")
            .GetProperty("classVariant").GetProperty("id").GetString()!;
        var cupNightId = Ok(await CallAsync(client, "bind_class_variant", new { projectId, classId = cupId, variantId = nightId }, token), "bind_class_variant")
            .GetProperty("classVariant").GetProperty("id").GetString()!;
        var recolored = Ok(await CallAsync(client, "update_class_variant", new { projectId, classVariantId = cupDayId, color = (string?)null }, token), "update_class_variant")
            .GetProperty("classVariant");
        if (recolored.GetProperty("color").ValueKind != JsonValueKind.Null) throw new InvalidOperationException("update_class_variant color: " + recolored);
        Error(await CallAsync(client, "update_class_variant", new { projectId, classVariantId = cupDayId, color = "red" }, token), "classes.color_invalid", null);
        var hidden = Ok(await CallAsync(client, "update_class_variant", new { projectId, classVariantId = cupNightId, archived = true }, token), "update_class_variant")
            .GetProperty("classVariant");
        if (hidden.GetProperty("available").GetBoolean()) throw new InvalidOperationException("update_class_variant archived: " + hidden);
        Error(await CallAsync(client, "update_class_variant", new { projectId, classVariantId = cupNightId, color = "#00ff00" }, token), "classes.variant_archived", null);
        var bound = Ok(await CallAsync(client, "list_classes", new { projectId }, token), "list_classes").GetProperty("classes");
        if (!bound.EnumerateArray().Any(item => item.GetProperty("id").GetString() == cupId
            && item.GetProperty("classVariants").EnumerateArray().Any(binding => binding.GetProperty("variantName").GetString() == "День")))
            throw new InvalidOperationException("list_classes bindings: " + bound);
        checks.Add("bind_class_variant, update_class_variant (colour, archive) and list_classes with bindings; a duplicate binding, a bad colour and the colour of an archived binding are refused");

        // Cut-out images: a folder by path and a file by uploadId, both followed with wait_job.
        var folder = Path.Combine(root, "mcp-class-assets");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "кружка-1.png"), McpPicture(64, 48, SKColors.Firebrick));
        File.WriteAllBytes(Path.Combine(folder, "кружка-2.png"), McpPicture(40, 60, SKColors.DarkOrange));
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "not an image");
        var started = Ok(await CallAsync(client, "import_class_assets", new { projectId, classVariantId = cupDayId, path = folder }, token), "import_class_assets");
        var job = await WaitMcpJobAsync(client, projectId, started.GetProperty("jobId").GetString()!, "import_class_assets", token);
        if (job.GetProperty("result").GetProperty("added").GetInt32() != 2) throw new InvalidOperationException("import_class_assets by path: " + job);
        var label = job.GetProperty("label");
        if (label.GetProperty("code").GetString() != "jobLabels.ai_label" || label.GetProperty("args").GetProperty("client").GetString() != "maut-smoke"
            || label.GetProperty("args").GetProperty("labelCode").GetString() != "jobLabels.class_variant"
            || label.GetProperty("args").GetProperty("label.class").GetString() != "Кружка")
            throw new InvalidOperationException("A job started through MCP has no AI label: " + label);
        var upload = Ok(await CallAsync(client, "upload_file", new { name = "кружка-3.png", base64 = Convert.ToBase64String(McpPicture(30, 30, SKColors.Gold)) }, token),
            "upload_file").GetProperty("uploadId").GetString()!;
        var uploaded = Ok(await CallAsync(client, "import_class_assets", new { projectId, classVariantId = cupDayId, uploadId = upload, waitSeconds = 60 }, token),
            "import_class_assets");
        if (uploaded.GetProperty("status").GetString() != "succeeded" || uploaded.GetProperty("job").GetProperty("result").GetProperty("added").GetInt32() != 1)
            throw new InvalidOperationException("import_class_assets by uploadId: " + uploaded);
        Error(await CallAsync(client, "import_class_assets", new { projectId, classVariantId = cupNightId, uploadId = upload }, token), "classes.variant_unavailable", null);
        Error(await CallAsync(client, "import_class_assets", new { projectId, classVariantId = cupDayId, path = Path.Combine(folder, "нет.png") }, token), "mcp.path_not_found", null);
        Error(await CallAsync(client, "import_class_assets", new { projectId, classVariantId = cupDayId }, token), "mcp.invalid_arguments", null);
        var assets = Ok(await CallAsync(client, "list_class_assets", new { projectId, classVariantId = cupDayId, limit = 2 }, token), "list_class_assets");
        var items = assets.GetProperty("items");
        if (items.GetArrayLength() != 2 || assets.GetProperty("nextCursor").ValueKind != JsonValueKind.String || items[0].TryGetProperty("originalUrl", out _))
            throw new InvalidOperationException("list_class_assets: " + assets);
        var rest = Ok(await CallAsync(client, "list_class_assets", new { projectId, classVariantId = cupDayId, cursor = assets.GetProperty("nextCursor").GetString() }, token),
            "list_class_assets").GetProperty("items");
        var deleted = Ok(await CallAsync(client, "delete_class_assets", new { projectId, assetIds = new[] { rest[0].GetProperty("id").GetString() } }, token), "delete_class_assets");
        if (deleted.GetProperty("deleted").GetInt64() != 1) throw new InvalidOperationException("delete_class_assets: " + deleted);
        Error(await CallAsync(client, "delete_class_assets", new { projectId, assetIds = new[] { rest[0].GetProperty("id").GetString() } }, token), "classes.not_found", null);
        checks.Add("import_class_assets by folder path and by uploadId runs as a job followed with wait_job and progress; the job is labelled «AI (maut-smoke) · Кружка · День»; list_class_assets pages without page URLs; delete_class_assets removes and a second time finds nothing");

        // Deleting a variant nothing uses, and unbinding.
        var unbound = Ok(await CallAsync(client, "unbind_class_variant", new { projectId, classVariantId = cupNightId }, token), "unbind_class_variant");
        if (unbound.GetProperty("classVariants").GetInt64() != 1) throw new InvalidOperationException("unbind_class_variant: " + unbound);
        var removed = Ok(await CallAsync(client, "delete_variant", new { projectId, variantId = nightId }, token), "delete_variant");
        if (removed.GetProperty("classVariants").GetInt64() != 0) throw new InvalidOperationException("delete_variant: " + removed);
        Error(await CallAsync(client, "delete_variant", new { projectId, variantId = nightId }, token), "variants.not_found", null);
        checks.Add("unbind_class_variant and delete_variant review the content and delete it; a second deletion is variants.not_found");
        return new McpCatalogIds(cupId, spoonId, dayId, cupDayId, spoonDayId);
    }

    /// <summary>wait_job with a progressToken until the job succeeds: progress grows and ends at 100; returns the job.</summary>
    private static async Task<JsonElement> WaitMcpJobAsync(McpClient client, string projectId, string jobId, string tool, CancellationToken token)
    {
        var notifications = new ConcurrentQueue<ProgressNotificationValue>();
        var waited = Ok(await CallAsync(client, "wait_job", new { projectId, jobId, timeoutSeconds = 90 }, token, new RecordingProgress(notifications)), "wait_job");
        var values = notifications.Select(item => item.Progress).ToArray();
        var job = waited.GetProperty("job");
        if (job.GetProperty("status").GetString() != "succeeded" || values.Length == 0 || values[^1] != 100 || values.Zip(values.Skip(1)).Any(pair => pair.Second <= pair.First))
            throw new InvalidOperationException($"{tool} job: {waited}; progress {string.Join(", ", values)}");
        return job;
    }
}
