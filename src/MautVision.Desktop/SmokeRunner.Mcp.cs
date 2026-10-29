using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Desktop.Mcp;
using Microsoft.Web.WebView2.Core;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    private static readonly string[] McpStepOneTools =
    [
        "get_app_info", "get_hardware", "set_interface_language", "list_jobs", "get_job", "wait_job", "cancel_job",
        "preview_folder", "upload_file", "view", "view_many",
        "list_projects", "get_project", "create_project", "update_project", "archive_project", "open_project_in_app", "delete_project",
    ];

    private static readonly JsonSerializerOptions McpJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Without MCP turned on, nothing of ASP.NET Core or the MCP SDK is loaded into the process: the whole smoke ran with the server off,
    /// as every installation does until the user turns it on.
    /// </summary>
    private static void RequireMcpNotLoaded(JsonNode report)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name ?? "")
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name.StartsWith("ModelContextProtocol", StringComparison.Ordinal))
            .ToArray();
        if (loaded.Length > 0) throw new InvalidOperationException("MCP is off, yet these assemblies are loaded: " + string.Join(", ", loaded));
        report["checks"]!.AsArray().Add("With MCP off, ASP.NET Core and the MCP SDK are never loaded");
    }

    /// <summary>
    /// The local MCP server end to end with the official C# client: the settings dialog turns it on (port, generated token), the guard
    /// refuses a missing or wrong token (401) and a foreign Origin or Host (403), the tool list carries schemas and annotations, every tool
    /// of the first set answers (views with overlay, region and a numbered grid), access levels block writes and deletion, deleting a
    /// project waits for "Allow" or "Deny" in the window and times out, the folder restriction resolves junctions, uploads keep their
    /// limits, wait_job times out without stopping the job and streams progress of a long job, a busy port is reported, and turning the
    /// server off closes the endpoint.
    /// </summary>
    private static async Task CheckMcpAsync(CoreWebView2 web, string root, string projectId, ICore core, McpService mcp, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        // The default port is made unavailable (on some machines Windows already reserves it): the first enable moves to a free port.
        var defaultBlocker = new TcpListener(IPAddress.Loopback, McpSettings.DefaultPort);
        try
        {
            defaultBlocker.Start();
        }
        catch (SocketException)
        {
        }
        int port;
        string secret;
        try
        {
            var enabled = await RunLifecycleScriptAsync(web, McpEnableScript, new { port = FreePort(), defaultPort = McpSettings.DefaultPort }, checks, token);
            secret = enabled["token"]!.GetValue<string>();
            port = enabled["port"]!.GetValue<int>();
        }
        finally
        {
            defaultBlocker.Stop();
        }
        if (port < McpSettings.FallbackPorts.Start.Value || port >= McpSettings.FallbackPorts.End.Value || mcp.Settings.Port != port)
            throw new InvalidOperationException($"The first enable did not move from the unavailable default port: {port}, settings {mcp.Settings.Port}.");
        checks.Add($"The first enable with an unavailable default port {McpSettings.DefaultPort} takes the free port {port} of the stable range and saves it");
        if (mcp.Settings.Token != secret || mcp.Status.State != "running") throw new InvalidOperationException("The page and the host disagree about the MCP server.");
        using (var stored = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, Host.HostSettings.FileName))))
        {
            var settings = stored.RootElement.GetProperty("mcp");
            if (!settings.GetProperty("enabled").GetBoolean() || settings.GetProperty("port").GetInt32() != port || settings.GetProperty("token").GetString() != secret)
                throw new InvalidOperationException("host-settings.json does not keep the MCP settings: " + settings);
        }
        checks.Add("host-settings.json keeps the MCP switch, port and token");
        var endpoint = $"http://127.0.0.1:{port}/mcp";
        await CheckMcpGuardAsync(endpoint, port, secret, checks, token);

        await using var client = await ConnectMcpAsync(endpoint, secret, token);
        var tools = await client.ListToolsAsync(cancellationToken: token);
        var names = tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        // Each area's smoke checks its own tools are listed; here the first set must be complete.
        if (McpStepOneTools.Except(names).ToArray() is { Length: > 0 } absent)
            throw new InvalidOperationException("MCP tools missing: " + string.Join(", ", absent));
        foreach (var tool in tools.Select(item => item.ProtocolTool))
        {
            if (tool.InputSchema.GetProperty("type").GetString() != "object" || tool.OutputSchema is not { } output || output.GetProperty("type").GetString() != "object"
                || string.IsNullOrWhiteSpace(tool.Description) || tool.Annotations is not { ReadOnlyHint: not null, DestructiveHint: not null, IdempotentHint: not null })
                throw new InvalidOperationException("Tool without schemas or annotations: " + tool.Name);
        }
        var byName = tools.ToDictionary(tool => tool.Name, tool => tool.ProtocolTool);
        if (byName["get_app_info"].Annotations!.ReadOnlyHint != true || byName["delete_project"].Annotations!.DestructiveHint != true
            || byName["create_project"].Annotations!.ReadOnlyHint != false || byName["create_project"].Annotations!.DestructiveHint != false)
            throw new InvalidOperationException("Tool annotations do not follow the tool classes.");
        checks.Add($"tools/list returns {names.Length} tools, the first set included, with input and output schemas and read-only, destructive and idempotent hints; no tool touches the MCP settings");

        var project = await core.Projects.GetAsync(projectId, token);
        var info = Ok(await CallAsync(client, "get_app_info", null, token), "get_app_info");
        if (info.GetProperty("access").GetProperty("level").GetString() != "full" || info.GetProperty("version").GetString() is not { Length: > 0 }
            || info.GetProperty("workDirectory").GetString() != root)
            throw new InvalidOperationException("get_app_info: " + info);
        var hardware = Ok(await CallAsync(client, "get_hardware", null, token), "get_hardware");
        if (hardware.GetProperty("cpu").GetProperty("logicalThreads").GetInt32() < 1) throw new InvalidOperationException("get_hardware: " + hardware);
        var library = Ok(await CallAsync(client, "list_projects", null, token), "list_projects");
        if (!library.GetProperty("projects").EnumerateArray().Any(item => item.GetProperty("id").GetString() == projectId))
            throw new InvalidOperationException("list_projects misses the smoke project.");
        var summary = Ok(await CallAsync(client, "get_project", new { projectId }, token), "get_project");
        if (summary.GetProperty("project").GetProperty("name").GetString() != project.Name || summary.GetProperty("overview").GetProperty("projectId").GetString() != projectId)
            throw new InvalidOperationException("get_project: " + summary);
        Error(await CallAsync(client, "get_project", new { projectId = Guid.NewGuid().ToString("D") }, token), "projects.not_found", "The project was not found");
        Error(await CallAsync(client, "get_project", new { projectId = "not-an-id" }, token), "mcp.invalid_arguments", null);
        Error(await CallAsync(client, "get_project", new { projectId, extra = 1 }, token), "mcp.invalid_arguments", null);
        checks.Add("get_app_info, get_hardware, list_projects and get_project answer with structuredContent; errors are isError with a code and the English text of the UI catalog");

        var resources = await client.ListResourcesAsync(cancellationToken: token);
        if (!resources.Any(item => item.Uri == "maut://projects") || !resources.Any(item => item.Uri == "maut://projects/" + projectId))
            throw new InvalidOperationException("resources/list: " + string.Join(", ", resources.Select(item => item.Uri)));
        var read = await client.ReadResourceAsync("maut://projects/" + projectId, cancellationToken: token);
        using (var resource = JsonDocument.Parse(((TextResourceContents)read.Contents[0]).Text))
        {
            if (resource.RootElement.GetProperty("project").GetProperty("id").GetString() != projectId) throw new InvalidOperationException("maut://projects/{id} is wrong.");
        }
        checks.Add("Resources maut://projects and maut://projects/{id} are listed and read as JSON");

        // Projects: create, change, archive and restore; the window shows a project on request.
        var created = Ok(await CallAsync(client, "create_project", new { name = "MCP smoke", baseModelId = project.BaseModelId, width = 320, height = 320 }, token),
            "create_project").GetProperty("project");
        var createdId = created.GetProperty("id").GetString()!;
        var updated = Ok(await CallAsync(client, "update_project", new { projectId = createdId, description = "From an AI client" }, token), "update_project").GetProperty("project");
        if (updated.GetProperty("description").GetString() != "From an AI client" || updated.GetProperty("name").GetString() != "MCP smoke"
            || updated.GetProperty("revision").GetInt64() <= created.GetProperty("revision").GetInt64())
            throw new InvalidOperationException("update_project: " + updated);
        Error(await CallAsync(client, "update_project", new { projectId = createdId, name = "Stale", revision = created.GetProperty("revision").GetInt64() }, token),
            "projects.revision_conflict", null);
        if (Ok(await CallAsync(client, "archive_project", new { projectId = createdId }, token), "archive_project").GetProperty("project").GetProperty("archivedAt").ValueKind == JsonValueKind.Null
            || Ok(await CallAsync(client, "archive_project", new { projectId = createdId, archived = false }, token), "archive_project").GetProperty("project").GetProperty("archivedAt").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("archive_project did not archive and restore.");
        Ok(await CallAsync(client, "open_project_in_app", new { projectId = createdId }, token), "open_project_in_app");
        await RunLifecycleScriptAsync(web, """
            await until(() => document.querySelector('[data-testid="active-project-name"]')?.textContent === 'MCP smoke', 'MCP project shown');
            assert(true, 'open_project_in_app opens the project in the window');
            return {checks};
            """, new { }, checks, token);
        checks.Add("create_project, update_project (revision conflict included) and archive_project change the library like the window does");

        await CheckMcpAccessLevelsAsync(client, mcp, createdId, checks, token);
        await CheckMcpConfirmationAsync(web, client, mcp, core, createdId, checks, token);
        await CheckMcpLanguageAsync(client, web, checks, token);
        await CheckMcpFilesAsync(client, mcp, root, checks, token);
        await CheckMcpViewAsync(client, core, root, projectId, checks, token);
        await CheckMcpJobsAsync(web, client, projectId, checks, token);
        await CheckMcpCatalogAndImagesAsync(client, core, root, projectId, checks, token);
        await CheckMcpLogAsync(web, checks, token);
        await CheckMcpPortAsync(web, mcp, endpoint, secret, checks, token);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<McpClient> ConnectMcpAsync(string endpoint, string secret, CancellationToken token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret },
            Name = "maut-smoke",
        });
        return await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "maut-smoke", Version = "1.0" } },
            cancellationToken: token);
    }

    private static async Task<CallToolResult> CallAsync(McpClient client, string tool, object? arguments, CancellationToken token,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        var values = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(arguments, McpJson).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        return await client.CallToolAsync(tool, values, progress, cancellationToken: token);
    }

    private static JsonElement Ok(CallToolResult result, string tool)
    {
        if (result.IsError == true || result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
            throw new InvalidOperationException($"{tool} failed: " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        if (!result.Content.OfType<TextContentBlock>().Any(block => block.Text.Length > 0)) throw new InvalidOperationException($"{tool} has no text for the model.");
        return structured;
    }

    /// <summary>A failed call: isError, the code and, when given, the start of the English text of the UI catalog.</summary>
    private static JsonElement Error(CallToolResult result, string code, string? message)
    {
        if (result.IsError != true || result.StructuredContent is not { } structured || structured.GetProperty("code").GetString() != code
            || (message is not null && structured.GetProperty("message").GetString()?.StartsWith(message, StringComparison.Ordinal) != true))
            throw new InvalidOperationException($"Expected error {code}: " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        return structured;
    }

    private static async Task CheckMcpGuardAsync(string endpoint, int port, string secret, JsonArray checks, CancellationToken token)
    {
        using var http = new HttpClient();
        async Task<HttpStatusCode> Post(string? authorization, string? origin = null, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (host is not null) request.Headers.Host = host;
            using var response = await http.SendAsync(request, token);
            return response.StatusCode;
        }
        if (await Post(null) != HttpStatusCode.Unauthorized || await Post("Bearer wrong-" + secret) != HttpStatusCode.Unauthorized
            || await Post(secret) != HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("A missing or wrong token must give 401.");
        checks.Add("The MCP endpoint answers 401 without the Bearer token or with a wrong one");
        if (await Post("Bearer " + secret, origin: "http://evil.example") != HttpStatusCode.Forbidden
            || await Post("Bearer " + secret, host: $"evil.example:{port}") != HttpStatusCode.Forbidden
            || await Post("Bearer " + secret, origin: "null") != HttpStatusCode.Forbidden)
            throw new InvalidOperationException("A foreign Origin or Host must give 403.");
        if (await Post("Bearer " + secret, origin: $"http://localhost:{port}") == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("A loopback Origin must pass the guard.");
        checks.Add("A foreign Origin or Host is refused with 403 even with the right token (DNS rebinding); a loopback Origin passes");
    }

    private static async Task CheckMcpAccessLevelsAsync(McpClient client, McpService mcp, string createdId, JsonArray checks, CancellationToken token)
    {
        await mcp.UpdateAsync(current => current with { AccessLevel = McpAccessLevels.ReadOnly }, token);
        Ok(await CallAsync(client, "list_projects", null, token), "list_projects");
        var denied = Error(await CallAsync(client, "create_project", new { name = "Blocked", baseModelId = "yolo26n", width = 320, height = 320 }, token), "mcp.access_denied",
            "The access level of the AI assistant does not allow this action.");
        if (denied.GetProperty("details").GetProperty("required").GetString() != McpAccessLevels.ReadWrite) throw new InvalidOperationException("access_denied details: " + denied);
        await mcp.UpdateAsync(current => current with { AccessLevel = McpAccessLevels.ReadWrite }, token);
        Ok(await CallAsync(client, "update_project", new { projectId = createdId, description = "Read and change" }, token), "update_project");
        Error(await CallAsync(client, "delete_project", new { projectId = createdId }, token), "mcp.access_denied", null);
        await mcp.UpdateAsync(current => current with { AccessLevel = McpAccessLevels.Full }, token);
        checks.Add("Access levels apply at once to a connected client: read_only blocks changes, read_write blocks deletion (mcp.access_denied), reading always works");
    }

    private static async Task CheckMcpConfirmationAsync(CoreWebView2 web, McpClient client, McpService mcp, ICore core, string createdId, JsonArray checks,
        CancellationToken token)
    {
        async Task<CallToolResult> AnswerAsync(string button)
        {
            var call = CallAsync(client, "delete_project", new { projectId = createdId }, token);
            await RunLifecycleScriptAsync(web, """
                const dialog = await until(() => document.querySelector('[data-testid="mcp-confirmation"]'), 'Confirmation');
                assert(dialog.dataset.action === 'delete_project' && dialog.textContent.includes('Удалить проект «MCP smoke»') && dialog.textContent.includes('maut-smoke')
                  && dialog.textContent.includes('delete_project'), 'The confirmation names the project, the client and the tool in Russian');
                document.querySelector('[data-testid="' + input.button + '"]').click();
                await until(() => !document.querySelector('[data-testid="mcp-confirmation"]'), 'Confirmation closed');
                return {checks};
                """, new { button }, checks, token);
            return await call;
        }
        Error(await AnswerAsync("mcp-confirmation-deny"), "mcp.confirmation_denied", "The user denied the action in the application window.");
        await core.Projects.GetAsync(createdId, token);
        checks.Add("delete_project waits for the confirmation in the window; «Отклонить» gives mcp.confirmation_denied and the project stays");

        mcp.Confirmations.SmokeTimeout = TimeSpan.FromSeconds(2);
        try
        {
            Error(await CallAsync(client, "delete_project", new { projectId = createdId }, token), "mcp.confirmation_timeout", null);
        }
        finally
        {
            mcp.Confirmations.SmokeTimeout = null;
        }
        await RunLifecycleScriptAsync(web, """
            await until(() => !document.querySelector('[data-testid="mcp-confirmation"]'), 'Expired confirmation closed');
            assert(true, 'An unanswered confirmation times out with mcp.confirmation_timeout and its dialog closes');
            return {checks};
            """, new { }, checks, token);

        var deleted = Ok(await AnswerAsync("mcp-confirmation-allow"), "delete_project");
        if (deleted.GetProperty("projectId").GetString() != createdId) throw new InvalidOperationException("delete_project: " + deleted);
        try
        {
            await core.Projects.GetAsync(createdId, token);
            throw new InvalidOperationException("The project is still there after «Разрешить».");
        }
        catch (CoreException error) when (error.Code == "projects.not_found")
        {
        }
        checks.Add("«Разрешить» lets delete_project remove the project");
    }

    private static async Task CheckMcpLanguageAsync(McpClient client, CoreWebView2 web, JsonArray checks, CancellationToken token)
    {
        var english = Ok(await CallAsync(client, "set_interface_language", new { language = "en" }, token), "set_interface_language");
        if (english.GetProperty("language").GetString() != "en") throw new InvalidOperationException("set_interface_language: " + english);
        await RunLifecycleScriptAsync(web, """
            await until(() => document.documentElement.lang === 'en' || document.querySelector('[data-testid="mcp-settings-button"]')?.getAttribute('aria-label')?.startsWith('AI assistants'), 'English page');
            assert(true, 'set_interface_language switches the window to English');
            return {checks};
            """, new { }, checks, token);
        Ok(await CallAsync(client, "set_interface_language", new { language = "ru" }, token), "set_interface_language");
        await RunLifecycleScriptAsync(web, """
            await until(() => document.querySelector('[data-testid="mcp-settings-button"]')?.getAttribute('aria-label')?.startsWith('Подключение ИИ'), 'Russian page');
            assert(true, 'and back to Russian');
            return {checks};
            """, new { }, checks, token);
    }

    private static async Task CheckMcpFilesAsync(McpClient client, McpService mcp, string root, JsonArray checks, CancellationToken token)
    {
        var allowed = Path.Combine(root, "mcp-allowed");
        Directory.CreateDirectory(Path.Combine(allowed, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(allowed, "a.png"), McpPicture(64, 48, SKColors.Teal), token);
        await File.WriteAllBytesAsync(Path.Combine(allowed, "nested", "b.jpg"), Photo(40, 30, 7), token);
        await File.WriteAllTextAsync(Path.Combine(allowed, "notes.txt"), "not an image", token);
        var flat = Ok(await CallAsync(client, "preview_folder", new { path = allowed }, token), "preview_folder");
        var deep = Ok(await CallAsync(client, "preview_folder", new { path = allowed, recursive = true }, token), "preview_folder");
        if (flat.GetProperty("matchingFiles").GetInt64() != 1 || deep.GetProperty("matchingFiles").GetInt64() != 2 || deep.GetProperty("otherFiles").GetInt64() != 1
            || !deep.GetProperty("examples").EnumerateArray().Any(item => item.GetString() == Path.Combine("nested", "b.jpg")))
            throw new InvalidOperationException("preview_folder: " + deep);
        Error(await CallAsync(client, "preview_folder", new { path = "relative\\folder" }, token), "mcp.path_invalid", null);
        Error(await CallAsync(client, "preview_folder", new { path = Path.Combine(root, "missing-folder") }, token), "mcp.path_not_found", null);
        checks.Add("preview_folder counts matching files with and without subfolders and rejects relative and missing paths");

        // The restriction: inside the allowed folder passes; outside and through a junction that leads outside is refused.
        var junction = Path.Combine(allowed, "link-out");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.Combine(root, "projects")}\"")
               { CreateNoWindow = true, UseShellExecute = false }))
        {
            await mklink!.WaitForExitAsync(token);
        }
        if (!Directory.Exists(junction)) throw new InvalidOperationException("The junction for the path check was not created.");
        Ok(await CallAsync(client, "preview_folder", new { path = Path.Combine(root, "projects") }, token), "preview_folder");
        await mcp.UpdateAsync(current => current with { RestrictFolders = true, AllowedFolders = [allowed] }, token);
        try
        {
            Ok(await CallAsync(client, "preview_folder", new { path = allowed, recursive = true }, token), "preview_folder");
            Error(await CallAsync(client, "preview_folder", new { path = Path.Combine(root, "projects") }, token), "mcp.path_not_allowed", null);
            Error(await CallAsync(client, "preview_folder", new { path = junction }, token), "mcp.path_not_allowed", null);
            Error(await CallAsync(client, "preview_folder", new { path = Path.Combine(allowed, "..", "projects") }, token), "mcp.path_not_allowed", null);
            Error(await CallAsync(client, "view", new { target = new { path = Path.Combine(root, "smoke-video", "улица.mp4") } }, token), "mcp.path_not_allowed", null);
            Ok(await CallAsync(client, "view", new { target = new { path = Path.Combine(allowed, "a.png") } }, token), "view");
        }
        finally
        {
            await mcp.UpdateAsync(current => current with { RestrictFolders = false, AllowedFolders = [] }, token);
            Directory.Delete(junction);
        }
        checks.Add("Any readable path works by default; with the restriction only allowed folders pass, «..» and a junction leading outside give mcp.path_not_allowed");

        var small = Ok(await CallAsync(client, "upload_file", new { name = "chat image.png", base64 = Convert.ToBase64String(McpPicture(80, 60, SKColors.Orange)) }, token),
            "upload_file");
        var uploadId = small.GetProperty("uploadId").GetString()!;
        var viewed = Ok(await CallAsync(client, "view", new { target = new { uploadId } }, token), "view");
        if (viewed.GetProperty("width").GetInt32() != 80 || viewed.GetProperty("kind").GetString() != "upload") throw new InvalidOperationException("view of an upload: " + viewed);
        var tooLarge = new byte[McpUploads.MaxFileBytes + 1];
        Error(await CallAsync(client, "upload_file", new { name = "big.bin", base64 = Convert.ToBase64String(tooLarge) }, token), "mcp.upload_too_large", null);
        var chunk = Convert.ToBase64String(new byte[McpUploads.MaxFileBytes]);
        for (var index = 0; index < 4; index++) Ok(await CallAsync(client, "upload_file", new { name = $"part{index}.bin", base64 = chunk }, token), "upload_file");
        Error(await CallAsync(client, "upload_file", new { name = "part5.bin", base64 = chunk }, token), "mcp.upload_quota_exceeded", null);
        Error(await CallAsync(client, "upload_file", new { name = "bad.png", base64 = "@@@" }, token), "mcp.invalid_arguments", null);
        var stored = Directory.GetFiles(mcp.Uploads.Directory, "*", SearchOption.AllDirectories);
        if (stored.Length != 5 || !stored.Any(file => Path.GetFileName(file) == "chat image.png")) throw new InvalidOperationException("Uploads: " + string.Join(", ", stored));
        checks.Add("upload_file stores a file under mcp-uploads and view shows it by uploadId; over 10 MB per file and 50 MB per session are refused");
    }

    private static async Task CheckMcpViewAsync(McpClient client, ICore core, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        var project = await core.Projects.PrepareAsync(projectId, token);
        ProjectImage? annotated = null;
        foreach (var group in await project.Images.ListGroupsAsync(token))
        {
            var page = await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 200, null, token);
            annotated = page.Items.FirstOrDefault(image => image.ObjectCount > 0);
            if (annotated is not null) break;
        }
        if (annotated is null) throw new InvalidOperationException("The smoke project has no annotated image for view.");
        var annotation = await project.Images.GetAnnotationAsync(annotated.Id, null, token);

        var whole = await CallAsync(client, "view", new { projectId, target = new { image = annotated.Id } }, token);
        var structured = Ok(whole, "view");
        var picture = DecodeMcpImage(whole);
        if (structured.GetProperty("boxes").GetArrayLength() != annotation.Objects.Count || structured.GetProperty("width").GetInt32() != annotated.Width
            || picture.Width != structured.GetProperty("picture").GetProperty("width").GetInt32() || Math.Max(picture.Width, picture.Height) > 1024)
            throw new InvalidOperationException("view of an image: " + structured);
        var zoomed = await CallAsync(client, "view", new
        {
            projectId, target = new { image = annotated.Id }, overlay = true, region = new { x = 0.25, y = 0.25, width = 0.5, height = 0.5 }, maxSize = 512, format = "png",
        }, token);
        var zoom = Ok(zoomed, "view");
        var zoomPicture = DecodeMcpImage(zoomed, "image/png");
        var expectedWidth = (int)Math.Round(annotated.Width * 0.5 * Math.Min(512.0 / Math.Max(annotated.Width * 0.5, annotated.Height * 0.5), 8));
        if (Math.Abs(zoomPicture.Width - expectedWidth) > 1 || zoom.GetProperty("region").GetProperty("width").GetDouble() != 0.5 || Math.Max(zoomPicture.Width, zoomPicture.Height) > 512)
            throw new InvalidOperationException($"view with region: {zoomPicture.Width}×{zoomPicture.Height}, expected width {expectedWidth}: " + zoom);
        // The overlay draws over the pixels: the same region without boxes differs where a box is.
        var bare = DecodeMcpImage(await CallAsync(client, "view", new
        {
            projectId, target = new { image = annotated.Id }, overlay = false, region = new { x = 0.25, y = 0.25, width = 0.5, height = 0.5 }, maxSize = 512, format = "png",
        }, token), "image/png");
        if (!Differs(bare, zoomPicture) && annotation.Objects.Any(item => Overlaps(item.Box, 0.25, 0.25, 0.5, 0.5)))
            throw new InvalidOperationException("overlay drew nothing inside the region.");
        Error(await CallAsync(client, "view", new { projectId, target = new { image = annotated.Id }, region = new { x = 0.8, y = 0, width = 0.5, height = 1 } }, token),
            "mcp.invalid_arguments", null);
        checks.Add("view shows a frame as JPEG with its boxes in structuredContent; overlay, region (zoom) and maxSize with PNG give the expected picture");

        var targets = new List<object> { new { image = annotated.Id } };
        var workspace = await project.Classes.GetWorkspaceAsync(token);
        if (workspace.ClassVariants.FirstOrDefault(item => item.AssetCount > 0) is { } binding
            && (await project.Classes.ListAssetsAsync(binding.Id, 1, null, token)).Items.FirstOrDefault() is { } asset)
            targets.Add(new { classAsset = asset.Id });
        foreach (var set in await project.Backgrounds.ListSetsAsync(token))
        {
            if ((await project.Backgrounds.ListItemsAsync(set.Id, new BackgroundFilter(), 1, null, token)).Items.FirstOrDefault() is { } background)
            {
                targets.Add(new { background = background.Id });
                break;
            }
        }
        if ((await project.Datasets.ListAsync(false, token)).FirstOrDefault(item => item.Counts.Train > 0) is { } dataset)
            targets.Add(new { datasetSample = dataset.Id, split = "train", index = 0 });
        if ((await project.Generation.ListRecipesAsync(token)).FirstOrDefault(item => item.ArchivedAt is null) is { } recipe)
            targets.Add(new { generationSample = recipe.Id, sampleIndex = 1, seed = 7 });
        var runs = await project.Training.ListRunsAsync(50, null, token);
        foreach (var run in runs.Items.Where(item => item.ResultVersionId is not null))
        {
            var version = await project.Training.GetVersionAsync(run.ResultVersionId!.Value, token);
            if (version.Files.FirstOrDefault(file => file.Kind == ModelFileKind.Plot && file.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) is { } plot)
            {
                targets.Add(new { trainingPlot = Path.GetFileName(plot.Name), runId = run.Id });
                break;
            }
        }
        if ((await project.Checks.ListChecksAsync(token)).FirstOrDefault(item => item.Preview is not null) is { } check) targets.Add(new { check = check.Id });
        targets.Add(new { videoFrame = Path.Combine(root, "smoke-video", "улица.mp4"), timeSeconds = 1.0 });
        targets.Add(new { path = Path.Combine(root, "mcp-allowed", "a.png") });
        var kinds = new List<string>();
        foreach (var target in targets)
        {
            var result = await CallAsync(client, "view", new { projectId, target, maxSize = 256 }, token);
            var value = Ok(result, "view " + JsonSerializer.Serialize(target));
            DecodeMcpImage(result);
            kinds.Add(value.GetProperty("kind").GetString()!);
        }
        string[] required = ["image", "classAsset", "background", "datasetSample", "generationSample", "videoFrame", "path"];
        if (required.Except(kinds).ToArray() is { Length: > 0 } missing) throw new InvalidOperationException("view did not cover: " + string.Join(", ", missing));
        checks.Add("view covers the targets of the project: " + string.Join(", ", kinds));

        var grid = await CallAsync(client, "view_many", new
        {
            projectId, targets = targets.Take(3).Append(new { image = Guid.NewGuid() }).ToArray(), maxSize = 800,
        }, token);
        var many = Ok(grid, "view_many");
        var items = many.GetProperty("items").EnumerateArray().ToArray();
        var gridPicture = DecodeMcpImage(grid);
        if (items.Length != 4 || items[0].GetProperty("number").GetInt32() != 1 || items[3].GetProperty("error").GetProperty("code").GetString() != "images.image_not_found"
            || items[1].TryGetProperty("error", out var failure) && failure.ValueKind != JsonValueKind.Null || gridPicture.Width > 800 || gridPicture.Width < 300)
            throw new InvalidOperationException("view_many: " + many);
        Error(await CallAsync(client, "view_many", new { projectId, targets = Enumerable.Repeat(new { image = annotated.Id }, 9).ToArray() }, token), "mcp.invalid_arguments", null);
        checks.Add("view_many draws up to 8 targets as one numbered grid; a failed target keeps its number with its error, more than 8 are refused");
    }

    private static SKBitmap DecodeMcpImage(CallToolResult result, string mimeType = "image/jpeg")
    {
        var image = result.Content.OfType<ImageContentBlock>().SingleOrDefault() ?? throw new InvalidOperationException("No image content.");
        if (image.MimeType != mimeType) throw new InvalidOperationException("Image type " + image.MimeType);
        return SKBitmap.Decode(image.DecodedData.ToArray()) ?? throw new InvalidOperationException("The image does not decode.");
    }

    private static bool Differs(SKBitmap left, SKBitmap right)
    {
        if (left.Width != right.Width || left.Height != right.Height) return true;
        for (var y = 0; y < left.Height; y += 2)
        {
            for (var x = 0; x < left.Width; x += 2)
            {
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return true;
            }
        }
        return false;
    }

    private static bool Overlaps(ImageBox box, double x, double y, double width, double height)
        => box.X < x + width && box.X + box.Width > x && box.Y < y + height && box.Y + box.Height > y;

    private static byte[] McpPicture(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Jobs through MCP: a slow training run is followed by wait_job with a progressToken (notifications/progress grow to the end), a hanging
    /// run makes wait_job time out without stopping it, cancel_job stops it, and list_jobs and get_job see both.
    /// </summary>
    private static async Task CheckMcpJobsAsync(CoreWebView2 web, McpClient client, string projectId, JsonArray checks, CancellationToken token)
    {
        var started = await RunLifecycleScriptAsync(web, """
            const {projectId} = input;
            const datasets = (await request('datasets.list', {projectId})).data.filter(item => !item.archivedAt && item.inputSizeMatches);
            const dataset = datasets.find(item => item.name === 'Проверка smoke') ?? datasets.find(item => item.name.startsWith('fake:')) ?? datasets[0];
            assert(dataset, 'A dataset for MCP jobs');
            const rename = async name => {
              const current = await request('datasets.get', {projectId, versionId: dataset.id});
              const renamed = await request('datasets.update', {projectId, versionId: dataset.id, name, description: current.data.description, revision: current.data.revision});
              if (!renamed.ok) throw new Error(JSON.stringify(renamed.error));
            };
            const original = dataset.name;
            const start = async (operationId) => {
              const run = await request('training.startRun', {projectId, datasetVersionId: dataset.id, parameters: {epochs: 8, batch: 4, device: 'cpu'}, presetName: null, operationId});
              if (!run.ok) throw new Error(JSON.stringify(run.error));
              return run.data.id;
            };
            await rename('fake:slow');
            const slow = await start('mcp-smoke-slow');
            return {checks, slow, datasetId: dataset.id, original};
            """, new { projectId }, checks, token);
        var slowId = started["slow"]!.GetValue<string>();
        var notifications = new ConcurrentQueue<ProgressNotificationValue>();
        var waited = Ok(await CallAsync(client, "wait_job", new { projectId, jobId = slowId, timeoutSeconds = 90 }, token, new RecordingProgress(notifications)), "wait_job");
        var values = notifications.Select(item => item.Progress).ToArray();
        if (waited.GetProperty("job").GetProperty("status").GetString() != "succeeded" || waited.GetProperty("timedOut").GetBoolean() || values.Length < 3
            || values.Zip(values.Skip(1)).Any(pair => pair.Second <= pair.First) || values[^1] != 100)
            throw new InvalidOperationException($"wait_job with progress: {waited}; progress {string.Join(", ", values)}");
        checks.Add($"wait_job with a progressToken streams growing notifications/progress from job.changed ({values.Length} for a slow run) and returns the finished job");

        var hang = await RunLifecycleScriptAsync(web, """
            const {projectId, datasetId} = input;
            const current = await request('datasets.get', {projectId, versionId: datasetId});
            await request('datasets.update', {projectId, versionId: datasetId, name: 'fake:hang', description: current.data.description, revision: current.data.revision});
            const run = await request('training.startRun', {projectId, datasetVersionId: datasetId, parameters: {epochs: 2, batch: 4, device: 'cpu'}, presetName: null, operationId: 'mcp-smoke-hang'});
            if (!run.ok) throw new Error(JSON.stringify(run.error));
            return {checks, id: run.data.id};
            """, new { projectId, datasetId = started["datasetId"]!.GetValue<string>() }, checks, token);
        var hangId = hang["id"]!.GetValue<string>();
        var stopwatch = Stopwatch.StartNew();
        var timedOut = Ok(await CallAsync(client, "wait_job", new { projectId, jobId = hangId, timeoutSeconds = 2 }, token), "wait_job");
        if (!timedOut.GetProperty("timedOut").GetBoolean() || timedOut.GetProperty("finished").GetBoolean() || stopwatch.Elapsed > TimeSpan.FromSeconds(15))
            throw new InvalidOperationException("wait_job timeout: " + timedOut);
        Error(await CallAsync(client, "wait_job", new { projectId, jobId = hangId, timeoutSeconds = 121 }, token), "mcp.invalid_arguments", null);
        var active = Ok(await CallAsync(client, "list_jobs", new { projectId, active = true }, token), "list_jobs");
        if (!active.GetProperty("jobs").EnumerateArray().Any(item => item.GetProperty("id").GetString() == hangId)) throw new InvalidOperationException("list_jobs active: " + active);
        var cancelled = Ok(await CallAsync(client, "cancel_job", new { projectId, jobId = hangId }, token), "cancel_job");
        if (!cancelled.GetProperty("job").GetProperty("cancelRequested").GetBoolean()) throw new InvalidOperationException("cancel_job: " + cancelled);
        var end = Ok(await CallAsync(client, "wait_job", new { projectId, jobId = hangId, timeoutSeconds = 60 }, token), "wait_job");
        if (end.GetProperty("job").GetProperty("status").GetString() != "cancelled") throw new InvalidOperationException("wait_job after cancel: " + end);
        var job = Ok(await CallAsync(client, "get_job", new { projectId, jobId = slowId }, token), "get_job");
        var history = Ok(await CallAsync(client, "list_jobs", new { projectId, limit = 5 }, token), "list_jobs");
        if (job.GetProperty("job").GetProperty("status").GetString() != "succeeded"
            || !history.GetProperty("jobs").EnumerateArray().Any(item => item.GetProperty("id").GetString() == hangId))
            throw new InvalidOperationException("get_job/list_jobs: " + history);
        Error(await CallAsync(client, "get_job", new { projectId, jobId = Guid.NewGuid() }, token), "jobs.not_found", null);
        await RunLifecycleScriptAsync(web, """
            const {projectId, datasetId, original} = input;
            const current = await request('datasets.get', {projectId, versionId: datasetId});
            const renamed = await request('datasets.update', {projectId, versionId: datasetId, name: original, description: current.data.description, revision: current.data.revision});
            assert(renamed.ok, 'The dataset name is restored after the MCP jobs');
            return {checks};
            """, new { projectId, datasetId = started["datasetId"]!.GetValue<string>(), original = started["original"]!.GetValue<string>() }, checks, token);
        checks.Add("wait_job returns timedOut after its timeout and the job keeps running (at most 120 s); cancel_job stops it; get_job and list_jobs see the runs");
    }

    private static async Task CheckMcpLogAsync(CoreWebView2 web, JsonArray checks, CancellationToken token)
        => await RunLifecycleScriptAsync(web, """
            const calls = await request('mcp.listCalls', {limit: 500});
            const odd = calls.ok ? calls.data.filter(call => call.client !== 'maut-smoke'
              || (call.tool === 'upload_file' && (call.arguments.length > 300 || !/"base64":"<\d+ characters>"/.test(call.arguments)))) : [];
            const logged = calls.ok && calls.data.length > 30 && odd.length === 0 && calls.data.some(call => call.tool === 'delete_project' && call.error === 'mcp.confirmation_denied');
            assert(logged, 'The call log keeps client, tool, arguments without base64 and the outcome of every call'
              + (logged ? '' : ': ' + (calls.ok ? calls.data.length + ' ' + JSON.stringify(odd.slice(0, 2)).slice(0, 600) : JSON.stringify(calls.error))));
            const dialog = await until(() => document.querySelector('[data-testid="mcp-settings-dialog"]'), 'MCP dialog still open');
            await until(() => dialog.querySelector('[data-testid="mcp-call-log"] [data-tool="view_many"]'), 'Calls in the dialog');
            assert(true, 'The settings dialog shows the calls as they happen (mcp.callAdded)');
            return {checks};
            """, new { }, checks, token);

    /// <summary>A busy port is reported in the dialog and never replaced silently; turning the server off closes the endpoint.</summary>
    private static async Task CheckMcpPortAsync(CoreWebView2 web, McpService mcp, string endpoint, string secret, JsonArray checks, CancellationToken token)
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            await mcp.UpdateAsync(current => current with { Port = busy }, token);
            if (mcp.Status is not { State: "port_busy" } status || status.Port != busy || mcp.Settings.Port != busy)
                throw new InvalidOperationException("A busy port is not reported: " + mcp.Status);
            await RunLifecycleScriptAsync(web, """
                await until(() => document.querySelector('[data-testid="mcp-status"]')?.dataset.status === 'port_busy'
                  && document.querySelector('[data-testid="mcp-status"]').textContent.includes(String(input.busy)), 'Busy port shown');
                assert(true, 'A busy port is shown in the dialog with its number');
                return {checks};
                """, new { busy }, checks, token);
        }
        finally
        {
            blocker.Stop();
        }
        var port = FreePort();
        await mcp.UpdateAsync(current => current with { Port = port }, token);
        if (mcp.Status.State != "running") throw new InvalidOperationException("The server did not move to a free port: " + mcp.Status);
        await using (var moved = await ConnectMcpAsync($"http://127.0.0.1:{port}/mcp", secret, token))
        {
            Ok(await CallAsync(moved, "list_projects", null, token), "list_projects");
        }
        checks.Add("Changing the port restarts the server at runtime without restarting the application");
        await RunLifecycleScriptAsync(web, """
            document.querySelector('[data-testid="mcp-enabled"]').click();
            await until(() => document.querySelector('[data-testid="mcp-status"]')?.dataset.status === 'stopped', 'Server off');
            document.dispatchEvent(new KeyboardEvent('keydown', {key: 'Escape', bubbles: true}));
            await until(() => !document.querySelector('[data-testid="mcp-settings-dialog"]'), 'Dialog closed');
            assert(document.querySelector('[data-testid="mcp-settings-button"]').dataset.running === 'false', 'The switch stops the server');
            return {checks};
            """, new { }, checks, token);
        using var http = new HttpClient();
        try
        {
            await http.PostAsync($"http://127.0.0.1:{port}/mcp", new StringContent("{}"), token);
            throw new InvalidOperationException("The endpoint still answers after the server was turned off.");
        }
        catch (HttpRequestException)
        {
        }
        if (Directory.Exists(mcp.Uploads.Directory)) throw new InvalidOperationException("Uploads were not removed when the server stopped.");
        checks.Add("Turning the server off closes the endpoint and removes the uploads");
    }

    /// <summary>Records progress synchronously; Progress&lt;T&gt; would post to the UI thread the smoke runs on.</summary>
    private sealed class RecordingProgress(ConcurrentQueue<ProgressNotificationValue> values) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => values.Enqueue(value);
    }

    private const string McpEnableScript = """
        const {port, defaultPort} = input;
        const $ = (selector, scope = document) => scope.querySelector(selector);
        const before = await request('mcp.getState', {});
        assert(before.ok && before.data.enabled === false && before.data.status === 'stopped' && before.data.accessLevel === 'full'
          && before.data.restrictFolders === false && before.data.launchOnRequest === true && before.data.token === null,
          'MCP is off on the first run with full access, no folder restriction, launch on request and no token yet');
        const button = await until(() => $('[data-testid="mcp-settings-button"]'), 'MCP button');
        assert(button.getAttribute('aria-label') === 'Подключение ИИ (MCP)' && button.dataset.running === 'false', 'The header offers «Подключение ИИ (MCP)» next to the language');
        button.click();
        const dialog = await until(() => $('[data-testid="mcp-settings-dialog"]'), 'MCP dialog');
        await until(() => $('[data-testid="mcp-status"]', dialog), 'MCP state');
        assert(dialog.textContent.includes('Подключение ИИ-ассистентов (MCP)') && $('[data-testid="mcp-status"]', dialog).dataset.status === 'stopped'
          && dialog.textContent.includes('передаются поставщику модели'), 'The settings dialog opens with the server off and the privacy note');
        const changed = await request('mcp.update', {port});
        assert(changed.ok && changed.data.port === port && changed.data.status === 'stopped', 'The port is saved while the server is off');
        const invalid = await request('mcp.update', {port: 80});
        assert(!invalid.ok && invalid.error.code === 'bridge.validation', 'A port below 1024 is refused');
        const restored = await request('mcp.update', {port: defaultPort});
        assert(restored.ok && restored.data.port === defaultPort, 'The default port is chosen again before the first enable');
        $('[data-testid="mcp-enabled"]', dialog).click();
        await until(() => $('[data-testid="mcp-status"]', dialog)?.dataset.status === 'running', 'Server running');
        const running = await request('mcp.getState', {});
        const chosen = running.data.port;
        assert(chosen !== defaultPort && running.data.endpoint === `http://127.0.0.1:${chosen}/mcp`, 'The first enable leaves the unavailable default port');
        assert(running.data.token && running.data.token.length >= 43
          && $('[data-testid="mcp-status"]', dialog).textContent.includes(running.data.endpoint), 'The switch starts the server on 127.0.0.1 with a generated token');
        await until(() => $('[data-testid="mcp-copy-claudeDesktop"]', dialog), 'Configurations');
        assert($('[data-testid="mcp-copy-claudeCode"]', dialog) && $('[data-testid="mcp-copy-mcpJson"]', dialog) && !dialog.textContent.includes(running.data.token)
          && button.dataset.running === 'true', 'Ready configurations for Claude Code and Claude Desktop are offered with the token masked; the header shows the running server');
        return {checks, token: running.data.token, port: chosen};
        """;
}
