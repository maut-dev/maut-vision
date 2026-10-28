using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Interfaces.Media;
using Microsoft.Data.Sqlite;

// End-to-end run of the installed application payload through the public Core API with the packaged Python worker:
// project → catalog → backgrounds (images and a video frame) → generation → dataset build and verify → training on the GPU →
// model version → evaluation on Test, image, video and screen checks → model ZIP → project export and import.
Console.OutputEncoding = Encoding.UTF8;
var arguments = Arguments.Parse(args);
if (arguments is null)
{
    Console.Error.WriteLine("Usage: MautVision.E2E --app-dir <installed application> --work-directory <empty or missing folder> "
        + "[--base-model-weights <yolo26n.pt>] [--device 0] [--keep]");
    return 2;
}
var appDir = Path.GetFullPath(arguments.AppDir);
var work = Path.GetFullPath(arguments.WorkDirectory);
if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
{
    Console.Error.WriteLine($"The work directory {work} is not empty.");
    return 2;
}
var root = Path.Combine(work, "core");
var inputs = Directory.CreateDirectory(Path.Combine(work, "inputs")).FullName;
var exports = Directory.CreateDirectory(Path.Combine(work, "exports")).FullName;
var worker = Path.Combine(appDir, "python", "maut-training-worker", "maut-training-worker.exe");
var catalog = Path.Combine(appDir, "catalog", "yolo-catalog.json");
var ffmpegDirectory = Path.Combine(appDir, "tools", "ffmpeg");
var ffmpeg = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
var log = new FileLog(Path.Combine(work, "core.log"));
var device = arguments.Device;
// Two epochs give a weak model (its scores stay below 0.05); a near-zero threshold shows that detections come back through the
// live checks, which is what the run proves, not the model quality.
const double Confidence = 0.001;
var total = Stopwatch.StartNew();
var timings = new List<(string Step, TimeSpan Elapsed, string Detail)>();
var failed = false;

