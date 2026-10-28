using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Projects.Transfer;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

[JobKind("smoke.transfer_hold")]
internal interface ITransferHoldJob : IJobDefinition<string>;

/// <summary>
/// Project transfer: export and import of a project (round trip with hard links and a large file, id and name conflicts, cancel and rollback of
/// both, archives that are not accepted: of an early format, damaged, cut short, foreign, with paths out of the folder, repeated
/// names, encryption, a newer schema, a changed file), the admission of an export (busy, nothing new while it runs), deletion with and
/// without cancelling jobs, tombstones and leftovers removed after a restart, the last project and the order of opens.
/// </summary>
internal static class TransferSmoke
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task<CoreException> RejectsCode(string code, Func<Task> action)
        {
            try { await action(); }
            catch (CoreException error) when (error.Code == code) { checks++; return error; }
            catch (CoreException error) { throw new InvalidOperationException($"Expected {code}, got {error.Code}: {error.Message}"); }
            throw new InvalidOperationException($"Expected CoreException {code}.");
        }

        var root = Path.Combine(fixture, "transfer-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var outside = Path.Combine(fixture, "transfer-outside");
        Directory.CreateDirectory(outside);
        var core = await createCore(root, null);
        var jobs = (JobsManager)core.Jobs;
        var holds = new ConcurrentDictionary<string, TaskCompletionSource>();
        jobs.Register<ITransferHoldJob, string>(new HoldHandler(holds));
        var manager = (ProjectsManager)core.Projects;
        var exports = ((MautVision.Core.Core)core).ProjectExports;
        var removed = new ConcurrentQueue<string>();
        var opened = new ConcurrentQueue<string>();
        manager.ProjectRemoved += (_, args) => removed.Enqueue(args.ProjectId);
        manager.ProjectOpened += (_, args) => opened.Enqueue(args.Project.Id);
        var importEvents = new ConcurrentQueue<ProjectImportInfo>();
        manager.ImportChanged += (_, args) => importEvents.Enqueue(args.Import);

        Check(await manager.GetLastProjectIdAsync() is null, "A new library has no last project.");
        var project = (Project)await manager.CreateAsync(new ProjectSettings("Перенос", "Проект для переноса", "yolo26n", new InputSize(320, 256)));
        await manager.OpenAsync(project.Id);
        Check(await manager.GetLastProjectIdAsync() == project.Id, "Opening a project remembers it as the last one.");

        // Material: three imported images, hard links to them under a dataset folder, a large file, excluded working files.
        var sources = Path.Combine(fixture, "transfer-sources");
        Directory.CreateDirectory(sources);
        var group = await project.Images.CreateGroupAsync("Кадры", "", ImageSplitHint.Unassigned);
        var paths = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(sources, $"кадр-{index}.png");
            await File.WriteAllBytesAsync(path, Png(64 + index, 48, (byte)(40 * index)));
            paths.Add(path);
        }
        var import = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray()), project.Id, "transfer-images");
        await WaitAsync(jobs, project.Id, import.Id, JobStatus.Succeeded);
        var images = (await project.Images.ListImagesAsync(group.Id, new ImageFilter(Archived: null))).Items;
        Check(images.Count == 3, "Three images are imported into the source project.");
        var projectDirectory = Path.Combine(root, "projects", project.Id);
        var filesDirectory = Path.Combine(projectDirectory, "files");
        var datasetFolder = Path.Combine(filesDirectory, "datasets", Guid.NewGuid().ToString("D"), "images", "train");
        Directory.CreateDirectory(datasetFolder);
        foreach (var image in images)
        {
            var source = Path.Combine(filesDirectory, "images", image.Id + ".png");
            if (!CreateHardLink(Path.Combine(datasetFolder, image.Id + ".png"), source, IntPtr.Zero)) throw new IOException("Hard link fixture failed.");
        }
        var weights = Path.Combine(filesDirectory, "models", Guid.NewGuid().ToString("D"), "weights.pt");
        Directory.CreateDirectory(Path.GetDirectoryName(weights)!);
        var large = new byte[ProjectExportHandler.InMemoryLimit + 123_457];
        new Random(5).NextBytes(large);
        await File.WriteAllBytesAsync(weights, large);
        Directory.CreateDirectory(Path.Combine(filesDirectory, "generation-runs", "run"));
        await File.WriteAllTextAsync(Path.Combine(filesDirectory, "generation-runs", "run", "frame.bin"), "working data");
        await File.WriteAllTextAsync(Path.Combine(filesDirectory, "images", ".partial.tmp"), "temporary");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "cache", "thumbnails"));
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "cache", "thumbnails", "x.webp"), "cache");
        var expected = SnapshotFiles(filesDirectory);
        Check(expected.Count == 7 && expected.Keys.All(path => !path.Contains("generation-runs") && !path.Contains("/.")), "The fixture has 7 material files to carry.");

        // Export: destination rules, admission while other work runs, then the archive.
        var archive = Path.Combine(outside, "Перенос 2026-10-23.zip");
        await RejectsCode("projects.export_destination", () => manager.PrepareExportAsync(project.Id, "relative.zip", "bad-1"));
        await RejectsCode("projects.export_destination", () => manager.PrepareExportAsync(project.Id, Path.Combine(outside, "a.txt"), "bad-2"));
        await RejectsCode("projects.export_destination", () => manager.PrepareExportAsync(project.Id, Path.Combine(outside, "missing", "a.zip"), "bad-3"));
        await RejectsCode("projects.export_destination", () => manager.PrepareExportAsync(project.Id, Path.Combine(projectDirectory, "inside.zip"), "bad-4"));
        holds["busy"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = await jobs.EnqueueAsync<ITransferHoldJob, string>("busy", project.Id, "hold-busy", label: "Удержание");
        Check(core.Jobs.CountActive() == 1, "CountActive counts the active jobs of open projects.");
        var refused = await RejectsCode("projects.transfer_busy", () => manager.PrepareExportAsync(project.Id, archive, "export-busy"));
        Check(refused.Details?["section"] == "smoke", $"transfer_busy names the area of the running job ({refused.Details?["section"]}).");
        await RejectsCode("projects.transfer_busy", () => jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(new ProjectExportInput("Перенос", archive),
            project.Id, "export-admission"));
        holds["busy"].SetResult();
        await WaitAsync(jobs, project.Id, busy.Id, JobStatus.Succeeded);

        await File.WriteAllTextAsync(archive, "previous archive");
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exports.Pause = async files =>
        {
            if (files != 3) return;
            paused.TrySetResult();
            await release.Task;
        };
        var cancelInput = await manager.PrepareExportAsync(project.Id, archive, "export-cancel");
        Check(cancelInput.DestinationPath == archive && cancelInput.ProjectName == "Перенос", "The export input names the destination and the project.");
        var cancelled = await jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(cancelInput, project.Id, "export-cancel", cancelInput.ProjectName);
        await paused.Task.WaitAsync(WaitLimit);
        await RejectsCode("projects.transfer_active", () => jobs.EnqueueAsync<ITransferHoldJob, string>("never", project.Id, "hold-during-export"));
        var review = await manager.ReviewDeletionAsync(project.Id);
        Check(review.TransferActive && review.Activities.Count == 0, "The deletion review reports the running export.");
        await RejectsCode("projects.transfer_active", () => manager.DeleteAsync(project.Id, review.Revision, cancelJobs: true));
        Check(Directory.GetFiles(outside, ".*.tmp").Length == 1, "The archive is written to a temporary dot file next to the destination.");
        await jobs.CancelAsync(project.Id, cancelled.Id);
        release.SetResult();
        var cancelledJob = await WaitAsync(jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        Check(await File.ReadAllTextAsync(archive) == "previous archive" && Directory.GetFiles(outside, ".*.tmp").Length == 0
            && Directory.GetFiles(projectDirectory, ".export-*").Length == 0, "A cancelled export leaves the destination and removes its temporary files.");
        Check(cancelledJob.Result?.Deserialize<ProjectExportResult>(JobsManager.Json) is { Files: >= 3, TotalFiles: 6 }, "A cancelled export keeps how far it got.");
        exports.Pause = null;

        var input = await manager.PrepareExportAsync(project.Id, archive, "export-1");
        var exportJob = await jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(input, project.Id, "export-1", input.ProjectName);
        var exported = await WaitAsync(jobs, project.Id, exportJob.Id, JobStatus.Succeeded);
        var result = exported.Result!.Value.Deserialize<ProjectExportResult>(JobsManager.Json)!;
        Check(exported.Label == "Перенос" && exported.Processed == exported.Total && exported.Total == 6 && result.Files == 6 && result.Links == 3
            && result.TotalBytes == result.Bytes && result.ArchiveBytes == new FileInfo(archive).Length,
            $"The export counts 6 files (4 material files, project.json, project.db) and 3 links with totals known upfront: {exported.Result}.");
        Check((await manager.PrepareExportAsync(project.Id, Path.Combine(outside, "other.zip"), "export-1")).DestinationPath == archive,
            "A repeated preparation of a queued export returns its input.");
        Check(Directory.GetFiles(outside, ".*.tmp").Length == 0 && Directory.GetFiles(projectDirectory, ".export-*").Length == 0, "A finished export leaves no temporary files.");
        using (var zip = ZipFile.OpenRead(archive))
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Check(names.SetEquals(expected.Keys.Where(path => !path.StartsWith("files/datasets/", StringComparison.Ordinal))
                    .Concat(["manifest.json", "project.json", "project.db"])),
                "The archive holds the manifest, project.json, project.db and each material file once; links, working and temporary files are left out.");
            foreach (var entry in zip.Entries)
            {
                await using var stream = entry.Open();
                await stream.CopyToAsync(Stream.Null);
            }
            Check(zip.Entries.Where(entry => entry.FullName.StartsWith("files/", StringComparison.Ordinal)).All(entry => entry.CompressedLength == entry.Length)
                && zip.GetEntry("project.db")!.CompressedLength < zip.GetEntry("project.db")!.Length,
                "Material files are stored, the database is deflated, and every entry reads back with its CRC.");
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Check(manifest.RootElement.GetProperty("format").GetString() == "maut-vision-sharp" && manifest.RootElement.GetProperty("formatVersion").GetInt32() == 1
                && manifest.RootElement.GetProperty("projectId").GetString() == project.Id && manifest.RootElement.GetProperty("links").GetArrayLength() == 3
                && manifest.RootElement.GetProperty("links").EnumerateArray().All(link => link.GetProperty("path").GetString()!.StartsWith("files/datasets/", StringComparison.Ordinal)
                    && link.GetProperty("target").GetString()!.StartsWith("files/images/", StringComparison.Ordinal)),
                "manifest.json names the format sharp v1, the project and the dataset paths as links to the images.");
            var database = Path.Combine(fixture, "transfer-snapshot.db");
            zip.GetEntry("project.db")!.ExtractToFile(database);
            Check(await ScalarAsync(database, "SELECT count(*) FROM jobs WHERE kind = 'projects.export' AND status IN ('queued', 'running');") == 0
                && await ScalarAsync(database, "SELECT count(*) FROM images;") == 3, "The snapshot has the images and no running export of its own.");
        }
        var bytes = await File.ReadAllBytesAsync(archive);
        Check(IndexOf(bytes, [0x50, 0x4b, 0x06, 0x06]) > 0 && IndexOf(bytes, [0x50, 0x4b, 0x06, 0x07]) > 0, "The archive ends with Zip64 records.");

        // Import: a copy of an existing project gets a new id and « (импорт)», a second one « (импорт 2)»; files and hard links come back.
        var stamp = Source(archive);
        var started = await manager.StartImportAsync(stamp, "import-1");
        Check(started.Status == ProjectImportStatus.Running && (await manager.StartImportAsync(stamp, "import-1")).Id == started.Id,
            "An import starts at once and a repeated operation returns it.");
        var first = await FinishImportAsync(manager, started.Id);
        Check(first.Status == ProjectImportStatus.Succeeded && first.ProjectId != project.Id && first.ProjectName == "Перенос (импорт)"
            && first.ProcessedFiles == first.TotalFiles && first.TotalFiles == 9 && first.ProcessedBytes == first.TotalBytes,
            $"An archive of an existing project is imported as a copy: {first}.");
        var events = importEvents.Where(item => item.Id == started.Id).Select(item => item.Sequence).ToArray();
        Check(events.Length >= 2 && events.Zip(events.Skip(1)).All(pair => pair.Second > pair.First), "Import snapshots come as events in increasing sequence.");
        var copy = (Project)await manager.GetAsync(first.ProjectId!);
        Check(copy.Description == "Проект для переноса" && copy.InputSize == new InputSize(320, 256), "The copy keeps the settings of the project.");
        var copyFiles = Path.Combine(root, "projects", copy.Id, "files");
        var actual = SnapshotFiles(copyFiles);
        Check(actual.Count == expected.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var content) && content.AsSpan().SequenceEqual(pair.Value)),
            "Every material file comes back with its content.");
        var ids = FolderScan.Scan(copyFiles, "files", _ => false, CancellationToken.None).ToDictionary(file => file.RelativePath, file => file.FileId);
        Check(images.All(image => ids[$"files/images/{image.Id}.png"] != 0
            && ids.Single(pair => pair.Key.StartsWith("files/datasets/", StringComparison.Ordinal) && pair.Key.EndsWith(image.Id + ".png", StringComparison.Ordinal)).Value
                == ids[$"files/images/{image.Id}.png"]), "Dataset frames are hard links to the imported images again.");
        await manager.OpenAsync(copy.Id);
        Check((await copy.Images.ListImagesAsync(group.Id, new ImageFilter(Archived: null))).Items.Select(item => item.Id).SequenceEqual(images.Select(item => item.Id))
            && (await core.Jobs.ListAsync(copy.Id)).All(job => job.Id != exportJob.Id && job.Status is not (JobStatus.Queued or JobStatus.Running or JobStatus.Interrupted)),
            "The copy opens with the same images and without the export that made it.");
        var second = await FinishImportAsync(manager, (await manager.StartImportAsync(stamp, "import-2")).Id);
        Check(second.ProjectName == "Перенос (импорт 2)" && second.ProjectId != copy.Id, "A further copy is named « (импорт 2)».");
        Check(ProjectsManager.UniqueName(new string('я', 120), new HashSet<string>([new string('я', 120)])) is { Length: 120 } fitted && fitted.EndsWith(" (импорт)", StringComparison.Ordinal),
            "A long name is shortened to fit 120 characters with the suffix.");

        // Cancel and failures leave the library as it was.
        var paths0 = LibraryFolders(root);
        var importPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Imports.Pause = async files =>
        {
            if (files != 2) return;
            importPaused.TrySetResult();
            await importRelease.Task;
        };
        var cancelling = await manager.StartImportAsync(stamp, "import-cancel");
        await importPaused.Task.WaitAsync(WaitLimit);
        await RejectsCode("projects.import_active", () => manager.StartImportAsync(stamp, "import-concurrent"));
        Check(Directory.GetDirectories(Path.Combine(root, "projects"), ".import-*").Length == 1, "An import extracts into one staging folder.");
        var requested = manager.CancelImport(cancelling.Id);
        Check(requested.CancelRequested && requested.Status == ProjectImportStatus.Running && manager.CancelImport(cancelling.Id).CancelRequested,
            "Cancel is requested at once and can be repeated.");
        importRelease.SetResult();
        var cancelledImport = await FinishImportAsync(manager, cancelling.Id);
        manager.Imports.Pause = null;
        Check(cancelledImport.Status == ProjectImportStatus.Cancelled && cancelledImport.ErrorCode is null && LibraryFolders(root).SetEquals(paths0),
            "A cancelled import removes its staging folder and adds nothing.");
        await RejectsCode("projects.import_not_found", () => Task.FromResult(manager.GetImport(Guid.NewGuid())));

        async Task RefusedAsync(string code, string path, string label, ProjectArchiveSource? source = null)
        {
            var refusedImport = await FinishImportAsync(manager, (await manager.StartImportAsync(source ?? Source(path), "refused-" + Guid.NewGuid().ToString("N"))).Id);
            Check(refusedImport.Status == ProjectImportStatus.Failed && refusedImport.ErrorCode == code && LibraryFolders(root).SetEquals(paths0),
                $"{label}: {code} without changes ({refusedImport.ErrorCode}: {refusedImport.ErrorMessage}).");
        }
        var legacy = Path.Combine(outside, "legacy.zip");
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                await writer.WriteAsync("""{"formatVersion":3,"projectId":"x","tables":[{"name":"images","rows":0}],"blobs":[]}""");
            zip.CreateEntry("tables/images.jsonl");
        }
        await RefusedAsync("projects.archive_legacy", legacy, "An archive of an early format (manifest with tables)");
        var damaged = Path.Combine(outside, "damaged.zip");
        var imageBytes = await File.ReadAllBytesAsync(Path.Combine(filesDirectory, "images", images[0].Id + ".png"));
        var corrupted = bytes.ToArray();
        var at = IndexOf(corrupted, imageBytes.AsSpan(100, 32).ToArray());
        corrupted[at] ^= 0xFF;
        await File.WriteAllBytesAsync(damaged, corrupted);
        await RefusedAsync("projects.archive_damaged", damaged, "A changed byte of a stored file (CRC-32)");
        var cut = Path.Combine(outside, "cut.zip");
        await File.WriteAllBytesAsync(cut, bytes.AsSpan(0, bytes.Length / 2).ToArray());
        await RefusedAsync("projects.archive_damaged", cut, "An archive cut short");
        var foreign = Path.Combine(outside, "foreign.zip");
        await File.WriteAllBytesAsync(foreign, Png(10, 10, 1));
        await RefusedAsync("projects.archive_unknown", foreign, "A file that is not a ZIP");
        await RefusedAsync("projects.archive_unknown", await RewriteAsync(archive, Path.Combine(outside, "escape.zip"), extra: ("files/../../escape.txt", [1, 2, 3])),
            "A path out of the project folder");
        await RefusedAsync("projects.archive_unknown", await RewriteAsync(archive, Path.Combine(outside, "other-format.zip"),
            manifest: text => text.Replace("maut-vision-sharp", "other-app", StringComparison.Ordinal)), "Another format");
        await RefusedAsync("projects.archive_damaged", await RewriteAsync(archive, Path.Combine(outside, "repeated.zip"), extra: ("project.json", [1])),
            "A repeated name");
        var encrypted = Path.Combine(outside, "encrypted.zip");
        var flagged = bytes.ToArray();
        var directory = IndexOf(flagged, [0x50, 0x4b, 0x01, 0x02]);
        flagged[directory + 8] |= 1;
        await File.WriteAllBytesAsync(encrypted, flagged);
        await RefusedAsync("projects.archive_unknown", encrypted, "An encrypted entry");
        await RefusedAsync("projects.storage_newer", await RewriteAsync(archive, Path.Combine(outside, "newer.zip"), database: async path =>
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {ProjectSchema.Migrations.Count + 1};";
            await command.ExecuteNonQueryAsync();
        }), "A project of a newer schema");
        await RefusedAsync("projects.archive_changed", archive, "A file changed after it was chosen", stamp with { Length = stamp.Length + 1 });

        // Deletion: busy work, a stale revision, cancelling the jobs; the folder goes away.
        holds["delete"] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = await jobs.EnqueueAsync<ITransferHoldJob, string>("delete", copy.Id, "hold-delete", label: "Удержание");
        await WaitAsync(jobs, copy.Id, held.Id, JobStatus.Running);
        var deletion = await manager.ReviewDeletionAsync(copy.Id);
        Check(deletion.Activities is [{ Kind: "smoke.transfer_hold", Label: "Удержание" }] && !deletion.TransferActive && deletion.Name == copy.Name,
            "The deletion review lists the running job with its label.");
        var busyError = await RejectsCode("projects.busy", () => manager.DeleteAsync(copy.Id, deletion.Revision, cancelJobs: false));
        Check(busyError.Details?["count"] == "1", "Deletion without cancelling refuses while a job runs.");
        await RejectsCode("projects.deletion_conflict", () => manager.DeleteAsync(copy.Id, deletion.Revision + 1, cancelJobs: true));
        var deleted = await manager.DeleteAsync(copy.Id, deletion.Revision, cancelJobs: true);
        Check(!deleted.CleanupPending && removed.Contains(copy.Id) && (await manager.ListAsync()).All(item => item.Id != copy.Id), "Deletion cancels the job and removes the project.");
        await RejectsCode("jobs.project_not_open", () => core.Jobs.GetAsync(copy.Id, held.Id));
        await RejectsCode("projects.not_found", () => manager.OpenAsync(copy.Id));
        await manager.RemovalsForTestingAsync();
        Check(!Directory.Exists(Path.Combine(root, "projects", copy.Id)) && Directory.GetDirectories(Path.Combine(root, "projects"), ".deleted-*").Length == 0,
            "The folder of the deleted project is removed in the background.");
        Check(await manager.GetLastProjectIdAsync() == null, "Deleting the last project forgets it.");

        // A busy file: the project is deleted at once, its files are removed after the next start (names only).
        var busyProject = await manager.GetAsync(second.ProjectId!);
        await manager.OpenAsync(busyProject.Id);
        var locked = Path.Combine(root, "projects", busyProject.Id, "files", "images", images[1].Id + ".png");
        PendingDeletion pendingDeletion;
        await using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result2 = await manager.DeleteAsync(busyProject.Id, busyProject.Revision, cancelJobs: false);
            await manager.RemovalsForTestingAsync();
            pendingDeletion = new PendingDeletion(result2.CleanupPending, File.Exists(Path.Combine(root, "projects", busyProject.Id, ProjectsManager.TombstoneName)),
                Directory.GetDirectories(Path.Combine(root, "projects"), ".deleted-*").Length);
        }
        Check(pendingDeletion.CleanupPending && (pendingDeletion.Tombstone || pendingDeletion.Renamed == 1)
            && (await manager.ListAsync()).All(item => item.Id != busyProject.Id), $"A busy file leaves a tombstone or a .deleted folder: {pendingDeletion}.");
        Directory.CreateDirectory(Path.Combine(root, "projects", ".import-leftover", "files"));
        await File.WriteAllTextAsync(Path.Combine(root, "projects", ".import-leftover", "files", "x"), "x");
        var restarted = (ProjectsManager)(await createCore(root, null)).Projects;
        Check((await restarted.ListAsync()).Select(item => item.Id).Order().SequenceEqual(new[] { project.Id }), "After a restart the library lists only the source project.");
        await restarted.CleanUpLeftoversAsync();
        await restarted.RemovalsForTestingAsync();
        Check(Directory.GetDirectories(Path.Combine(root, "projects")).Select(Path.GetFileName).SequenceEqual([project.Id]),
            "Cleanup after the start removes tombstones, .deleted and .import leftovers.");

        // Without conflicts the archive keeps its id and name.
        await manager.OpenAsync(project.Id);
        var sourceReview = await manager.ReviewDeletionAsync(project.Id);
        Check((await manager.DeleteAsync(project.Id, sourceReview.Revision, cancelJobs: false)).ProjectId == project.Id, "The source project is deleted.");
        await manager.RemovalsForTestingAsync();
        var back = await FinishImportAsync(manager, (await manager.StartImportAsync(stamp, "import-back")).Id);
        Check(back.Status == ProjectImportStatus.Succeeded && back.ProjectId == project.Id && back.ProjectName == "Перенос",
            "Without a conflict the project keeps its id and name.");

        // The last project and the order of opens: a slow open that ends after a newer one is not a switch.
        var other = await manager.CreateAsync(new ProjectSettings("Другой", "", "yolo26n", new InputSize(320, 256)));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = manager.Opening!;
        manager.Opening = async (value, token) =>
        {
            if (value.Id == back.ProjectId) await gate.Task;
            await opening(value, token);
        };
        while (opened.TryDequeue(out _)) { }
        var slow = manager.OpenAsync(back.ProjectId!);
        await manager.OpenAsync(other.Id);
        Check(await manager.GetLastProjectIdAsync() == other.Id, "The latest open is the last project.");
        gate.SetResult();
        Check((await slow).Id == back.ProjectId && await manager.GetLastProjectIdAsync() == other.Id && opened.ToArray().SequenceEqual([other.Id]),
            "A slow open that ends later still opens its project but neither becomes the last nor raises the switch.");
        manager.Opening = opening;
        Check(await ((ProjectsManager)(await createCore(root, null)).Projects).GetLastProjectIdAsync() == other.Id, "The last project survives a restart.");
        return checks;
    }

    private sealed record PendingDeletion(bool CleanupPending, bool Tombstone, int Renamed);

    private static ProjectArchiveSource Source(string path)
    {
        var info = new FileInfo(path);
        return new ProjectArchiveSource(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static async Task<ProjectImportInfo> FinishImportAsync(ProjectsManager manager, Guid importId)
    {
        await manager.Imports.CompletionForTesting(importId).WaitAsync(WaitLimit);
        return manager.GetImport(importId);
    }

    /// <summary>Rebuilds an archive with System.IO.Compression, changing the manifest or the database, or adding an entry.</summary>
    private static async Task<string> RewriteAsync(string archive, string target, Func<string, string>? manifest = null, Func<string, Task>? database = null,
        (string Name, byte[] Content)? extra = null)
    {
        var work = target + ".parts";
        Directory.CreateDirectory(work);
        using (var source = ZipFile.OpenRead(archive))
        using (var output = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var part = Path.Combine(work, Guid.NewGuid().ToString("N"));
                entry.ExtractToFile(part);
                if (entry.FullName == "manifest.json" && manifest is not null) await File.WriteAllTextAsync(part, manifest(await File.ReadAllTextAsync(part)));
                if (entry.FullName == "project.db" && database is not null) await database(part);
                output.CreateEntryFromFile(part, entry.FullName, CompressionLevel.Fastest);
            }
            if (extra is { } added)
            {
                await using var stream = output.CreateEntry(added.Name).Open();
                await stream.WriteAsync(added.Content);
            }
        }
        SqliteConnection.ClearAllPools();
        Directory.Delete(work, recursive: true);
        return target;
    }

    private static HashSet<string> LibraryFolders(string root)
        => Directory.GetDirectories(Path.Combine(root, "projects")).Select(path => Path.GetFileName(path)!).ToHashSet(StringComparer.Ordinal);

    /// <summary>Material files relative to the project with '/', without working and temporary files.</summary>
    private static Dictionary<string, byte[]> SnapshotFiles(string filesDirectory)
        => Directory.EnumerateFiles(filesDirectory, "*", SearchOption.AllDirectories)
            .Select(path => ("files/" + Path.GetRelativePath(filesDirectory, path).Replace('\\', '/'), path))
            .Where(item => !item.Item1.StartsWith("files/generation-runs/", StringComparison.Ordinal) && !Path.GetFileName(item.path).StartsWith('.'))
            .ToDictionary(item => item.Item1, item => File.ReadAllBytes(item.path), StringComparer.Ordinal);

    private static async Task<long> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    private static byte[] Png(int width, int height, byte shade)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(shade, (byte)(255 - shade), 90));
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static async Task<JobInfo> WaitAsync(IJobsManager jobs, string projectId, Guid jobId, JobStatus status)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = await jobs.GetAsync(projectId, jobId);
            if (job.Status == status) return job;
            if (job.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Succeeded or JobStatus.Blocked or JobStatus.Interrupted)
                throw new InvalidOperationException($"Job {job.OperationId} is {job.Status} ({job.Error?.Code}: {job.Error?.Message}), expected {status}.");
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"Job {job.OperationId} is {job.Status}, expected {status}.");
            await Task.Delay(20);
        }
    }

    /// <summary>A job that runs until its input's gate opens or it is cancelled: background work of a project.</summary>
    private sealed class HoldHandler(ConcurrentDictionary<string, TaskCompletionSource> holds) : IJobHandler<string>
    {
        public Task ExecuteAsync(string input, JobContext context) => holds[input].Task.WaitAsync(context.CancellationToken);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
