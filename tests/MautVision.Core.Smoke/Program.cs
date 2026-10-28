using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

var temporaryParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var fixture = Path.Combine(temporaryParent, "maut-core-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var catalog = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../catalog/yolo-catalog.json"));
// The pinned ffmpeg build fetched by tools/get-ffmpeg.ps1 (smoke.ps1 runs it through start.ps1).
var ffmpeg = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.dev/tools/ffmpeg"));
var checks = 0;
var junctions = new List<string>();
try
{
    var root = Path.Combine(fixture, "library");
    await Rejects<ArgumentException>(() => Task.FromResult(CoreFactory.GetInstance(new CoreOptions("", catalog))));
    await Rejects<ArgumentException>(() => Task.FromResult(CoreFactory.GetInstance(new CoreOptions(root, " "))));
    var options = new CoreOptions(root, catalog);
    var core = CoreFactory.GetInstance(options);
    Check(!core.GetType().IsVisible, "Core implementation must be hidden.");
    var parallelCore = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => CoreFactory.GetInstance(options))));
    Check(parallelCore.All(value => ReferenceEquals(core, value)), "Concurrent factory calls share one Core.");
    var equivalent = new CoreOptions(Path.Combine(root, ".") + Path.DirectorySeparatorChar, Path.Combine(Path.GetDirectoryName(catalog)!, ".", Path.GetFileName(catalog)));
    Check(ReferenceEquals(core, CoreFactory.GetInstance(equivalent)), "Equivalent paths reuse Core.");
    Check(ReferenceEquals(core, CoreFactory.GetInstance(options with { LoggerFactory = new LogCapture() })), "A logger factory is not part of the Core configuration.");
    await Rejects<InvalidOperationException>(() => Task.FromResult(CoreFactory.GetInstance(new CoreOptions(root + "-other", catalog))));
    await Rejects<InvalidOperationException>(() => Task.FromResult(CoreFactory.GetInstance(new CoreOptions(root, catalog + ".other"))));
    await Rejects<InvalidOperationException>(() => Task.FromResult(core.Projects));
    await Rejects<InvalidOperationException>(() => Task.FromResult(core.BaseModels));
    await Rejects<InvalidOperationException>(() => Task.FromResult(core.Jobs));
    Check(!Directory.Exists(root), "Getting Core does not initialize project storage.");
    using (var cancelledInitialization = new CancellationTokenSource())
    {
        cancelledInitialization.Cancel();
        await Rejects<OperationCanceledException>(() => core.InitializeAsync(cancelledInitialization.Token));
    }
    await Rejects<InvalidOperationException>(() => Task.FromResult(core.Projects));
    await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => core.InitializeAsync())));
    var initializedProjects = core.Projects;
    Check(initializedProjects is not null, "Initialization makes managers available after a cancelled attempt.");
    await core.InitializeAsync();
    Check(ReferenceEquals(initializedProjects, core.Projects), "Repeated initialization preserves manager instances.");
    var retryWorkDirectory = Path.Combine(fixture, "retry-work");
    await File.WriteAllTextAsync(retryWorkDirectory, "Initialization must not overwrite this file.");
    var retryCore = ConstructIsolatedCore(new CoreOptions(retryWorkDirectory, catalog));
    await Rejects<IOException>(() => retryCore.InitializeAsync());
    await Rejects<InvalidOperationException>(() => Task.FromResult(retryCore.Projects));
    await Rejects<InvalidOperationException>(() => Task.FromResult(retryCore.BaseModels));
    await Rejects<InvalidOperationException>(() => Task.FromResult(retryCore.Jobs));
    Check(await File.ReadAllTextAsync(retryWorkDirectory) == "Initialization must not overwrite this file.", "Failed initialization preserves the blocking file.");
    File.Delete(retryWorkDirectory);
    await retryCore.InitializeAsync();
    Check(Directory.Exists(retryWorkDirectory) && retryCore.Projects is not null, "The same Core can retry initialization and create a missing work directory.");
    Check(typeof(CoreFactory).Assembly.GetExportedTypes().All(type => type == typeof(CoreFactory) || type.Namespace?.StartsWith("MautVision.Core.Interfaces", StringComparison.Ordinal) == true), "Only factory and contract types are public.");
    // A catalogue copied into the work directory by earlier builds is not application data any more.
    Directory.CreateDirectory(Path.Combine(root, "BaseModels"));
    await File.WriteAllTextAsync(Path.Combine(root, "BaseModels", "yolo-catalog.json"), "[]");
    var models = await core.BaseModels.ListAsync();
    Check(models.Count == 59 && models[0].Id == "yolo26n", "Core reads the shipped catalogue and ignores a leftover copy.");
    Check(ReferenceEquals(models[0], await core.BaseModels.GetOrNullAsync(models[0].Id)), "Catalogue lookup shares runtime instances.");
    Check(await core.BaseModels.GetOrNullAsync("unknown") is null, "Missing base model returns null.");
    Check(models.All(model => model.Status == BaseModelStatus.NotInstalled), "A fresh work directory has no installed base models.");
    checks += await BaseModelsSmoke.RunAsync(fixture);

    IProjectsManager manager = core.Projects;
    Check(!manager.GetType().IsVisible, "Manager implementation must be hidden outside Core.");
    Check((await manager.ListAsync()).Count == 0, "New library must be empty.");
    await RejectsCode("projects.base_model_not_installed", () => manager.CreateAsync(Settings("Без модели")));
    Check((await manager.ListAsync()).Count == 0, "A project is not created without installed weights.");
    SeedInstalledModel(root);
    // The weights appear after the catalogue was loaded, so a fresh composition reads them.
    manager = (await CreateIsolatedCore(root)).Projects;
    var changes = new List<IProject>();
    manager.ProjectChanged += (_, _) => throw new InvalidOperationException("Broken observer.");
    manager.ProjectChanged += (_, args) => changes.Add(args.Project);
    IProject project = await manager.CreateAsync(Settings("  Первый проект  ") with { Description = "  Описание  " });
    Check(!project.GetType().IsVisible, "Project implementation must be hidden outside Core.");
    Check(project.Name == "Первый проект" && project.Description == "Описание", "Names and descriptions must be trimmed.");
    Check(project.BaseModelId == "yolo26n" && project.InputSize == new InputSize(640, 480) && project.Revision == 1
        && project.ArchivedAt is null && project.UpdatedAt == project.CreatedAt, "Creation stores settings as revision 1.");
    Check(changes.Count == 1 && ReferenceEquals(changes[0], project), "Creation emits ProjectChanged despite a failing observer.");
    Check(Guid.ParseExact(project.Id, "D").ToString("D") == project.Id, "IDs must be canonical.");
    Check(ReferenceEquals(project, await manager.GetAsync(project.Id)), "Repeated get returns cached instance.");
    var notifications = 0;
    manager.ProjectOpened += (_, _) => throw new InvalidOperationException("Broken observer.");
    manager.ProjectOpened += (_, args) => { if (ReferenceEquals(project, args.Project)) notifications++; };
    await manager.OpenAsync(project.Id);
    Check(notifications == 1, "Open emits a real Core event despite another failing subscriber.");
    await manager.GetAsync(project.Id);
    Check(notifications == 1, "Get does not emit an open event.");
    var concurrentOpen = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => manager.GetAsync(project.Id)));
    Check(concurrentOpen.All(value => ReferenceEquals(value, project)), "Concurrent gets share one instance.");
    var file = Path.Combine(root, "projects", project.Id, "project.json");
    using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(file)))
    {
        Check(json.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(new[] { "archivedAt", "baseModelId", "createdAt", "description", "formatVersion", "id", "inputSize", "name", "revision", "updatedAt" }), "Persist only project camelCase metadata.");
        Check(json.RootElement.GetProperty("formatVersion").GetInt32() == 1, "Metadata records its format version.");
        Check(json.RootElement.GetProperty("createdAt").GetDateTimeOffset() == project.CreatedAt, "Timestamp persists exactly.");
    }
    Check(!ProjectFiles(file).Any(name => name.EndsWith(".tmp", StringComparison.Ordinal)) && ProjectFiles(file).Contains("project.json"), "Creation leaves no staging files.");
    var restarted = await CreateIsolatedCore(root);
    Check(ReferenceEquals(core, CoreFactory.GetInstance(options)), "Factory retains its original instance throughout the process.");
    manager = restarted.Projects;
    var restored = await manager.GetAsync(project.Id);
    Check(!ReferenceEquals(project, restored) && restored.Id == project.Id && restored.Name == project.Name && restored.CreatedAt == project.CreatedAt
        && restored.Description == project.Description && restored.InputSize == project.InputSize && restored.Revision == 1, "Restart loads a fresh instance from project.json.");
    await restored.UpdateAsync(Settings("Переименованный") with { InputSize = new InputSize(320, 256) }, expectedRevision: 1);
    Check(restored.Name == "Переименованный" && restored.InputSize == new InputSize(320, 256) && restored.Revision == 2 && restored.UpdatedAt >= restored.CreatedAt, "Update changes the shared instance and its revision.");
    await RejectsCode("projects.revision_conflict", () => restored.UpdateAsync(Settings("Устаревший"), expectedRevision: 1));
    Check(restored.Name == "Переименованный", "A conflicting update changes nothing.");
    await RejectsCode("projects.input_size_invalid", () => restored.UpdateAsync(Settings("Размер") with { InputSize = new InputSize(100, 64) }, 2));
    await RejectsCode("projects.input_size_invalid", () => restored.UpdateAsync(Settings("Размер") with { InputSize = new InputSize(0, 64) }, 2));
    await RejectsCode("projects.base_model_not_found", () => restored.UpdateAsync(Settings("Модель") with { BaseModelId = "unknown" }, 2));
    await RejectsCode("projects.base_model_not_installed", () => restored.UpdateAsync(Settings("Модель") with { BaseModelId = "yolo26s" }, 2));
    await RejectsCode("projects.description_invalid", () => restored.UpdateAsync(Settings("Описание") with { Description = new string('x', 2001) }, 2));
    await restored.SetArchivedAsync(true);
    Check(restored.ArchivedAt is not null && restored.Revision == 3, "Archiving is a saved change.");
    await restored.SetArchivedAsync(true);
    Check(restored.Revision == 3, "Repeated archiving changes nothing.");
    await RejectsCode("projects.archived", () => restored.UpdateAsync(Settings("Архив"), 3));
    await restored.SetArchivedAsync(false);
    Check(restored.ArchivedAt is null && restored.Revision == 4, "Restoring from the archive is a saved change.");
    var reloaded = await (await CreateIsolatedCore(root)).Projects.GetAsync(project.Id);
    Check(reloaded.Name == "Переименованный" && reloaded.Revision == 4 && reloaded.ArchivedAt is null && reloaded.InputSize == new InputSize(320, 256), "Changes persist across restart.");
    Check(!ProjectFiles(file).Any(name => name.EndsWith(".tmp", StringComparison.Ordinal)), "Updates leave no staging files.");
    Check(ReferenceEquals((await manager.ListAsync()).Single(), restored), "List and get share cached instances.");
    var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => manager.CreateAsync(Settings("Project " + index))));
    Check(created.Select(value => value.Id).Distinct().Count() == created.Length, "Concurrent creates have unique IDs.");
    Check((await manager.ListAsync()).Count == created.Length + 1, "List discovers created directories.");
    await manager.CreateAsync(Settings("x"));
    await manager.CreateAsync(Settings(new string('x', 120)));
    foreach (var name in new[] { "", " \t\r\n", new string('x', 121) })
        await RejectsCode("projects.name_invalid", () => manager.CreateAsync(Settings(name)));
    await Rejects<ArgumentNullException>(() => manager.CreateAsync(null!));
    foreach (var id in new[] { "", "..", "../outside", "C:\\outside", "/etc/passwd", project.Id + "/../outside", "ABCDEFAB-1234-4123-8123-ABCDEFABCDEF", Guid.Empty.ToString("D") })
        await Rejects<ArgumentException>(() => manager.GetAsync(id));
    await RejectsCode("projects.not_found", () => manager.GetAsync(Guid.NewGuid().ToString("D")));
    Directory.CreateDirectory(Path.Combine(root, "projects", "foreign-directory"));
    var incompleteId = Guid.NewGuid().ToString("D");
    Directory.CreateDirectory(Path.Combine(root, "projects", incompleteId));
    Check((await manager.ListAsync()).Count == created.Length + 3, "Foreign/unpublished directories are ignored.");
    await RejectsCode("projects.not_found", () => manager.GetAsync(incompleteId));

    var original = await File.ReadAllTextAsync(file);
    var listedBeforeDamage = (await manager.ListAsync()).Count;
    await File.WriteAllTextAsync(file, original.Replace(project.Id, created[0].Id));
    Check(ReferenceEquals(await manager.GetAsync(project.Id), restored), "A loaded instance stays the source of truth.");
    var projectsLog = new LogCapture();
    manager = (await CreateIsolatedCore(root, projectsLog)).Projects;
    await Rejects<InvalidDataException>(() => manager.GetAsync(project.Id));
    Check((await manager.ListAsync()).Count == listedBeforeDamage - 1, "A damaged project does not hide the rest of the library.");
    Check(projectsLog.Records.Any(record => record.Level == LogLevel.Warning && record.Exception is InvalidDataException
        && record.Category.EndsWith("ProjectsManager", StringComparison.Ordinal) && record.Message.Contains(project.Id)), "A skipped damaged project is logged with its id.");
    await File.WriteAllTextAsync(file, "{not-json}");
    await Rejects<InvalidDataException>(() => manager.GetAsync(project.Id));
    await File.WriteAllTextAsync(file, original);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await Rejects<OperationCanceledException>(() => manager.ListAsync(cancelled.Token));
        await Rejects<OperationCanceledException>(() => manager.GetAsync(project.Id, cancelled.Token));
        await Rejects<OperationCanceledException>(() => manager.CreateAsync(Settings("Cancelled"), cancelled.Token));
    }

    var outside = Path.Combine(fixture, "outside");
    Directory.CreateDirectory(outside);
    var sentinel = Path.Combine(outside, "sentinel.txt");
    await File.WriteAllTextAsync(sentinel, "preserved");
    var rootLink = Path.Combine(fixture, "root-link");
    await LinkDirectory(rootLink, outside);
    junctions.Add(rootLink);
    var linkedManager = (await CreateIsolatedCore(rootLink)).Projects;
    await Rejects<IOException>(() => linkedManager.ListAsync());
    var projectsLinkedRoot = Path.Combine(fixture, "projects-linked-root");
    Directory.CreateDirectory(projectsLinkedRoot);
    SeedInstalledModel(projectsLinkedRoot);
    await LinkDirectory(Path.Combine(projectsLinkedRoot, "projects"), outside);
    junctions.Add(Path.Combine(projectsLinkedRoot, "projects"));
    linkedManager = (await CreateIsolatedCore(projectsLinkedRoot)).Projects;
    await Rejects<IOException>(() => linkedManager.ListAsync());
    await Rejects<IOException>(() => linkedManager.CreateAsync(Settings("Rejected")));
    manager = (await CreateIsolatedCore(root)).Projects;
    var linkedId = Guid.NewGuid().ToString("D");
    await LinkDirectory(Path.Combine(root, "projects", linkedId), outside);
    junctions.Add(Path.Combine(root, "projects", linkedId));
    await Rejects<IOException>(() => manager.GetAsync(linkedId));
    await Rejects<IOException>(() => manager.ListAsync());
    Directory.Delete(Path.Combine(root, "projects", linkedId));
    junctions.Remove(Path.Combine(root, "projects", linkedId));
    var linkedMetadataDirectory = Path.Combine(root, "projects", linkedId);
    Directory.CreateDirectory(linkedMetadataDirectory);
    await LinkDirectory(Path.Combine(linkedMetadataDirectory, "project.json"), outside);
    junctions.Add(Path.Combine(linkedMetadataDirectory, "project.json"));
    await Rejects<IOException>(() => manager.GetAsync(linkedId));
    Check(await File.ReadAllTextAsync(sentinel) == "preserved" && Directory.GetFiles(outside).Length == 1, "Reparse rejection preserves foreign data.");
    checks += await ProjectStoreSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel, LinkDirectory, junctions);
    checks += await JobsSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel);
    checks += await ResourcesSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel, LinkDirectory, junctions);
    checks += await CatalogSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel);
    checks += await ImagesSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel, LinkDirectory, junctions);
    checks += await VideoSmoke.RunAsync(fixture, ffmpeg, CreateIsolatedCore, SeedInstalledModel);
    checks += await BackgroundsSmoke.RunAsync(fixture, ffmpeg, CreateIsolatedCore, SeedInstalledModel);
    checks += await GenerationSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel);
    checks += await DatasetsSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel);
    checks += await TrainingSmoke.RunAsync(fixture, catalog, ConstructIsolatedCore);
    checks += await ChecksSmoke.RunAsync(fixture, catalog, ConstructIsolatedCore);
    checks += await HardwareSmoke.RunAsync(fixture);
    checks += await TransferSmoke.RunAsync(fixture, CreateIsolatedCore, SeedInstalledModel);
}
catch (Exception error)
{
    // The cleanup below may fail on files the failed check left open and would hide the cause.
    Console.Error.WriteLine("Core smoke failed: " + error);
    throw;
}
finally
{
    // Idle pooled connections keep project.db open; release them before removing the fixture.
    SqliteConnection.ClearAllPools();
    var resolved = Path.GetFullPath(fixture);
    if (!string.Equals(Path.GetDirectoryName(resolved), temporaryParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("maut-core-smoke-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unsafe smoke cleanup target.");
    foreach (var link in junctions)
    {
        var absoluteLink = Path.GetFullPath(link);
        if (!absoluteLink.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(absoluteLink) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("Unsafe junction cleanup target.");
        Directory.Delete(absoluteLink, recursive: false);
    }
    Directory.Delete(resolved, recursive: true);
}
Console.WriteLine($"PASS: {checks} Core smoke checks.");

async Task<ICore> CreateIsolatedCore(string root, ILoggerFactory? loggerFactory = null)
{
    var isolatedOptions = new CoreOptions(root, catalog) { FfmpegDirectory = ffmpeg };
    var instance = ConstructIsolatedCore(loggerFactory is null ? isolatedOptions : isolatedOptions with { LoggerFactory = loggerFactory });
    await instance.InitializeAsync();
    return instance;
}

static string[] ProjectFiles(string projectJson) => Directory.GetFiles(Path.GetDirectoryName(projectJson)!).Select(name => Path.GetFileName(name)).ToArray();

static void SeedInstalledModel(string root)
{
    // A stored file of the catalogue size is reported as installed; content is verified only before use.
    var weights = Path.Combine(root, "BaseModels", "weights");
    Directory.CreateDirectory(weights);
    using var file = File.Create(Path.Combine(weights, "yolo26n.pt"));
    file.SetLength(5544453);
}

static ProjectSettings Settings(string name) => new(name, "", "yolo26n", new InputSize(640, 480));

async Task RejectsCode(string code, Func<Task> action)
{
    try { await action(); }
    catch (CoreException error) when (error.Code == code) { checks++; return; }
    throw new InvalidOperationException($"Expected CoreException {code}.");
}

static ICore ConstructIsolatedCore(CoreOptions options)
{
    // These fixtures test isolated composition and persisted data, without resetting the process singleton.
    var implementation = typeof(CoreFactory).Assembly.GetType("MautVision.Core.Core", throwOnError: true)!;
    return (ICore)Activator.CreateInstance(implementation, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null, args: [options], culture: null)!;
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

async Task Rejects<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task LinkDirectory(string link, string target)
{
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(link, target);
        return;
    }
    var start = new ProcessStartInfo("cmd.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not create junction fixture.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new IOException("Junction fixture failed: " + await stderr + await stdout);
}