Console.WriteLine($"E2E: application {appDir}");
Console.WriteLine($"E2E: work directory {work}, Core log {log.Path}");
ICore core = null!;
IProject project = null!;
var classIds = new List<Guid>();
var bindingIds = new List<Guid>();
Guid variantId = default, setId = default, groupId = default, datasetId = default, versionId = default;
DatasetVersion dataset = null!;
ModelVersion version = null!;
string? importedProjectId = null;
try
{
    await Step("Core and packaged worker", async () =>
    {
        foreach (var required in new[] { worker, catalog, ffmpeg, Path.Combine(ffmpegDirectory, "ffprobe.exe") })
            if (!File.Exists(required)) throw new E2EException($"Missing in the installed application: {required}");
        if (arguments.BaseModelWeights is { } seed) await SeedBaseModelAsync(seed);
        core = CoreFactory.GetInstance(new CoreOptions(root, catalog)
        {
            LoggerFactory = log,
            FfmpegDirectory = ffmpegDirectory,
            PythonWorker = PythonWorkerLaunch.Packaged(worker),
        });
        await core.InitializeAsync();
        var status = core.Python.GetStatus();
        if (status is not { Configured: true, Available: true, Mode: "packaged" }) throw new E2EException($"Worker runtime: {status}");
        return $"runtime {status.Mode}, {worker}";
    });

    await Step("Base model yolo26n", async () =>
    {
        var model = await core.BaseModels.GetOrNullAsync("yolo26n") ?? throw new E2EException("yolo26n is not in the catalogue.");
        var source = model.Status == BaseModelStatus.Installed ? "seeded" : "downloaded";
        if (model.Status != BaseModelStatus.Installed) await model.InstallAsync();
        if (model.Status != BaseModelStatus.Installed) throw new E2EException($"Installation ended {model.Status} ({model.ErrorCode}).");
        if (!await model.VerifyIntegrityAsync()) throw new E2EException("SHA-256 of the installed weights does not match the catalogue.");
        return $"{source}, {model.ByteSize:N0} bytes, SHA-256 verified";
    });

    await Step("Project", async () =>
    {
        var created = await core.Projects.CreateAsync(new ProjectSettings("Сквозной прогон", "E2E", "yolo26n", new InputSize(320, 320)));
        project = await core.Projects.OpenAsync(created.Id);
        return $"{project.Id}, input 320×320";
    });

    await Step("Classes and variant images", async () =>
    {
        var day = await project.Variants.CreateAsync("День", "");
        variantId = day.Id;
        string[] names = ["Круг", "Треугольник", "Квадрат"];
        var added = 0;
        for (var index = 0; index < names.Length; index++)
        {
            var klass = await project.Classes.CreateAsync(names[index], "");
            var binding = await project.Classes.AddVariantAsync(klass.Id, day.Id);
            classIds.Add(klass.Id);
            bindingIds.Add(binding.Id);
            var files = Enumerable.Range(0, 2).Select(variant =>
            {
                var path = Path.Combine(inputs, $"object-{index}-{variant}.png");
                Assets.WriteObject(path, index, variant);
                return path;
            }).ToArray();
            var job = await RunJobAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id, files), "assets-" + index);
            var result = Result<ImportClassAssetsResult>(job);
            if (result.Added != files.Length) throw new E2EException($"Class {names[index]}: added {result.Added} of {files.Length} ({result.Rejected} rejected).");
            added += result.Added;
        }
        return $"3 classes × 1 variant, {added} images";
    });

    await Step("Backgrounds from images", async () =>
    {
        var set = await project.Backgrounds.CreateSetAsync("Синтетические фоны", "");
        setId = set.Id;
        var sources = new List<BackgroundFileSource>();
        for (var index = 0; index < 8; index++)
        {
            var path = Path.Combine(inputs, $"background-{index}.png");
            Assets.WriteBackground(path, 320, 320, 100 + index);
            sources.Add(new BackgroundFileSource(path));
        }
        var photo = Path.Combine(inputs, "photo.jpg");
        Assets.WriteBackground(photo, 800, 600, 999, SkiaSharp.SKEncodedImageFormat.Jpeg);
        sources.Add(new BackgroundFileSource(photo, new BackgroundCrop(200, 120)));
        var job = await RunJobAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(set.Id, sources), "backgrounds-images");
        var result = Result<ImportBackgroundsResult>(job);
        if (result.Added != sources.Count) throw new E2EException($"Added {result.Added} of {sources.Count}: {string.Join(", ", result.Issues.Select(item => item.Reason))}");
        return $"{result.Added} backgrounds (8 whole, 1 crop)";
    });

    await Step("Background from a video frame", async () =>
    {
        var clip = Path.Combine(inputs, "clip.mp4");
        await Assets.FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=4", "-c:v", "libopenh264", "-pix_fmt", "yuv420p", clip);
        var info = await core.Media.ProbeVideoAsync(clip);
        var frames = new VideoFrameRange(1000, 1000, 1);
        var job = await RunJobAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(setId, [],
            new BackgroundVideoSource(clip, frames, new BackgroundCrop(160, 20))), "backgrounds-video");
        var result = Result<ImportBackgroundsResult>(job);
        var items = await project.Backgrounds.ListItemsAsync(setId, new BackgroundFilter(), 100);
        if (result.Added != 1 || items.Items.Count(item => item.Origin.Kind == BackgroundOriginKind.VideoFrame) != 1)
            throw new E2EException($"Video frame: added {result.Added}, issues {string.Join(", ", result.Issues.Select(item => item.Reason))}");
        return $"ffmpeg clip {info.Width}×{info.Height} {info.DurationMs} ms {info.Codec}, 1 frame; set has {items.Items.Count} backgrounds";
    });

    await Step("Generation", async () =>
    {
        var recipe = await project.Generation.CreateRecipeAsync(new GenerationRecipeInput("Сквозной", "", 320, 320, [setId], bindingIds,
            [new GenerationVariantWeight(variantId, 100)], ObjectsPerImage: 3, EmptyProbability: 0.05, Scale: 0.35));
        var input = await project.Generation.PrepareRunAsync(recipe.Id, recipe.Revision, 42, 160, "generation");
        var job = await RunJobAsync<IGenerationRunJob, GenerationRunInput>(input, "generation");
        var result = Result<GenerationRunResult>(job);
        if (result.ProducedCount != 160 || result.OutputGroupId is null) throw new E2EException($"Produced {result.ProducedCount} of 160.");
        groupId = result.OutputGroupId.Value;
        return $"{result.ProducedCount} frames, {result.MissingObjectFrames} with missing objects";
    });

    await Step("Dataset build", async () =>
    {
        var specification = new DatasetSpecification("Сквозной", "", [groupId], classIds, bindingIds, [], DatasetUnassignedVariantRole.Target,
            DatasetUnselectedClassRole.Negative, IncludeReviewedEmpty: true, AllowPartialGeneration: false, Seed: 7, ValRatio: 0.2, TestRatio: 0.15);
        var plan = await project.Datasets.PlanAsync(specification, "dataset-plan");
        var input = await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, "dataset-build");
        var job = await RunJobAsync<IDatasetBuildJob, DatasetBuildInput>(input, "dataset-build");
        datasetId = job.Id;
        var built = Result<DatasetBuildResult>(job);
        dataset = await project.Datasets.GetAsync(datasetId);
        var counts = dataset.Counts;
        // Generated PNG frames are hard links of the project images: a copy here means links fail on this volume or path.
        if (counts.Train == 0 || counts.Validation == 0 || counts.Test == 0 || dataset.ClassMap.Count != 3 || dataset.IntegrityState != DatasetIntegrityState.Verified
            || built.Linked != built.Produced)
            throw new E2EException($"Dataset: {counts}, {dataset.ClassMap.Count} classes, {dataset.IntegrityState}.");
        return $"train {counts.Train} / val {counts.Validation} / test {counts.Test}, {dataset.IntegrityState}; frames linked {built.Linked}, "
            + $"copied {built.Copied}, converted {built.Converted}";
    });

    await Step("Dataset integrity verify", async () =>
    {
        var job = await RunJobAsync<IDatasetVerifyJob, DatasetVerifyInput>(await project.Datasets.PrepareVerifyAsync(datasetId, "dataset-verify"), "dataset-verify");
        var result = Result<DatasetVerifyResult>(job);
        if (result.IssueCount != 0 || result.State != DatasetIntegrityState.Verified || result.CheckedFiles != result.TotalFiles)
            throw new E2EException($"Verify: {result.IssueCount} issues, {result.CheckedFiles}/{result.TotalFiles} files, {result.State}.");
        return $"{result.CheckedFiles} files, {result.Bytes:N0} bytes, 0 issues";
    });

    await Step("Training yolo26n (2 epochs, imgsz 320, batch 8)", async () =>
    {
        var parameters = new Dictionary<string, JsonElement>
        {
            ["epochs"] = JsonSerializer.SerializeToElement(2),
            ["batch"] = JsonSerializer.SerializeToElement(8),
            ["device"] = JsonSerializer.SerializeToElement(device),
        };
        var input = await project.Training.PrepareRunAsync(new TrainingRunRequest(datasetId, parameters), "training");
        if (input.ImageSize != 320) throw new E2EException($"imgsz {input.ImageSize}.");
        var job = await RunJobAsync<ITrainingRunJob, TrainingRunInput>(input, "training", TimeSpan.FromMinutes(30),
            id => Path.Combine(root, "projects", project.Id, "training", "jobs", id.ToString("D"), "logs"));
        var run = await project.Training.GetRunAsync(job.Id);
        versionId = run.ResultVersionId ?? throw new E2EException("The run has no model version.");
        version = await project.Training.GetVersionAsync(versionId);
        var folder = Path.Combine(root, "projects", project.Id, "files", "models", versionId.ToString("D"));
        foreach (var (kind, name) in new[] { (ModelFileKind.WeightsPt, "best.pt"), (ModelFileKind.Onnx, "best.onnx") })
        {
            var file = version.Files.SingleOrDefault(item => item.Kind == kind && item.Name == name) ?? throw new E2EException($"The version has no {name}.");
            var path = Path.Combine(folder, name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.ByteSize || Sha256(path) != file.Sha256)
                throw new E2EException($"{name} on disk does not match the version.");
        }
        var environment = run.Environment is { } value ? value : default;
        string Env(string key) => environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty(key, out var item) ? item.ToString() : "?";
        var duration = (job.FinishedAt - job.StartedAt) ?? TimeSpan.Zero;
        var map = version.Metrics.TryGetValue("mAP50", out var mAP50) ? mAP50 : double.NaN;
        return $"version {version.Number}, {version.EpochsCompleted} epochs, job {duration.TotalSeconds:N1} s, mAP50 {map:N3}, best.pt "
            + $"{Size(version, ModelFileKind.WeightsPt)}, best.onnx {Size(version, ModelFileKind.Onnx)}; {Env("deviceName")}, torch {Env("torch")}, "
            + $"ultralytics {Env("ultralytics")}, CUDA {Env("cuda")}";
    });

    await Step("Evaluation on Test", async () =>
    {
        var input = await project.Checks.PrepareEvaluationAsync(new ModelEvaluationRequest(versionId, datasetId, EvaluationSplit.Test, device, 8), "evaluation");
        var job = await RunJobAsync<IModelEvaluationJob, ModelEvaluationInput>(input, "evaluation", TimeSpan.FromMinutes(10),
            id => Path.Combine(root, "projects", project.Id, "checks", "jobs", id.ToString("D"), "logs"));
        var evaluation = await project.Checks.GetEvaluationAsync(job.Id);
        if (evaluation.State != ModelEvaluationState.Succeeded || evaluation.ImageCount != dataset.Counts.Test || !evaluation.Metrics.ContainsKey("mAP50"))
            throw new E2EException($"Evaluation {evaluation.State}, {evaluation.ImageCount} images, metrics {string.Join(", ", evaluation.Metrics.Keys)}.");
        var duration = (job.FinishedAt - job.StartedAt) ?? TimeSpan.Zero;
        return $"{evaluation.ImageCount} images, job {duration.TotalSeconds:N1} s, mAP50 {evaluation.Metrics["mAP50"]:N3}, "
            + $"mAP50-95 {evaluation.Metrics.GetValueOrDefault("mAP50_95"):N3}, {evaluation.ClassMetrics.Count} classes";
    });

    var datasetImages = Path.Combine(root, "projects", project.Id, "files", "datasets", datasetId.ToString("D"), "images");
    await Step("Image check", async () =>
    {
        var source = Directory.EnumerateFiles(Path.Combine(datasetImages, "test")).Order(StringComparer.Ordinal).First();
        var image = Path.Combine(inputs, "check" + Path.GetExtension(source));
        File.Copy(source, image);
        var check = await RunCheckAsync(project.Checks.StartImageCheckAsync(new ImageCheckRequest(versionId, Source(image), new LiveCheckParameters(Confidence, null, device))));
        if (check.Summary.FramesProcessed != 1 || check.Summary.Detections == 0) throw new E2EException($"Frames {check.Summary.FramesProcessed}, detections {check.Summary.Detections}.");
        return $"first frame {check.Progress.FirstFrameMs} ms after the worker start, {check.LastFrame?.Detections.Count} detections (max confidence "
            + $"{check.Summary.Classes.Max(item => item.MaxConfidence):N3}), "
            + $"inference {check.LastFrame?.InferenceMs} ms, total {check.Progress.ElapsedMs} ms";
    });

    await Step("Video check", async () =>
    {
        // A clip of dataset frames, so the model has something to find; 10 fps, every frame checked.
        var frames = Directory.CreateDirectory(Path.Combine(inputs, "frames")).FullName;
        var sources = Directory.EnumerateFiles(Path.Combine(datasetImages, "train"), "*.png").Order(StringComparer.Ordinal).Take(60).ToArray();
        for (var index = 0; index < sources.Length; index++) File.Copy(sources[index], Path.Combine(frames, $"f{index:D3}.png"));
        var video = Path.Combine(inputs, "check.mp4");
        await Assets.FfmpegAsync(ffmpeg, "-framerate", "10", "-i", Path.Combine(frames, "f%03d.png"), "-c:v", "libopenh264", "-pix_fmt", "yuv420p", video);
        var info = await core.Media.ProbeVideoAsync(video);
        var request = new VideoCheckRequest(versionId, Source(video) with { Width = info.Width, Height = info.Height, DurationMs = info.DurationMs, Fps = info.FrameRate },
            new LiveCheckParameters(Confidence, null, device), 100, null);
        var check = await RunCheckAsync(project.Checks.StartVideoCheckAsync(request));
        var processed = check.Summary.FramesProcessed;
        if (processed < sources.Length - 2 || check.Summary.FramesWithDetections == 0)
            throw new E2EException($"Frames {processed} of {sources.Length}, {check.Summary.FramesWithDetections} with detections.");
        var seconds = (check.Progress.ElapsedMs - (check.Progress.FirstFrameMs ?? 0)) / 1000.0;
        return $"{processed} frames {info.Width}×{info.Height}, {check.Summary.FramesWithDetections} with detections, first frame {check.Progress.FirstFrameMs} ms, "
            + $"{(processed - 1) / Math.Max(seconds, 0.001):N1} fps after the first frame, total {check.Progress.ElapsedMs} ms";
    });

    await Step("Screen check (5 s)", async () =>
    {
        var monitor = core.Screen.ListMonitors().First(item => item.IsPrimary);
        CheckRegion? region = monitor.Width > 320 || monitor.Height > 320 ? new CheckRegion(0, 0, 320, 320) : null;
        var check = await RunCheckAsync(project.Checks.StartScreenCheckAsync(new ScreenCheckRequest(versionId, monitor.Index,
            new LiveCheckParameters(Confidence, null, device), 10, 5, region)), TimeSpan.FromSeconds(90));
        if (check.StopReason != LiveCheckStopReason.TimeLimit || check.Summary.FramesProcessed < 5)
            throw new E2EException($"Screen check stopped by {check.StopReason} after {check.Summary.FramesProcessed} frames.");
        var seconds = (check.Progress.ElapsedMs - (check.Progress.FirstFrameMs ?? 0)) / 1000.0;
        return $"monitor {monitor.Index} {monitor.Width}×{monitor.Height}, {check.Summary.FramesProcessed} frames, "
            + $"{(check.Summary.FramesProcessed - 1) / Math.Max(seconds, 0.001):N1} fps after the first frame (requested 10), "
            + $"first frame {check.Progress.FirstFrameMs} ms, total {check.Progress.ElapsedMs} ms";
    });

    await Step("Model ZIP export", async () =>
    {
        var archive = Path.Combine(exports, $"model-v{version.Number}.zip");
        var input = await project.Training.PrepareExportAsync(versionId, archive, "model-export");
        var result = Result<ModelExportResult>(await RunJobAsync<IModelExportJob, ModelExportInput>(input, "model-export"));
        using var zip = ZipFile.OpenRead(archive);
        var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "weights/best.pt", "weights/best.onnx", "manifest.json", "classes.json", "README.md" })
            if (!names.Contains(required)) throw new E2EException($"The model archive has no {required}.");
        if (zip.GetEntry("weights/best.pt")!.Length != version.Files.Single(item => item.Kind == ModelFileKind.WeightsPt).ByteSize)
            throw new E2EException("weights/best.pt size differs from the version.");
        return $"{result.FileName}, {result.Bytes:N0} bytes, {zip.Entries.Count} entries";
    });

    var projectArchive = Path.Combine(exports, "project.zip");
    await Step("Project export", async () =>
    {
        var input = await core.Projects.PrepareExportAsync(project.Id, projectArchive, "project-export");
        var job = await core.Jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(input, project.Id, "project-export", CodedText.Plain(input.ProjectName));
        var result = Result<ProjectExportResult>(await WaitJobAsync(project.Id, job.Id, TimeSpan.FromMinutes(10), null));
        if (result.Links < dataset.Counts.Total) throw new E2EException($"{result.Links} links for {dataset.Counts.Total} hard-linked dataset frames.");
        return $"{result.Files:N0} files, {result.Links:N0} links, {result.Bytes:N0} bytes → archive {result.ArchiveBytes:N0} bytes in {result.ElapsedMs} ms";
    });

    await Step("Project import round trip", async () =>
    {
        var file = new FileInfo(projectArchive);
        var import = await core.Projects.StartImportAsync(new ProjectArchiveSource(file.FullName, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)), "project-import");
        var watch = Stopwatch.StartNew();
        while (import.Status == ProjectImportStatus.Running)
        {
            if (watch.Elapsed > TimeSpan.FromMinutes(10)) throw new E2EException("The import did not finish in 10 minutes.");
            await Task.Delay(50);
            import = core.Projects.GetImport(import.Id);
        }
        if (import.Status != ProjectImportStatus.Succeeded) throw new E2EException($"Import {import.Status}: {import.ErrorCode} {import.ErrorMessage}");
        importedProjectId = import.ProjectId!;
        var copy = await core.Projects.OpenAsync(importedProjectId);
        var copyDataset = await copy.Datasets.GetAsync(datasetId);
        var copyVersion = await copy.Training.GetVersionAsync(versionId);
        var copyClasses = (await copy.Classes.GetWorkspaceAsync()).Classes.Count;
        var copySet = await copy.Backgrounds.GetSetAsync(setId);
        if (copyDataset.ManifestHash != dataset.ManifestHash || copyClasses != 3 || copySet.ItemCount != 10
            || !copyVersion.Files.Select(item => (item.Name, item.Sha256)).SequenceEqual(version.Files.Select(item => (item.Name, item.Sha256))))
            throw new E2EException("The imported project differs from the original.");
        var weights = Path.Combine(root, "projects", importedProjectId, "files", "models", versionId.ToString("D"), "best.pt");
        if (Sha256(weights) != version.Files.Single(item => item.Kind == ModelFileKind.WeightsPt).Sha256) throw new E2EException("Imported best.pt differs.");
        var verify = await core.Jobs.EnqueueAsync<IDatasetVerifyJob, DatasetVerifyInput>(await copy.Datasets.PrepareVerifyAsync(datasetId, "copy-verify"), copy.Id, "copy-verify");
        var verified = Result<DatasetVerifyResult>(await WaitJobAsync(copy.Id, verify.Id, TimeSpan.FromMinutes(5), null));
        if (verified.State != DatasetIntegrityState.Verified || verified.IssueCount != 0) throw new E2EException($"Imported dataset: {verified.State}, {verified.IssueCount} issues.");
        return $"«{import.ProjectName}», {import.TotalFiles:N0} files, {(import.FinishedAt - import.StartedAt)?.TotalMilliseconds:N0} ms; dataset, model files and "
            + $"backgrounds match, imported dataset verified ({verified.CheckedFiles} files)";
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

Console.WriteLine();
Console.WriteLine(failed ? "E2E FAILED" : "E2E PASSED");
foreach (var (step, elapsed, detail) in timings) Console.WriteLine($"  {elapsed.TotalSeconds,7:N1} s  {step}");
Console.WriteLine($"  {total.Elapsed.TotalSeconds,7:N1} s  total");
SqliteConnection.ClearAllPools();
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
        catch (IOException) when (attempt < 40)
        {
            await Task.Delay(250);
        }
        catch (UnauthorizedAccessException) when (attempt < 40)
        {
            await Task.Delay(250);
        }
    }
    Console.WriteLine($"The work directory is removed: {work}");
}
// Core keeps background workers; the run is over.
Environment.Exit(failed ? 1 : 0);
return 0;

