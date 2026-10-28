using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Generation;
using MautVision.Core.Projects.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Generation: the random generator, sampling, sizes and backgrounds against reference values
/// (computed with an independent JavaScript implementation), the skip of objects without room, sprites; recipes with validation of every field, revisions,
/// archive and deletion; blockers registered in the catalog, the backgrounds and the images; start checks; samples equal to the
/// run; runs with groups, lineage, reviewed annotations and empty frames; the same bytes for any thread count and after a restart
/// in the middle; cancel and failure leave a partial group; deletion of runs and repeat drafts; the throughput at 640×640.
/// </summary>
internal static class GenerationSmoke
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

        var root = Path.Combine(fixture, "generation-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var sources = Path.Combine(fixture, "generation-sources");
        Directory.CreateDirectory(sources);
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Генерация", "", "yolo26n", new InputSize(320, 256)));
        await RejectsCode("generation.project_not_open", () => project.Generation.ListRecipesAsync());
        await core.Projects.OpenAsync(project.Id);
        var generation = project.Generation;
        var events = new ConcurrentQueue<GenerationChangedEventArgs>();
        core.Projects.GenerationChanged += (_, args) => events.Enqueue(args);
        var handler = ((MautVision.Core.Core)core).GenerationRuns;
        var fixtures = await CreateSourcesAsync(core, project, sources, Check);

        // Recipes: every field is validated and named; references must exist.
        var form = new GenerationRecipeInput("  Кошки и собаки  ", "  Улица  ", 320, 256, [fixtures.SetA, fixtures.SetB], [fixtures.CatDay, fixtures.CatNight, fixtures.DogDay],
            [new(fixtures.Day, 70), new(fixtures.Night, 30), new(fixtures.Unused, 0)], 3, 0.25, 0.5);
        var invalid = new (string Field, GenerationRecipeInput Input)[]
        {
            ("name", form with { Name = "  " }),
            ("name", form with { Name = new string('я', 121) }),
            ("description", form with { Description = new string('x', 4001) }),
            ("canvas", form with { Width = 100 }),
            ("canvas", form with { Height = 0 }),
            ("backgroundSetIds", form with { BackgroundSetIds = [] }),
            ("backgroundSetIds", form with { BackgroundSetIds = [fixtures.SetA, fixtures.SetA] }),
            ("backgroundSetIds", form with { BackgroundSetIds = [Guid.Empty] }),
            ("backgroundSetIds", form with { BackgroundSetIds = [Guid.NewGuid()] }),
            ("classVariantIds", form with { ClassVariantIds = [] }),
            ("classVariantIds", form with { ClassVariantIds = [fixtures.CatDay, fixtures.CatDay] }),
            ("classVariantIds", form with { ClassVariantIds = [Guid.NewGuid()] }),
            ("classVariantIds", form with { VariantWeights = [new(fixtures.Day, 100), new(fixtures.Night, 0), new(fixtures.Unused, 0)] }),
            ("variantWeights", form with { VariantWeights = [] }),
            ("variantWeights", form with { VariantWeights = [new(fixtures.Day, 70), new(fixtures.Night, 29)] }),
            ("variantWeights", form with { VariantWeights = [new(fixtures.Day, 110), new(fixtures.Night, -10)] }),
            ("variantWeights", form with { VariantWeights = [new(fixtures.Day, double.NaN), new(fixtures.Night, 100)] }),
            ("variantWeights", form with { VariantWeights = [new(fixtures.Day, 50), new(fixtures.Day, 50)] }),
            ("variantWeights", form with { VariantWeights = [new(fixtures.Day, 70), new(fixtures.Night, 30), new(Guid.NewGuid(), 0)] }),
            ("objectsPerImage", form with { ObjectsPerImage = -1 }),
            ("emptyProbability", form with { EmptyProbability = 1.5 }),
            ("emptyProbability", form with { EmptyProbability = double.NaN }),
            ("scale", form with { Scale = 0 }),
            ("scale", form with { Scale = double.PositiveInfinity }),
        };
        foreach (var (field, candidate) in invalid)
        {
            var error = await RejectsCode("generation.invalid_recipe", () => generation.CreateRecipeAsync(candidate));
            Check(error.Details?["field"] == field, $"An invalid {field} is reported with its field ({error.Details?["field"]}).");
        }
        Check((await generation.ListRecipesAsync()).Count == 0, "Rejected recipes are not stored.");
        var almost = await generation.CreateRecipeAsync(form with { Name = "Почти 100", VariantWeights = [new(fixtures.Day, 70.0000005), new(fixtures.Night, 30), new(fixtures.Unused, 0)] });
        Check(almost.VariantWeights[0].Percentage == 70.0000005, "Shares within 1e-6 of 100 % are accepted and stored exactly.");
        var recipe = await generation.CreateRecipeAsync(form);
        Check(recipe is { Name: "Кошки и собаки", Description: "Улица", Revision: 1, ArchivedAt: null, ObjectsPerImage: 3 } && recipe.Width == 320
            && recipe.ClassVariantIds.SequenceEqual([fixtures.CatDay, fixtures.CatNight, fixtures.DogDay]) && recipe.VariantWeights[2].Percentage == 0,
            "A recipe is trimmed and stored with its lists in order at revision 1.");
        Check(events.Any(change => change.Recipes.Any(item => item.Id == recipe.Id && item.Revision == 1)), "Creation raises GenerationChanged.");
        await RejectsCode("generation.revision_conflict", () => generation.UpdateRecipeAsync(recipe.Id, form with { ObjectsPerImage = 2 }, 7));
        recipe = await generation.UpdateRecipeAsync(recipe.Id, form with { ObjectsPerImage = 2 }, recipe.Revision);
        Check(recipe is { Revision: 2, ObjectsPerImage: 2 }, "An update saves a new revision.");
        Check((await generation.UpdateRecipeAsync(recipe.Id, recipe.ToInput(), recipe.Revision)).Revision == 2, "An unchanged update saves nothing.");
        recipe = await generation.SetRecipeArchivedAsync(recipe.Id, true, recipe.Revision);
        Check(recipe is { Revision: 3, ArchivedAt: not null }, "Archiving is a saved change.");
        await RejectsCode("generation.recipe_archived", () => generation.UpdateRecipeAsync(recipe.Id, form, recipe.Revision));
        await RejectsCode("generation.unavailable", () => generation.PrepareRunAsync(recipe.Id, recipe.Revision, 1, 1, "archived-run"));

        // Blockers in other sections, archived presets included; a 0 % share and other sets do not block.
        var dayReview = await project.Variants.ReviewDeletionAsync(fixtures.Day);
        Check(dayReview.Blockers.Any(blocker => blocker is { Code: "generation.preset_reference", Count: 2 } && blocker.Examples.Contains("Кошки и собаки")),
            "A weighted project variant used by presets (an archived one too) cannot be deleted.");
        Check(!(await project.Variants.ReviewDeletionAsync(fixtures.Unused)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference"),
            "A project variant with a 0 % share does not block.");
        Check((await project.Classes.ReviewDeletionAsync(fixtures.Cat)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference")
            && (await project.Classes.ReviewVariantDeletionAsync(fixtures.CatNight)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference")
            && !(await project.Classes.ReviewDeletionAsync(fixtures.Spare)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference"),
            "Classes and class variants used by presets cannot be deleted; others can.");
        await RejectsCode("variants.deletion_blocked", () => project.Variants.DeleteAsync(fixtures.Day, dayReview.Revision));
        Check((await project.Backgrounds.ReviewSetDeletionAsync(fixtures.SetA)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference"),
            "A background set used by presets cannot be deleted.");
        var setAItems = (await project.Backgrounds.ListItemsAsync(fixtures.SetA, new BackgroundFilter(), 100)).Items.Select(item => item.Id).ToArray();
        Check(!(await project.Backgrounds.ReviewItemsDeletionAsync(fixtures.SetA, setAItems[..1])).Blockers.Any()
            && (await project.Backgrounds.ReviewItemsDeletionAsync(fixtures.SetA, setAItems)).Blockers.Any(blocker => blocker.Code == "generation.preset_reference"),
            "Deleting some backgrounds of a used set is allowed, deleting all its active ones is not.");
        Check(!(await project.Backgrounds.ReviewSetDeletionAsync(fixtures.SetEmpty)).Blockers.Any(), "An unused set can be deleted.");
        recipe = await generation.SetRecipeArchivedAsync(recipe.Id, false, recipe.Revision);
        await generation.DeleteRecipeAsync(almost.Id, almost.Revision);
        Check((await generation.ListRecipesAsync()).Single().Id == recipe.Id && events.Any(change => change.Recipes.Any(item => item.Id == almost.Id && item.Revision is null)),
            "A recipe is deleted with its revision and the event names it.");
        await RejectsCode("generation.unavailable", () => generation.GetRecipeAsync(almost.Id));

        // Start checks.
        await RejectsCode("generation.count_invalid", () => generation.PrepareRunAsync(recipe.Id, recipe.Revision, 1, 0, "zero"));
        await RejectsCode("generation.revision_conflict", () => generation.PrepareRunAsync(recipe.Id, 1, 1, 1, "stale"));
        async Task<GenerationRecipe> Variant(string name, GenerationRecipeInput input) => await generation.CreateRecipeAsync(input with { Name = name });
        var square = await Variant("Квадрат", form with { Width = 256, Height = 256 });
        await RejectsCode("generation.canvas_mismatch", () => generation.PrepareRunAsync(square.Id, 1, 1, 1, "square"));
        var otherSize = await Variant("Другой размер", form with { BackgroundSetIds = [fixtures.SetA, fixtures.SetSquare] });
        await RejectsCode("generation.background_size_mismatch", () => generation.PrepareRunAsync(otherSize.Id, 1, 1, 1, "other-size"));
        var emptySets = await Variant("Пустые фоны", form with { BackgroundSetIds = [fixtures.SetEmpty] });
        await RejectsCode("generation.backgrounds_empty", () => generation.PrepareRunAsync(emptySets.Id, 1, 1, 1, "empty-sets"));
        var bare = await Variant("Без изображений", form with { ClassVariantIds = [fixtures.CatDay, fixtures.BareDay] });
        await RejectsCode("generation.variant_without_asset", () => generation.PrepareRunAsync(bare.Id, 1, 1, 1, "bare"));
        var nightless = await Variant("Ночь без привязок", form with { ClassVariantIds = [fixtures.CatDay] });
        await RejectsCode("generation.variant_without_asset", () => generation.PrepareRunAsync(nightless.Id, 1, 1, 1, "nightless"));
        var jpeg = await Variant("JPEG", form with { ClassVariantIds = [fixtures.JpegDay], VariantWeights = [new(fixtures.Day, 100)] });
        await RejectsCode("generation.unsupported_asset", () => generation.PrepareRunAsync(jpeg.Id, 1, 1, 1, "jpeg"));
        var night = await project.Classes.GetVariantAsync(fixtures.CatNight);
        night = await project.Classes.SetVariantArchivedAsync(night.Id, true, night.Revision);
        await RejectsCode("generation.variant_unavailable", () => generation.PrepareRunAsync(recipe.Id, recipe.Revision, 1, 1, "archived-binding"));
        await project.Classes.SetVariantArchivedAsync(night.Id, false, night.Revision);
        var opaque = await Variant("Непрозрачный", form with { ClassVariantIds = [fixtures.OpaqueDay], VariantWeights = [new(fixtures.Day, 100)],
            ObjectsPerImage = 1, EmptyProbability = 0 });
        await RejectsCode("generation.unsupported_asset", () => generation.PreviewSamplesAsync(opaque.ToInput(), 1, 1));
        var opaqueInput = await generation.PrepareRunAsync(opaque.Id, 1, 1, 3, "opaque");
        var opaqueRun = await WaitAsync(core.Jobs, project.Id, (await Enqueue(core, project, opaqueInput, "opaque")).Id, JobStatus.Failed);
        var opaqueGroup = await project.Images.GetGroupAsync(Result(opaqueRun).OutputGroupId!.Value);
        Check(opaqueRun.Error?.Code == "generation.unsupported_asset" && opaqueGroup is { GenerationState: ImageGenerationState.Partial, ImageCount: 0 },
            "An image with an alpha channel but no transparent pixels fails the run by its pixels; the group is partial.");

        // Samples: the pixels and boxes of the first frames of a run with the same seed.
        var samples = await generation.PreviewSamplesAsync(recipe.ToInput(), 42, 4);
        Check(samples.Count == 4 && samples.Select(sample => sample.SampleIndex).SequenceEqual([0, 1, 2, 3]) && samples.All(sample => sample is { Width: 320, Height: 256 }
            && Decode(sample.Png) == (320, 256) && fixtures.Backgrounds.Contains(sample.BackgroundId)
            && sample.Objects.All(item => item.Box is { X: >= 0, Y: >= 0 } && item.Box.X + item.Box.Width <= 1 && item.Box.Y + item.Box.Height <= 1)),
            "Samples are PNG frames of the canvas size on recipe backgrounds with boxes inside the frame.");
        Check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "files", "generation-runs")) || !Directory.EnumerateFileSystemEntries(Path.Combine(root, "projects", project.Id, "files", "generation-runs")).Any(),
            "Samples store nothing.");

        // A run: group, order, lineage, reviewed annotations, empty frames, result; blockers while it produces.
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Workers = 1;
        handler.WindowSize = 3;
        handler.Pause = async (point, batch) =>
        {
            if (point != "saved" || batch != 0) return;
            paused.TrySetResult();
            await resume.Task;
        };
        var input = await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 42, 10, "run-1");
        Check(input.Recipe.Id == recipe.Id && input.Recipe.Revision == recipe.Revision && input.Seed == 42 && input.RequestedCount == 10
            && input.OutputGroupName == "Кошки и собаки · " + DateTime.Now.ToString("dd.MM.yyyy"), "The run input copies the recipe and names the group «<recipe> · <date>».");
        var job = await Enqueue(core, project, input, "run-1");
        await paused.Task.WaitAsync(WaitLimit);
        var running = await generation.GetRunAsync(job.Id);
        Check(running is { ProducedCount: 3, RequestedCount: 10, Seed: 42, OutputGroupId: not null } && running.Recipe.Name == "Кошки и собаки",
            "A running run has its group and the published prefix.");
        var producing = await project.Images.GetGroupAsync(running.OutputGroupId!.Value);
        Check(producing is { Origin: ImageGroupOrigin.Generated, GenerationState: ImageGenerationState.Producing, ImageCount: 3 } && producing.GenerationRunId == job.Id,
            "The output group is generated, belongs to the run and is producing.");
        var producingReview = await project.Images.ReviewGroupDeletionAsync(producing.Id);
        Check(producingReview.Blockers.Any(blocker => blocker.Code == "images.generation_active" && blocker.Examples.SequenceEqual([producing.Name])),
            "A producing group cannot be deleted: «Генерация группы «<имя>»».");
        await RejectsCode("images.group_busy", () => project.Images.SetGroupArchivedAsync(producing.Id, true, producing.Revision));
        var firstImage = (await project.Images.ListImagesAsync(producing.Id, new ImageFilter(), 10)).Items[0];
        await RejectsCode("images.group_busy", () => project.Images.SaveAnnotationAsync(firstImage.Id, new AnnotationInput([], null, ImageReviewState.Reviewed), 1));
        await RejectsCode("generation.deletion_blocked", () => generation.DeleteRecipeAsync(recipe.Id, recipe.Revision));
        await RejectsCode("generation.run_active", () => generation.DeleteRunAsync(job.Id));
        Check(Directory.EnumerateFiles(Path.Combine(root, "projects", project.Id, "files", "generation-runs", job.Id.ToString("D")), "*", SearchOption.AllDirectories).Count()
            == fixtures.ReachableFiles, "The snapshot holds only the images of the recipe bindings and the backgrounds of its sets.");
        resume.TrySetResult();
        handler.Pause = null;
        var done = await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
        var result = Result(done);
        var group = await project.Images.GetGroupAsync(running.OutputGroupId.Value);
        var images = (await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 100)).Items;
        var annotations = await project.Images.ListAnnotationsAsync(images.Select(image => image.Id).ToArray());
        Check(result is { RequestedCount: 10, ProducedCount: 10 } && result.OutputGroupId == group.Id && result.Seed == 42 && done.Processed == 10 && done.Total == 10
            && group is { GenerationState: ImageGenerationState.Completed, ImageCount: 10, SplitHint: ImageSplitHint.Unassigned } && group.Name == input.OutputGroupName,
            "A finished run completes its group with every requested frame.");
        Check(images.Select(image => image.DisplayName).SequenceEqual(Enumerable.Range(0, 10).Select(index => "generated-" + index))
            && images.All(image => image is { Format: ImageFileFormat.Png, Width: 320, Height: 256, ReviewState: ImageReviewState.Reviewed, AnnotationRevision: 1 }
                && image.Lineage is { Kind: ImageLineageKind.Generated } && image.Lineage.GenerationRunId == job.Id && image.Lineage.RecipeRevision == recipe.Revision
                && image.Lineage.SourceFamilyId == "generation:" + job.Id.ToString("D") && image.OperationId == "run-1")
            && images.Select(image => image.Lineage.SampleIndex).SequenceEqual(Enumerable.Range(0, 10).Select(index => (int?)index)),
            "Images follow the sample order with lineage generated {run, sample, recipe revision} and family generation:<run>.");
        Check(annotations.All(annotation => annotation is { ReviewState: ImageReviewState.Reviewed, Roi: null })
            && annotations.Any(annotation => annotation.Objects.Count == 0) && annotations.Any(annotation => annotation.Objects.Count > 0)
            && images.Zip(annotations).All(pair => pair.First.ObjectCount == pair.Second.Objects.Count),
            "Annotations are reviewed without ROI; empty frames are reviewed without objects.");
        Check(images.Take(4).Zip(samples).All(pair => pair.First.PixelHash == Pixels(pair.Second.Png))
            && annotations.Take(4).Zip(samples).All(pair => pair.First.Objects.Select(item => (item.ClassId, item.ClassVariantId, item.Box))
                .SequenceEqual(pair.Second.Objects.Select(item => (item.ClassId, (Guid?)item.ClassVariantId, item.Box)))),
            "Samples have the pixels and boxes of the run's first frames.");
        Check(annotations.SelectMany(annotation => annotation.Objects).All(item => fixtures.BindingClasses[item.ClassVariantId!.Value] == item.ClassId),
            "Objects reference the class of their binding.");
        Check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "files", "generation-runs", job.Id.ToString("D"))),
            "The snapshot is released when the run finishes.");
        var listed = await generation.ListRunsAsync();
        Check(listed.Total == 2 && listed.Items[0].Job.Id == job.Id && listed.Items[0].ProducedCount == 10 && listed.Items[1].Job.Id == opaqueRun.Id,
            "Runs are listed newest first with the published count.");
        Check(events.Any(change => change.Runs.Contains(job.Id)), "A prepared run raises GenerationChanged.");

        // The same seed gives the same bytes with any number of threads.
        handler.Workers = 4;
        handler.WindowSize = 4;
        var parallel = await RunAsync(core, project, await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 42, 10, "run-parallel"), "run-parallel");
        var parallelImages = (await project.Images.ListImagesAsync(Result(parallel).OutputGroupId!.Value, new ImageFilter(), 100)).Items;
        var parallelAnnotations = await project.Images.ListAnnotationsAsync(parallelImages.Select(image => image.Id).ToArray());
        Check(parallelImages.Select(image => image.Sha256).SequenceEqual(images.Select(image => image.Sha256))
            && parallelAnnotations.Select(Boxes).SequenceEqual(annotations.Select(Boxes)),
            "Another thread count and window give the same PNG bytes and boxes.");
        var otherSeed = await RunAsync(core, project, await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 43, 4, "run-seed"), "run-seed");
        var otherImages = (await project.Images.ListImagesAsync(Result(otherSeed).OutputGroupId!.Value, new ImageFilter(), 100)).Items;
        Check(!otherImages.Select(image => image.Sha256).SequenceEqual(images.Take(4).Select(image => image.Sha256)), "Another seed gives other frames.");

        // An object without room is skipped and counted.
        var crowded = await Variant("Тесно", form with { ObjectsPerImage = 12, Scale = 0.9, EmptyProbability = 0 });
        var crowdedRun = Result(await RunAsync(core, project, await generation.PrepareRunAsync(crowded.Id, 1, 5, 6, "run-crowded"), "run-crowded"));
        var crowdedAnnotations = await project.Images.ListAnnotationsAsync((await project.Images.ListImagesAsync(crowdedRun.OutputGroupId!.Value, new ImageFilter(), 10))
            .Items.Select(image => image.Id).ToArray());
        Check(crowdedRun is { ProducedCount: 6, MissingObjectFrames: > 0 } && crowdedAnnotations.All(annotation => annotation.Objects.Count < 12)
            && crowdedAnnotations.All(annotation => !Overlaps(annotation.Objects.Select(item => item.Box).ToArray())),
            "Objects without room are skipped: frames are published with fewer objects, never overlapping, and counted.");

        // Cancel and failure keep what was published; the group is partial.
        handler.Workers = 1;
        handler.WindowSize = 2;
        var cancelPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, batch) =>
        {
            if (point != "saved" || batch != 1) return;
            cancelPaused.TrySetResult();
            await cancelResume.Task;
        };
        var cancelJob = await Enqueue(core, project, await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 7, 30, "run-cancel"), "run-cancel");
        await cancelPaused.Task.WaitAsync(WaitLimit);
        await core.Jobs.CancelAsync(project.Id, cancelJob.Id);
        cancelResume.TrySetResult();
        var cancelled = Result(await WaitAsync(core.Jobs, project.Id, cancelJob.Id, JobStatus.Cancelled));
        var cancelledGroup = await project.Images.GetGroupAsync(cancelled.OutputGroupId!.Value);
        Check(cancelled.ProducedCount == 4 && cancelledGroup is { GenerationState: ImageGenerationState.Partial, ImageCount: 4 },
            "A cancelled run keeps its published frames and its group becomes partial.");
        handler.Pause = (point, batch) => point == "saved" && batch == 0 ? throw new IOException("Simulated write failure.") : Task.CompletedTask;
        var failed = await WaitAsync(core.Jobs, project.Id, (await Enqueue(core, project,
            await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 8, 10, "run-fail"), "run-fail")).Id, JobStatus.Failed);
        var failedGroup = await project.Images.GetGroupAsync(Result(failed).OutputGroupId!.Value);
        Check(failed.Error?.Code == "generation.execution_failed" && Result(failed).ProducedCount == 2 && failedGroup is { GenerationState: ImageGenerationState.Partial, ImageCount: 2 },
            "A failed run keeps its published frames and its group becomes partial.");
        handler.Pause = null;
        Check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "files", "generation-runs"))
            || !Directory.EnumerateFileSystemEntries(Path.Combine(root, "projects", project.Id, "files", "generation-runs")).Any(),
            "Cancelled and failed runs release their snapshots.");

        // Repeat drafts and deletion of runs.
        var draft = await generation.GetRepeatDraftAsync(job.Id);
        Check(draft.Recipe.Name == "Кошки и собаки" && draft.Seed == 42 && draft.RequestedCount == 10 && draft.SourceRunId == job.Id
            && draft.Recipe.ClassVariantIds.SequenceEqual(recipe.ClassVariantIds) && draft.Recipe.VariantWeights.SequenceEqual(recipe.VariantWeights),
            "\"Configure repeat\" gives the run settings with the preset name (the UI adds the repeat suffix), its seed and count.");
        var longName = await Variant(new string('д', 120), form);
        var longRun = await RunAsync(core, project, await generation.PrepareRunAsync(longName.Id, 1, 1, 1, "run-long"), "run-long");
        var longDraft = await generation.GetRepeatDraftAsync(longRun.Id);
        Check(longDraft.Recipe.Name == new string('д', 120)
            && (await project.Images.GetGroupAsync(Result(longRun).OutputGroupId!.Value)).Name.Length == 120,
            "A long preset name stays whole in the repeat draft; the group name is shortened to 120 characters.");
        await generation.DeleteRunAsync(job.Id);
        await RejectsCode("generation.unavailable", () => generation.GetRunAsync(job.Id));
        Check((await project.Images.GetGroupAsync(group.Id)).ImageCount == 10 && (await generation.ListRunsAsync()).Items.All(item => item.Job.Id != job.Id),
            "A deleted run leaves its images.");
        await RejectsCode("generation.deleted_operation", () => generation.PrepareRunAsync(recipe.Id, recipe.Revision, 42, 10, "run-1"));
        var again = await generation.PrepareRunAsync(recipe.Id, recipe.Revision, 1, 1, "run-parallel");
        Check(again.Seed == 42 && again.RequestedCount == 10, "A repeated request of an existing operation returns that run's input.");
        await generation.DeleteRecipeAsync(recipe.Id, recipe.Revision);
        var kept = await generation.GetRunAsync(parallel.Id);
        Check(kept.Recipe.Name == "Кошки и собаки" && kept.Recipe.Id == recipe.Id, "Runs keep the recipe copy after the recipe is deleted.");

        // A restart in the middle of a run: it resumes from its checkpoint without duplicates and with the same bytes.
        await CheckRestartAsync(fixture, createCore, seedInstalledModel, Check);
        await MeasureAsync(fixture, createCore, seedInstalledModel, Check);
        return checks;
    }

    /// <summary>Reference values computed with an independent JavaScript implementation.</summary>
    private static void CheckAlgorithm(Action<bool, string> check)
    {
        static uint[] Draws(uint seed, int sampleIndex, int count)
        {
            var random = new FrameRandom(seed, sampleIndex);
            return Enumerable.Range(0, count).Select(_ => random.NextUInt32()).ToArray();
        }
        check(Draws(12345, 0, 4).SequenceEqual([2566602495u, 2711965971u, 2674996497u, 1984766809u])
            && Draws(4294967295, 7, 3).SequenceEqual([1057206203u, 3657806699u, 3552123713u])
            && Draws(0, 2147483647, 2).SequenceEqual([3524353788u, 1924613307u]),
            "mulberry32 per frame from seed ^ imul(sampleIndex + 1, 0x9e3779b9) matches the reference values.");
        check(new[] { 0, 1, 2, 3, 4, 1000 }.Select(index => FrameRandom.BackgroundIndex(42, index, 7)).SequenceEqual([5, 6, 4, 5, 6, 6])
            && new[] { 0, 1, 2 }.Select(index => FrameRandom.BackgroundIndex(4294967295, index, 1000)).SequenceEqual([760, 225, 986]),
            "The background index (seed + imul(i + 1, 2654435761)) mod N matches the reference values.");

        // Bindings: 0 = a1 (images 0, 1), 1 = a2 (image 2) of variant A; 2 = b1 (images 3, 4) of variant B; C has no share.
        (int, int)[][] sizes = [[(120, 80), (1000, 300)], [(50, 400)], [(333, 333), (7, 9)]];
        int[][] ids = [[0, 1], [2], [3, 4]];
        LayoutWeight[] weights = [new(0, []), new(70, [0, 1]), new(30, [2])];
        var layout = new GenerationLayout(640, 480, 3, 0.2, 0.5, weights, [2, 1, 2], 7);
        string Plan(GenerationLayout value, uint seed, int index)
        {
            var plan = GenerationPlanner.Plan(value, seed, index, (binding, asset) => sizes[binding][asset]);
            return JsonSerializer.Serialize(new
            {
                objects = plan.Objects.Select(item => new[] { ids[item.Binding][item.Asset], item.Left, item.Top, item.Width, item.Height }),
                skipped = plan.Missing,
            });
        }
        string[] expected =
        [
            """{"objects":[[4,446,455,7,9],[2,331,141,30,240],[4,322,129,7,9]],"skipped":0}""",
            """{"objects":[[4,411,420,7,9],[2,267,191,30,240],[2,569,147,30,240]],"skipped":0}""",
            """{"objects":[],"skipped":0}""",
            """{"objects":[[0,455,224,120,80],[2,363,133,30,240],[2,332,92,30,240]],"skipped":0}""",
            """{"objects":[[2,55,199,30,240],[2,510,159,30,240],[2,12,70,30,240]],"skipped":0}""",
            """{"objects":[[4,424,265,7,9],[2,457,18,30,240],[1,136,57,320,96]],"skipped":0}""",
        ];
        check(Enumerable.Range(0, 6).Select(index => Plan(layout, 42, index)).SequenceEqual(expected),
            "Variant by weight, binding and image uniformly, size and placement match the reference values frame by frame.");
        var crowded = layout with { ObjectsPerImage = 6, EmptyProbability = 0, Scale = 0.6 };
        string[] crowdedExpected =
        [
            """{"objects":[[2,156,39,36,288],[3,222,163,288,288],[1,219,35,384,115],[4,632,6,7,9]],"skipped":2}""",
            """{"objects":[[3,335,88,288,288],[3,14,116,288,288],[4,464,11,7,9],[4,0,59,7,9]],"skipped":2}""",
            """{"objects":[[4,341,429,7,9],[0,214,22,120,80],[3,56,124,288,288],[2,475,182,36,288],[0,385,36,120,80]],"skipped":1}""",
        ];
        check(Enumerable.Range(0, 3).Select(index => Plan(crowded, 7, index)).SequenceEqual(crowdedExpected),
            "Improvement: an object without room is skipped, the next objects keep the same draws as the reference.");
        check(GenerationPlanner.ObjectSize(layout with { Scale = 2 }, 1000, 300) == (640, 192) && GenerationPlanner.ObjectSize(layout, 7, 9) == (7, 9)
            && GenerationPlanner.ObjectSize(layout with { Width = 10, Height = 10, Scale = 0.25 }, 5, 5) == (3, 3)
            && GenerationPlanner.ObjectSize(layout with { Width = 64, Height = 64, Scale = 0.01 }, 1000, 1000) == (1, 1),
            "Sizes: a scale above 1 acts as 1, small sprites never grow, halves round up as Math.round, a side is at least 1.");
        var full = new GenerationLayout(32, 32, 3, 0, 1, [new(100, [0])], [1], 1);
        var plan = GenerationPlanner.Plan(full, 1, 0, (_, _) => (32, 32));
        check(plan.Objects.Count == 1 && plan.Missing == 2 && plan.Objects[0] is { Left: 0, Top: 0, Width: 32, Height: 32 },
            "A full frame keeps the first object and counts the rest as missing.");

        check(GenerationRunHandler.FailureCode(new IOException("full", unchecked((int)0x80070070)), prepared: true) == "generation.disk_full"
            && GenerationRunHandler.FailureCode(new InvalidOperationException("wrapped", new OutOfMemoryException()), prepared: true) == "generation.out_of_memory"
            && GenerationRunHandler.FailureCode(new IOException("locked"), prepared: false) == "generation.preparation_failed"
            && GenerationRunHandler.FailureCode(new IOException("locked"), prepared: true) == "generation.execution_failed",
            "Run failures are named: full disk, lack of memory, preparation or execution.");

        var pixels = new byte[4 * 4 * 4];
        check(SpritePixels.AlphaBounds(pixels, 4, 4) is null, "A fully transparent image has no sprite.");
        Array.Fill(pixels, (byte)255);
        check(SpritePixels.AlphaBounds(pixels, 4, 4) is null, "An image without transparent pixels has no sprite.");
        Array.Clear(pixels);
        pixels[(1 * 4 + 2) * 4 + 3] = 1;
        pixels[(2 * 4 + 1) * 4 + 3] = 200;
        check(SpritePixels.AlphaBounds(pixels, 4, 4) == new SKRectI(1, 1, 3, 3), "The trim takes the bounds of pixels with alpha above 0.");
    }

    private sealed record Sources(Guid Day, Guid Night, Guid Unused, Guid Cat, Guid Spare, Guid CatDay, Guid CatNight, Guid DogDay, Guid BareDay, Guid JpegDay,
        Guid OpaqueDay, Guid SetA, Guid SetB, Guid SetEmpty, Guid SetSquare, IReadOnlySet<Guid> Backgrounds, IReadOnlyDictionary<Guid, Guid> BindingClasses,
        int ReachableFiles);

    /// <summary>Catalog with transparent sprites, a JPEG and an opaque PNG with alpha, background sets of the canvas and of another size.</summary>
    private static async Task<Sources> CreateSourcesAsync(ICore core, Project project, string directory, Action<bool, string> check)
    {
        var day = await project.Variants.CreateAsync("День", "");
        var night = await project.Variants.CreateAsync("Ночь", "");
        var unused = await project.Variants.CreateAsync("Макет", "");
        var cat = await project.Classes.CreateAsync("Кошка", "");
        var dog = await project.Classes.CreateAsync("Собака", "");
        var spare = await project.Classes.CreateAsync("Запасной", "");
        var catDay = await project.Classes.AddVariantAsync(cat.Id, day.Id);
        var catNight = await project.Classes.AddVariantAsync(cat.Id, night.Id);
        var dogDay = await project.Classes.AddVariantAsync(dog.Id, day.Id);
        var bareDay = await project.Classes.AddVariantAsync(spare.Id, day.Id);
        var jpegClass = await project.Classes.CreateAsync("JPEG", "");
        var jpegDay = await project.Classes.AddVariantAsync(jpegClass.Id, day.Id);
        var opaqueClass = await project.Classes.CreateAsync("Непрозрачный", "");
        var opaqueDay = await project.Classes.AddVariantAsync(opaqueClass.Id, day.Id);
        async Task Import(Guid binding, string operation, params string[] files)
        {
            var job = await core.Jobs.EnqueueAsync<MautVision.Core.Interfaces.Jobs.IImportClassAssetsJob, ImportClassAssetsInput>(
                new ImportClassAssetsInput(binding, files), project.Id, operation);
            await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
        }
        string Write(string name, byte[] content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }
        await Import(catDay.Id, "assets-cat-day", Write("кошка-день-1.png", Sprite(90, 60, 1)), Write("кошка-день-2.png", Sprite(40, 120, 2)));
        await Import(catNight.Id, "assets-cat-night", Write("кошка-ночь.png", Sprite(200, 150, 3)));
        await Import(dogDay.Id, "assets-dog-day", Write("собака-день.png", Sprite(70, 70, 4)));
        await Import(jpegDay.Id, "assets-jpeg", Write("фото.jpg", Encode(Solid(50, 50), SKEncodedImageFormat.Jpeg)));
        await Import(opaqueDay.Id, "assets-opaque", Write("непрозрачный.png", Encode(Solid(50, 50), SKEncodedImageFormat.Png)));
        check((await project.Classes.GetVariantAsync(opaqueDay.Id)).OpaqueAssetCount == 0 && (await project.Classes.GetVariantAsync(jpegDay.Id)).OpaqueAssetCount == 1,
            "The opaque PNG keeps its alpha channel, the JPEG has none.");

        var backgrounds = project.Backgrounds;
        async Task<(Guid Set, IReadOnlyList<Guid> Items)> Set(string name, int count, int width, int height, int seed)
        {
            var set = await backgrounds.CreateSetAsync(name, "");
            if (count == 0) return (set.Id, []);
            var files = Enumerable.Range(0, count).Select(index => new BackgroundFileSource(Write($"{name}-{index}.png", Background(width, height, seed + index)))).ToArray();
            var job = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(set.Id, files), project.Id, "bg-" + name);
            await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
            return (set.Id, (await backgrounds.ListItemsAsync(set.Id, new BackgroundFilter(), 100)).Items.Select(item => item.Id).ToArray());
        }
        var setA = await Set("Улица", 3, 320, 256, 10);
        var setB = await Set("Двор", 2, 320, 256, 20);
        var setEmpty = await Set("Пусто", 0, 320, 256, 0);
        await project.UpdateAsync(new ProjectSettings(project.Name, "", "yolo26n", new InputSize(256, 256)), project.Revision);
        var setSquare = await Set("Квадрат", 1, 256, 256, 30);
        await project.UpdateAsync(new ProjectSettings(project.Name, "", "yolo26n", new InputSize(320, 256)), project.Revision);
        return new Sources(day.Id, night.Id, unused.Id, cat.Id, spare.Id, catDay.Id, catNight.Id, dogDay.Id, bareDay.Id, jpegDay.Id, opaqueDay.Id,
            setA.Set, setB.Set, setEmpty.Set, setSquare.Set, setA.Items.Concat(setB.Items).ToHashSet(),
            new Dictionary<Guid, Guid> { [catDay.Id] = cat.Id, [catNight.Id] = cat.Id, [dogDay.Id] = dog.Id },
            ReachableFiles: 4 + 5);
    }

    /// <summary>The process dies after a window is committed and before its checkpoint; a new Core resumes without repeats.</summary>
    private static async Task CheckRestartAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        var root = Path.Combine(fixture, "generation-restart");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var dying = await createCore(root, null);
        var project = (Project)await dying.Projects.CreateAsync(new ProjectSettings("Перезапуск", "", "yolo26n", new InputSize(320, 256)));
        await dying.Projects.OpenAsync(project.Id);
        var sources = Path.Combine(fixture, "generation-restart-sources");
        Directory.CreateDirectory(sources);
        var fixtures = await CreateSourcesAsync(dying, project, sources, check);
        var recipe = await project.Generation.CreateRecipeAsync(new GenerationRecipeInput("Перезапуск", "", 320, 256, [fixtures.SetA, fixtures.SetB],
            [fixtures.CatDay, fixtures.CatNight, fixtures.DogDay], [new(fixtures.Day, 60), new(fixtures.Night, 40)], 3, 0.1, 0.6));
        var handler = ((MautVision.Core.Core)dying).GenerationRuns;
        handler.Workers = 2;
        handler.WindowSize = 4;
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, batch) =>
        {
            if (point != "published" || batch != 1) return;
            died.TrySetResult();
            await new TaskCompletionSource().Task;
        };
        var interrupted = await Enqueue(dying, project, await project.Generation.PrepareRunAsync(recipe.Id, 1, 99, 12, "restart"), "restart");
        await died.Task.WaitAsync(WaitLimit);
        var groupId = (await project.Generation.GetRunAsync(interrupted.Id)).OutputGroupId!.Value;
        check((await project.Images.GetGroupAsync(groupId)) is { ImageCount: 8, GenerationState: ImageGenerationState.Producing },
            "Before the restart the second window is committed but not yet checkpointed.");
        // The application is gone: its deferred writes never land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();

        var revived = await createCore(root, null);
        var revivedProject = (Project)await revived.Projects.OpenAsync(project.Id);
        var resumed = Result(await WaitAsync(revived.Jobs, project.Id, interrupted.Id, JobStatus.Succeeded));
        var images = (await revivedProject.Images.ListImagesAsync(groupId, new ImageFilter(), 100)).Items;
        var group = await revivedProject.Images.GetGroupAsync(groupId);
        var reference = await RunAsync(revived, revivedProject, await revivedProject.Generation.PrepareRunAsync(recipe.Id, 1, 99, 12, "restart-reference"), "restart-reference");
        var referenceImages = (await revivedProject.Images.ListImagesAsync(Result(reference).OutputGroupId!.Value, new ImageFilter(), 100)).Items;
        var imagesDirectory = Path.Combine(root, "projects", project.Id, "files", "images");
        check(resumed is { ProducedCount: 12 } && group is { ImageCount: 12, GenerationState: ImageGenerationState.Completed }
            && images.Select(image => image.Lineage.SampleIndex).SequenceEqual(Enumerable.Range(0, 12).Select(index => (int?)index))
            && images.Select(image => image.Sha256).SequenceEqual(referenceImages.Select(image => image.Sha256))
            && Directory.GetFiles(imagesDirectory).Length == 24 && !Directory.EnumerateFiles(imagesDirectory, ".*").Any(),
            "A run resumed after a restart publishes each frame once, in order, with the bytes of an uninterrupted run.");
        check(!Directory.Exists(Path.Combine(root, "projects", project.Id, "files", "generation-runs", interrupted.Id.ToString("D"))),
            "The resumed run releases its snapshot.");
    }

    /// <summary>640×640 frames with 3 objects: the run throughput and the share of compositing in a frame.</summary>
    private static async Task MeasureAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        var root = Path.Combine(fixture, "generation-perf");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Замер", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var directory = Path.Combine(fixture, "generation-perf-sources");
        Directory.CreateDirectory(directory);
        var variant = await project.Variants.CreateAsync("День", "");
        var bindings = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var klass = await project.Classes.CreateAsync("Класс " + index, "");
            var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id);
            var sprites = Enumerable.Range(0, 2).Select(asset =>
            {
                var path = Path.Combine(directory, $"sprite-{index}-{asset}.png");
                File.WriteAllBytes(path, Sprite(260 + asset * 40, 220 + index * 30, 100 + index * 10 + asset));
                return path;
            }).ToArray();
            var job = await core.Jobs.EnqueueAsync<MautVision.Core.Interfaces.Jobs.IImportClassAssetsJob, ImportClassAssetsInput>(
                new ImportClassAssetsInput(binding.Id, sprites), project.Id, "perf-assets-" + index);
            await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
            bindings.Add(binding.Id);
        }
        var set = await project.Backgrounds.CreateSetAsync("Фоны", "");
        var backgrounds = Enumerable.Range(0, 40).Select(index =>
        {
            var path = Path.Combine(directory, $"background-{index}.png");
            File.WriteAllBytes(path, Background(640, 640, 500 + index, noise: 10));
            return new BackgroundFileSource(path);
        }).ToArray();
        var import = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(set.Id, backgrounds), project.Id, "perf-bg");
        await WaitAsync(core.Jobs, project.Id, import.Id, JobStatus.Succeeded);
        var recipe = await project.Generation.CreateRecipeAsync(new GenerationRecipeInput("Замер", "", 640, 640, [set.Id], bindings, [new(variant.Id, 100)], 3, 0, 0.4));

        // One frame on one thread: planning and compositing against PNG encoding and hashing.
        var manifest = await project.GenerationManager.LoadManifestAsync(recipe, CancellationToken.None);
        var files = await project.GenerationManager.GetFilesAsync(CancellationToken.None);
        using var renderer = new GenerationRenderer(manifest.Layout(recipe), 1,
            (binding, asset) => files.GetPath(ClassAsset.ResourceKind, manifest.Bindings[binding].Assets[asset].Id, manifest.Bindings[binding].Assets[asset].Extension),
            index => files.GetPath(BackgroundItem.ResourceKind, manifest.Backgrounds[index], ".png"));
        for (var index = 0; index < 60; index++) renderer.Render(index).Dispose();
        const int Frames = 60;
        var whole = Stopwatch.StartNew();
        for (var index = 0; index < Frames; index++) renderer.Render(index).Dispose();
        var frameMs = whole.Elapsed.TotalMilliseconds / Frames;
        // The encoding share of a frame on a rendered frame, and the PNG settings: the store's Paeth at zlib level 1 against Skia's default.
        using var sample = renderer.Render(0);
        using var decoded = SKBitmap.Decode(sample.Png.ToArray());
        using var copy = decoded.Copy(SKColorType.Rgba8888);
        var real = copy.GetPixelSpan().ToArray();
        double EncodeMs(SKPngEncoderOptions options, out long bytes)
        {
            var previous = ImagePixels.PngOptions;
            ImagePixels.PngOptions = options;
            try
            {
                using (var warm = ImagePixels.EncodePng(real, 640, 640)) bytes = warm.Size;
                var timer = Stopwatch.StartNew();
                for (var index = 0; index < 20; index++)
                {
                    using var png = ImagePixels.EncodePng(real, 640, 640);
                    ImagePixels.Hash(real, 640, 640);
                }
                return timer.Elapsed.TotalMilliseconds / 20;
            }
            finally
            {
                ImagePixels.PngOptions = previous;
            }
        }
        var encodeMs = EncodeMs(ImagePixels.PngOptions, out var fastBytes);
        var defaultMs = EncodeMs(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6), out var defaultBytes);

        const int RunFrames = 400;
        var watch = Stopwatch.StartNew();
        var run = await RunAsync(core, project, await project.Generation.PrepareRunAsync(recipe.Id, 1, 7, RunFrames, "perf-run"), "perf-run");
        var rate = RunFrames / watch.Elapsed.TotalSeconds;
        var result = Result(run);
        check(result.ProducedCount == RunFrames, "The measured run publishes every frame.");
        check(rate >= 50, $"640×640 with 3 objects runs at least 50 frames/s ({rate:F0}).");
        Console.WriteLine($"Generation 640x640, 3 objects, photo-like backgrounds: run {rate:F0} frames/s with {((MautVision.Core.Core)core).GenerationRuns.Workers} workers; "
            + $"one frame on one thread {frameMs:F2} ms: PNG encode + hashes {encodeMs:F2} ms, plan + composite {frameMs - encodeMs:F2} ms; "
            + $"PNG Paeth/1 {fastBytes / 1024} KB vs AllFilters/6 {defaultBytes / 1024} KB in {defaultMs:F2} ms.");
    }

    private static Task<JobInfo> Enqueue(ICore core, IProject project, GenerationRunInput input, string operation)
        => core.Jobs.EnqueueAsync<IGenerationRunJob, GenerationRunInput>(input, project.Id, operation);

    private static async Task<JobInfo> RunAsync(ICore core, IProject project, GenerationRunInput input, string operation)
        => await WaitAsync(core.Jobs, project.Id, (await Enqueue(core, project, input, operation)).Id, JobStatus.Succeeded);

    private static GenerationRunResult Result(JobInfo job) => job.Result!.Value.Deserialize<GenerationRunResult>(JsonSerializerOptions.Web)!;

    private static string Boxes(ImageAnnotation annotation) => string.Join(";", annotation.Objects.Select(item => $"{item.ClassVariantId}:{item.Box}"));

    private static bool Overlaps(IReadOnlyList<ImageBox> boxes)
    {
        for (var first = 0; first < boxes.Count; first++)
        {
            for (var second = first + 1; second < boxes.Count; second++)
            {
                var (a, b) = (boxes[first], boxes[second]);
                if (a.X + a.Width - 1e-9 > b.X && b.X + b.Width - 1e-9 > a.X && a.Y + a.Height - 1e-9 > b.Y && b.Y + b.Height - 1e-9 > a.Y) return true;
            }
        }
        return false;
    }

    private static (int, int) Decode(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        return (bitmap.Width, bitmap.Height);
    }

    private static string Pixels(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return ImagePixels.Decode(stream)!.Value.PixelHash;
    }

    /// <summary>A transparent PNG with an opaque shape and a soft edge.</summary>
    private static byte[] Sprite(int width, int height, int seed)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)), IsAntialias = true };
            canvas.DrawOval(new SKRect(width * 0.1f, height * 0.15f, width * 0.9f, height * 0.85f), paint);
            paint.Color = new SKColor(255, 255, 255, 128);
            canvas.DrawRect(width * 0.3f, height * 0.3f, width * 0.2f, height * 0.2f, paint);
        }
        return Encode(bitmap, SKEncodedImageFormat.Png);
    }

    /// <summary>An opaque picture stored as RGBA (unpremultiplied): an alpha channel without transparent pixels.</summary>
    private static SKBitmap Solid(int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.OrangeRed);
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
    {
        using (bitmap)
        {
            using var data = bitmap.Encode(format, 90);
            return data.ToArray();
        }
    }

    private static byte[] Background(int width, int height, int seed, int noise = 0)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var paint = new SKPaint();
            for (var shape = 0; shape < 12; shape++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawRect(random.Next(width), random.Next(height), width / 4f + random.Next(width / 2), height / 4f + random.Next(height / 2), paint);
            }
        }
        if (noise > 0)
        {
            // Sensor-like noise: photographs compress far worse than flat shapes, which matters for the PNG cost.
            var pixels = bitmap.GetPixelSpan();
            for (var index = 0; index < pixels.Length; index++)
            {
                if (index % 4 != 3) pixels[index] = (byte)Math.Clamp(pixels[index] + random.Next(-noise, noise + 1), 0, 255);
            }
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
}
