using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Catalog;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>The tools of backgrounds, generation and datasets, listed by tools/list next to the first set.</summary>
    private static readonly string[] McpDatasetChainTools =
    [
        "list_background_sets", "create_background_set", "update_background_set", "delete_background_set", "import_backgrounds",
        "import_video_backgrounds", "list_backgrounds", "delete_backgrounds",
        "list_recipes", "get_recipe", "create_recipe", "update_recipe", "delete_recipe", "start_generation", "list_generation_runs",
        "list_datasets", "get_dataset_version", "plan_dataset", "build_dataset", "verify_dataset", "export_dataset", "archive_dataset", "delete_dataset_version",
    ];

    /// <summary>What the chain made: the project, the fixture class and binding, and the background sets of images and of the video.</summary>
    private sealed record McpChain(string ProjectId, Guid ClassId, Guid VariantId, Guid BindingId, string ImageSetId, string VideoSetId, string Folder);

    /// <summary>
    /// The chain of an AI client through MCP only: background sets from image files (whole and cropped), an upload and an ffmpeg video →
    /// a recipe → generation → plan → build → integrity check → YOLO export checked on disk → views of a background, a generation sample
    /// and a dataset sample; every tool of the three areas with its error paths, the manifest resource and the prompts. Only the catalog
    /// (a class with transparent images) is prepared through Core: its tools belong to another area.
    /// </summary>
    private static async Task CheckMcpDatasetChainAsync(McpClient client, ICore core, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        var tools = (await client.ListToolsAsync(cancellationToken: token)).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);
        if (McpDatasetChainTools.Except(tools.Keys).ToArray() is { Length: > 0 } absent) throw new InvalidOperationException("MCP tools missing: " + string.Join(", ", absent));
        foreach (var name in McpDatasetChainTools)
        {
            var annotations = tools[name].Annotations!;
            var expectRead = name.StartsWith("list_", StringComparison.Ordinal) || name.StartsWith("get_", StringComparison.Ordinal) || name == "plan_dataset";
            var expectDestructive = name.StartsWith("delete_", StringComparison.Ordinal);
            if (annotations.ReadOnlyHint != expectRead || annotations.DestructiveHint != expectDestructive || tools[name].OutputSchema is null)
                throw new InvalidOperationException("Annotations of " + name);
        }
        checks.Add($"tools/list has the {McpDatasetChainTools.Length} tools of backgrounds, generation and datasets with schemas; reads are read-only, deletions destructive");
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: token);
        if (!templates.Any(item => item.UriTemplate == "maut://projects/{projectId}/datasets/{versionId}/manifest")) throw new InvalidOperationException("The manifest template is not listed.");
        var chain = await CheckMcpBackgroundsAsync(client, core, root, projectId, checks, token);
        var (recipeId, groupId) = await CheckMcpGenerationAsync(client, chain, checks, token);
        await CheckMcpDatasetsAsync(client, chain, recipeId, groupId, checks, token);
        await CheckMcpPromptsAsync(client, chain, checks, token);
    }

    private static async Task<McpChain> CheckMcpBackgroundsAsync(McpClient client, ICore core, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var (width, height) = (project.InputSize.Width, project.InputSize.Height);
        var folder = Path.Combine(root, "mcp-chain");
        var images = Path.Combine(folder, "backgrounds");
        Directory.CreateDirectory(images);
        string Write(string directory, string name, byte[] content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        // The catalog fixture: a class bound to a project variant with two transparent cut-outs.
        var variant = await project.Variants.CreateAsync("MCP цепочка", "", token);
        var klass = await project.Classes.CreateAsync("MCP объект", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        var assets = await core.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id,
            [Write(folder, "mcp-sprite-1.png", Sprite(90, 60, SKColors.Gold)), Write(folder, "mcp-sprite-2.png", Sprite(50, 110, SKColors.Crimson))]),
            projectId, "mcp-chain-assets", cancellationToken: token);
        for (var attempt = 0; ; attempt++)
        {
            var current = await core.Jobs.GetAsync(projectId, assets.Id, token);
            if (current.Status == JobStatus.Succeeded) break;
            if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 600) throw new InvalidOperationException("MCP chain assets: " + current.Status);
            await Task.Delay(50, token);
        }

        Write(images, "01-exact.png", Frame(width, height, SKColors.SteelBlue));
        Write(images, "02-exact.png", Frame(width, height, SKColors.DarkOliveGreen));
        var larger = Write(images, "03-larger.png", Frame(width + 100, height + 60, SKColors.Sienna));
        Write(images, "04-smaller.png", Frame(width / 2, height / 2, SKColors.Plum));

        Error(await CallAsync(client, "create_background_set", new { projectId, name = "  " }, token), "backgrounds.name_invalid", null);
        var set = Ok(await CallAsync(client, "create_background_set", new { projectId, name = "MCP фоны", description = "From an AI client" }, token),
            "create_background_set").GetProperty("set");
        var setId = set.GetProperty("id").GetString()!;
        if (set.GetProperty("targetSize").GetProperty("width").GetInt32() != width) throw new InvalidOperationException("create_background_set size: " + set);
        var renamed = Ok(await CallAsync(client, "update_background_set", new { projectId, setId, name = "MCP фоны 1" }, token), "update_background_set").GetProperty("set");
        Error(await CallAsync(client, "update_background_set", new { projectId, setId, name = "Stale", revision = set.GetProperty("revision").GetInt64() }, token),
            "backgrounds.revision_conflict", null);
        var archived = Ok(await CallAsync(client, "update_background_set", new { projectId, setId, archived = true }, token), "update_background_set").GetProperty("set");
        Error(await CallAsync(client, "import_backgrounds", new { projectId, setId, paths = new[] { images } }, token), "backgrounds.set_archived", null);
        var restored = Ok(await CallAsync(client, "update_background_set", new { projectId, setId, archived = false }, token), "update_background_set").GetProperty("set");
        if (renamed.GetProperty("name").GetString() != "MCP фоны 1" || archived.GetProperty("archivedAt").ValueKind == JsonValueKind.Null
            || restored.GetProperty("archivedAt").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("update_background_set: " + restored);
        checks.Add("create_background_set and update_background_set name, archive and restore a set with the revision rules of the window");

        Error(await CallAsync(client, "import_backgrounds", new { projectId, setId, paths = new[] { Path.Combine(images, "missing.png") } }, token), "mcp.path_not_found", null);
        Error(await CallAsync(client, "import_backgrounds", new { projectId, setId, paths = new[] { larger }, crops = new[] { new { sourceIndex = 0, left = 0, top = 0 } } }, token),
            "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "import_backgrounds", new { projectId, setId }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "import_backgrounds", new { projectId, setId, path = Directory.CreateDirectory(Path.Combine(folder, "empty")).FullName, mode = "crop" }, token), "mcp.no_files", null);
        var whole = Ok(await CallAsync(client, "import_backgrounds", new { projectId, setId, paths = new[] { images } }, token), "import_backgrounds");
        if (whole.GetProperty("sources").GetInt32() != 4) throw new InvalidOperationException("import_backgrounds sources: " + whole);
        var wholeJob = await McpFinishedJobAsync(client, projectId, whole.GetProperty("jobId").GetString()!, token);
        var wholeResult = wholeJob.GetProperty("result");
        if (wholeResult.GetProperty("added").GetInt32() != 2 || wholeResult.GetProperty("rejected").GetInt32() != 2
            || !wholeResult.GetProperty("issues").EnumerateArray().All(issue => issue.GetProperty("reason").GetString() == "backgrounds.wrong_size"))
            throw new InvalidOperationException("Whole backgrounds: " + wholeResult);
        var crop = Ok(await CallAsync(client, "import_backgrounds", new
        {
            projectId, setId, paths = new[] { larger }, mode = "crop", crops = new[] { new { sourceIndex = 0, left = 10, top = 5 } },
            blur = new { minRadius = 1.0, maxRadius = 2.0, seed = 5 }, waitSeconds = 60,
        }, token), "import_backgrounds");
        if (crop.GetProperty("status").GetString() != "succeeded" || crop.GetProperty("cropped").GetInt32() != 1
            || crop.GetProperty("job").GetProperty("result").GetProperty("added").GetInt32() != 1)
            throw new InvalidOperationException("Cropped background: " + crop);
        var upload = Ok(await CallAsync(client, "upload_file", new { name = "фон.png", base64 = Convert.ToBase64String(Frame(width, height, SKColors.Teal)) }, token),
            "upload_file").GetProperty("uploadId").GetString()!;
        var uploaded = Ok(await CallAsync(client, "import_backgrounds", new { projectId, setId, uploadIds = new[] { upload }, waitSeconds = 60 }, token), "import_backgrounds");
        if (uploaded.GetProperty("job").GetProperty("result").GetProperty("added").GetInt32() != 1) throw new InvalidOperationException("Uploaded background: " + uploaded);
        var listed = Ok(await CallAsync(client, "list_backgrounds", new { projectId, setId, limit = 2 }, token), "list_backgrounds");
        var next = Ok(await CallAsync(client, "list_backgrounds", new { projectId, setId, cursor = listed.GetProperty("nextCursor").GetString() }, token), "list_backgrounds");
        var backgrounds = listed.GetProperty("backgrounds").EnumerateArray().Concat(next.GetProperty("backgrounds").EnumerateArray()).ToArray();
        var cropped = backgrounds.Single(item => item.GetProperty("origin").GetProperty("kind").GetString() == "image-crop");
        if (backgrounds.Length != 4 || cropped.GetProperty("origin").GetProperty("crop").GetProperty("left").GetInt32() != 10
            || cropped.GetProperty("origin").GetProperty("blurRadius").GetDouble() < 1 || backgrounds.Any(item => item.GetProperty("width").GetInt32() != width))
            throw new InvalidOperationException("list_backgrounds: " + string.Join(", ", backgrounds.Select(item => item.ToString())));
        var view = await CallAsync(client, "view", new { projectId, target = new { background = cropped.GetProperty("id").GetString() } }, token);
        using (var picture = DecodeMcpImage(view))
        {
            var shown = Ok(view, "view");
            if (shown.GetProperty("kind").GetString() != "background" || shown.GetProperty("width").GetInt32() != width || picture.Width < 64)
                throw new InvalidOperationException("view background: " + shown);
        }
        checks.Add("import_backgrounds takes whole images of the set size from a folder (others skipped as wrong_size), crops a larger file at the given corner with blur, and an upload; list_backgrounds pages them and an id is a view target");

        // Video: a clip larger than the set, three frames a second apart, the region centered.
        var video = Path.Combine(folder, "фон.mp4");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={width + 64}x{height + 32}:rate=10:duration=3",
            "-c:v", "libopenh264", "-g", "10", video })
            start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("MCP chain video was not created: " + await errors);
        }
        var videoSetId = Ok(await CallAsync(client, "create_background_set", new { projectId, name = "MCP видео" }, token), "create_background_set")
            .GetProperty("set").GetProperty("id").GetString()!;
        Error(await CallAsync(client, "import_video_backgrounds", new { projectId, setId = videoSetId, videoPath = larger }, token), "video.unsupported", null);
        Error(await CallAsync(client, "import_video_backgrounds", new { projectId, setId = videoSetId }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "import_video_backgrounds", new { projectId, setId = videoSetId, videoPath = video, stepSeconds = 1, count = 9 }, token),
            "backgrounds.count_invalid", null);
        var fromVideo = Ok(await CallAsync(client, "import_video_backgrounds", new { projectId, setId = videoSetId, videoPath = video, stepSeconds = 1 }, token),
            "import_video_backgrounds");
        if (fromVideo.GetProperty("availableFrames").GetInt32() != 3 || fromVideo.GetProperty("region").GetProperty("left").GetInt32() != 32)
            throw new InvalidOperationException("import_video_backgrounds: " + fromVideo);
        var videoJob = await McpFinishedJobAsync(client, projectId, fromVideo.GetProperty("jobId").GetString()!, token);
        if (videoJob.GetProperty("result").GetProperty("added").GetInt32() != 3) throw new InvalidOperationException("Video backgrounds: " + videoJob);
        var videoItems = Ok(await CallAsync(client, "list_backgrounds", new { projectId, setId = videoSetId }, token), "list_backgrounds").GetProperty("backgrounds");
        if (videoItems.GetArrayLength() != 3 || videoItems[1].GetProperty("origin").GetProperty("kind").GetString() != "video-frame"
            || videoItems[1].GetProperty("origin").GetProperty("timestampMs").GetInt64() != 1000)
            throw new InvalidOperationException("Video backgrounds listed: " + videoItems);
        checks.Add("import_video_backgrounds cuts frames of an ffmpeg clip every stepSeconds at a centered region; a wrong file, a missing source and too many frames are refused");

        var sets = Ok(await CallAsync(client, "list_background_sets", new { projectId }, token), "list_background_sets").GetProperty("sets").EnumerateArray().ToArray();
        if (sets.Single(item => item.GetProperty("id").GetString() == setId).GetProperty("itemCount").GetInt64() != 4) throw new InvalidOperationException("list_background_sets");
        var removedId = backgrounds.Single(item => item.GetProperty("origin").GetProperty("sourceName").GetString() == "фон.png").GetProperty("id").GetString();
        var deleted = Ok(await CallAsync(client, "delete_backgrounds", new { projectId, setId, backgroundIds = new[] { removedId } }, token), "delete_backgrounds");
        if (deleted.GetProperty("items").GetInt64() != 1) throw new InvalidOperationException("delete_backgrounds: " + deleted);
        Error(await CallAsync(client, "delete_backgrounds", new { projectId, setId, backgroundIds = Array.Empty<string>() }, token), "mcp.invalid_arguments", null);
        var spare = Ok(await CallAsync(client, "create_background_set", new { projectId, name = "MCP лишний" }, token), "create_background_set").GetProperty("set").GetProperty("id").GetString();
        if (!Ok(await CallAsync(client, "delete_background_set", new { projectId, setId = spare }, token), "delete_background_set").GetProperty("setDeleted").GetBoolean())
            throw new InvalidOperationException("delete_background_set");
        Error(await CallAsync(client, "delete_background_set", new { projectId, setId = spare }, token), "backgrounds.set_not_found", null);
        checks.Add("list_background_sets counts the backgrounds; delete_backgrounds and delete_background_set delete permanently after the review of the window");
        return new McpChain(projectId, klass.Id, variant.Id, binding.Id, setId, videoSetId, folder);
    }

    /// <summary>wait_job until the job ends; it must succeed. Returns the job snapshot.</summary>
    private static async Task<JsonElement> McpFinishedJobAsync(McpClient client, string projectId, string jobId, CancellationToken token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var waited = Ok(await CallAsync(client, "wait_job", new { projectId, jobId, timeoutSeconds = 60 }, token), "wait_job");
            if (!waited.GetProperty("finished").GetBoolean()) continue;
            var job = waited.GetProperty("job");
            if (job.GetProperty("status").GetString() != "succeeded") throw new InvalidOperationException("MCP job did not succeed: " + job);
            return job.Clone();
        }
        throw new TimeoutException("MCP job did not finish: " + jobId);
    }

    /// <summary>prompts/list holds the three scenarios; prompts/get fills the arguments in; a missing argument is refused.</summary>
    private static async Task CheckMcpPromptsAsync(McpClient client, McpChain chain, JsonArray checks, CancellationToken token)
    {
        var prompts = await client.ListPromptsAsync(cancellationToken: token);
        foreach (var name in new[] { "prepare_dataset_from_folder", "synthetic_dataset", "generate_from_video" })
        {
            if (!prompts.Any(prompt => prompt.Name == name && prompt.ProtocolPrompt.Arguments is { Count: > 0 })) throw new InvalidOperationException("prompts/list misses " + name);
        }
        var prompt = await client.GetPromptAsync("synthetic_dataset", new Dictionary<string, object?> { ["classesFolder"] = chain.Folder, ["backgroundsFolder"] = @"D:\фоны" },
            cancellationToken: token);
        var text = string.Join("\n", prompt.Messages.Select(message => (message.Content as TextContentBlock)?.Text));
        if (!text.Contains(chain.Folder, StringComparison.Ordinal) || !text.Contains(@"D:\фоны", StringComparison.Ordinal) || !text.Contains("start_generation", StringComparison.Ordinal))
            throw new InvalidOperationException("prompts/get synthetic_dataset: " + text);
        var refused = false;
        try
        {
            await client.GetPromptAsync("generate_from_video", new Dictionary<string, object?>(), cancellationToken: token);
        }
        catch (ModelContextProtocol.McpException)
        {
            refused = true;
        }
        if (!refused) throw new InvalidOperationException("prompts/get without a required argument was accepted.");
        checks.Add("prompts prepare_dataset_from_folder, synthetic_dataset and generate_from_video guide through the tool chain with their arguments; a missing argument is refused");
    }
}