async Task Step(string name, Func<Task<string>> action)
{
    var watch = Stopwatch.StartNew();
    try
    {
        var detail = await action();
        timings.Add((name, watch.Elapsed, detail));
        Console.WriteLine($"PASS  {name} ({watch.Elapsed.TotalSeconds:N1} s): {detail}");
    }
    catch (Exception error)
    {
        timings.Add((name, watch.Elapsed, "failed"));
        Console.WriteLine($"FAIL  {name} ({watch.Elapsed.TotalSeconds:N1} s): {(error is E2EException or CoreException ? error.Message : error.ToString())}");
        if (error is CoreException core) Console.WriteLine($"      code {core.Code}");
        Console.WriteLine($"      Core log: {log.Path}");
        throw new StepFailedException();
    }
}

async Task<JobInfo> RunJobAsync<TJob, TInput>(TInput input, string operationId, TimeSpan? limit = null, Func<Guid, string>? logs = null)
    where TJob : IJobDefinition<TInput>
{
    var job = await core.Jobs.EnqueueAsync<TJob, TInput>(input, project.Id, operationId);
    return await WaitJobAsync(project.Id, job.Id, limit ?? TimeSpan.FromMinutes(5), logs);
}

async Task<JobInfo> WaitJobAsync(string projectId, Guid jobId, TimeSpan limit, Func<Guid, string>? logs)
{
    var watch = Stopwatch.StartNew();
    var reported = TimeSpan.Zero;
    while (true)
    {
        var job = await core.Jobs.GetAsync(projectId, jobId);
        if (job.Status == JobStatus.Succeeded) return job;
        if (job.Status is not (JobStatus.Queued or JobStatus.Running) || watch.Elapsed > limit)
        {
            var where = logs is null ? "" : $"; worker logs {logs(jobId)}";
            throw new E2EException($"Job {job.Kind} {job.OperationId} is {job.Status} after {watch.Elapsed.TotalSeconds:N0} s: {job.Error?.Code} {job.Error?.Message}{where}");
        }
        if (watch.Elapsed - reported > TimeSpan.FromSeconds(15))
        {
            reported = watch.Elapsed;
            Console.WriteLine($"      … {job.Kind} {job.Status} {job.Progress:N0}% {job.ProgressMessage?.Code ?? job.ProgressMessage?.Text}");
        }
        await Task.Delay(100);
    }
}

