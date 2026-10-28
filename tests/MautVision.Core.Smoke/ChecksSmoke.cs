using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Projects;
using SkiaSharp;
using static TrainingSmoke;

/// <summary>
/// Model checks with the fake worker: deleting model versions (blockers, evaluations deleted with the version, two-phase files), the
/// reproducible ZIP export with verified weights, evaluations on dataset parts (compatibility, the GPU queue shared with training,
/// metrics, the comparison matrix, the dataset blocker, recovery as «Прервано»), live checks by image, video and screen (immediate
/// refusal while the GPU is busy, hard-linked inputs, previews as files, coalesced events, bounded journals, stop, stop on a
/// project switch and the time limit), monitors and the screenshot in C#.
/// </summary>
internal static class ChecksSmoke
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    internal static async Task<int> RunAsync(string fixture, string catalog, Func<CoreOptions, ICore> construct)
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

        // The fake yolo26n weights of a catalogue made for this run, as in the training smoke.
        var root = Path.Combine(fixture, "ch");
        var weights = Path.Combine(root, "BaseModels", "weights");
        Directory.CreateDirectory(weights);
        var weightBytes = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(Path.Combine(weights, "yolo26n.pt"), weightBytes);
        var entries = JsonNode.Parse(await File.ReadAllTextAsync(catalog))!.AsArray();
        var entry = entries.Single(item => item!["id"]!.GetValue<string>() == "yolo26n")!;
        entry["byteSize"] = weightBytes.Length;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(weightBytes));
        var smokeCatalog = Path.Combine(fixture, "checks-catalog.json");
        await File.WriteAllTextAsync(smokeCatalog, entries.ToJsonString());
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!);
        var fakeWorker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MautVision.FakeWorker", "bin", configuration!, "net10.0",
            "MautVision.FakeWorker.exe"));
        async Task<ICore> CoreWith(PythonWorkerLaunch? launch)
        {
            var created = construct(new CoreOptions(root, smokeCatalog) { PythonWorker = launch });
            await created.InitializeAsync();
            return created;
        }

        var core = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Проверки", "", "yolo26n", new InputSize(64, 64)));
        await RejectsCode("checks.project_not_open", () => project.Checks.ListChecksAsync());
        await core.Projects.OpenAsync(project.Id);
        var trainingEvents = new ConcurrentQueue<TrainingChangedEventArgs>();
        var checkEvents = new ConcurrentQueue<ChecksChangedEventArgs>();
        core.Projects.TrainingChanged += (_, args) => trainingEvents.Enqueue(args);
        core.Projects.ChecksChanged += (_, args) => checkEvents.Enqueue(args);
        var dataset = await BuildDatasetAsync(core, project, Path.Combine(fixture, "ch-sources"));
        Check(dataset.Counts is { Validation: > 0, Test: 0 }, "The dataset has a validation part and no test part.");
        var training = project.Training;
        var checksManager = project.Checks;

        async Task<Guid> TrainAsync(string operationId)
        {
            await SetNameAsync(project, dataset.Id, "Успех");
            var input = await training.PrepareRunAsync(new TrainingRunRequest(dataset.Id, Params(("epochs", 1), ("batch", 4))), operationId);
            var job = await core.Jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(input, project.Id, operationId);
            await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
            return (await training.GetRunAsync(job.Id)).ResultVersionId!.Value;
        }
        var first = await training.GetVersionAsync(await TrainAsync("ch-train-1"));
        var second = await training.GetVersionAsync(await TrainAsync("ch-train-2"));
        Check(first.IsWorking && !second.IsWorking, "The first version is the working one.");

        // ---- Deleting the working version is refused ----
        var workingReview = await training.ReviewVersionDeletionAsync(first.Id);
        Check(workingReview.IsWorking && workingReview.Blockers.Single().Code == "training.version_working" && workingReview.Bytes > 0,
            "The deletion review of the working version names the blocker.");
        var inUse = await RejectsCode("training.version_in_use", () => training.DeleteVersionAsync(first.Id));
        Check(inUse.Details?["reason"] == "working", "Deleting the working version names the reason working.");

        // ---- ZIP export: verified weights, fixed layout, reproducible bytes ----
        var exports = Path.Combine(fixture, "ch-exports");
        Directory.CreateDirectory(exports);
        async Task<JobInfo> ExportAsync(Guid versionId, string fileName, string operationId, JobStatus expected)
        {
            var input = await training.PrepareExportAsync(versionId, Path.Combine(exports, fileName), operationId);
            var job = await core.Jobs.EnqueueAsync<IModelExportJob, ModelExportInput>(input, project.Id, operationId);
            return await WaitAsync(core.Jobs, project.Id, job.Id, expected);
        }
        var exported = await ExportAsync(first.Id, "model-v1.zip", "ch-export-1", JobStatus.Succeeded);
        var result = exported.Result!.Value.Deserialize<ModelExportResult>(MautVision.Core.Jobs.JobsManager.Json)!;
        var zipPath = Path.Combine(exports, "model-v1.zip");
        Check(result.FileName == "model-v1.zip" && result.VersionNumber == 1 && result.Bytes == new FileInfo(zipPath).Length
            && result.Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath))) && exported.Progress == 100,
            "The export result names the file, its size and SHA-256.");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Check(zip.Entries.Select(item => item.FullName).SequenceEqual(["weights/best.pt", "weights/best.onnx", "manifest.json", "classes.json", "README.md"])
                && zip.Entries.All(item => item.LastWriteTime.Year == 2000), "The archive has the fixed order and date of entries.");
            foreach (var file in first.Files.Where(file => file.Kind is ModelFileKind.WeightsPt or ModelFileKind.Onnx))
            {
                using var content = zip.GetEntry("weights/" + file.Name)!.Open();
                Check(Convert.ToHexStringLower(SHA256.HashData(content)) == file.Sha256, $"weights/{file.Name} in the archive is the version file.");
            }
            using var manifestStream = zip.GetEntry("manifest.json")!.Open();
            var manifest = JsonNode.Parse(manifestStream)!;
            Check(manifest["schemaVersion"]!.GetValue<int>() == 1 && manifest["model"]!["version"]!.GetValue<int>() == 1 && manifest["model"]!["architecture"]!.GetValue<string>() == "yolo26n"
                && manifest["classes"]!.AsArray().Count == 2 && manifest["artifacts"]!.AsArray().All(item => item!["sha256"]!.GetValue<string>().Length == 64)
                && manifest["dataset"]!["versionId"]!.GetValue<string>() == dataset.Id.ToString("D") && manifest["metrics"]!["mAP50"] is not null,
                "manifest.json carries the passport, the classes and the artifacts with SHA-256.");
            using var classesStream = zip.GetEntry("classes.json")!.Open();
            var classes = JsonNode.Parse(classesStream)!;
            Check(classes["classes"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).SequenceEqual(["Кошка", "Собака"])
                && classes["classes"]![0]!["class_id"]!.GetValue<string>() == dataset.ClassMap[0].ClassId.ToString("D"), "classes.json maps outputs to classes.");
            using var readme = new StreamReader(zip.GetEntry("README.md")!.Open());
            var text = await readme.ReadToEndAsync();
            Check(text.StartsWith("# Модель, версия 1") && text.Contains("| 0 | Кошка |") && text.Contains("sha256 " + first.Files.First(file => file.Kind == ModelFileKind.WeightsPt).Sha256),
                "README.md describes the model in Russian with the class table and hashes.");
        }
        var again = await ExportAsync(first.Id, "again.zip", "ch-export-2", JobStatus.Succeeded);
        Check(again.Result!.Value.Deserialize<ModelExportResult>(MautVision.Core.Jobs.JobsManager.Json)!.Sha256 == result.Sha256, "Exporting the same version again gives the same bytes.");
        // A weights file that differs from its record is never packed.
        var secondFolder = Path.Combine(root, "projects", project.Id, "files", "models", second.Id.ToString("D"));
        var onnx = Path.Combine(secondFolder, "best.onnx");
        var onnxBytes = await File.ReadAllBytesAsync(onnx);
        await File.WriteAllBytesAsync(onnx, [.. onnxBytes, 1]);
        var corrupt = await ExportAsync(second.Id, "broken.zip", "ch-export-broken", JobStatus.Failed);
        Check(corrupt.Error?.Code == "training.artifact_corrupt" && !File.Exists(Path.Combine(exports, "broken.zip"))
            && Directory.GetFiles(exports, "*.tmp").Length == 0, "A damaged weights file fails the export with artifact_corrupt and leaves no file.");
        await File.WriteAllBytesAsync(onnx, onnxBytes);
        // While an export runs the version cannot be deleted.
        var exportPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exportRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)core).ModelExports.Pause = async _ =>
        {
            exportPaused.TrySetResult();
            await exportRelease.Task;
        };
        var pausedExport = await core.Jobs.EnqueueAsync<IModelExportJob, ModelExportInput>(
            await training.PrepareExportAsync(second.Id, Path.Combine(exports, "paused.zip"), "ch-export-paused"), project.Id, "ch-export-paused");
        await exportPaused.Task.WaitAsync(WaitLimit);
        Check((await training.ReviewVersionDeletionAsync(second.Id)).Blockers.Single().Code == "training.export_active", "A running export blocks deleting the version.");
        Check((await RejectsCode("training.version_in_use", () => training.DeleteVersionAsync(second.Id))).Details?["reason"] == "export",
            "Deleting a version being exported names the reason export.");
        exportRelease.SetResult();
        ((MautVision.Core.Core)core).ModelExports.Pause = null;
        await WaitAsync(core.Jobs, project.Id, pausedExport.Id, JobStatus.Succeeded);

        // ---- Evaluations: compatibility and parameters are checked before queueing ----
        var emptyTest = await RejectsCode("checks.evaluation_split_empty", () => checksManager.PrepareEvaluationAsync(EvaluationRequest(first.Id, dataset.Id) with
        {
            Split = EvaluationSplit.Test,
        }, "ch-ev-test"));
        Check(emptyTest.Details?["split"] == "Test", "An empty Test part is named in the details.");
        foreach (var (request, field) in new[] { (EvaluationRequest(first.Id, dataset.Id) with { Batch = 0 }, "batch"), (EvaluationRequest(first.Id, dataset.Id) with { Device = "gpu" }, "device") })
        {
            var invalid = await RejectsCode("checks.evaluation_parameters", () => checksManager.PrepareEvaluationAsync(request, "ch-ev-" + field));
            Check(invalid.Details?["field"] == field, $"An invalid evaluation {field} names its field.");
        }
        await RejectsCode("training.version_not_found", () => checksManager.PrepareEvaluationAsync(EvaluationRequest(Guid.NewGuid(), dataset.Id), "ch-ev-none"));
        var subset = await BuildSubsetAsync(core, project, dataset);
        await RejectsCode("checks.evaluation_classes_mismatch", () => checksManager.PrepareEvaluationAsync(EvaluationRequest(first.Id, subset.Id), "ch-ev-classes"));

        // A successful evaluation of the validation part.
        await SetNameAsync(project, dataset.Id, "Оценка");
        var evaluation = await EvaluateAsync(core, project, EvaluationRequest(first.Id, dataset.Id), "ch-ev-ok");
        await WaitAsync(core.Jobs, project.Id, evaluation, JobStatus.Succeeded);
        var evaluated = await checksManager.GetEvaluationAsync(evaluation);
        Check(evaluated.State == ModelEvaluationState.Succeeded && evaluated.Metrics["mAP50"] == 0.6 && evaluated.ImageCount == dataset.Counts.Validation
            && evaluated.ClassMetrics.Count == 2 && evaluated.ClassMetrics[0].ClassId == dataset.ClassMap[0].ClassId && evaluated.ClassMetrics[1].Name == "Собака"
            && evaluated.Dataset.Name == "Оценка" && evaluated.VersionNumber == 1 && evaluated.Split == EvaluationSplit.Validation && evaluated.Revision > 0
            && evaluated.Environment?.GetProperty("torch").GetString() == "2.14.0+fake", "The evaluation stores metrics, per-class metrics with classes and the image count.");
        Check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "checks", "jobs", evaluation.ToString("D"))), "A successful evaluation removes its folder.");
        Check(checkEvents.Any(item => item.Evaluations.Any(change => change.Id == evaluation)), "ChecksChanged names the evaluation.");
        var repeated = await checksManager.PrepareEvaluationAsync(EvaluationRequest(first.Id, dataset.Id) with { Batch = 3 }, "ch-ev-ok");
        Check(repeated.Batch == 16, "A repeated operation returns its input.");

        // The GPU queue is shared with training; a hanging evaluation blocks the dataset and the version and refuses live checks.
        var trainingInput = await training.PrepareRunAsync(new TrainingRunRequest(dataset.Id, Params(("epochs", 1), ("batch", 4))), "ch-train-queued");
        await SetNameAsync(project, dataset.Id, "fake:hang");
        var hanging = await EvaluateAsync(core, project, EvaluationRequest(second.Id, dataset.Id), "ch-ev-hang");
        var queuedRun = (await core.Jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(trainingInput, project.Id, "ch-train-queued")).Id;
        await WaitUntilAsync(async () => (await checksManager.GetEvaluationAsync(hanging)).State == ModelEvaluationState.Running, "the running evaluation");
        // Training names its manifest by the current dataset name when it starts: the queued run must not hang as well.
        await SetNameAsync(project, dataset.Id, "Оценка");
        Check((await core.Jobs.GetAsync(project.Id, queuedRun)).Status == JobStatus.Queued, "Training waits for the GPU while an evaluation runs.");
        Check((await project.Datasets.ReviewDeletionAsync(dataset.Id)).Blockers.Any(blocker => blocker.Code == "checks.evaluation_active"),
            "A running evaluation blocks deleting its dataset.");
        Check((await RejectsCode("training.version_in_use", () => training.DeleteVersionAsync(second.Id))).Details?["reason"] == "evaluation",
            "A running evaluation blocks deleting its version.");
        var image = Path.Combine(fixture, "ch-image.jpg");
        await File.WriteAllBytesAsync(image, ImagesSmoke.Jpeg(64, 48, 7));
        await RejectsCode("checks.gpu_busy_evaluation", () => checksManager.StartImageCheckAsync(ImageRequest(first.Id, image)));
        await core.Jobs.CancelAsync(project.Id, hanging);
        await WaitAsync(core.Jobs, project.Id, hanging, JobStatus.Cancelled);
        Check((await checksManager.GetEvaluationAsync(hanging)).State == ModelEvaluationState.Cancelled, "A cancelled evaluation is «Остановлено».");
        await WaitAsync(core.Jobs, project.Id, queuedRun, JobStatus.Succeeded);
        Check(true, "The queued training runs after the evaluation.");
        var third = await training.GetVersionAsync((await training.GetRunAsync(queuedRun)).ResultVersionId!.Value);

        // Listing and the comparison matrix: the latest successful evaluation of each version on a dataset part.
        await SetNameAsync(project, dataset.Id, "Оценка 2");
        var later = await EvaluateAsync(core, project, EvaluationRequest(first.Id, dataset.Id), "ch-ev-later");
        await WaitAsync(core.Jobs, project.Id, later, JobStatus.Succeeded);
        var ofSecond = await EvaluateAsync(core, project, EvaluationRequest(second.Id, dataset.Id), "ch-ev-second");
        await WaitAsync(core.Jobs, project.Id, ofSecond, JobStatus.Succeeded);
        var all = await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter());
        var ofFirst = await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter(first.Id), limit: 1);
        var onTest = await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter(Split: EvaluationSplit.Test));
        Check(all.Total == 4 && all.Items[0].Id == ofSecond && ofFirst.Total == 2 && ofFirst.Items.Single().Id == later && onTest.Total == 0,
            "Evaluations are listed newest first with filters and totals.");
        var latest = await checksManager.ListLatestEvaluationsAsync(dataset.Id, EvaluationSplit.Validation);
        Check(latest.Select(item => item.Id).Order().SequenceEqual(new[] { later, ofSecond }.Order()), "The matrix takes the latest successful evaluation per version.");

        // ---- Live checks ----
        // An image: maxDetections reaches the worker, the preview is a file read by the host, the journal keeps the frame.
        var imageCheck = await checksManager.StartImageCheckAsync(ImageRequest(first.Id, image) with { Parameters = new LiveCheckParameters(0.3, 2, "cpu") });
        Check(imageCheck.State == LiveCheckState.Preparing && imageCheck.Mode == LiveCheckMode.Image && imageCheck.Source.Name == "ch-image.jpg", "An image check starts at once.");
        var imageDone = await WaitCheckAsync(checksManager, imageCheck.Id);
        var preview = checksManager.GetPreview(imageCheck.Id);
        Check(imageDone.State == LiveCheckState.Succeeded && imageDone.LastFrame?.Detections.Count == 2 && imageDone.Summary.Detections == 2
            && imageDone.Summary.Classes.Count == 2 && imageDone.Summary.Classes[0].ClassId == dataset.ClassMap[0].ClassId
            && imageDone.RecentFrames.Single().Ordinal == 1 && imageDone.Settings.MaxDetections == 2 && imageDone.Progress.FirstFrameMs >= 0,
            $"The image check passes maxDetections and keeps its frame ({imageDone.State} {imageDone.Error?.Code}).");
        Check(preview is { ContentType: "image/jpeg" } && preview.Content.Take(2).SequenceEqual(new byte[] { 0xFF, 0xD8 }) && imageDone.Preview?.Number == 1,
            "The preview of the frame is a JPEG kept by the host.");
        // The state is published before the folder is cleaned up in the same finally block.
        var imageFolder = Path.Combine(root, "projects", project.Id, "checks", "live", imageCheck.Id.ToString("D"));
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(imageFolder)), "the folder of the finished check to go");
        Check(!Directory.Exists(imageFolder), "A finished check removes its folder.");
        await RejectsCode("checks.prediction_source", () => checksManager.StartImageCheckAsync(ImageRequest(first.Id, Path.Combine(fixture, "ch-sources", "..", "nothing.txt"))));
        var changing = Path.Combine(fixture, "ch-changing.jpg");
        await File.WriteAllBytesAsync(changing, ImagesSmoke.Jpeg(64, 48, 8));
        var stale = ImageRequest(first.Id, changing);
        await File.AppendAllTextAsync(changing, "x");
        await RejectsCode("checks.source_changed", () => checksManager.StartImageCheckAsync(stale));
        var badConfidence = await RejectsCode("checks.prediction_parameters", () => checksManager.StartImageCheckAsync(ImageRequest(first.Id, image) with
        {
            Parameters = new LiveCheckParameters(1.5, null, "cpu"),
        }));
        Check(badConfidence.Details?["field"] == "confidence", "An invalid confidence names its field.");

        // A check that waits: inputs are hard links, the GPU is busy for others, an evaluation started meanwhile waits for it.
        var hangImage = Path.Combine(fixture, "fake-hang.png");
        await File.WriteAllBytesAsync(hangImage, ImagesSmoke.Jpeg(32, 32, 9));
        var waitingCheck = await checksManager.StartImageCheckAsync(ImageRequest(first.Id, hangImage));
        await WaitUntilAsync(async () => (await checksManager.GetCheckAsync(waitingCheck.Id)).State == LiveCheckState.Running, "the running image check");
        var liveFolder = Path.Combine(root, "projects", project.Id, "checks", "live", waitingCheck.Id.ToString("D"));
        Check(DatasetsSmoke.LinkCount(Path.Combine(liveFolder, "input", "source", "source.png")) >= 2 && DatasetsSmoke.LinkCount(Path.Combine(liveFolder, "input", "model", "best.pt")) >= 2,
            "The source and the weights of a check are hard links, not copies.");
        await RejectsCode("checks.gpu_busy_prediction", () => checksManager.StartImageCheckAsync(ImageRequest(first.Id, image)));
        Check((await training.ReviewVersionDeletionAsync(first.Id)).Blockers.Any(blocker => blocker.Code == "checks.live_active"), "A live check blocks deleting its version.");
        await SetNameAsync(project, dataset.Id, "Во время проверки");
        var waitingEvaluation = await EvaluateAsync(core, project, EvaluationRequest(third.Id, dataset.Id), "ch-ev-waiting");
        await Task.Delay(300);
        Check((await core.Jobs.GetAsync(project.Id, waitingEvaluation)).Status == JobStatus.Queued, "An evaluation started during a live check waits in the queue.");
        var stopping = await checksManager.StopCheckAsync(waitingCheck.Id);
        Check(stopping.State is LiveCheckState.Cancelling or LiveCheckState.Cancelled && stopping.StopReason == LiveCheckStopReason.User, "«Остановить» shows «Остановка…».");
        var stopped = await WaitCheckAsync(checksManager, waitingCheck.Id);
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(liveFolder)), "the folder of the stopped check to go");
        Check(stopped.State == LiveCheckState.Cancelled && !Directory.Exists(liveFolder), "The stopped check is «Остановлено» and its folder is gone.");
        await WaitAsync(core.Jobs, project.Id, waitingEvaluation, JobStatus.Succeeded);
        Check(true, "The waiting evaluation runs after the check released the GPU.");

        // Training holds the GPU: a live check is refused with gpu_busy_training.
        await SetNameAsync(project, dataset.Id, "fake:hang");
        var busyRun = (await core.Jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(
            await training.PrepareRunAsync(new TrainingRunRequest(dataset.Id, Params(("epochs", 3))), "ch-train-busy"), project.Id, "ch-train-busy")).Id;
        await WaitUntilAsync(async () => (await training.GetRunAsync(busyRun)).EpochCount == 1, "the running training");
        await RejectsCode("checks.gpu_busy_training", () => checksManager.StartImageCheckAsync(ImageRequest(first.Id, image)));
        await core.Jobs.CancelAsync(project.Id, busyRun);
        await WaitAsync(core.Jobs, project.Id, busyRun, JobStatus.Cancelled);

        // A video: progress, the summary over every frame, previews of frames without detections, coalesced events.
        var video = Path.Combine(fixture, "ch-clip.mp4");
        await File.WriteAllBytesAsync(video, RandomNumberGenerator.GetBytes(256));
        checkEvents.Clear();
        var videoCheck = await checksManager.StartVideoCheckAsync(new VideoCheckRequest(first.Id, Source(video, 640, 360), new LiveCheckParameters(0.25, null, "cpu"), 100,
            new CheckRegion(10, 20, 64, 64)));
        var videoDone = await WaitCheckAsync(checksManager, videoCheck.Id);
        Check(videoDone.State == LiveCheckState.Succeeded && videoDone.Summary.FramesProcessed == 12 && videoDone.Summary.FramesWithDetections == 6
            && videoDone.Journal is { Total: 6, Kept: 6, Truncated: false } && videoDone.Progress.FramesTotal == 12 && videoDone.Source.DurationMs == 1200
            && videoDone.Preview is not null && videoDone.Settings.StepMs == 100 && videoDone.Settings.Region == new CheckRegion(10, 20, 64, 64),
            $"The video check sums every frame and keeps the journal ({videoDone.Summary.FramesProcessed}/{videoDone.Summary.FramesWithDetections}, {videoDone.Journal}).");
        var videoEvents = checkEvents.Where(item => item.Check?.Id == videoCheck.Id).ToArray();
        Check(videoEvents.Length is >= 2 and <= 12 && videoEvents.Max(item => item.Check!.Sequence) == videoDone.Sequence
            && videoEvents.Count(item => item.Preview is not null) >= 1, $"Video events are coalesced and end with the final snapshot ({videoEvents.Length}).");
        var tail = await checksManager.ListJournalAsync(videoCheck.Id, limit: 2);
        var older = await checksManager.ListJournalAsync(videoCheck.Id, before: tail.Items[0].Ordinal, limit: 10);
        var newer = await checksManager.ListJournalAsync(videoCheck.Id, after: 4);
        Check(tail.Items.Select(item => item.Ordinal).SequenceEqual([5L, 6L]) && older.Items.Select(item => item.Ordinal).SequenceEqual([1L, 2L, 3L, 4L])
            && newer.Items.Select(item => item.Ordinal).SequenceEqual([5L, 6L]), "The journal pages forward and backward.");
        await RejectsCode("checks.region_invalid", () => checksManager.StartVideoCheckAsync(new VideoCheckRequest(first.Id, Source(video, 640, 360),
            new LiveCheckParameters(0.25, null, "cpu"), 100, new CheckRegion(600, 0, 64, 64))));
        var manyVideo = Path.Combine(fixture, "fake-many.mp4");
        await File.WriteAllBytesAsync(manyVideo, RandomNumberGenerator.GetBytes(256));
        checkEvents.Clear();
        var many = await WaitCheckAsync(checksManager, (await checksManager.StartVideoCheckAsync(new VideoCheckRequest(first.Id, Source(manyVideo, 640, 360),
            new LiveCheckParameters(0.25, null, "cpu"), 100, null))).Id);
        var manyEvents = checkEvents.Count(item => item.Check?.Id == many.Id);
        Check(many.State == LiveCheckState.Succeeded && many.Journal is { Total: 5100, Kept: 5000, Truncated: true, FirstOrdinal: 1, LastOrdinal: 5000 }
            && many.Summary.FramesWithDetections == 5100 && manyEvents < 200, $"A long video keeps the first 5 000 frames, the full summary and few events ({manyEvents}).");

        // The screen: monitors and the screenshot in C#; the ring of the last 2 000 frames; the time limit ends with a warning.
        var monitors = core.Screen.ListMonitors();
        Check(monitors.Count >= 1 && monitors[0].Index == 1 && monitors.Count(item => item.IsPrimary) == 1 && monitors.All(item => item.Width > 0 && item.Height > 0),
            "Monitors are listed from 1 in physical pixels with one primary.");
        var shot = await core.Screen.CaptureAsync(monitors[0].Index);
        using (var decoded = SKBitmap.Decode(shot.Content))
        {
            Check(shot.ContentType == "image/png" && decoded.Width == monitors[0].Width && decoded.Height == monitors[0].Height && shot.Monitor == monitors[0],
                "The screenshot is a PNG of the monitor in physical pixels.");
        }
        await RejectsCode("screen.monitor_not_found", () => core.Screen.CaptureAsync(99));
        await RejectsCode("screen.monitor_not_found", () => checksManager.StartScreenCheckAsync(ScreenRequest(first.Id, 99, 3, 5, null)));
        await RejectsCode("checks.region_invalid", () => checksManager.StartScreenCheckAsync(ScreenRequest(first.Id, 1, 3, 5, new CheckRegion(0, 0, 32, 64))));
        await RejectsCode("checks.prediction_parameters", () => checksManager.StartScreenCheckAsync(ScreenRequest(first.Id, 1, 0, 5, null)));
        var ring = await WaitCheckAsync(checksManager, (await checksManager.StartScreenCheckAsync(ScreenRequest(first.Id, 1, 60, 2, new CheckRegion(0, 0, 64, 64)))).Id);
        Check(ring.State == LiveCheckState.Succeeded && ring.StopReason == LiveCheckStopReason.TimeLimit && ring.Warnings.Any(item => item.Code == "live_time_limit")
            && ring.Journal.Kept == 2000 && ring.Journal.Total > 2000 && ring.Journal.FirstOrdinal == ring.Journal.Total - 1999 && ring.Source.Monitor == 1,
            $"The screen keeps a ring of the last 2 000 frames and ends at its time limit with a warning ({ring.Journal}, {ring.State}).");
        var screen = await checksManager.StartScreenCheckAsync(ScreenRequest(first.Id, 1, 5, 60, null));
        await WaitUntilAsync(async () => (await checksManager.GetCheckAsync(screen.Id)).LastFrame is not null, "a screen frame");
        var running = await checksManager.GetCheckAsync(screen.Id);
        Check(running.State == LiveCheckState.Running && running.LastFrame!.Index >= 0 && (await checksManager.ListChecksAsync())[0].Id == screen.Id,
            "A running screen check shows the current frame.");
        // Opening another project is a switch: the check stops and the GPU is free again.
        var other = await core.Projects.CreateAsync(new ProjectSettings("Другой", "", "yolo26n", new InputSize(64, 64)));
        await core.Projects.OpenAsync(other.Id);
        var switched = await WaitCheckAsync(checksManager, screen.Id);
        Check(switched.State == LiveCheckState.Cancelled && switched.StopReason == LiveCheckStopReason.ProjectSwitched, "Opening another project stops the screen check.");
        await core.Projects.OpenAsync(project.Id);
        await RejectsCode("checks.check_not_found", () => checksManager.GetCheckAsync(Guid.NewGuid()));

        // ---- Deleting a version with its evaluations ----
        var before = (await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter(second.Id))).Total;
        var deletion = await training.ReviewVersionDeletionAsync(second.Id);
        Check(deletion.Blockers.Count == 0 && deletion.Evaluations == before && before == 2, "The review counts the evaluations deleted with the version.");
        var deleted = await training.DeleteVersionAsync(second.Id);
        Check(deleted.Evaluations == 2 && deleted.Number == 2 && (await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter(second.Id))).Total == 0
            && (await checksManager.ListEvaluationsAsync(new ModelEvaluationFilter())).Total == 3, "The evaluations of the version are deleted with it.");
        await RejectsCode("training.version_not_found", () => training.GetVersionAsync(second.Id));
        await RejectsCode("checks.evaluation_not_found", () => checksManager.GetEvaluationAsync(ofSecond));
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(secondFolder)), "the version folder cleanup");
        Check(trainingEvents.Any(item => item.Versions.Any(change => change.Id == second.Id && change.Revision is null)), "TrainingChanged reports the deleted version.");

        // ---- The application ends during an evaluation: «Прервано» on the next open ----
        await SetNameAsync(project, dataset.Id, "fake:hang");
        var dying = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var dyingProject = (Project)await dying.Projects.OpenAsync(project.Id);
        var lost = await EvaluateAsync(dying, dyingProject, EvaluationRequest(first.Id, dataset.Id), "ch-ev-lost");
        await WaitUntilAsync(async () => (await dyingProject.Checks.GetEvaluationAsync(lost)).State == ModelEvaluationState.Running, "the evaluation before the end");
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        var revived = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var revivedProject = (Project)await revived.Projects.OpenAsync(project.Id);
        var interrupted = await revivedProject.Checks.GetEvaluationAsync(lost);
        Check(interrupted.State == ModelEvaluationState.Interrupted && interrupted.Error?.Code == "jobs.interrupted" && interrupted.Metrics.Count == 0,
            "An evaluation that ended with the application is «Прервано».");
        var lostFolder = Path.Combine(root, "projects", project.Id, "checks", "jobs", lost.ToString("D"));
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(Path.Combine(lostFolder, "input"))), "the interrupted evaluation folder cleanup");
        Check((await revivedProject.Datasets.ReviewDeletionAsync(dataset.Id)).Blockers.Count == 0, "The interrupted evaluation does not block the dataset.");
        return checks;
    }

    private static ModelEvaluationRequest EvaluationRequest(Guid versionId, Guid datasetId) => new(versionId, datasetId, EvaluationSplit.Validation, "cpu");

    private static ImageCheckRequest ImageRequest(Guid versionId, string path) => new(versionId, Source(path), new LiveCheckParameters(0.25, null, "cpu"));

    private static ScreenCheckRequest ScreenRequest(Guid versionId, int monitor, double fps, int maxDurationSec, CheckRegion? region)
        => new(versionId, monitor, new LiveCheckParameters(0.25, null, "0"), fps, maxDurationSec, region);

    private static CheckSourceFile Source(string path, int? width = null, int? height = null)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? new CheckSourceFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), width, height)
            : new CheckSourceFile(path, 0, DateTimeOffset.UnixEpoch, width, height);
    }

    private static async Task<Guid> EvaluateAsync(ICore core, Project project, ModelEvaluationRequest request, string operationId)
    {
        var input = await project.Checks.PrepareEvaluationAsync(request, operationId);
        return (await core.Jobs.EnqueueAsync<IModelEvaluationJob, ModelEvaluationInput>(input, project.Id, operationId)).Id;
    }

    private static async Task<LiveCheck> WaitCheckAsync(IChecksManager checks, Guid checkId)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var check = await checks.GetCheckAsync(checkId);
            if (check.State is LiveCheckState.Succeeded or LiveCheckState.Failed or LiveCheckState.Cancelled) return check;
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"Live check {checkId} is {check.State}.");
            await Task.Delay(20);
        }
    }

    /// <summary>A dataset of one of the two classes: the other becomes negatives; its classes differ from the versions.</summary>
    private static async Task<DatasetVersion> BuildSubsetAsync(ICore core, Project project, DatasetVersion full)
    {
        var spec = full.Specification with
        {
            Name = "Одна кошка",
            ClassIds = [full.Specification.ClassIds[0]],
            TargetVariantIds = [full.Specification.TargetVariantIds[0]],
        };
        var plan = await project.Datasets.PlanAsync(spec, "ch-subset-plan");
        var build = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, "ch-subset-build"),
            project.Id, "ch-subset-build");
        await WaitAsync(core.Jobs, project.Id, build.Id, JobStatus.Succeeded);
        return await project.Datasets.GetAsync(build.Id);
    }
}
