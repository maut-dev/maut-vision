using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Hardware;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Interfaces.Projects.Training;
using MautVision.Core.Interfaces.Python;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;

/// <summary>
/// Training with a fake worker (tests/MautVision.FakeWorker, protocol v1 without PyTorch): runtime and parameter checks, global
/// presets, a run through preparing and training to a model version with history, log, effective parameters (a large line),
/// environment and files; singleCls and the working version; failure codes of the worker and of the protocol; cancellation that
/// ends the whole process tree; GPU exclusivity and the dataset blocker with hard-linked input; recovery after the application
/// ended (a published result accepted, otherwise interrupted) and a queue that survives the restart; job folder cleanup.
/// </summary>
internal static class TrainingSmoke
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

        // The parameter registry of the UI has the same keys, in the same order, with the same defaults as Core.
        var registry = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(catalog)!, "..", "ui", "src", "api", "projects", "training",
            "training-parameters.ts")));
        var uiParameters = System.Text.RegularExpressions.Regex.Matches(registry, @"\{ key: ""(\w+)"", ultralyticsKey: ""\w+"",[^\n]*?defaultValue: ([^,}]+)[,}]")
            .Select(match => (Key: match.Groups[1].Value, Default: match.Groups[2].Value.Trim())).ToArray();
        var mismatched = uiParameters.Where(item => !MautVision.Core.Training.TrainingParameterRules.Keys.Contains(item.Key) || !JsonNode.DeepEquals(JsonNode.Parse(item.Default.Replace("_", "")),
            JsonNode.Parse(MautVision.Core.Training.TrainingParameterRules.Default(item.Key).GetRawText()))).Select(item => item.Key).ToArray();
        Check(uiParameters.Select(item => item.Key).Order().SequenceEqual(MautVision.Core.Training.TrainingParameterRules.Keys.Order()) && mismatched.Length == 0
            , $"training-parameters.ts lists the keys and defaults of TrainingParameterRules ({uiParameters.Length} keys, mismatched: {string.Join(", ", mismatched)}).");

        // Hardware: nvidia-smi output parsing and a missing driver.
        var parsed = NvidiaSmi.ParseInventory("0, NVIDIA GeForce RTX 4090, 24564, 581.15\n1, Card, with comma, 8192, 581.15\nbad line\n");
        Check(parsed.Count == 2 && parsed[0] == new MautVision.Core.Interfaces.Hardware.GpuInfo(0, "NVIDIA GeForce RTX 4090", 24564, "581.15")
            && parsed[1].Name == "Card, with comma", "nvidia-smi lines are parsed; a malformed line is skipped.");
        var hardware = new HardwareManager(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
        {
            Candidates = [Path.Combine(fixture, "no-nvidia-smi.exe")],
            NvmlCandidates = [Path.Combine(fixture, "no-nvml.dll")],
        };
        var none = await hardware.ListGpusAsync();
        Check(!none.DriverAvailable && none.Gpus.Count == 0, "Without NVML and nvidia-smi there is no GPU and no error.");

        var root = Path.Combine(fixture, "tr");
        var weights = Path.Combine(root, "BaseModels", "weights");
        Directory.CreateDirectory(weights);
        var weightBytes = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(Path.Combine(weights, "yolo26n.pt"), weightBytes);
        // A catalogue whose yolo26n is the small fake file: VerifyIntegrityAsync passes without the real weights.
        var entries = JsonNode.Parse(await File.ReadAllTextAsync(catalog))!.AsArray();
        var entry = entries.Single(item => item!["id"]!.GetValue<string>() == "yolo26n")!;
        entry["byteSize"] = weightBytes.Length;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(weightBytes));
        var smokeCatalog = Path.Combine(fixture, "training-catalog.json");
        await File.WriteAllTextAsync(smokeCatalog, entries.ToJsonString());
        var configuration = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!);
        var fakeWorker = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MautVision.FakeWorker", "bin", configuration!, "net10.0",
            "MautVision.FakeWorker.exe"));
        Check(File.Exists(fakeWorker), "The fake worker is built next to the smoke.");
        async Task<ICore> CoreWith(PythonWorkerLaunch? launch)
        {
            var created = construct(new CoreOptions(root, smokeCatalog) { PythonWorker = launch });
            await created.InitializeAsync();
            return created;
        }

        var core = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        Check(core.Python.GetStatus() is { Configured: true, Available: true, Mode: "packaged", Code: null }, "The runtime status names the packaged worker.");
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Обучение", "", "yolo26n", new InputSize(64, 64)));
        await RejectsCode("training.project_not_open", () => project.Training.ListRunsAsync());
        await core.Projects.OpenAsync(project.Id);
        var events = new ConcurrentQueue<TrainingChangedEventArgs>();
        core.Projects.TrainingChanged += (_, args) => events.Enqueue(args);
        var version = await BuildDatasetAsync(core, project, Path.Combine(fixture, "tr-sources"));
        var dataset = version.Id;
        Task Rename(string name) => SetNameAsync(project, dataset, name);
        Check(version.ClassMap.Count == 2, "The dataset has two classes.");

        // Runtime: not configured, missing files.
        var bare = await CoreWith(null);
        Check(bare.Python.GetStatus() is { Configured: false, Available: false, Code: "runtime_not_configured" }, "Without a launch description the runtime is not configured.");
        var bareProject = await bare.Projects.OpenAsync(project.Id);
        await RejectsCode("training.runtime_not_configured", () => bareProject.Training.PrepareRunAsync(new TrainingRunRequest(dataset, Params()), "bare"));
        var missing = await CoreWith(PythonWorkerLaunch.Packaged(Path.Combine(fixture, "missing-worker.exe")));
        var missingProject = await missing.Projects.OpenAsync(project.Id);
        await RejectsCode("training.runtime_missing", () => missingProject.Training.PrepareRunAsync(new TrainingRunRequest(dataset, Params()), "missing"));
        Check(PythonWorkerLaunch.Interpreter("C:/python.exe", "C:/src") is { Arguments: ["-m", "worker"], SourceDirectory: "C:/src", Mode: "interpreter" },
            "The interpreter launch runs -m worker with the sources on PYTHONPATH.");

        // Presets: global, unique by name, overwrite, delete, persisted.
        var presets = core.TrainingPresets;
        await RejectsCode("training.preset_name_invalid", () => presets.SaveAsync("  ", Params()));
        var invalidPreset = await RejectsCode("training.parameter_invalid", () => presets.SaveAsync("Плохой", Params(("mosaic", 2))));
        Check(invalidPreset.Details?["field"] == "mosaic", "An invalid preset value names its field.");
        var first = await presets.SaveAsync(" Быстрый ", Params(("epochs", 5), ("batch", 8)));
        var other = await presets.SaveAsync("Точный", Params(("epochs", 200)));
        var overwritten = await presets.SaveAsync("быстрый", Params(("epochs", 7)));
        Check(overwritten.Id == first.Id && overwritten.Name == "быстрый" && overwritten.Parameters["epochs"].GetInt32() == 7 && !overwritten.Parameters.ContainsKey("batch"),
            "Saving under an existing name (any case) overwrites that preset.");
        Check((await presets.ListAsync()).Select(item => item.Id).SequenceEqual([first.Id, other.Id]), "Presets are listed by name.");
        await presets.DeleteAsync(other.Id);
        await RejectsCode("training.preset_not_found", () => presets.DeleteAsync(other.Id));
        var reread = await (await CoreWith(null)).TrainingPresets.ListAsync();
        Check(reread.Count == 1 && reread[0].Id == first.Id && reread[0].Parameters["epochs"].GetInt32() == 7 && File.Exists(Path.Combine(root, "training", "presets.json")),
            "Presets live in the work directory and survive a restart.");

        // Parameters and the dataset are checked before the run is queued.
        var training = project.Training;
        foreach (var (key, value) in new (string, object?)[] { ("epochs", 0), ("batch", 1.5), ("device", "gpu"), ("unknown", 1), ("compile", true), ("scale", new[] { 0.5, 0.2 }) })
        {
            var error = await RejectsCode("training.parameter_invalid", () => training.PrepareRunAsync(new TrainingRunRequest(dataset, Params((key, value))), "invalid-" + checks));
            Check(error.Details?["field"] == key, $"An invalid parameter names {key}.");
        }
        await RejectsCode("training.parameter_invalid", () => training.PrepareRunAsync(new TrainingRunRequest(dataset, Params(("classes", new[] { 5 }))), "classes"));
        await RejectsCode("datasets.unavailable", () => training.PrepareRunAsync(new TrainingRunRequest(Guid.NewGuid(), Params()), "no-dataset"));
        var current = await project.Datasets.GetAsync(dataset);
        current = await project.Datasets.SetArchivedAsync(dataset, true, current.Revision);
        await RejectsCode("datasets.archived", () => training.PrepareRunAsync(new TrainingRunRequest(dataset, Params()), "archived"));
        await project.Datasets.SetArchivedAsync(dataset, false, current.Revision);
        var defaults = await training.PrepareRunAsync(new TrainingRunRequest(dataset, Params(("epochs", 3)), "Быстрый"), "defaults");
        Check(defaults.Parameters["optimizer"].GetString() == "AdamW" && defaults.Parameters["mosaic"].GetDouble() == 0 && defaults.Parameters["cls"].GetDouble() == 1
            && defaults.Parameters["lr0"].ValueKind == JsonValueKind.Null && defaults.ImageSize == 64 && defaults.Architecture == "yolo26n" && defaults.PresetName == "Быстрый",
            "A run gets every parameter with the defaults tuned in the worker.");

        // A successful run: preparing → training → a model version.
        await Rename("Успех");
        var ok = await StartAsync(core, project, Params(("epochs", 3), ("batch", 4)), "run-ok");
        Check((await training.PrepareRunAsync(new TrainingRunRequest(dataset, Params(("epochs", 99))), "run-ok")).Parameters["epochs"].GetInt32() == 3,
            "A repeated start of the operation returns its input.");
        var done = await WaitAsync(core.Jobs, project.Id, ok, JobStatus.Succeeded);
        var run = await training.GetRunAsync(ok);
        Check(run.State == TrainingRunState.Succeeded && run.ResultVersionId is { } && run.EpochCount == 3 && run.LogCount > 5 && run.Progress.Epochs == 3
            && run.Dataset.Name == "Успех" && run.Dataset.VersionId == dataset && done.Progress == 100 && done.ProgressMessage?.Code == "progress.saving_version",
            "The run ends with a version, its epochs and its log.");
        Check(run.EffectiveParams is { } effective && effective.GetProperty("padding").GetString()!.Length == 300_000 && effective.GetProperty("optimizer").GetString() == "AdamW",
            "A large effectiveParams line is accepted and stored.");
        Check(run.Environment is { } environment && environment.GetProperty("deviceName").GetString() == "Поддельная видеокарта" && environment.GetProperty("cuda").GetString() == "13.0",
            "The environment of the worker is stored with the run.");
        Check(done.Result?.Deserialize<TrainingRunResult>(JobsManager.Json)?.VersionId == run.ResultVersionId, "The job result names the version.");
        var page = await training.ListEpochsAsync(ok, limit: 2);
        var rest = await training.ListEpochsAsync(ok, page.NextAfter, 2);
        Check(page.Items.Select(item => item.Epoch).SequenceEqual([1, 2]) && page.Total == 3 && page.NextAfter == 2 && rest.Items.Single().Epoch == 3 && rest.NextAfter is null
            && rest.Items[0].Metrics["mAP50"] > 0.149 && rest.Items[0].Losses.ContainsKey("box_loss") && rest.Items[0].Fitness > 0.089, "History pages go by epoch.");
        var tail = await training.TailLogAsync(ok, 3);
        var log = await training.ListLogAsync(ok, limit: 1000);
        Check(tail.Items.Count == 3 && tail.Items[^1].Message is { Code: "trainingLog.version_created", Args: { } created } && created["number"] == "1" && log.Items.Count == log.Total && log.Total == run.LogCount
            && log.Items.Any(item => item.Kind == TrainingLogKind.Stage && item.Message.Text == "training") && log.Items[0].Message.Code == "trainingLog.preparing_folder"
            && log.Items.Any(item => item.Kind == TrainingLogKind.Log && item.Message.Text!.Contains("box_loss")), $"The log keeps stages, warnings, console lines and system lines ({log.Total}/{run.LogCount}: {string.Join(" | ", log.Items.Select(item => item.Sequence + ":" + item.Kind + ":" + (item.Message.Code ?? item.Message.Text)))}).");
        var first1 = await training.GetVersionAsync(run.ResultVersionId!.Value);
        var versionFolder = Path.Combine(root, "projects", project.Id, "files", "models", first1.Id.ToString("D"));
        Check(first1.Number == 1 && first1.IsWorking && first1.RunId == ok && first1.EpochsCompleted == 3 && !first1.StoppedEarly && first1.BestEpoch == 3
            && first1.Classes.Select(item => (item.Index, item.ClassId)).SequenceEqual(version.ClassMap.Select(item => (item.Index, (Guid?)item.ClassId)))
            && first1.Notes.Single() is { Code: "worker.fake_warning", Text: "Fake warning." } && first1.Metrics["mAP50"] == 0.3 && first1.Dataset.ManifestHash == version.ManifestHash
            && first1.Parameters["batch"].GetInt32() == 4 && first1.Environment.GetProperty("torch").GetString() == "2.14.0+fake", "The first version is the working one with its passport.");
        Check(first1.Files.Select(file => file.Name).Order().SequenceEqual(["best.onnx", "best.pt", "plots/results.png", "training.log"])
            && first1.Files.All(file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine([versionFolder, .. file.Name.Split('/')])))) == file.Sha256),
            "Artifacts and reports are stored under files/models/<versionId>/ with their hashes.");
        var plot = first1.Files.Single(file => file.Kind == ModelFileKind.Plot);
        await using (var content = await project.Resources.OpenAsync(new ResourceReference(ModelVersion.ResourceKind, plot.ResourceId), ResourceVariant.Original))
        {
            Check(content.ContentType == "image/png", "A plot of the version is served by the resource channel.");
        }
        Check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "training", "jobs", ok.ToString("D"))), "The job folder is removed after acceptance.");
        Check(events.Any(item => item.Runs.Any(change => change.Id == ok)) && events.Any(item => item.Versions.Any(change => change.Id == first1.Id)),
            "TrainingChanged names the run and the new version.");

        // singleCls: one merged output; the working version moves explicitly.
        var single = await StartAsync(core, project, Params(("epochs", 1), ("singleCls", true)), "run-single");
        await WaitAsync(core.Jobs, project.Id, single, JobStatus.Succeeded);
        var second = await training.GetVersionAsync((await training.GetRunAsync(single)).ResultVersionId!.Value);
        Check(second.Number == 2 && !second.IsWorking && second.Classes is [{ Index: 0, ClassId: null, Name: "item" } merged] && merged.SourceClassIds.Count == 2,
            "singleCls gives one merged class that stands for the selected classes.");
        var working = await training.SetWorkingVersionAsync(second.Id);
        Check(working.IsWorking && !(await training.GetVersionAsync(first1.Id)).IsWorking && (await training.ListVersionsAsync()).Select(item => item.Number).SequenceEqual([2, 1]),
            "Exactly one version is working; versions are listed newest first.");

        // Failures: the worker's code, its English message and the text for the UI, protocol violations; no version, logs kept.
        foreach (var (scenario, code, text) in new[]
        {
            ("fail=out_of_memory", "training.worker_out_of_memory", "Worker message: out_of_memory."),
            ("crash", "training.worker_crashed", "fake failure"),
            ("badsha", "training.artifact_integrity", "SHA-256"),
            ("sequence", "training.protocol_sequence", "order"),
            ("exit0", "training.result_missing", "result.json"),
        })
        {
            await Rename("fake:" + scenario);
            var failing = await StartAsync(core, project, Params(("epochs", 2)), "run-" + scenario);
            var failed = await WaitAsync(core.Jobs, project.Id, failing, JobStatus.Failed);
            var failedRun = await training.GetRunAsync(failing);
            var folder = Path.Combine(root, "projects", project.Id, "training", "jobs", failing.ToString("D"));
            Check(failed.Error?.Code == code && failedRun.State == TrainingRunState.Failed && failedRun.Error?.Code == code && failedRun.Error.Message.Contains(text)
                && (scenario != "fail=out_of_memory" || failedRun.Error.Worker is { Code: "worker.out_of_memory.fake", Text: "Worker message: out_of_memory.", Args: { } workerArgs }
                    && workerArgs["code"] == "out_of_memory")
                && (scenario == "fail=out_of_memory" || failedRun.Error.Worker is null)
                && failedRun.ResultVersionId is null && File.Exists(Path.Combine(folder, "logs", "stderr.log")) && !Directory.Exists(Path.Combine(folder, "input")),
                $"{scenario}: the run fails with {code}, keeps its logs and drops its inputs ({failed.Error?.Code}: {failedRun.Error?.Message}).");
        }
        Check((await training.ListVersionsAsync()).Count == 2 && Directory.GetDirectories(Path.Combine(root, "projects", project.Id, "files", "models")).Length == 2,
            "Failed runs leave no version and no model files.");

        // Cancellation ends the whole tree; one GPU job at a time; the dataset is blocked and linked while training reads it.
        await Rename("fake:hang");
        var hanging = await StartAsync(core, project, Params(("epochs", 5)), "run-hang");
        var queued = await StartAsync(core, project, Params(("epochs", 5)), "run-queued");
        var hangFolder = Path.Combine(root, "projects", project.Id, "training", "jobs", hanging.ToString("D"));
        var childPid = await WaitForFileAsync(Path.Combine(hangFolder, "logs", "child.pid"));
        await WaitUntilAsync(async () => (await training.GetRunAsync(hanging)).EpochCount == 1 && (await core.Jobs.GetAsync(project.Id, hanging)).ProgressMessage is { Code: "progress.epoch", Args: { } hangingArgs }
            && hangingArgs["epoch"] == "1" && hangingArgs["total"] == "5",
            "the first epoch of the hanging run");
        await WaitUntilAsync(async () => (await training.ListLogAsync(hanging, limit: 1000)).Items.Any(item => item.Kind == TrainingLogKind.Warning
            && item.Message is { Code: "worker.fake_hang", Text: "The fake worker waits for cancellation." }), "the warning of the hanging run as a code with its message");
        var hangingRun = await training.GetRunAsync(hanging);
        Check(hangingRun.State == TrainingRunState.Training && hangingRun.Progress.Epoch == 1 && hangingRun.Progress.Epochs == 5
            && (await core.Jobs.GetAsync(project.Id, hanging)).Progress == 20, "A running run reports its epoch.");
        Check((await core.Jobs.GetAsync(project.Id, queued)).Status == JobStatus.Queued && (await training.GetRunAsync(queued)).State == TrainingRunState.Queued,
            "A second training waits for the GPU.");
        var review = await project.Datasets.ReviewDeletionAsync(dataset);
        Check(review.Blockers.Any(blocker => blocker.Code == "training.active" && blocker.Count == 2), "Queued and running training block deleting the dataset.");
        var linked = Directory.EnumerateFiles(Path.Combine(hangFolder, "input", "dataset", "images"), "*", SearchOption.AllDirectories).First();
        Check(DatasetsSmoke.LinkCount(linked) >= 2, "The dataset input of the job is hard-linked.");
        int workerPid;
        using (var reader = new StreamReader(new FileStream(Path.Combine(hangFolder, "logs", "stderr.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            workerPid = int.Parse(reader.ReadLine()!.Split(' ')[2].TrimEnd(','));
        await core.Jobs.CancelAsync(project.Id, queued);
        await core.Jobs.CancelAsync(project.Id, hanging);
        Check((await training.GetRunAsync(hanging)).State is TrainingRunState.Cancelling or TrainingRunState.Cancelled, "A cancelled run shows «Отмена…».");
        await WaitAsync(core.Jobs, project.Id, hanging, JobStatus.Cancelled);
        Check(Exited(workerPid) && Exited(childPid), "Cancellation ends the worker and its child process.");
        Check((await training.GetRunAsync(hanging)).State == TrainingRunState.Cancelled && (await training.GetRunAsync(queued)).State == TrainingRunState.Cancelled
            && !Directory.Exists(Path.Combine(hangFolder, "input")) && (await project.Datasets.ReviewDeletionAsync(dataset)).Blockers.Count == 0,
            "Cancelled runs release the dataset and their inputs.");

        // The application ends during training: a published result is accepted, otherwise the run is interrupted; the queue survives.
        await Rename("fake:publish-hang");
        var dying = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var dyingProject = await dying.Projects.OpenAsync(project.Id);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying).TrainingRuns.Pause = async (point, _) =>
        {
            paused.TrySetResult();
            await new TaskCompletionSource().Task;
        };
        var published = await StartAsync(dying, (Project)dyingProject, Params(("epochs", 2)), "run-published");
        var waiting = await StartAsync(dying, (Project)dyingProject, Params(("epochs", 2)), "run-waiting");
        await WaitForFileAsync(Path.Combine(root, "projects", project.Id, "training", "jobs", published.ToString("D"), "output", "result.json"));
        // Closing the job objects is what the system does when the host dies: the worker tree ends.
        ((MautVision.Core.Core)dying).PythonWorkers.CloseAllForTesting();
        await paused.Task.WaitAsync(WaitLimit);
        await SetNameAsync((Project)dyingProject, dataset, "Успех после перезапуска");
        // The application is gone: its deferred writes (job progress, the recorder) must not land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        var revived = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var revivedProject = (Project)await revived.Projects.OpenAsync(project.Id);
        var accepted = await revived.Jobs.GetAsync(project.Id, published);
        var acceptedRun = await revivedProject.Training.GetRunAsync(published);
        Check(accepted.Status == JobStatus.Succeeded && acceptedRun.State == TrainingRunState.Succeeded && acceptedRun.ResultVersionId is { } recovered
            && (await revivedProject.Training.GetVersionAsync(recovered)).Number == 3, "A result published before the application ended becomes a version on open "
            + $"(job {accepted.Status}, run {acceptedRun.State} {acceptedRun.Error?.Code}, version {acceptedRun.ResultVersionId}).");
        await WaitAsync(revived.Jobs, project.Id, waiting, JobStatus.Succeeded);
        Check((await revivedProject.Training.GetRunAsync(waiting)) is { State: TrainingRunState.Succeeded, ResultVersionId: not null }, "A queued run survives the restart and runs after it.");

        await SetNameAsync(revivedProject, dataset, "fake:hang");
        var dying2 = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var dying2Project = (Project)await dying2.Projects.OpenAsync(project.Id);
        var paused2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying2).TrainingRuns.Pause = async (_, _) =>
        {
            paused2.TrySetResult();
            await new TaskCompletionSource().Task;
        };
        var lost = await StartAsync(dying2, dying2Project, Params(("epochs", 3)), "run-lost");
        var lostFolder = Path.Combine(root, "projects", project.Id, "training", "jobs", lost.ToString("D"));
        var lostChild = await WaitForFileAsync(Path.Combine(lostFolder, "logs", "child.pid"));
        await WaitUntilAsync(async () => (await dying2Project.Training.GetRunAsync(lost)).EpochCount == 1, "the saved epoch of the lost run");
        ((MautVision.Core.Core)dying2).PythonWorkers.CloseAllForTesting();
        await paused2.Task.WaitAsync(WaitLimit);
        Check(Exited(lostChild), "Closing the job object ends the worker tree as the death of the host does.");
        await ((MautVision.Core.Core)dying2).EndForTestingAsync();
        var revived2 = await CoreWith(PythonWorkerLaunch.Packaged(fakeWorker));
        var revived2Project = (Project)await revived2.Projects.OpenAsync(project.Id);
        var lostRun = await revived2Project.Training.GetRunAsync(lost);
        Check(lostRun.State == TrainingRunState.Interrupted && lostRun.Error?.Code == "jobs.interrupted" && lostRun.ResultVersionId is null && lostRun.EpochCount == 1,
            "A run without a published result is interrupted; its saved epochs stay.");
        await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(Path.Combine(lostFolder, "input"))), "the interrupted folder cleanup");
        Check(File.Exists(Path.Combine(lostFolder, "logs", "stderr.log")), "The interrupted run keeps its logs.");
        var listed = await revived2Project.Training.ListRunsAsync(limit: 3);
        Check(listed.Total == 12 && listed.Items.Count == 3 && listed.Items[0].Id == lost, "Runs are listed newest first with the total.");
        return checks;
    }

    internal static Dictionary<string, JsonElement> Params(params (string Key, object? Value)[] values)
        => values.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal);

    internal static async Task<Guid> StartAsync(ICore core, Project project, Dictionary<string, JsonElement> parameters, string operationId)
    {
        var input = await project.Training.PrepareRunAsync(new TrainingRunRequest((await project.Datasets.ListAsync())[0].Id, parameters), operationId);
        return (await core.Jobs.EnqueueAsync<ITrainingRunJob, TrainingRunInput>(input, project.Id, operationId)).Id;
    }

    internal static async Task SetNameAsync(Project project, Guid versionId, string name)
    {
        var current = await project.Datasets.GetAsync(versionId);
        await project.Datasets.UpdateAsync(versionId, name, "", current.Revision);
    }

    /// <summary>Two classes, eight annotated frames, a built dataset version of the project input size.</summary>
    internal static async Task<DatasetVersion> BuildDatasetAsync(ICore core, Project project, string sources)
    {
        Directory.CreateDirectory(sources);
        var day = await project.Variants.CreateAsync("День", "");
        var cat = await project.Classes.CreateAsync("Кошка", "");
        var dog = await project.Classes.CreateAsync("Собака", "");
        var catDay = await project.Classes.AddVariantAsync(cat.Id, day.Id);
        var dogDay = await project.Classes.AddVariantAsync(dog.Id, day.Id);
        var group = await project.Images.CreateGroupAsync("Кадры обучения", "", ImageSplitHint.Unassigned);
        var paths = Enumerable.Range(0, 8).Select(index =>
        {
            var path = Path.Combine(sources, $"t{index}.jpg");
            File.WriteAllBytes(path, ImagesSmoke.Jpeg(64, 64, 500 + index));
            return path;
        }).ToArray();
        var import = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray(), SkipDuplicates: false), project.Id, "training-import");
        await WaitAsync(core.Jobs, project.Id, import.Id, JobStatus.Succeeded);
        var index = 0;
        foreach (var image in (await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 100)).Items)
        {
            var (klass, binding) = index++ % 2 == 0 ? (cat.Id, catDay.Id) : (dog.Id, dogDay.Id);
            await project.Images.SaveAnnotationAsync(image.Id, new AnnotationInput([new AnnotationObjectInput(null, klass, binding, new ImageBox(0.2, 0.2, 0.5, 0.5))],
                null, ImageReviewState.Reviewed), image.AnnotationRevision);
        }
        var spec = new DatasetSpecification("Обучение", "", [group.Id], [cat.Id, dog.Id], [catDay.Id, dogDay.Id], [], DatasetUnassignedVariantRole.Target,
            DatasetUnselectedClassRole.Negative, false, false, 5, 0.25, 0);
        var plan = await project.Datasets.PlanAsync(spec, "training-plan");
        var build = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, "training-build"),
            project.Id, "training-build");
        await WaitAsync(core.Jobs, project.Id, build.Id, JobStatus.Succeeded);
        return await project.Datasets.GetAsync(build.Id);
    }

    internal static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    internal static async Task<int> WaitForFileAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = (await File.ReadAllTextAsync(path)).Trim();
                    if (text.Length > 0) return int.TryParse(text, out var value) ? value : 0;
                }
                catch (IOException)
                {
                    // Still being written.
                }
            }
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"{Path.GetFileName(path)} did not appear.");
            await Task.Delay(20);
        }
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    internal static async Task<JobInfo> WaitAsync(IJobsManager jobs, string projectId, Guid jobId, JobStatus status)
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
}