async Task<LiveCheck> RunCheckAsync(Task<LiveCheck> start, TimeSpan? limit = null)
{
    var check = await start;
    var watch = Stopwatch.StartNew();
    while (check.State is LiveCheckState.Preparing or LiveCheckState.Running or LiveCheckState.Cancelling)
    {
        if (watch.Elapsed > (limit ?? TimeSpan.FromMinutes(3)))
        {
            await project.Checks.StopCheckAsync(check.Id);
            throw new E2EException($"The {check.Mode} check is still {check.State} after {watch.Elapsed.TotalSeconds:N0} s.");
        }
        await Task.Delay(50);
        check = await project.Checks.GetCheckAsync(check.Id, 0);
    }
    if (check.State != LiveCheckState.Succeeded)
    {
        throw new E2EException($"The {check.Mode} check is {check.State}: {check.Error?.Code} {check.Error?.Message}; worker folder "
            + Path.Combine(root, "projects", project.Id, "checks", "live", check.Id.ToString("D")));
    }
    return check;
}

async Task SeedBaseModelAsync(string seed)
{
    var entry = JsonNode.Parse(await File.ReadAllTextAsync(catalog))!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "yolo26n")!;
    var expected = entry["sha256"]!.GetValue<string>();
    if (Sha256(seed) != expected) throw new E2EException($"{seed} is not the catalogue yolo26n.pt (SHA-256 {expected}).");
    var weights = Directory.CreateDirectory(Path.Combine(root, "BaseModels", "weights")).FullName;
    File.Copy(seed, Path.Combine(weights, "yolo26n.pt"));
}

