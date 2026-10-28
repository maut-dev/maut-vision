using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Datasets;
using MautVision.Core.Projects.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Datasets: the split hashing against reference values (computed with an independent JavaScript implementation), labels and
/// data.yaml byte for byte; specification checks; roles of objects, archived classes and variants turned into negatives, empty
/// frames, partial generation, split hints with the mixed-source warning, insufficient samples, the class index order; plan
/// staleness; builds with hard links and oriented PNG (EXIF-rotated JPEG, WebP, BMP), split collisions, cancel and failure that
/// leave nothing, a restart in the middle without duplicates; sample pages and resources; integrity checks listing every
/// problem; ZIP export layout, overwrite and cancel; archive rules; deletion blockers, two-phase deletion and the kept passport;
/// the training source and materialization.
/// </summary>
internal static class DatasetsSmoke
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(180);

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

        CheckAlgorithm(Check);

        // Deeper than MAX_PATH a hard link is still a link and not a silent copy (a deep work directory doubled every dataset frame).
        var deep = Directory.CreateDirectory(Path.Combine(fixture, "deep-links", new string('d', 120), new string('e', 120))).FullName;
        await File.WriteAllBytesAsync(Path.Combine(deep, "source.png"), [1, 2, 3]);
        var deepTarget = Path.Combine(deep, "target.png");
        Check(deepTarget.Length > 260 && DatasetFiles.Link(Path.Combine(deep, "source.png"), deepTarget), "A hard link longer than MAX_PATH is created.");

        var root = Path.Combine(fixture, "datasets-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var sources = Path.Combine(fixture, "datasets-sources");
        Directory.CreateDirectory(sources);
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Датасеты", "", "yolo26n", new InputSize(320, 256)));
        await RejectsCode("datasets.project_not_open", () => project.Datasets.ListAsync());
        await RejectsCode("datasets.project_not_open", () => project.Datasets.GetSummaryAsync());
        await core.Projects.OpenAsync(project.Id);
        var datasets = project.Datasets;
        Check(await datasets.GetSummaryAsync() == new DatasetsSummary(0, 0, 0), "A new project counts no datasets.");
        var events = new ConcurrentQueue<DatasetsChangedEventArgs>();
        core.Projects.DatasetsChanged += (_, args) => events.Enqueue(args);
        var implementation = (MautVision.Core.Core)core;
        var builds = implementation.DatasetBuilds;
        var fx = await CreateFixtureAsync(core, project, sources, Check);

        // Specification: every field is checked and named.
        var spec = new DatasetSpecification("  Датасет  ", "  Описание  ", [fx.Unassigned, fx.TrainGroup, fx.ValidationGroup, fx.TestGroup, fx.Generated, fx.Partial],
            [fx.Cat, fx.Dog], [fx.CatDay, fx.DogDay], [], DatasetUnassignedVariantRole.Target, DatasetUnselectedClassRole.Negative, false, false, 7, 0.3, 0.2);
        var invalid = new (string Code, string Field, DatasetSpecification Value)[]
        {
            ("datasets.name_invalid", "name", spec with { Name = "  " }),
            ("datasets.name_invalid", "name", spec with { Name = new string('я', 121) }),
            ("datasets.description_invalid", "description", spec with { Description = new string('x', 4001) }),
            ("datasets.selection_invalid", "groupIds", spec with { GroupIds = [] }),
            ("datasets.selection_invalid", "groupIds", spec with { GroupIds = [fx.Unassigned, fx.Unassigned] }),
            ("datasets.selection_invalid", "classIds", spec with { ClassIds = [] }),
            ("datasets.selection_invalid", "classIds", spec with { ClassIds = [Guid.Empty] }),
            ("datasets.variants_invalid", "variants", spec with { NegativeVariantIds = [fx.CatDay] }),
            ("datasets.roles_invalid", "roles", spec with { UnassignedVariantRole = (DatasetUnassignedVariantRole)9 }),
            ("datasets.split_invalid", "ratios", spec with { ValRatio = 0 }),
            ("datasets.split_invalid", "ratios", spec with { TestRatio = -0.1 }),
            ("datasets.split_invalid", "ratios", spec with { ValRatio = 0.5, TestRatio = 0.5 }),
            ("datasets.split_invalid", "ratios", spec with { ValRatio = double.NaN }),
        };
        foreach (var (code, field, value) in invalid)
        {
            var error = await RejectsCode(code, () => datasets.PlanAsync(value, "invalid-" + checks));
            Check(error.Details?["field"] == field, $"{code} names the field {field} ({error.Details?["field"]}).");
        }
        await RejectsCode("datasets.variants_invalid", () => datasets.PlanAsync(spec with { TargetVariantIds = [Guid.NewGuid()] }, "unknown-variant"));
        await RejectsCode("datasets.group_unavailable", () => datasets.PlanAsync(spec with { GroupIds = [fx.Unassigned, Guid.NewGuid()] }, "missing-group"));
        await RejectsCode("datasets.group_unavailable", () => datasets.PlanAsync(spec with { GroupIds = [fx.Archived] }, "archived-group"));
        await RejectsCode("datasets.class_unavailable", () => datasets.PlanAsync(spec with { ClassIds = [fx.Cat, fx.Old] }, "archived-class"));
        await RejectsCode("datasets.unselected_class", () => datasets.PlanAsync(spec with { UnselectedClassRole = DatasetUnselectedClassRole.Reject }, "reject-class"));
        await RejectsCode("datasets.unassigned_variant", () => datasets.PlanAsync(spec with { UnassignedVariantRole = DatasetUnassignedVariantRole.Reject }, "reject-variant"));
        await RejectsCode("datasets.variant_role_missing", () => datasets.PlanAsync(spec with { TargetVariantIds = [fx.CatDay] }, "role-missing"));

        // The plan: skip reasons, roles, splits by hints and by hashing, class indexes in catalog order.
        var plan = await datasets.PlanAsync(spec, "plan-strict");
        Check((await datasets.PlanAsync(spec with { Seed = 1 }, "plan-strict")).Id == plan.Id, "A repeated plan operation returns its plan.");
        Check(plan.Specification.Name == "Датасет" && plan.Specification.Description == "Описание" && plan.Width == 320 && plan.Height == 256,
            "The plan trims the texts and takes the frame size of the project input.");
        Check(plan.Skipped == new DatasetPlanSkips(1, 1, 2, 0), $"Skips: unreviewed 1, partial generation 1, without targets 2 ({plan.Skipped}).");
        Check(plan.Counts.Total == 22 && plan.Classes.Select(item => (item.Index, item.ClassId)).SequenceEqual([(0, fx.Dog), (1, fx.Cat)])
            && plan.Classes[1].Name == "Кошка \"Мурка\"", "22 frames; class indexes follow the catalog order (Dog before Cat after the reorder), not the request.");
        Check(plan.Warnings.Count == 0 && plan.SufficientSamples, "A clean plan has no warnings.");
        Check(plan.Composition.Groups.Select(group => group.Images).SequenceEqual([17, 1, 1, 1, 2, 0]) && plan.Composition.FromGenerated == 2
            && plan.Composition.Boxes == 23 && plan.Composition.BoxesPerClass.Select(item => item.Boxes).SequenceEqual([5, 18]) && plan.Composition.Negatives == 1,
            "The plan composition counts groups, origins, boxes per class and negatives.");
        var lenient = await datasets.PlanAsync(spec with { IncludeReviewedEmpty = true, AllowPartialGeneration = true }, "plan-lenient");
        Check(lenient.Counts.Total == 25 && lenient.Skipped == new DatasetPlanSkips(1, 0, 0, 0) && lenient.Composition.WithoutBoxes == 2,
            "Empty frames and partial generations are included on request; a frame with only demoted objects is empty.");
        Check(lenient.Warnings.SingleOrDefault(warning => warning.Code == "datasets.objects_as_negatives")?.Count == 2 && lenient.Composition.Negatives == 3,
            "Objects of an archived class and an archived variant become negatives and are reported.");
        var buildSpec = spec with { IncludeReviewedEmpty = true };
        var buildPlan = await datasets.PlanAsync(buildSpec, "plan-build");
        Check(buildPlan.Counts.Total == 24, "The build plan has 24 frames.");

        // Splits: hinted groups go wholly to their split, unassigned frames by the reference hashing.
        var frames = await ReadFramesAsync(project, buildSpec);
        Check(frames[fx.Images["i.jpg"]] == DatasetSplit.Train && frames[fx.Images["j.jpg"]] == DatasetSplit.Validation && frames[fx.Images["t.jpg"]] == DatasetSplit.Test,
            "Groups with a split hint go to their split.");
        var unassigned = fx.UnassignedFrames.Where(frame => frames.ContainsKey(frame.Id)).ToArray();
        var expected = DatasetSelection.Split(unassigned.Select(frame => (frame.Family, frame.PixelHash)).ToArray(), 7, 0.3, 0.2);
        Check(unassigned.Length == 21 && unassigned.Select((frame, index) => frames[frame.Id] == expected[index]).All(same => same),
            "Unassigned frames (manual and generated) follow the reference hashing.");
        var insufficient = await datasets.PlanAsync(spec with { GroupIds = [fx.TrainGroup] }, "plan-insufficient");
        Check(!insufficient.SufficientSamples && insufficient.Warnings[0] == new DatasetPlanWarning("datasets.insufficient_samples", 0),
            "A plan without validation frames warns about insufficient samples.");

        // A source shared by a hinted group and an unassigned one: the mixed warning, and a collision when pixels land in two splits.
        DatasetPlan? collided = null, mixedOnly = null;
        for (uint seed = 0; seed < 64 && (collided is null || mixedOnly is null); seed++)
        {
            var candidate = await datasets.PlanAsync(spec with { GroupIds = [fx.Unassigned, fx.Duplicate], Seed = seed, ValRatio = 0.45, TestRatio = 0.45 }, "mixed-" + seed);
            Check(candidate.Warnings.Any(warning => warning is { Code: "datasets.mixed_source_splits", Count: 2 }), "A source in a hinted and an unassigned group is reported.");
            if (candidate.Warnings.Any(warning => warning.Code == "datasets.split_collision")) collided ??= candidate;
            else mixedOnly ??= candidate;
        }
        Check(collided is not null && mixedOnly is not null && collided.Warnings.Single(warning => warning.Code == "datasets.split_collision").Count == 2,
            "Identical pixels in two splits are a collision; in one split only the mixed warning remains.");
        await RejectsCode("datasets.split_collision", () => datasets.PrepareBuildAsync(collided!.Id, collided.Hash, "collided"));

        // Build checks: hash, staleness of images and catalog, size, empty plan.
        await RejectsCode("datasets.revision_conflict", () => datasets.PrepareBuildAsync(buildPlan.Id, "0", "wrong-hash"));
        await RejectsCode("datasets.unavailable", () => datasets.PrepareBuildAsync(Guid.NewGuid(), "0", "missing-plan"));
        var stalePlan = await datasets.PlanAsync(buildSpec, "plan-stale");
        var x00 = await project.Images.GetImageAsync(fx.Images["x00.jpg"]);
        var annotation = await project.Images.GetAnnotationAsync(x00.Id);
        await project.Images.SaveAnnotationAsync(x00.Id, new AnnotationInput(annotation.Objects.Select(item => new AnnotationObjectInput(item.Id, item.ClassId,
            item.ClassVariantId, item.Box with { X = item.Box.X + 0.01 })).ToArray(), null, ImageReviewState.Reviewed), x00.AnnotationRevision);
        await RejectsCode("datasets.plan_stale", () => datasets.PrepareBuildAsync(stalePlan.Id, stalePlan.Hash, "stale-annotation"));
        buildPlan = await datasets.PlanAsync(buildSpec, "plan-build-2");
        var dogClass = (await project.Classes.GetWorkspaceAsync()).Classes.Single(item => item.Id == fx.Dog);
        dogClass = await project.Classes.UpdateAsync(fx.Dog, "Пёс", "", dogClass.Revision);
        await RejectsCode("datasets.plan_stale", () => datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "stale-catalog"));
        await project.Classes.UpdateAsync(fx.Dog, "Собака", "", dogClass.Revision);
        await project.UpdateAsync(new ProjectSettings(project.Name, project.Description, project.BaseModelId, new InputSize(640, 640)), project.Revision);
        await RejectsCode("datasets.input_size_mismatch", () => datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "other-size"));
        await project.UpdateAsync(new ProjectSettings(project.Name, project.Description, project.BaseModelId, new InputSize(320, 256)), project.Revision);
        var empty = await datasets.PlanAsync(spec with { GroupIds = [fx.Empty] }, "plan-empty");
        await RejectsCode("datasets.empty", () => datasets.PrepareBuildAsync(empty.Id, empty.Hash, "empty"));

        // A build: hard links for JPEG and PNG, oriented PNG for an EXIF-rotated JPEG, WebP and BMP; labels and data.yaml byte-exact.
        builds.Workers = 2;
        builds.WindowSize = 5;
        var input = await datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "build-1");
        Check(Same(await datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "build-1"), input), "A repeated build operation returns its input.");
        var job = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(input, project.Id, "build-1");
        var built = await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
        var result = built.Result!.Value.Deserialize<DatasetBuildResult>(JobsManager.Json)!;
        Check(result is { Total: 24, Produced: 24, Linked: 21, Copied: 0, Converted: 3 } && built.Processed == 24 && built.Total == 24,
            $"The build links 21 JPEG and PNG files and converts 3 ({result}).");
        var version = await datasets.GetAsync(job.Id);
        Check(version is { Name: "Датасет", Description: "Описание", Revision: 1, IntegrityState: DatasetIntegrityState.Verified, Width: 320, Height: 256 }
            && version.Counts.Total == 24 && version.ClassMap.Select(item => item.Name).SequenceEqual(["Собака", "Кошка \"Мурка\""])
            && Same(version.Composition, buildPlan.Composition)
            && version.Variants.Any(item => item.ClassVariantId == fx.CatNight && item.Name == "Ночь") && version.VerifiedAt is not null,
            "The version is ready and verified, with the class map, variant names and the composition of its plan.");
        Check(events.Any(change => change.Versions.Any(item => item.Id == version.Id && item.Revision == 1)), "Publication raises DatasetsChanged.");
        Check((await datasets.ListAsync()).Single().Id == version.Id, "The version is listed.");
        Check(await datasets.GetSummaryAsync() == new DatasetsSummary(1, 0, 0), "A published version counts as ready.");
        var files = Path.Combine(root, "projects", project.Id, "files");
        var folder = Path.Combine(files, "datasets", version.Id.ToString("D"));
        var samples = await AllSamplesAsync(datasets, version.Id);
        Check(samples.Count == 24 && samples.Select(sample => sample.Id).Distinct().Count() == 24, "Every frame is one sample.");
        var a = samples.Single(sample => sample.Id == fx.Images["a.jpg"]);
        var aPath = Path.Combine(folder, "images", Key(a.Split), a.Id + ".jpg");
        var aSource = Path.Combine(files, "images", a.Id + ".jpg");
        Check(a.Format == ImageFileFormat.Jpeg && LinkCount(aPath) == 2 && File.ReadAllBytes(aPath).SequenceEqual(File.ReadAllBytes(aSource)),
            "A JPEG without orientation is a hard link to the project file.");
        Check(File.ReadAllText(Path.Combine(folder, "labels", Key(a.Split), a.Id + ".txt"))
            == "1 0.25000000 0.40000000 0.30000000 0.40000000\n0 0.29012346 0.25000000 0.33333333 0.50000000\n",
            "The label has one line per target box with 8 decimals, as toFixed(8).");
        foreach (var name in new[] { "c-rot.jpg", "d.webp", "e.bmp" })
        {
            var sample = samples.Single(item => item.Id == fx.Images[name]);
            var source = await project.Images.GetImageAsync(sample.Id);
            var path = Path.Combine(folder, "images", Key(sample.Split), sample.Id + ".png");
            using var stream = File.OpenRead(path);
            var decoded = ImagePixels.Decode(stream);
            Check(sample.Format == ImageFileFormat.Png && LinkCount(path) == 1 && decoded is { Format: ImageFileFormat.Png } && decoded.Value.PixelHash == source.PixelHash
                && decoded.Value.Width == source.Width && decoded.Value.Height == source.Height && sample.PixelHash == source.PixelHash,
                $"{name} becomes a PNG of the oriented frame with the pixels of the source.");
        }
        Check(samples.Single(item => item.Id == fx.Images["c-rot.jpg"]) is { Width: 30, Height: 60 }, "The rotated JPEG keeps its oriented size.");
        var h = samples.Single(item => item.Id == fx.Images["h.jpg"]);
        var e = samples.Single(item => item.Id == fx.Images["e.bmp"]);
        Check(h.Boxes.Count == 0 && h.Negatives.Count == 2 && new FileInfo(Path.Combine(folder, "labels", Key(h.Split), h.Id + ".txt")).Length == 0
            && e.Negatives.Single().ClassId == fx.Bird && e.Boxes.Single().Index == 1, "Negatives are kept apart from the label; an empty frame has an empty label.");
        var dataYaml = File.ReadAllBytes(Path.Combine(folder, "data.yaml"));
        Check(Encoding.UTF8.GetString(dataYaml) == "path: .\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n  0: \"Собака\"\n  1: \"Кошка \\\"Мурка\\\"\"\n",
            "data.yaml has relative splits and JSON-quoted names by index.");
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "manifest.json"))))
        {
            var rootElement = manifest.RootElement;
            Check(rootElement.GetProperty("schemaVersion").GetInt32() == 1 && rootElement.GetProperty("classes").GetArrayLength() == 2
                && rootElement.GetProperty("classes")[1].GetProperty("classId").GetGuid() == fx.Cat && rootElement.GetProperty("classes")[1].GetProperty("index").GetInt32() == 1
                && rootElement.GetProperty("samples").GetArrayLength() == 24 && rootElement.GetProperty("width").GetInt32() == 320
                && Hash(File.ReadAllBytes(Path.Combine(folder, "manifest.json"))) == version.ManifestHash,
                "manifest.json lists classes [{index, classId, name}] and every sample; its hash is stored.");
        }
        Check(!Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories).Any(), "The build leaves no temporary files.");

        // Samples: keyset pages by split, a single sample and its files through the resource channel.
        var page = await datasets.ListSamplesAsync(version.Id, DatasetSplit.Train, 2);
        var next = await datasets.ListSamplesAsync(version.Id, DatasetSplit.Train, 200, page.NextCursor);
        Check(page.Items.Count == 2 && page.NextCursor is not null && next.NextCursor is null && next.Items.Count == version.Counts.Train - 2
            && !next.Items.Any(item => page.Items.Any(first => first.Id == item.Id)), "Sample pages continue by the cursor without repeats.");
        await RejectsCode("datasets.cursor_invalid", () => datasets.ListSamplesAsync(version.Id, DatasetSplit.Train, 10, "garbage"));
        Check(Same(await datasets.GetSampleAsync(version.Id, a.Id), a), "A sample is read by its ID.");
        await RejectsCode("datasets.unavailable", () => datasets.GetSampleAsync(version.Id, Guid.NewGuid()));
        await using (var original = await project.Resources.OpenAsync(new ResourceReference(DatasetSample.ResourceKind, a.ResourceId), ResourceVariant.Original))
        {
            var bytes = new MemoryStream();
            await original.Stream.CopyToAsync(bytes);
            Check(original.ContentType == "image/jpeg" && bytes.ToArray().SequenceEqual(File.ReadAllBytes(aPath)), "The sample file is served as a resource.");
        }
        var rotated = samples.Single(item => item.Id == fx.Images["c-rot.jpg"]);
        await using (var thumbnail = await project.Resources.OpenAsync(new ResourceReference(DatasetSample.ResourceKind, rotated.ResourceId), ResourceVariant.Thumbnail))
        {
            using var bitmap = SKBitmap.Decode(thumbnail.Stream);
            Check(thumbnail.ContentType == "image/jpeg" && bitmap.Width == 30 && bitmap.Height == 60, "A sample thumbnail is served.");
        }
        await RejectsCode("resources.not_found", () => project.Resources.OpenAsync(new ResourceReference(DatasetSample.ResourceKind, Guid.NewGuid()), ResourceVariant.Original));

        // Operations: the build in the history with its specification (repeat), name changes and archive with revisions.
        var history = await datasets.ListOperationsAsync(DatasetOperationKind.Build);
        Check(history.Items.Single(item => item.Job.Id == version.Id) is { VersionAvailable: true, Kind: DatasetOperationKind.Build } operation
            && Same(operation.Specification, input.Specification) && operation.PlanId == buildPlan.Id && operation.VersionName == "Датасет",
            "The build history keeps the specification for «Настроить повтор».");
        await RejectsCode("datasets.revision_conflict", () => datasets.UpdateAsync(version.Id, "Новое", "", 7));
        await RejectsCode("datasets.name_invalid", () => datasets.UpdateAsync(version.Id, " ", "", version.Revision));
        version = await datasets.UpdateAsync(version.Id, "  Новое имя  ", "  Новое описание  ", version.Revision);
        Check(version is { Name: "Новое имя", Description: "Новое описание", Revision: 2 }, "Name and description change with a new revision.");
        version = await datasets.SetArchivedAsync(version.Id, true, version.Revision);
        Check(version is { ArchivedAt: not null, Revision: 3 } && (await datasets.ListAsync(includeArchived: false)).Count == 0, "Archived versions are listed only on request.");
        await RejectsCode("datasets.archived", () => datasets.UpdateAsync(version.Id, "Архив", "", version.Revision));
        await RejectsCode("datasets.archived", () => datasets.PrepareVerifyAsync(version.Id, "verify-archived"));
        await RejectsCode("datasets.archived", () => datasets.PrepareExportAsync(version.Id, Path.Combine(fixture, "archived.zip"), "export-archived"));
        await RejectsCode("datasets.archived", () => datasets.GetTrainingSourceAsync(version.Id));
        Check(await datasets.GetSummaryAsync() == new DatasetsSummary(0, 1, 0), "An archived version counts as archived, not ready.");
        version = await datasets.SetArchivedAsync(version.Id, false, version.Revision);

        // Integrity: every file is checked and every problem listed; a corrupt version is not exported or trained on.
        var verified = await RunVerifyAsync(core, project, version.Id, "verify-1");
        Check(verified is { TotalFiles: 50, CheckedFiles: 50, IssueCount: 0, State: DatasetIntegrityState.Verified } && verified.Bytes > 0,
            $"A sound version checks 50 files ({verified}).");
        var cPath = Path.Combine(folder, "images", Key(rotated.Split), rotated.Id + ".png");
        var cBytes = File.ReadAllBytes(cPath);
        var x00Sample = samples.Single(item => item.Id == fx.Images["x00.jpg"]);
        var x00Label = Path.Combine(folder, "labels", Key(x00Sample.Split), x00Sample.Id + ".txt");
        var x00Bytes = File.ReadAllBytes(x00Label);
        File.Delete(cPath);
        File.WriteAllText(x00Label, "0 0.5 0.5 0.1 0.1\n");
        File.AppendAllText(Path.Combine(folder, "data.yaml"), "# changed\n");
        var corrupt = await RunVerifyAsync(core, project, version.Id, "verify-2");
        Check(corrupt is { IssueCount: 3, CheckedFiles: 50, State: DatasetIntegrityState.Corrupt }
            && corrupt.Issues.Any(issue => issue is { Role: DatasetFileRole.DataYaml, Code: "datasets.file_changed", SampleId: null })
            && corrupt.Issues.Any(issue => issue.Role == DatasetFileRole.Image && issue.Code == "datasets.file_missing" && issue.SampleId == rotated.Id)
            && corrupt.Issues.Any(issue => issue.Role == DatasetFileRole.Label && issue.Code == "datasets.file_changed" && issue.SampleId == x00Sample.Id),
            "The check lists all three problems, not only the first.");
        version = await datasets.GetAsync(version.Id);
        Check(version.IntegrityState == DatasetIntegrityState.Corrupt, "The version becomes corrupt.");
        Check(await datasets.GetSummaryAsync() == new DatasetsSummary(0, 0, 1), "A corrupt version counts as corrupt, not ready.");
        await RejectsCode("datasets.corrupt", () => datasets.PrepareExportAsync(version.Id, Path.Combine(fixture, "corrupt.zip"), "export-corrupt"));
        await RejectsCode("datasets.corrupt", () => datasets.GetTrainingSourceAsync(version.Id));
        File.WriteAllBytes(cPath, cBytes);
        File.WriteAllBytes(x00Label, x00Bytes);
        File.WriteAllBytes(Path.Combine(folder, "data.yaml"), dataYaml);
        Check((await RunVerifyAsync(core, project, version.Id, "verify-3")).State == DatasetIntegrityState.Verified, "A repaired version checks as verified again.");
        version = await datasets.GetAsync(version.Id);

        // Export: ZIP layout, images stored, overwrite of the chosen file, cancel that leaves nothing.
        var exports = Path.Combine(fixture, "datasets-exports");
        Directory.CreateDirectory(exports);
        var destination = Path.Combine(exports, "Датасет.zip");
        File.WriteAllText(destination, "the previous file");
        await RejectsCode("datasets.export_destination", () => datasets.PrepareExportAsync(version.Id, "relative.zip", "export-relative"));
        await RejectsCode("datasets.export_destination", () => datasets.PrepareExportAsync(version.Id, Path.Combine(exports, "data.txt"), "export-txt"));
        await RejectsCode("datasets.export_destination", () => datasets.PrepareExportAsync(version.Id, Path.Combine(exports, "missing", "a.zip"), "export-missing"));
        var exportInput = await datasets.PrepareExportAsync(version.Id, destination, "export-1");
        var exportJob = await core.Jobs.EnqueueAsync<IDatasetExportJob, DatasetExportInput>(exportInput, project.Id, "export-1");
        var exported = (await WaitAsync(core.Jobs, project.Id, exportJob.Id, JobStatus.Succeeded)).Result!.Value.Deserialize<DatasetExportResult>(JobsManager.Json)!;
        Check(exported is { TotalImages: 24, ExportedImages: 24, FileName: "Датасет.zip" } && exported.Bytes == new FileInfo(destination).Length,
            $"The export result counts images and bytes ({exported}).");
        using (var zip = ZipFile.OpenRead(destination))
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToHashSet();
            var aEntry = zip.GetEntry($"images/{Key(a.Split)}/{a.Id}.jpg")!;
            using var aStream = aEntry.Open();
            var aZipped = new MemoryStream();
            aStream.CopyTo(aZipped);
            using var metadata = JsonDocument.Parse(zip.GetEntry("dataset-metadata.json")!.Open());
            Check(names.Contains("data.yaml") && names.Contains("manifest.json") && names.Contains("dataset-metadata.json")
                && names.Count(name => name.StartsWith("images/", StringComparison.Ordinal)) == 24 && names.Count(name => name.StartsWith("labels/", StringComparison.Ordinal)) == 24
                && names.Count(name => name.StartsWith("negatives/", StringComparison.Ordinal)) == 2 && names.Contains($"negatives/{Key(h.Split)}/{h.Id}.json"),
                "The ZIP has data.yaml, manifest.json, metadata, images, labels and negatives of the frames that have them.");
            Check(zip.Entries.Where(entry => entry.FullName.StartsWith("images/", StringComparison.Ordinal)).All(entry => entry.CompressedLength == entry.Length)
                && aZipped.ToArray().SequenceEqual(File.ReadAllBytes(aSource)), "Images are stored without recompression.");
            Check(metadata.RootElement.GetProperty("id").GetGuid() == version.Id && metadata.RootElement.GetProperty("manifestHash").GetString() == version.ManifestHash
                && metadata.RootElement.GetProperty("name").GetString() == "Новое имя", "dataset-metadata.json names the version and its manifest hash.");
        }
        Check(!Directory.EnumerateFiles(exports, "*.tmp").Any(), "The confirmed file is replaced and no temporary file stays.");
        var exportedBytes = File.ReadAllBytes(destination);
        var exportHandler = implementation.DatasetExports;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        exportHandler.Pause = async count =>
        {
            if (count != 3) return;
            paused.TrySetResult();
            await resume.Task;
        };
        var cancelJob = await core.Jobs.EnqueueAsync<IDatasetExportJob, DatasetExportInput>(await datasets.PrepareExportAsync(version.Id, destination, "export-2"),
            project.Id, "export-2");
        await paused.Task.WaitAsync(WaitLimit);
        var busy = await datasets.ReviewDeletionAsync(version.Id);
        Check(busy.Blockers.Any(blocker => blocker is { Code: "datasets.export_active", Count: 1 }), "An export of the version blocks its deletion.");
        await RejectsCode("datasets.deletion_blocked", () => datasets.DeleteAsync(version.Id, busy.Revision));
        await RejectsCode("datasets.operation_active", () => datasets.DeleteOperationAsync(cancelJob.Id));
        Check(Directory.EnumerateFiles(exports, "*.tmp").Count() == 1, "The export writes a temporary file next to the destination.");
        await core.Jobs.CancelAsync(project.Id, cancelJob.Id);
        resume.TrySetResult();
        await WaitAsync(core.Jobs, project.Id, cancelJob.Id, JobStatus.Cancelled);
        exportHandler.Pause = null;
        Check(File.ReadAllBytes(destination).SequenceEqual(exportedBytes) && !Directory.EnumerateFiles(exports, "*.tmp").Any(),
            "A cancelled export leaves the destination as it was and no temporary file.");
        var exportHistory = await datasets.ListOperationsAsync(DatasetOperationKind.Export);
        Check(exportHistory.Total == 2 && exportHistory.Items.All(item => item.FileName == "Датасет.zip" && item.VersionId == version.Id && item.VersionAvailable),
            "The export history names the file and the version.");
        await datasets.DeleteOperationAsync(cancelJob.Id);
        Check((await datasets.ListOperationsAsync(DatasetOperationKind.Export)).Total == 1, "A finished operation is removed from the history.");

        // Training: the training source and a materialized job folder of hard links.
        var training = await datasets.GetTrainingSourceAsync(version.Id);
        Check(training.Classes.Select(item => item.Index).SequenceEqual([0, 1]) && File.Exists(training.DataYamlPath) && training.Counts.Total == 24
            && training.Directory == folder, "The training source is the version folder with classes 0..n−1.");
        var target = Path.Combine(fixture, "datasets-train-job", "input");
        var materialized = await datasets.MaterializeAsync(version.Id, target);
        Check(materialized.Directory == target && File.ReadAllBytes(Path.Combine(target, "data.yaml")).SequenceEqual(dataYaml) && LinkCount(aPath) == 3
            && Directory.EnumerateFiles(Path.Combine(target, "images"), "*", SearchOption.AllDirectories).Count() == 24
            && Directory.EnumerateFiles(Path.Combine(target, "labels"), "*", SearchOption.AllDirectories).Count() == 24,
            "Materialization links images and labels into the job folder and copies data.yaml and manifest.json.");
        await RejectsCode("datasets.materialize_target", () => datasets.MaterializeAsync(version.Id, target));
        await project.UpdateAsync(new ProjectSettings(project.Name, project.Description, project.BaseModelId, new InputSize(640, 640)), project.Revision);
        await RejectsCode("datasets.input_size_mismatch", () => datasets.GetTrainingSourceAsync(version.Id));
        await project.UpdateAsync(new ProjectSettings(project.Name, project.Description, project.BaseModelId, new InputSize(320, 256)), project.Revision);

        // A build that is cancelled or fails leaves nothing; while it runs, the images it reads cannot be deleted.
        var cancelPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builds.Pause = async (point, window) =>
        {
            if (point != "saved" || window != 0) return;
            cancelPaused.TrySetResult();
            await cancelResume.Task;
        };
        var cancelled = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "build-cancel"),
            project.Id, "build-cancel");
        await cancelPaused.Task.WaitAsync(WaitLimit);
        Check((await project.Images.ReviewGroupDeletionAsync(fx.Unassigned)).Blockers.Any(blocker => blocker is { Code: "datasets.build_active", Count: 1 })
            && !(await project.Images.ReviewGroupDeletionAsync(fx.Empty)).Blockers.Any(), "A running build blocks deleting the images it reads, not others.");
        var cancelledFolder = Path.Combine(files, "datasets", cancelled.Id.ToString("D"));
        Check(Directory.Exists(cancelledFolder), "A running build writes its folder.");
        await core.Jobs.CancelAsync(project.Id, cancelled.Id);
        cancelResume.TrySetResult();
        await WaitAsync(core.Jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        builds.Pause = null;
        await project.FileCleanup.CleanupTask;
        Check(!Directory.Exists(cancelledFolder) && await CountAsync(project, $"SELECT count(*) FROM dataset_versions WHERE id = '{cancelled.Id:D}'") == 0
            && await CountAsync(project, $"SELECT count(*) FROM dataset_samples WHERE version_id = '{cancelled.Id:D}'") == 0,
            "A cancelled build leaves neither rows nor files.");
        Check((await datasets.GetOperationAsync(cancelled.Id)) is { VersionAvailable: false, Kind: DatasetOperationKind.Build }, "The cancelled build stays in the history.");
        var missingSource = Path.Combine(files, "images", fx.Images["x11.jpg"] + ".jpg");
        File.Move(missingSource, missingSource + ".away");
        var failing = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "build-fail"),
            project.Id, "build-fail");
        var failed = await WaitAsync(core.Jobs, project.Id, failing.Id, JobStatus.Failed);
        File.Move(missingSource + ".away", missingSource);
        await project.FileCleanup.CleanupTask;
        Check(failed.Error?.Code == "datasets.source_missing" && !Directory.Exists(Path.Combine(files, "datasets", failing.Id.ToString("D")))
            && await CountAsync(project, $"SELECT count(*) FROM dataset_versions WHERE id = '{failing.Id:D}'") == 0, "A failed build leaves nothing.");

        // Deletion: blockers of other features, the review revision, two phases and the kept passport.
        implementation.DatasetBlockers.Register((_, _, versionId, _) => Task.FromResult<IReadOnlyList<DeletionBlocker>>(
            versionId == fx.BlockedVersion ? [new DeletionBlocker("training.active", 1, ["Обучение"])] : []));
        var review = await datasets.ReviewDeletionAsync(version.Id);
        Check(review is { Samples: 24, Name: "Новое имя" } && review.Operations == 5 && review.Blockers.Count == 0,
            $"The deletion review counts samples and the history of the version: its build, three checks and one export ({review.Operations}).");
        await RejectsCode("datasets.deletion_revision_conflict", () => datasets.DeleteAsync(version.Id, "stale"));
        var deleted = await datasets.DeleteAsync(version.Id, review.Revision);
        await project.FileCleanup.CleanupTask;
        Check(deleted is { Samples: 24, Operations: 5 } && !Directory.Exists(folder) && File.Exists(aSource) && File.Exists(Path.Combine(target, "images", Key(a.Split), a.Id + ".jpg"))
            && await CountAsync(project, $"SELECT count(*) FROM dataset_samples WHERE version_id = '{version.Id:D}'") == 0,
            "Deletion removes the samples and the folder; project images and materialized copies stay.");
        await RejectsCode("datasets.unavailable", () => datasets.GetAsync(version.Id));
        await RejectsCode("datasets.unavailable", () => datasets.GetOperationAsync(version.Id));
        var passport = await datasets.GetPassportAsync(version.Id);
        Check(passport.DeletedAt is not null && passport.ClassMap.Count == 2 && passport.ManifestHash == version.ManifestHash && passport.Composition.Images.Total == 24
            && Same(passport.Specification, version.Specification), "The passport of a deleted version stays for models.");
        Check(events.Any(change => change.Versions.Any(item => item.Id == version.Id && item.Revision is null)), "Deletion raises DatasetsChanged.");
        Check((await datasets.ListAsync()).Count == 0 && (await datasets.ListOperationsAsync(DatasetOperationKind.Verify)).Total == 0, "A deleted version and its history are gone.");
        Check(await datasets.GetSummaryAsync() == new DatasetsSummary(0, 0, 0), "A deleted version is not counted.");

        // A registered provider of another feature blocks deletion of its version.
        var second = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await datasets.PrepareBuildAsync(buildPlan.Id, buildPlan.Hash, "build-2"),
            project.Id, "build-2");
        await WaitAsync(core.Jobs, project.Id, second.Id, JobStatus.Succeeded);
        fx.BlockedVersion = second.Id;
        var blocked = await datasets.ReviewDeletionAsync(second.Id);
        Check(blocked.Blockers.Single() is { Code: "training.active", Count: 1 }, "A provider registered for training blocks the deletion.");
        await RejectsCode("datasets.deletion_blocked", () => datasets.DeleteAsync(second.Id, blocked.Revision));
        await CheckRestartAsync(fixture, createCore, seedInstalledModel, Check);

        return checks;
    }

    /// <summary>Reference values computed with an independent JavaScript implementation.</summary>
    private static void CheckAlgorithm(Action<bool, string> check)
    {
        check(DatasetSelection.Value(0, "file:h00:h00") == 960849873 / 4294967296.0 && DatasetSelection.Value(42, "video:v1:p1") == 3687131615 / 4294967296.0
            && DatasetSelection.Value(4294967295, "generated-image:g:p7") == 3733940571 / 4294967296.0,
            "v = uint32BE(sha256(\"seed:key\")) / 2^32 matches the reference values.");
        var independent = Enumerable.Range(0, 40).Select(index => ($"file:h{index:00}", $"h{index:00}")).ToArray();
        foreach (var (seed, val, test, letters) in new (uint, double, double, string)[]
        {
            (0, 0.2, 0.1, "vrvrrtrrrvrvrrrrrrrvvrvrrrvvrrrvttvrrrrr"),
            (42, 0.2, 0.1, "vrtttvvvrrvrrvrrrrrtrrrvrrrtrrrrrttrvrvr"),
            (4294967295, 0.25, 0, "rrrrvrrvrrrvrrrrvrrrrvrrrrrvvrvrrrrrrrrr"),
            (7, 0.3, 0.3, "rtrvrtrrrrrvrrvvttrrvtvttrvtrvvvrvrtrrvr"),
        })
        {
            var splits = DatasetSelection.Split(independent, seed, val, test);
            check(string.Concat(splits.Select(split => split switch { DatasetSplit.Train => 'r', DatasetSplit.Validation => 'v', _ => 't' })) == letters,
                $"Independent frames keep their reference split with seed {seed}.");
        }
        var linked = new (string, string)[] { ("video:v1", "p1"), ("video:v1", "p2"), ("file:p3", "p3"), ("file:p9", "p3"), ("video:v2", "p2"),
            ("generated-image:g", "p7"), ("generated-image:h", "p8") };
        foreach (var (seed, expected) in new (uint, string)[] { (1, "rrrrrvt"), (2, "ttrrtrr"), (3, "tttttrr"), (99, "rrvvrrr") })
        {
            var splits = DatasetSelection.Split(linked, seed, 0.3, 0.2);
            check(string.Concat(splits.Select(split => split switch { DatasetSplit.Train => 'r', DatasetSplit.Validation => 'v', _ => 't' })) == expected,
                $"Frames joined by family and pixels share the split of their component with seed {seed}.");
        }
        var label = DatasetFiles.Label([new DatasetBox(0, Guid.Empty, null, 0.1 + 0.3 / 2, 0.2 + 0.4 / 2, 0.3, 0.4),
            new DatasetBox(1, Guid.Empty, null, 0.123456789 + 0.333333333 / 2, 0.000000004 + 0.5 / 2, 0.333333333, 0.5),
            new DatasetBox(2, Guid.Empty, null, 0.7 + 0.3 / 2, 0.6 + 0.4 / 2, 0.3, 0.4),
            new DatasetBox(3, Guid.Empty, null, 0.0000000049 + 0.1234567895 / 2, 0.33333333335 + 0.00000001 / 2, 0.1234567895, 0.00000001)]);
        check(Encoding.ASCII.GetString(label) == "0 0.25000000 0.40000000 0.30000000 0.40000000\n1 0.29012346 0.25000000 0.33333333 0.50000000\n"
            + "2 0.85000000 0.80000000 0.30000000 0.40000000\n3 0.06172840 0.33333334 0.12345679 0.00000001\n", "Labels match toFixed(8) byte for byte.");
        check(DatasetFiles.Label([]).Length == 0, "A frame without targets has an empty label.");
        var yaml = DatasetFiles.DataYaml([new DatasetClassEntry(0, Guid.Empty, "Кошка"), new DatasetClassEntry(1, Guid.Empty, "A \"b\" \\ c\n\u0001")], hasTest: false);
        check(Encoding.UTF8.GetString(yaml) == "path: .\ntrain: images/train\nval: images/val\nnames:\n  0: \"Кошка\"\n  1: \"A \\\"b\\\" \\\\ c\\n\\u0001\"\n",
            "data.yaml without test frames has no test line; names are quoted as JSON.stringify does.");
    }

    private static async Task CheckRestartAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        var root = Path.Combine(fixture, "datasets-restart");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var dying = await createCore(root, null);
        var project = (Project)await dying.Projects.CreateAsync(new ProjectSettings("Перезапуск датасета", "", "yolo26n", new InputSize(320, 256)));
        await dying.Projects.OpenAsync(project.Id);
        var sources = Path.Combine(fixture, "datasets-restart-sources");
        Directory.CreateDirectory(sources);
        var day = await project.Variants.CreateAsync("День", "");
        var cat = await project.Classes.CreateAsync("Кошка", "");
        var catDay = await project.Classes.AddVariantAsync(cat.Id, day.Id);
        var group = await project.Images.CreateGroupAsync("Кадры", "", ImageSplitHint.Unassigned);
        var paths = Enumerable.Range(0, 10).Select(index =>
        {
            var path = Path.Combine(sources, $"r{index:00}.jpg");
            File.WriteAllBytes(path, ImagesSmoke.Jpeg(24, 24, 300 + index));
            return path;
        }).ToArray();
        var import = await dying.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray(), SkipDuplicates: false), project.Id, "restart-import");
        await WaitAsync(dying.Jobs, project.Id, import.Id, JobStatus.Succeeded);
        foreach (var image in (await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 100)).Items)
        {
            await project.Images.SaveAnnotationAsync(image.Id, new AnnotationInput([new AnnotationObjectInput(null, cat.Id, catDay.Id, new ImageBox(0.2, 0.2, 0.5, 0.5))],
                null, ImageReviewState.Reviewed), image.AnnotationRevision);
        }
        var spec = new DatasetSpecification("Перезапуск", "", [group.Id], [cat.Id], [catDay.Id], [], DatasetUnassignedVariantRole.Target,
            DatasetUnselectedClassRole.Negative, false, false, 3, 0.3, 0.2);
        var plan = await project.Datasets.PlanAsync(spec, "restart-plan");
        var handler = ((MautVision.Core.Core)dying).DatasetBuilds;
        handler.Workers = 2;
        handler.WindowSize = 3;
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, window) =>
        {
            if (point != "produced" || window != 1) return;
            died.TrySetResult();
            await new TaskCompletionSource().Task;
        };
        var job = await dying.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, "restart-build"),
            project.Id, "restart-build");
        await died.Task.WaitAsync(WaitLimit);
        var produced = await CountAsync(project, $"SELECT count(*) FROM dataset_samples WHERE version_id = '{job.Id:D}' AND image_hash IS NOT NULL");
        check(produced == 6, $"Before the restart the second window is saved but not yet checkpointed ({produced}).");
        // The application is gone: its deferred progress write must not land on top of the resumed job, nor reopen project.db later.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();

        var revived = await createCore(root, null);
        var revivedProject = (Project)await revived.Projects.OpenAsync(project.Id);
        var result = (await WaitAsync(revived.Jobs, project.Id, job.Id, JobStatus.Succeeded)).Result!.Value.Deserialize<DatasetBuildResult>(JobsManager.Json)!;
        var version = await revivedProject.Datasets.GetAsync(job.Id);
        var folder = Path.Combine(root, "projects", project.Id, "files", "datasets", job.Id.ToString("D"));
        check(result is { Total: 10, Produced: 10 } && version.Counts.Total == 10
            && Directory.EnumerateFiles(Path.Combine(folder, "images"), "*", SearchOption.AllDirectories).Count() == 10
            && Directory.EnumerateFiles(Path.Combine(folder, "labels"), "*", SearchOption.AllDirectories).Count() == 10
            && await CountAsync(revivedProject, $"SELECT count(*) FROM dataset_samples WHERE version_id = '{job.Id:D}'") == 10
            && !Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories).Any(),
            "A build resumed after a restart produces each sample once and publishes the version.");
        var verify = await RunVerifyAsync(revived, revivedProject, version.Id, "restart-verify");
        check(verify is { IssueCount: 0, State: DatasetIntegrityState.Verified, TotalFiles: 22 }, "The resumed version checks as verified.");
        // A write takes the writer gate after the job's last write has released its connection, so no connection outlives the smoke.
        var renamed = await revivedProject.Datasets.UpdateAsync(version.Id, "После перезапуска", "", (await revivedProject.Datasets.GetAsync(version.Id)).Revision);
        check(renamed.Name == "После перезапуска", "The resumed version can be renamed.");
    }

    private sealed class Fixture
    {
        public Guid Cat, Dog, Bird, Old, CatDay, CatNight, DogDay;
        public Guid Unassigned, TrainGroup, ValidationGroup, TestGroup, Duplicate, Generated, Partial, Archived, Empty;
        public Guid BlockedVersion;
        public Dictionary<string, Guid> Images = new(StringComparer.Ordinal);
        public List<(Guid Id, string Family, string PixelHash)> UnassignedFrames = [];
    }

    private static async Task<Fixture> CreateFixtureAsync(ICore core, Project project, string sources, Action<bool, string> check)
    {
        var fx = new Fixture();
        var day = await project.Variants.CreateAsync("День", "");
        var night = await project.Variants.CreateAsync("Ночь", "");
        var cat = await project.Classes.CreateAsync("Кошка \"Мурка\"", "");
        var dog = await project.Classes.CreateAsync("Собака", "");
        var bird = await project.Classes.CreateAsync("Птица", "");
        var old = await project.Classes.CreateAsync("Старый", "");
        (fx.Cat, fx.Dog, fx.Bird, fx.Old) = (cat.Id, dog.Id, bird.Id, old.Id);
        fx.CatDay = (await project.Classes.AddVariantAsync(cat.Id, day.Id)).Id;
        var catNight = await project.Classes.AddVariantAsync(cat.Id, night.Id);
        fx.CatNight = catNight.Id;
        fx.DogDay = (await project.Classes.AddVariantAsync(dog.Id, day.Id)).Id;
        var birdDay = (await project.Classes.AddVariantAsync(bird.Id, day.Id)).Id;
        var oldDay = (await project.Classes.AddVariantAsync(old.Id, day.Id)).Id;
        var workspace = await project.Classes.GetWorkspaceAsync();
        await project.Classes.ReorderAsync([dog.Id, cat.Id, bird.Id, old.Id], workspace.ClassOrderRevision);

        var images = project.Images;
        fx.Unassigned = (await images.CreateGroupAsync("Без назначения", "", ImageSplitHint.Unassigned)).Id;
        fx.TrainGroup = (await images.CreateGroupAsync("Обучение", "", ImageSplitHint.Train)).Id;
        fx.ValidationGroup = (await images.CreateGroupAsync("Проверка", "", ImageSplitHint.Validation)).Id;
        fx.TestGroup = (await images.CreateGroupAsync("Тест", "", ImageSplitHint.Test)).Id;
        fx.Duplicate = (await images.CreateGroupAsync("Повтор", "", ImageSplitHint.Train)).Id;
        fx.Empty = (await images.CreateGroupAsync("Пустая", "", ImageSplitHint.Unassigned)).Id;
        var archived = await images.CreateGroupAsync("Архив", "", ImageSplitHint.Unassigned);
        fx.Archived = (await images.SetGroupArchivedAsync(archived.Id, true, archived.Revision)).Id;

        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(sources, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        var aBytes = ImagesSmoke.Jpeg(64, 48, 1);
        var files = new List<(Guid Group, string Path)>
        {
            (fx.Unassigned, Write("a.jpg", aBytes)),
            (fx.Unassigned, Write("b.png", ImagesSmoke.Png(40, 30, false, 2))),
            (fx.Unassigned, Write("c-rot.jpg", ImagesSmoke.WithOrientation(ImagesSmoke.Jpeg(60, 30, 3), 6))),
            (fx.Unassigned, Write("d.webp", ImagesSmoke.Webp(50, 40, 4))),
            (fx.Unassigned, Write("e.bmp", ImagesSmoke.Bmp(30, 20))),
            (fx.Unassigned, Write("f.jpg", ImagesSmoke.Jpeg(32, 32, 6))),
            (fx.Unassigned, Write("g.jpg", ImagesSmoke.Jpeg(32, 32, 7))),
            (fx.Unassigned, Write("h.jpg", ImagesSmoke.Jpeg(32, 32, 8))),
        };
        files.AddRange(Enumerable.Range(0, 12).Select(index => (fx.Unassigned, Write($"x{index:00}.jpg", ImagesSmoke.Jpeg(24, 24, 100 + index)))));
        files.Add((fx.TrainGroup, Write("i.jpg", ImagesSmoke.Jpeg(32, 32, 20))));
        files.Add((fx.ValidationGroup, Write("j.jpg", ImagesSmoke.Jpeg(32, 32, 21))));
        files.Add((fx.TestGroup, Write("t.jpg", ImagesSmoke.Jpeg(32, 32, 22))));
        var duplicateFolder = Path.Combine(sources, "duplicate");
        Directory.CreateDirectory(duplicateFolder);
        File.WriteAllBytes(Path.Combine(duplicateFolder, "a-copy.jpg"), aBytes);
        files.Add((fx.Duplicate, Path.Combine(duplicateFolder, "a-copy.jpg")));
        foreach (var group in files.GroupBy(file => file.Group))
        {
            var job = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Key,
                group.Select(file => new ImageImportSource(ImageImportSourceKind.File, file.Path)).ToArray(), SkipDuplicates: false), project.Id, "import-" + group.Key);
            await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
            foreach (var image in (await images.ListImagesAsync(group.Key, new ImageFilter(), 200)).Items) fx.Images[image.DisplayName] = image.Id;
        }
        check(fx.Images.Count == 24, "The dataset fixture imports 24 images.");

        async Task Annotate(string name, params (Guid Class, Guid? Variant, ImageBox Box)[] objects)
        {
            var image = await images.GetImageAsync(fx.Images[name]);
            await images.SaveAnnotationAsync(image.Id, new AnnotationInput(objects.Select(item => new AnnotationObjectInput(null, item.Class, item.Variant, item.Box)).ToArray(),
                null, ImageReviewState.Reviewed), image.AnnotationRevision);
        }
        var box = new ImageBox(0.25, 0.25, 0.5, 0.5);
        await Annotate("a.jpg", (cat.Id, fx.CatDay, new ImageBox(0.1, 0.2, 0.3, 0.4)), (dog.Id, fx.DogDay, new ImageBox(0.123456789, 0.000000004, 0.333333333, 0.5)));
        await Annotate("b.png", (cat.Id, null, box));
        await Annotate("c-rot.jpg", (cat.Id, fx.CatDay, box));
        await Annotate("d.webp", (dog.Id, fx.DogDay, box));
        await Annotate("e.bmp", (cat.Id, fx.CatDay, box), (bird.Id, birdDay, new ImageBox(0, 0, 0.2, 0.2)));
        await Annotate("f.jpg");
        await Annotate("h.jpg", (old.Id, oldDay, box), (cat.Id, fx.CatNight, new ImageBox(0.1, 0.1, 0.2, 0.2)));
        for (var index = 0; index < 12; index++) await Annotate($"x{index:00}.jpg", (cat.Id, fx.CatDay, box));
        foreach (var name in new[] { "i.jpg", "j.jpg", "t.jpg" }) await Annotate(name, (dog.Id, fx.DogDay, box));
        await Annotate("a-copy.jpg", (cat.Id, fx.CatDay, box));
        // Archived later: the class Old and the variant Cat · Night; their objects become negatives.
        old = (await project.Classes.GetWorkspaceAsync()).Classes.Single(item => item.Id == old.Id);
        await project.Classes.SetArchivedAsync(old.Id, true, old.Revision);
        catNight = await project.Classes.GetVariantAsync(catNight.Id);
        await project.Classes.SetVariantArchivedAsync(catNight.Id, true, catNight.Revision);

        // Generated groups: a completed one with two frames and a partial one with one.
        async Task<List<Guid>> Generate(string name, int count, ImageGenerationState state, int seed)
        {
            var runId = Guid.NewGuid();
            var group = await project.ImagesManager.CreateGeneratedGroupAsync(Guid.NewGuid(), name, runId, CancellationToken.None);
            var materialFiles = await project.Store.GetFilesAsync();
            var rows = new List<ImportedImage>();
            for (var index = 0; index < count; index++)
            {
                var id = Guid.NewGuid();
                var bytes = ImagesSmoke.Png(32, 32, false, seed + index);
                Directory.CreateDirectory(materialFiles.GetDirectory(ProjectImage.ResourceKind));
                File.WriteAllBytes(materialFiles.GetPath(ProjectImage.ResourceKind, id, ".png"), bytes);
                var decoded = ImagePixels.Decode(new MemoryStream(bytes))!.Value;
                rows.Add(new ImportedImage(id, $"generated-{index}", ImageFileFormat.Png, 32, 32, bytes.Length, Hash(bytes), decoded.PixelHash,
                    new ImageLineage(ImageLineageKind.Generated, "generation:" + runId.ToString("D"), GenerationRunId: runId, SampleIndex: index, RecipeRevision: 1),
                    [new AnnotationObject(Guid.NewGuid(), cat.Id, fx.CatDay, box)]));
            }
            await project.ImagesManager.InsertGeneratedAsync(group.Id, runId, rows, "generated-" + name, raise: true, CancellationToken.None);
            await project.ImagesManager.FinishGeneratedGroupAsync(group.Id, state, CancellationToken.None);
            return [group.Id, .. rows.Select(row => row.Id)];
        }
        var generated = await Generate("Генерация", 2, ImageGenerationState.Completed, 40);
        fx.Generated = generated[0];
        fx.Partial = (await Generate("Незавершённая", 1, ImageGenerationState.Partial, 50))[0];

        // Frames of the unassigned group and of the generated one, in plan input order, with their families for the reference split.
        foreach (var name in new[] { "a.jpg", "b.png", "c-rot.jpg", "d.webp", "e.bmp", "f.jpg", "g.jpg", "h.jpg" }.Concat(Enumerable.Range(0, 12).Select(index => $"x{index:00}.jpg")))
        {
            var image = await images.GetImageAsync(fx.Images[name]);
            fx.UnassignedFrames.Add((image.Id, image.Lineage.SourceFamilyId, image.PixelHash));
        }
        foreach (var id in generated.Skip(1))
        {
            var image = await images.GetImageAsync(id);
            fx.UnassignedFrames.Add((image.Id, "generated-image:" + image.Id.ToString("D"), image.PixelHash));
        }
        return fx;
    }

    /// <summary>The split of every selected frame, recomputed through the plan selection with the current sources.</summary>
    private static async Task<Dictionary<Guid, DatasetSplit>> ReadFramesAsync(Project project, DatasetSpecification specification)
    {
        var catalog = await project.ClassesManager.ReadDatasetCatalogAsync(CancellationToken.None);
        var sources = await project.ImagesManager.ReadDatasetSourcesAsync(specification.GroupIds, CancellationToken.None);
        var selection = DatasetSelection.Select(DatasetSelection.Validate(specification), catalog, sources, 320, 256);
        return selection.Frames.ToDictionary(frame => frame.Image.Id, frame => frame.Split);
    }

    private static async Task<List<DatasetSample>> AllSamplesAsync(IDatasetsManager datasets, Guid versionId)
    {
        var result = new List<DatasetSample>();
        foreach (var split in new[] { DatasetSplit.Train, DatasetSplit.Validation, DatasetSplit.Test })
        {
            string? cursor = null;
            do
            {
                var page = await datasets.ListSamplesAsync(versionId, split, 200, cursor);
                result.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        return result;
    }

    private static async Task<DatasetVerifyResult> RunVerifyAsync(ICore core, IProject project, Guid versionId, string operationId)
    {
        var input = await project.Datasets.PrepareVerifyAsync(versionId, operationId);
        var job = await core.Jobs.EnqueueAsync<IDatasetVerifyJob, DatasetVerifyInput>(input, project.Id, operationId);
        return (await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded)).Result!.Value.Deserialize<DatasetVerifyResult>(JobsManager.Json)!;
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

    private static Task<long> CountAsync(Project project, string sql) => project.Store.ReadAsync(async (connection, token) =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(token))!;
    });

    private static string Key(DatasetSplit split) => DatasetFiles.SplitKey(split);

    private static bool Same<T>(T left, T right) => JsonSerializer.Serialize(left, JobsManager.Json) == JsonSerializer.Serialize(right, JobsManager.Json);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Number of hard links of a file (GetFileInformationByHandle).</summary>
    internal static uint LinkCount(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) throw new IOException("GetFileInformationByHandle failed.");
        return information.NumberOfLinks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created;
        public System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
        public System.Runtime.InteropServices.ComTypes.FILETIME Written;
        public uint VolumeSerialNumber;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh;
        public uint IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);
}
