using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Datasets through MCP from the generated group: plan (an invalid split refused), build, list and get with samples, the manifest
    /// resource, integrity check, YOLO export checked on disk with the overwrite rule, a dataset sample through view, archive and restore, a
    /// build in one call and its deletion, and finally the recipe archived and deleted.
    /// </summary>
    private static async Task CheckMcpDatasetsAsync(McpClient client, McpChain chain, string recipeId, string groupId, JsonArray checks, CancellationToken token)
    {
        var projectId = chain.ProjectId;
        var classIds = new[] { chain.ClassId };
        Error(await CallAsync(client, "plan_dataset", new { projectId, name = "MCP датасет", groupIds = new[] { groupId }, classIds, valRatio = 0.0 }, token),
            "datasets.split_invalid", null);
        var planned = Ok(await CallAsync(client, "plan_dataset", new { projectId, name = "MCP датасет", groupIds = new[] { groupId }, classIds, valRatio = 0.25, testRatio = 0.25, seed = 3 },
            token), "plan_dataset");
        var plan = planned.GetProperty("plan");
        if (plan.GetProperty("counts").GetProperty("total").GetInt32() != 16 || !plan.GetProperty("buildable").GetBoolean()
            || plan.GetProperty("classes").GetArrayLength() != 1)
            throw new InvalidOperationException("plan_dataset: " + planned);
        var built = Ok(await CallAsync(client, "build_dataset", new { projectId, planId = plan.GetProperty("id").GetString() }, token), "build_dataset");
        var versionId = built.GetProperty("versionId").GetString()!;
        if (versionId != built.GetProperty("jobId").GetString()) throw new InvalidOperationException("build_dataset: " + built);
        await McpFinishedJobAsync(client, projectId, versionId, token);
        Error(await CallAsync(client, "build_dataset", new { projectId, planId = plan.GetProperty("id").GetString(), name = "Both" }, token), "mcp.invalid_arguments", null);
        var datasets = Ok(await CallAsync(client, "list_datasets", new { projectId }, token), "list_datasets").GetProperty("datasets");
        if (!datasets.EnumerateArray().Any(item => item.GetProperty("id").GetString() == versionId)) throw new InvalidOperationException("list_datasets misses the build.");
        var version = Ok(await CallAsync(client, "get_dataset_version", new { projectId, versionId, samplesPerSplit = 3 }, token), "get_dataset_version");
        var counts = version.GetProperty("dataset").GetProperty("counts");
        var split = new[] { "train", "val", "test" }.First(name => counts.GetProperty(name).GetInt32() > 0);
        var samples = version.GetProperty("samples").GetProperty(split);
        if (version.GetProperty("dataset").GetProperty("integrityState").GetString() != "verified" || samples.GetArrayLength() == 0
            || samples[0].GetProperty("boxes").GetArrayLength() == 0 || samples[0].GetProperty("groupId").GetString() != groupId)
            throw new InvalidOperationException("get_dataset_version: " + version);
        var read = await client.ReadResourceAsync($"maut://projects/{projectId}/datasets/{versionId}/manifest", cancellationToken: token);
        using (var manifest = JsonDocument.Parse(((TextResourceContents)read.Contents[0]).Text))
        {
            if (manifest.RootElement.GetProperty("samples").GetArrayLength() != 16 || manifest.RootElement.GetProperty("dataset").GetProperty("id").GetString() != versionId)
                throw new InvalidOperationException("The dataset manifest resource is incomplete.");
        }
        var view = await CallAsync(client, "view", new { projectId, target = new { datasetSample = versionId, split, index = 0 } }, token);
        using (DecodeMcpImage(view))
        {
            var shown = Ok(view, "view");
            if (shown.GetProperty("kind").GetString() != "datasetSample" || shown.GetProperty("boxes").GetArrayLength() == 0) throw new InvalidOperationException("view datasetSample: " + shown);
        }
        var bySample = Ok(await CallAsync(client, "view", new { projectId, target = new { datasetSample = versionId, sampleId = samples[0].GetProperty("sampleId").GetString() } }, token), "view");
        if (bySample.GetProperty("sampleId").GetString() != samples[0].GetProperty("sampleId").GetString()) throw new InvalidOperationException("view datasetSample by sampleId");
        checks.Add("plan_dataset counts the generated group per split and refuses a zero validation share; build_dataset builds a verified version; list_datasets, get_dataset_version with samples, the manifest resource and view of a sample agree");

        var verify = Ok(await CallAsync(client, "verify_dataset", new { projectId, versionId, waitSeconds = 60 }, token), "verify_dataset");
        if (verify.GetProperty("status").GetString() != "succeeded" || verify.GetProperty("job").GetProperty("result").GetProperty("state").GetString() != "verified")
            throw new InvalidOperationException("verify_dataset: " + verify);
        var exportFolder = Directory.CreateDirectory(Path.Combine(chain.Folder, "export")).FullName;
        var zip = Path.Combine(exportFolder, "mcp.zip");
        var exported = Ok(await CallAsync(client, "export_dataset", new { projectId, versionId, destinationPath = zip }, token), "export_dataset");
        await McpFinishedJobAsync(client, projectId, exported.GetProperty("jobId").GetString()!, token);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            if (!names.Any(name => name.EndsWith("data.yaml", StringComparison.Ordinal)) || names.Count(name => name.Contains("labels/", StringComparison.Ordinal)) < 16
                || names.Count(name => name.Contains("images/", StringComparison.Ordinal)) < 16)
                throw new InvalidOperationException("The exported ZIP: " + string.Join(", ", names.Take(10)));
        }
        Error(await CallAsync(client, "export_dataset", new { projectId, versionId, destinationPath = zip }, token), "mcp.destination_exists", null);
        Error(await CallAsync(client, "export_dataset", new { projectId, versionId, destinationPath = Path.Combine(exportFolder, "mcp.txt") }, token), "mcp.invalid_arguments", null);
        var replaced = Ok(await CallAsync(client, "export_dataset", new { projectId, versionId, destinationPath = zip, overwrite = true, waitSeconds = 60 }, token), "export_dataset");
        var intoFolder = Ok(await CallAsync(client, "export_dataset", new { projectId, versionId, destinationPath = exportFolder, waitSeconds = 60 }, token), "export_dataset");
        if (replaced.GetProperty("status").GetString() != "succeeded" || intoFolder.GetProperty("status").GetString() != "succeeded"
            || !File.Exists(intoFolder.GetProperty("path").GetString()) || Path.GetDirectoryName(intoFolder.GetProperty("path").GetString()) != exportFolder)
            throw new InvalidOperationException("export_dataset overwrite and folder: " + intoFolder);
        checks.Add("verify_dataset checks the files; export_dataset writes a YOLO ZIP (data.yaml, images, labels) to a path or a folder, refuses to replace a file without overwrite and replaces it with overwrite");

        var archived = Ok(await CallAsync(client, "archive_dataset", new { projectId, versionId }, token), "archive_dataset").GetProperty("dataset");
        Error(await CallAsync(client, "verify_dataset", new { projectId, versionId }, token), "datasets.archived", null);
        var restored = Ok(await CallAsync(client, "archive_dataset", new { projectId, versionId, archived = false }, token), "archive_dataset").GetProperty("dataset");
        if (archived.GetProperty("archivedAt").ValueKind == JsonValueKind.Null || restored.GetProperty("archivedAt").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("archive_dataset");
        var direct = Ok(await CallAsync(client, "build_dataset", new { projectId, name = "MCP сразу", groupIds = new[] { groupId }, classIds, waitSeconds = 60 }, token), "build_dataset");
        if (direct.GetProperty("status").GetString() != "succeeded" || direct.GetProperty("plan").ValueKind != JsonValueKind.Object) throw new InvalidOperationException("build_dataset in one call: " + direct);
        var deleted = Ok(await CallAsync(client, "delete_dataset_version", new { projectId, versionId = direct.GetProperty("versionId").GetString() }, token), "delete_dataset_version");
        if (deleted.GetProperty("samples").GetInt64() != 16) throw new InvalidOperationException("delete_dataset_version: " + deleted);
        Error(await CallAsync(client, "delete_dataset_version", new { projectId, versionId = Guid.NewGuid() }, token), "datasets.unavailable", null);
        checks.Add("archive_dataset archives (verification refused) and restores; build_dataset plans and builds in one call; delete_dataset_version deletes it, an unknown version is datasets.unavailable");

        Ok(await CallAsync(client, "update_recipe", new { projectId, recipeId, archived = true }, token), "update_recipe");
        Error(await CallAsync(client, "start_generation", new { projectId, recipeId, count = 1 }, token), "generation.unavailable", null);
        Ok(await CallAsync(client, "delete_recipe", new { projectId, recipeId }, token), "delete_recipe");
        Error(await CallAsync(client, "get_recipe", new { projectId, recipeId }, token), "generation.unavailable", null);
        checks.Add("update_recipe archives a recipe (it cannot run) and delete_recipe deletes it");
    }
}