static T Result<T>(JobInfo job) => job.Result is { } result
    ? result.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new E2EException($"Job {job.OperationId} has an empty result.")
    : throw new E2EException($"Job {job.OperationId} has no result.");

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static string Size(ModelVersion version, ModelFileKind kind) => $"{version.Files.Single(item => item.Kind == kind).ByteSize / 1048576.0:N1} MB";

static CheckSourceFile Source(string path)
{
    var info = new FileInfo(path);
    return new CheckSourceFile(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
}

internal sealed record Arguments(string AppDir, string WorkDirectory, string? BaseModelWeights, string Device, bool Keep)
{
    internal static Arguments? Parse(string[] args)
    {
        string? app = null, work = null, weights = null, device = "0";
        var keep = false;
        for (var index = 0; index < args.Length; index++)
        {
            string Next() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
            switch (args[index])
            {
                case "--app-dir": app = Next(); break;
                case "--work-directory": work = Next(); break;
                case "--base-model-weights": weights = Next(); break;
                case "--device": device = Next(); break;
                case "--keep": keep = true; break;
                default: return null;
            }
        }
        return app is null || work is null ? null : new Arguments(app, work, weights is null ? null : Path.GetFullPath(weights), device, keep);
    }
}

internal sealed class E2EException(string message) : Exception(message);

internal sealed class StepFailedException : Exception;
