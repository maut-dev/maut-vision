using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// The end-to-end run through MCP only (--mcp): the installed application with its packaged worker, driven as an AI client drives it. MCP is
/// turned on in host-settings.json before the start; the stdio proxy maut-vision-mcp.exe starts the closed application in the tray and serves a
/// session; the official C# client over Streamable HTTP runs the chain project → classes and images → backgrounds → recipe → generation →
/// dataset plan, build and verification → training on the GPU → model version → evaluation (through the proxy, with progress) → image check
/// with its picture → model ZIP → view_many. The proxy's refusals (MCP off, start on request off) are checked first.
/// </summary>
internal static class McpRun
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly List<(string Step, TimeSpan Elapsed, string Detail)> Timings = [];

    internal static async Task<int> RunAsync(Arguments arguments)
    {
        var appDir = Path.GetFullPath(arguments.AppDir);
        var work = Path.GetFullPath(arguments.WorkDirectory);
        if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
        {
            Console.Error.WriteLine($"The work directory {work} is not empty.");
            return 2;
        }
        var appWork = Directory.CreateDirectory(Path.Combine(work, "app")).FullName;
        var inputs = Directory.CreateDirectory(Path.Combine(work, "inputs")).FullName;
        var exports = Directory.CreateDirectory(Path.Combine(work, "exports")).FullName;
        var proxy = Path.Combine(appDir, "maut-vision-mcp.exe");
        var device = arguments.Device;
        var total = Stopwatch.StartNew();
        var failed = false;
        Console.WriteLine($"E2E MCP: application {appDir}");
        Console.WriteLine($"E2E MCP: work directory {appWork}");

        McpClient? viaProxy = null;
        McpClient? http = null;
        Process? app = null;
        var proxyErrors = new List<string>();
        try
        {
            var port = FreePort();
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await Step("Proxy refusals", async () =>
            {
                if (!File.Exists(proxy)) throw new E2EException($"The installed application has no {proxy}.");
                var off = Directory.CreateDirectory(Path.Combine(work, "proxy-off")).FullName;
                WriteSettings(off, enabled: false, port, token, launchOnRequest: true);
                var disabled = await RunProxyAsync(proxy, off);
                var noLaunch = Directory.CreateDirectory(Path.Combine(work, "proxy-no-launch")).FullName;
                WriteSettings(noLaunch, enabled: true, port, token, launchOnRequest: false);
                var notRunning = await RunProxyAsync(proxy, noLaunch);
                if (disabled.ExitCode != 1 || !disabled.Error.Contains("turned off") || notRunning.ExitCode != 1 || !notRunning.Error.Contains("not running"))
                    throw new E2EException($"MCP off: {disabled.ExitCode} {disabled.Error}; no start on request: {notRunning.ExitCode} {notRunning.Error}");
                return $"MCP off → exit 1 «{disabled.Error.Trim()}»; start on request off → exit 1";
            });

            await Step("Settings: MCP on, base model seeded", async () =>
            {
                WriteSettings(appWork, enabled: true, port, token, launchOnRequest: true);
                if (arguments.BaseModelWeights is not { } seed) return $"port {port}, base model downloaded by install_base_model";
                var weights = Directory.CreateDirectory(Path.Combine(appWork, "BaseModels", "weights")).FullName;
                File.Copy(seed, Path.Combine(weights, "yolo26n.pt"));
                await Task.CompletedTask;
                return $"port {port}, yolo26n.pt seeded";
            });

            await Step("Proxy starts the closed application in the tray", async () =>
            {
                if (FindApp(appDir) is not null) throw new E2EException("The application is already running.");
                var watch = Stopwatch.StartNew();
                viaProxy = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = proxy,
                    Arguments = ["--work-directory", appWork],
                    Name = "maut-vision-mcp",
                    StandardErrorLines = line =>
                    {
                        lock (proxyErrors) proxyErrors.Add(line);
                    },
                }), new McpClientOptions { ClientInfo = new Implementation { Name = "e2e-proxy", Version = "1.0" } });
                var connected = watch.Elapsed;
                app = FindApp(appDir) ?? throw new E2EException("The proxy answered, yet no application process runs.");
                app.Refresh();
                if (app.MainWindowHandle != IntPtr.Zero) throw new E2EException("The application started by the proxy shows a window.");
                var tools = await viaProxy.ListToolsAsync();
                var info = Ok(await viaProxy.CallToolAsync("get_app_info", new Dictionary<string, object?>()), "get_app_info");
                if (info.GetProperty("workDirectory").GetString() != appWork) throw new E2EException("get_app_info: " + info);
                var resources = await viaProxy.ListResourcesAsync();
                var prompts = await viaProxy.ListPromptsAsync();
                var prompt = await viaProxy.GetPromptAsync("prepare_dataset_from_folder", new Dictionary<string, object?> { ["folder"] = inputs });
                if (tools.Count < 90 || !resources.Any(item => item.Uri == "maut://projects") || prompts.Count < 4 || prompt.Messages.Count == 0)
                    throw new E2EException($"Through the proxy: {tools.Count} tools, {resources.Count} resources, {prompts.Count} prompts.");
                var picture = Path.Combine(inputs, "proxy-view.png");
                Assets.WriteBackground(picture, 64, 48, 5);
                var view = await viaProxy.CallToolAsync("view", Args(new { target = new { path = picture } }));
                Ok(view, "view");
                if (Image(view) is not { Length: > 0 }) throw new E2EException("view through the proxy returned no image content.");
                return $"connected in {connected.TotalSeconds:N1} s (application start included), no window; {tools.Count} tools, {resources.Count} resources, "
                    + $"{prompts.Count} prompts, view with image content";
            });

            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            http = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                Name = "e2e-http",
            }), new McpClientOptions { ClientInfo = new Implementation { Name = "e2e-http", Version = "1.0" } });
            var client = http;

            await Step("Base model yolo26n", async () =>
            {
                var installed = await CallAsync(client, "install_base_model", new { modelId = "yolo26n", waitSeconds = 120 });
                var status = installed.GetProperty("model").GetProperty("status").GetString();
                if (status != "installed") throw new E2EException("install_base_model: " + installed);
                return "installed";
            });

            string projectId = "";
            await Step("Project", async () =>
            {
                var project = (await CallAsync(client, "create_project", new { name = "Сквозной MCP", description = "E2E", baseModelId = "yolo26n", width = 320, height = 320 }))
                    .GetProperty("project");
                projectId = project.GetProperty("id").GetString()!;
                return $"{projectId}, input 320×320";
            });

            var classIds = new List<string>();
            await Step("Variant, classes and images (import_class_assets)", async () =>
            {
                var variantId = (await CallAsync(client, "create_variant", new { projectId, name = "День" })).GetProperty("variant").GetProperty("id").GetString();
                string[] names = ["Круг", "Треугольник", "Квадрат"];
                var added = 0;
                for (var index = 0; index < names.Length; index++)
                {
                    var classId = (await CallAsync(client, "create_class", new { projectId, name = names[index] })).GetProperty("class").GetProperty("id").GetString()!;
                    classIds.Add(classId);
                    var bindingId = (await CallAsync(client, "bind_class_variant", new { projectId, classId, variantId })).GetProperty("classVariant").GetProperty("id").GetString();
                    var files = Enumerable.Range(0, 2).Select(variant =>
                    {
                        var path = Path.Combine(inputs, $"object-{index}-{variant}.png");
                        Assets.WriteObject(path, index, variant);
                        return path;
                    }).ToArray();
                    var job = await FinishedAsync(client, projectId, await CallAsync(client, "import_class_assets", new { projectId, classVariantId = bindingId, paths = files, waitSeconds = 60 }));
                    added += job.GetProperty("result").GetProperty("added").GetInt32();
                }
                if (added != 6) throw new E2EException($"{added} of 6 images added.");
                return $"3 classes × 1 variant, {added} images";
            });

            string setId = "";
            await Step("Background set (import_backgrounds)", async () =>
            {
                setId = (await CallAsync(client, "create_background_set", new { projectId, name = "Синтетические фоны" })).GetProperty("set").GetProperty("id").GetString()!;
                var folder = Directory.CreateDirectory(Path.Combine(inputs, "backgrounds")).FullName;
                for (var index = 0; index < 8; index++) Assets.WriteBackground(Path.Combine(folder, $"background-{index}.png"), 320, 320, 100 + index);
                var photo = Path.Combine(inputs, "photo.jpg");
                Assets.WriteBackground(photo, 800, 600, 999, SkiaSharp.SKEncodedImageFormat.Jpeg);
                var whole = await FinishedAsync(client, projectId, await CallAsync(client, "import_backgrounds", new { projectId, setId, path = folder, waitSeconds = 60 }));
                var crop = await FinishedAsync(client, projectId, await CallAsync(client, "import_backgrounds", new
                {
                    projectId, setId, paths = new[] { photo }, mode = "crop", crops = new[] { new { sourceIndex = 0, left = 200, top = 120 } }, waitSeconds = 60,
                }));
                var added = whole.GetProperty("result").GetProperty("added").GetInt32() + crop.GetProperty("result").GetProperty("added").GetInt32();
                if (added != 9) throw new E2EException($"{added} of 9 backgrounds added.");
                return $"{added} backgrounds (8 whole from a folder, 1 cropped)";
            });

            string groupId = "";
            await Step("Recipe and generation (160 frames)", async () =>
            {
                var recipeId = (await CallAsync(client, "create_recipe", new
                {
                    projectId, name = "Сквозной", backgroundSetIds = new[] { setId }, objectsPerImage = 3, emptyProbability = 0.05, scale = 0.35,
                })).GetProperty("recipe").GetProperty("id").GetString();
                var started = await CallAsync(client, "start_generation", new { projectId, recipeId, count = 160, seed = 42, waitSeconds = 120 });
                var job = await FinishedAsync(client, projectId, started);
                groupId = started.GetProperty("run").GetProperty("outputGroupId").GetString() ?? throw new E2EException("No output group: " + started);
                var produced = job.GetProperty("result").GetProperty("producedCount").GetInt32();
                if (produced != 160) throw new E2EException($"Produced {produced} of 160.");
                return $"{produced} frames into group {groupId}";
            });

            string datasetId = "";
            await Step("Dataset plan, build and verification", async () =>
            {
                var plan = (await CallAsync(client, "plan_dataset", new { projectId, name = "Сквозной", groupIds = new[] { groupId }, classIds, valRatio = 0.2, testRatio = 0.15, seed = 7 }))
                    .GetProperty("plan");
                if (!plan.GetProperty("buildable").GetBoolean()) throw new E2EException("Not buildable: " + plan);
                var built = await CallAsync(client, "build_dataset", new { projectId, planId = plan.GetProperty("id").GetString(), waitSeconds = 120 });
                await FinishedAsync(client, projectId, built);
                datasetId = built.GetProperty("versionId").GetString()!;
                var verified = await FinishedAsync(client, projectId, await CallAsync(client, "verify_dataset", new { projectId, versionId = datasetId, waitSeconds = 120 }));
                var counts = plan.GetProperty("counts");
                if (verified.GetProperty("result").GetProperty("issueCount").GetInt32() != 0) throw new E2EException("Verification: " + verified);
                return $"train {counts.GetProperty("train")} / val {counts.GetProperty("val")} / test {counts.GetProperty("test")}, verified without issues";
            });

            string versionId = "";
            await Step("Training (2 epochs, imgsz 320, packaged worker)", async () =>
            {
                var started = await CallAsync(client, "start_training", new
                {
                    projectId, datasetVersionId = datasetId, parameters = new { epochs = 2, batch = 8, device }, waitSeconds = 120,
                });
                var job = await FinishedAsync(client, projectId, started, TimeSpan.FromMinutes(30));
                var runId = started.GetProperty("runId").GetString();
                var run = (await CallAsync(client, "get_training_run", new { projectId, runId, maxEpochs = 2 })).GetProperty("run");
                var versions = (await CallAsync(client, "list_model_versions", new { projectId })).GetProperty("versions");
                if (versions.GetArrayLength() != 1) throw new E2EException("list_model_versions: " + versions);
                versionId = versions[0].GetProperty("id").GetString()!;
                var version = (await CallAsync(client, "get_model_version", new { projectId, versionId })).GetProperty("version");
                var input = version.TryGetProperty("input", out var size) ? size.ToString() : "?";
                if (run.GetProperty("state").GetString() is not ("succeeded" or "Succeeded")) throw new E2EException("Run: " + run.GetProperty("state"));
                return $"run {runId} {job.GetProperty("status")}, input {input}, version {version.GetProperty("number")}, "
                    + $"mAP50 {(versions[0].GetProperty("metrics").TryGetProperty("mAP50", out var map) ? map.GetDouble().ToString("N3") : "?")}";
            });

            await Step("Evaluation on Test through the proxy (progress)", async () =>
            {
                var notifications = 0;
                var progress = new Progress<ProgressNotificationValue>(_ => Interlocked.Increment(ref notifications));
                var result = await viaProxy!.CallToolAsync("evaluate_model",
                    Args(new { projectId, versionId, datasetVersionId = datasetId, split = "test", device, waitSeconds = 120 }), progress);
                var started = Ok(result, "evaluate_model");
                var job = await FinishedAsync(viaProxy!, projectId, started, TimeSpan.FromMinutes(10));
                var evaluation = (await CallAsync(client, "list_evaluations", new { projectId, versionId, split = "test" })).GetProperty("evaluations")[0];
                await Task.Delay(200);
                if (notifications == 0) throw new E2EException("No progress notifications reached the client through the proxy.");
                return $"{job.GetProperty("status")}, {notifications} progress notifications through the proxy, "
                    + $"mAP50 {(evaluation.GetProperty("metrics").TryGetProperty("mAP50", out var map) ? map.GetDouble().ToString("N3") : "?")}";
            });

            await Step("Image check (predict_image)", async () =>
            {
                var source = Directory.EnumerateFiles(Path.Combine(appWork, "projects", projectId, "files", "datasets", datasetId, "images", "test"))
                    .Order(StringComparer.Ordinal).First();
                var image = Path.Combine(inputs, "check" + Path.GetExtension(source));
                File.Copy(source, image);
                var result = await client.CallToolAsync("predict_image", Args(new { projectId, versionId, path = image, confidence = 0.001, device, waitSeconds = 120 }));
                var structured = Ok(result, "predict_image");
                var picture = Image(result) ?? throw new E2EException("predict_image returned no image content.");
                var detections = structured.GetProperty("detections").GetArrayLength();
                if (!structured.GetProperty("finished").GetBoolean() || detections == 0) throw new E2EException("predict_image: " + structured);
                return $"{detections} detections, picture {picture.Length:N0} bytes";
            });

            await Step("Model export to disk", async () =>
            {
                var archive = Path.Combine(exports, "model.zip");
                await FinishedAsync(client, projectId, await CallAsync(client, "export_model", new { projectId, versionId, destinationPath = archive, waitSeconds = 120 }));
                using var zip = ZipFile.OpenRead(archive);
                foreach (var required in new[] { "weights/best.pt", "weights/best.onnx", "manifest.json" })
                    if (zip.GetEntry(required) is null) throw new E2EException($"The model archive has no {required}.");
                return $"{new FileInfo(archive).Length:N0} bytes, {zip.Entries.Count} entries";
            });

            await Step("view_many", async () =>
            {
                var result = await client.CallToolAsync("view_many", Args(new
                {
                    projectId, overlay = true,
                    targets = new object[]
                    {
                        new { datasetSample = datasetId, split = "train", index = 0 }, new { datasetSample = datasetId, split = "test", index = 0 },
                        new { background = (await CallAsync(client, "list_backgrounds", new { projectId, setId, limit = 1 })).GetProperty("backgrounds")[0].GetProperty("id").GetString() },
                        new { path = Path.Combine(inputs, "object-0-0.png") },
                    },
                }));
                var structured = Ok(result, "view_many");
                var picture = Image(result) ?? throw new E2EException("view_many returned no image.");
                return $"4 targets in one grid, {picture.Length:N0} bytes; {structured.GetProperty("items").GetArrayLength()} items";
            });

            await Step("Application closes from the tray", async () =>
            {
                var process = app ?? throw new E2EException("No application process.");
                await http!.DisposeAsync();
                http = null;
                // As Restart Manager closes it (an installer update): WM_QUERYENDSESSION and WM_ENDSESSION to its windows, the hidden one too.
                var windows = AskToClose(process.Id);
                if (windows == 0) throw new E2EException("The application has no window to ask.");
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await process.WaitForExitAsync(limit.Token);
                await viaProxy!.DisposeAsync();
                viaProxy = null;
                return $"{windows} windows asked, exit code {process.ExitCode}";
            });
        }
        catch (StepFailedException)
        {
            failed = true;
        }
        catch (Exception error)
        {
            failed = true;
            Console.WriteLine("FAIL: " + error);
        }
        finally
        {
            if (http is not null) await http.DisposeAsync();
            if (app is { HasExited: false })
            {
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync();
            }
            if (viaProxy is not null) await viaProxy.DisposeAsync();
        }
        if (failed)
        {
            lock (proxyErrors)
            {
                foreach (var line in proxyErrors) Console.WriteLine("      proxy: " + line);
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed ? "E2E MCP FAILED" : "E2E MCP PASSED");
        foreach (var (step, elapsed, _) in Timings) Console.WriteLine($"  {elapsed.TotalSeconds,7:N1} s  {step}");
        Console.WriteLine($"  {total.Elapsed.TotalSeconds,7:N1} s  total");
        if (failed || arguments.Keep)
        {
            Console.WriteLine($"The work directory is kept: {work}");
        }
        else
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                    break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 40)
                {
                    await Task.Delay(250);
                }
            }
            Console.WriteLine($"The work directory is removed: {work}");
        }
        return failed ? 1 : 0;
    }

    private static async Task Step(string name, Func<Task<string>> action)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var detail = await action();
            Timings.Add((name, watch.Elapsed, detail));
            Console.WriteLine($"PASS  {name} ({watch.Elapsed.TotalSeconds:N1} s): {detail}");
        }
        catch (Exception error)
        {
            Timings.Add((name, watch.Elapsed, "failed"));
            Console.WriteLine($"FAIL  {name} ({watch.Elapsed.TotalSeconds:N1} s): {(error is E2EException ? error.Message : error.ToString())}");
            throw new StepFailedException();
        }
    }

    private static Dictionary<string, object?> Args(object values)
        => JsonSerializer.SerializeToElement(values, Json).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, object arguments) => Ok(await client.CallToolAsync(tool, Args(arguments)), tool);

    private static JsonElement Ok(CallToolResult result, string tool)
    {
        if (result.IsError == true || result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
            throw new E2EException($"{tool} failed: " + string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        return structured.Clone();
    }

    private static byte[]? Image(CallToolResult result) => result.Content.OfType<ImageContentBlock>().FirstOrDefault()?.DecodedData.ToArray();

    /// <summary>The job of a started answer, followed with wait_job until it ends; it must succeed.</summary>
    private static async Task<JsonElement> FinishedAsync(McpClient client, string projectId, JsonElement started, TimeSpan? limit = null)
    {
        var job = started.GetProperty("job");
        var watch = Stopwatch.StartNew();
        var reported = TimeSpan.Zero;
        while (job.GetProperty("status").GetString() is "queued" or "running")
        {
            if (watch.Elapsed > (limit ?? TimeSpan.FromMinutes(5))) throw new E2EException("The job did not finish: " + job);
            var waited = await CallAsync(client, "wait_job", new { projectId, jobId = started.GetProperty("jobId").GetString(), timeoutSeconds = 30 });
            job = waited.GetProperty("job");
            if (watch.Elapsed - reported > TimeSpan.FromSeconds(30))
            {
                reported = watch.Elapsed;
                Console.WriteLine($"      … {job.GetProperty("kind")} {job.GetProperty("status")} {job.GetProperty("progress")}%");
            }
        }
        if (job.GetProperty("status").GetString() != "succeeded") throw new E2EException("The job did not succeed: " + job);
        return job;
    }

    /// <summary>host-settings.json as the settings dialog writes it.</summary>
    private static void WriteSettings(string workDirectory, bool enabled, int port, string token, bool launchOnRequest)
    {
        var settings = new JsonObject
        {
            ["mcp"] = new JsonObject
            {
                ["enabled"] = enabled, ["port"] = port, ["accessLevel"] = "full", ["token"] = token, ["restrictFolders"] = false,
                ["allowedFolders"] = new JsonArray(), ["launchOnRequest"] = launchOnRequest,
            },
        };
        File.WriteAllText(Path.Combine(workDirectory, "host-settings.json"), settings.ToJsonString());
    }

    /// <summary>The proxy alone with stdin closed at once: its exit code and stderr.</summary>
    private static async Task<(int ExitCode, string Error)> RunProxyAsync(string proxy, string workDirectory)
    {
        var start = new ProcessStartInfo(proxy) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--work-directory");
        start.ArgumentList.Add(workDirectory);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await error);
    }

    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const nint EndSessionCloseApp = 0x1;

    /// <summary>Sends the end-of-session pair to every top-level window of the process; returns how many windows got it.</summary>
    private static int AskToClose(int processId)
    {
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner == processId) windows.Add(window);
            return true;
        }, 0);
        foreach (var window in windows) SendMessageTimeout(window, WmQueryEndSession, 0, EndSessionCloseApp, 0x0002, 5000, out _);
        foreach (var window in windows) SendMessageTimeout(window, WmEndSession, 1, EndSessionCloseApp, 0x0002, 5000, out _);
        return windows.Count;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    private static Process? FindApp(string appDir)
    {
        var expected = Path.Combine(appDir, "MautVision.Desktop.exe");
        foreach (var process in Process.GetProcessesByName("MautVision.Desktop"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return process;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }
        return null;
    }

    private static int FreePort()
    {
        for (var port = 39100; port < 40000; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
            }
        }
        throw new E2EException("No free port in 39100–39999.");
    }
}
