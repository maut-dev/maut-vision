using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Variants, classes, bindings and source images: validation, names, revisions, archive rules, order, colours, deletion
/// with review, blockers and the cleanup queue, the import job (formats, duplicates, alpha, cancel, restart) and timings.
/// </summary>
internal static class CatalogSmoke
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
        async Task<T> Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T error) { checks++; return error; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
        async Task RejectsCode(string code, Func<Task> action)
        {
            var error = await Rejects<CoreException>(action);
            if (error.Code != code) throw new InvalidOperationException($"Expected {code}, got {error.Code}.");
        }

        var root = Path.Combine(fixture, "catalog-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var sources = Path.Combine(fixture, "catalog-sources");
        Directory.CreateDirectory(sources);
        var core = await createCore(root, null);
        var project = await core.Projects.CreateAsync(new ProjectSettings("Каталог", "", "yolo26n", new InputSize(640, 640)));
        var assetsDirectory = Path.Combine(root, "projects", project.Id, "files", "class-assets");
        string Database() => Path.Combine(root, "projects", project.Id, ProjectStore.DatabaseFileName);

        // Before OpenAsync the catalog is refused with a clear code and project.db is not created.
        await RejectsCode("variants.project_not_open", () => project.Variants.ListAsync());
        await RejectsCode("classes.project_not_open", () => project.Classes.GetWorkspaceAsync());
        await RejectsCode("classes.project_not_open", () => project.Classes.CreateAsync("Кот", ""));
        Check(!File.Exists(Database()), "Catalog calls on an unopened project do not open project.db.");
        await core.Projects.OpenAsync(project.Id);

        var events = new ConcurrentQueue<CatalogChangedEventArgs>();
        core.Projects.CatalogChanged += (_, args) => events.Enqueue(args);
        var variants = project.Variants;
        var classes = project.Classes;

        // Variants: validation, trimming, case-insensitive uniqueness including the archive, revisions.
        var empty = await classes.GetWorkspaceAsync();
        Check(empty.Variants.Count == 0 && empty.Classes.Count == 0 && empty.ClassVariants.Count == 0 && empty.ClassOrderRevision == 1,
            "A new project has an empty catalog.");
        foreach (var name in new[] { "", "   ", new string('я', 121) })
            await RejectsCode("variants.name_invalid", () => variants.CreateAsync(name, ""));
        await RejectsCode("variants.description_invalid", () => variants.CreateAsync("Описание", new string('x', 2001)));
        var day = await variants.CreateAsync("  День  ", "  Светлый фон  ");
        Check(day.Name == "День" && day.Description == "Светлый фон" && day.Revision == 1 && day.SortOrder == 0 && day.ArchivedAt is null,
            "Variant names and descriptions are trimmed; a new variant has revision 1.");
        var night = await variants.CreateAsync(new string('Ж', 120), "");
        Check(night.SortOrder == 1, "A new variant goes to the end.");
        await RejectsCode("variants.name_duplicate", () => variants.CreateAsync("день", ""));
        await RejectsCode("variants.name_duplicate", () => variants.CreateAsync(" ДЕНЬ ", ""));
        await RejectsCode("variants.name_duplicate", () => variants.CreateAsync(new string('ж', 120), ""));
        day = await variants.UpdateAsync(day.Id, "ДЕНЬ", "Светлый фон", day.Revision);
        Check(day.Name == "ДЕНЬ" && day.Revision == 2, "Renaming to the same name in another case is allowed.");
        await RejectsCode("variants.revision_conflict", () => variants.UpdateAsync(day.Id, "Утро", "", 1));
        await RejectsCode("variants.not_found", () => variants.UpdateAsync(Guid.NewGuid(), "Утро", "", 1));
        night = await variants.UpdateAsync(night.Id, "Ночь", "", night.Revision);
        var archivedVariant = await variants.CreateAsync("Сумерки", "");
        archivedVariant = await variants.SetArchivedAsync(archivedVariant.Id, true, archivedVariant.Revision);
        Check(archivedVariant.ArchivedAt is not null && archivedVariant.Revision == 2, "Archiving a variant is a saved change.");
        Check((await variants.SetArchivedAsync(archivedVariant.Id, true, archivedVariant.Revision)).Revision == 2, "Repeated archiving changes nothing.");
        await RejectsCode("variants.name_duplicate", () => variants.CreateAsync("сумерки", ""));
        await RejectsCode("variants.revision_conflict", () => variants.SetArchivedAsync(archivedVariant.Id, false, 1));
        // An archived object is read-only until restored.
        await RejectsCode("variants.archived", () => variants.UpdateAsync(archivedVariant.Id, "Вечер", "Новое описание", archivedVariant.Revision));
        Check((await variants.ListAsync()).Single(item => item.Id == archivedVariant.Id) is { Name: "Сумерки", Revision: 2 },
            "A rejected edit of an archived variant changes nothing.");
        Check((await variants.ListAsync()).Select(item => item.Name).SequenceEqual(["ДЕНЬ", "Ночь", "Сумерки"]), "Variants are listed in order with the archive.");

        // Classes: same name rules, order at the end.
        var cat = await classes.CreateAsync("Кошка", "Домашняя");
        var dog = await classes.CreateAsync("Собака", "");
        await RejectsCode("classes.name_duplicate", () => classes.CreateAsync("КОШКА", ""));
        await RejectsCode("classes.name_invalid", () => classes.CreateAsync(" ", ""));
        await RejectsCode("classes.revision_conflict", () => classes.UpdateAsync(cat.Id, "Кот", "", cat.Revision + 1));
        cat = await classes.UpdateAsync(cat.Id, "Кот", "Домашний", cat.Revision);
        Check(cat.Name == "Кот" && cat.Revision == 2, "A class is renamed with its revision.");
        var fox = await classes.CreateAsync("Лиса", "");
        Check(cat.SortOrder == 0 && dog.SortOrder == 1 && fox.SortOrder == 2, "New classes go to the end of the order.");

        // Bindings: availability, archive rules and duplicates.
        var catDay = await classes.AddVariantAsync(cat.Id, day.Id);
        Check(catDay.Available && catDay.Color is null && catDay.AssetCount == 0 && catDay.ClassId == cat.Id && catDay.VariantId == day.Id,
            "A project variant is bound to a class.");
        await RejectsCode("classes.duplicate_variant_binding", () => classes.AddVariantAsync(cat.Id, day.Id));
        await RejectsCode("classes.project_variant_unavailable", () => classes.AddVariantAsync(cat.Id, archivedVariant.Id));
        await RejectsCode("classes.project_variant_unavailable", () => classes.AddVariantAsync(cat.Id, Guid.NewGuid()));
        await RejectsCode("classes.not_found", () => classes.AddVariantAsync(Guid.NewGuid(), day.Id));
        var catNight = await classes.AddVariantAsync(cat.Id, night.Id);
        var dogDay = await classes.AddVariantAsync(dog.Id, day.Id);
        fox = await classes.SetArchivedAsync(fox.Id, true, fox.Revision);
        await RejectsCode("classes.class_unavailable", () => classes.AddVariantAsync(fox.Id, day.Id));
        await RejectsCode("classes.archived", () => classes.UpdateAsync(fox.Id, "Лис", "", fox.Revision));
        fox = await classes.SetArchivedAsync(fox.Id, false, fox.Revision);
        fox = await classes.UpdateAsync(fox.Id, "Лиса", "Рыжая", fox.Revision);
        Check(fox.Description == "Рыжая", "A restored class is editable again.");
        var foxNight = await classes.AddVariantAsync(fox.Id, night.Id);
        night = await variants.SetArchivedAsync(night.Id, true, night.Revision);
        var unavailable = await classes.GetVariantAsync(catNight.Id);
        Check(!unavailable.Available && unavailable.ArchivedAt is null && unavailable.Revision == catNight.Revision,
            "Archiving a project variant makes its bindings unavailable without changing them.");
        catNight = await classes.SetVariantArchivedAsync(catNight.Id, true, catNight.Revision);
        await RejectsCode("classes.variant_archived", () => classes.SetVariantColorAsync(catNight.Id, "#112233", catNight.Revision));
        await RejectsCode("classes.project_variant_unavailable", () => classes.SetVariantArchivedAsync(catNight.Id, false, catNight.Revision));
        await RejectsCode("classes.duplicate_variant_binding", () => classes.AddVariantAsync(cat.Id, night.Id));
        night = await variants.SetArchivedAsync(night.Id, false, night.Revision);
        cat = await classes.SetArchivedAsync(cat.Id, true, cat.Revision);
        await RejectsCode("classes.class_unavailable", () => classes.SetVariantArchivedAsync(catNight.Id, false, catNight.Revision));
        Check(!(await classes.GetVariantAsync(catDay.Id)).Available, "Archiving a class makes its bindings unavailable.");
        cat = await classes.SetArchivedAsync(cat.Id, false, cat.Revision);
        catNight = await classes.SetVariantArchivedAsync(catNight.Id, false, catNight.Revision);
        Check(catNight.Available && catNight.ArchivedAt is null, "A binding is restored once its class and project variant are active.");

        // Colours: set, normalize, reset, validation; the default comes from FNV-1a of the binding ID as in the old UI.
        await RejectsCode("classes.color_invalid", () => classes.SetVariantColorAsync(catDay.Id, "red", catDay.Revision));
        await RejectsCode("classes.color_invalid", () => classes.SetVariantColorAsync(catDay.Id, "#12345", catDay.Revision));
        catDay = await classes.SetVariantColorAsync(catDay.Id, "#AABBCC", catDay.Revision);
        Check(catDay.Color == "#aabbcc" && catDay.DisplayColor == "#aabbcc" && catDay.Revision == 2, "A colour is saved in lowercase with a revision.");
        await RejectsCode("classes.revision_conflict", () => classes.SetVariantColorAsync(catDay.Id, null, 1));
        catDay = await classes.SetVariantColorAsync(catDay.Id, null, catDay.Revision);
        Check(catDay.Color is null && VariantColors.Palette.Contains(catDay.DisplayColor) && catDay.Revision == 3, "Reset returns the default palette colour.");
        Check(VariantColors.Default(Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301")) == "#f97316"
            && VariantColors.Default(Guid.Parse("a8098c1a-f86e-11da-bd1a-00112444be1e")) == "#a3e635", "Default colours match the old UI hash.");

        // Order: full permutation with the order revision; archived classes keep their place.
        var workspace = await classes.GetWorkspaceAsync();
        Check(workspace.Classes.Select(item => item.Id).SequenceEqual([cat.Id, dog.Id, fox.Id]) && workspace.ClassVariants.Count == 4,
            "The workspace lists classes in order with their bindings.");
        await RejectsCode("classes.order_revision_conflict", () => classes.ReorderAsync([dog.Id, cat.Id, fox.Id], workspace.ClassOrderRevision - 1));
        await RejectsCode("classes.order_invalid", () => classes.ReorderAsync([dog.Id, cat.Id], workspace.ClassOrderRevision));
        await RejectsCode("classes.order_invalid", () => classes.ReorderAsync([dog.Id, cat.Id, cat.Id], workspace.ClassOrderRevision));
        await RejectsCode("classes.order_invalid", () => classes.ReorderAsync([dog.Id, cat.Id, Guid.NewGuid()], workspace.ClassOrderRevision));
        var order = await classes.ReorderAsync([fox.Id, cat.Id, dog.Id], workspace.ClassOrderRevision);
        Check(order.OrderRevision == workspace.ClassOrderRevision + 1 && order.Classes.Select(item => item.Id).SequenceEqual([fox.Id, cat.Id, dog.Id])
            && order.Classes.All(item => item.Revision == (item.Id == fox.Id ? fox.Revision : item.Id == cat.Id ? cat.Revision : dog.Revision)),
            "Reorder applies the permutation with a new order revision and keeps class revisions.");
        await RejectsCode("classes.order_revision_conflict", () => classes.ReorderAsync([cat.Id, dog.Id, fox.Id], workspace.ClassOrderRevision));
        var newcomer = await classes.CreateAsync("Ёж", "");
        await RejectsCode("classes.order_revision_conflict", () => classes.ReorderAsync([fox.Id, cat.Id, dog.Id, newcomer.Id], order.OrderRevision));
        var restartedCore = await createCore(root, null);
        var restartedProject = await restartedCore.Projects.OpenAsync(project.Id);
        var persisted = await restartedProject.Classes.GetWorkspaceAsync();
        Check(persisted.Classes.Select(item => item.Id).SequenceEqual([fox.Id, cat.Id, dog.Id, newcomer.Id]) && persisted.ClassOrderRevision == order.OrderRevision + 1,
            "The class order and its revision survive a restart.");
        Check(persisted.Variants.Select(item => item.Name).SequenceEqual(["ДЕНЬ", "Ночь", "Сумерки"]) && persisted.CatalogRevision > 1,
            "The workspace has the variants and a catalog revision.");

        var eventList = events.ToArray();
        Check(eventList.Length > 20 && eventList.Zip(eventList.Skip(1)).All(pair => pair.Second.CatalogRevision > pair.First.CatalogRevision)
            && eventList.All(item => item.ProjectId == project.Id), "Every catalog change raises catalog.changed with a growing catalog revision.");
        Check(eventList.Any(item => item.ClassVariants.Any(change => change.Id == catDay.Id && change.Revision == catDay.Revision))
            && eventList.Any(item => item.Variants.Any(change => change.Id == night.Id && change.Revision == night.Revision)),
            "Events name the changed objects with their new revisions.");

        // Import: formats, alpha, broken and unsupported files, duplicates, result summary.
        var jobs = core.Jobs;
        var import = ((MautVision.Core.Core)core).ImportClassAssets;
        string Source(string name, byte[] content)
        {
            var path = Path.Combine(sources, name);
            File.WriteAllBytes(path, content);
            return path;
        }
        var transparentPng = Png(120, 80, alpha: true, seed: 1);
        var files = new[]
        {
            Source("прозрачный.png", transparentPng),
            Source("opaque.png", Png(64, 48, alpha: false, seed: 2)),
            Source("photo.jpg", Jpeg(800, 600, seed: 3)),
            Source("alpha.webp", Webp(50, 40, alpha: true, seed: 4)),
            Source("bitmap.bmp", Bmp(33, 17)),
            Source("broken.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5]),
            Source("animation.gif", Gif()),
            Source("empty.png", []),
            Path.Combine(sources, "missing.png"),
            Source("copy.png", transparentPng),
            // Valid headers with missing pixel data: only a full decode finds them.
            Source("truncated.png", Noisy(300, 200, seed: 5, SKEncodedImageFormat.Png)[..^4096]),
            Source("truncated.jpg", Truncated(Noisy(640, 480, seed: 6, SKEncodedImageFormat.Jpeg))),
        };
        var first = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(catDay.Id, files), project.Id, "import-formats");
        var done = await WaitAsync(jobs, project.Id, first.Id, JobStatus.Succeeded);
        var summary = done.Result!.Value.Deserialize<ImportClassAssetsResult>(JsonSerializerOptions.Web)!;
        Check(summary is { Total: 12, Added: 5, Duplicates: 1, Rejected: 6 } && done.Processed == 12 && done.Total == 12,
            $"Import adds PNG, JPEG, WebP and BMP, skips the duplicate and rejects the rest: {done.Result}.");
        Check(summary.RejectedFiles.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => (item.Name, item.Reason)).SequenceEqual(
            [("animation.gif", "classes.asset_invalid"), ("broken.png", "classes.asset_invalid"), ("empty.png", "classes.asset_invalid"), ("missing.png", "classes.asset_unreadable"),
                ("truncated.jpg", "classes.asset_invalid"), ("truncated.png", "classes.asset_invalid")]),
            "Rejected files are listed by name with reasons, without folders; truncated pixel data is rejected.");
        var page = await classes.ListAssetsAsync(catDay.Id);
        var byFormat = page.Items.Where(item => item.Format != ClassAssetFormat.Png || item.HasAlpha).ToDictionary(item => item.Format);
        Check(page.NextCursor is null && page.Items.Count == 5 && page.Items.All(item => item.OperationId == "import-formats" && item.Sha256.Length == 64),
            "The asset page lists imported images.");
        Check(byFormat[ClassAssetFormat.Png] is { Width: 120, Height: 80, HasAlpha: true }
            && page.Items.Count(item => item.Format == ClassAssetFormat.Png && !item.HasAlpha && item.Width == 64) == 1
            && byFormat[ClassAssetFormat.Jpeg] is { Width: 800, Height: 600, HasAlpha: false }
            && byFormat[ClassAssetFormat.Webp] is { Width: 50, Height: 40, HasAlpha: true }
            && byFormat[ClassAssetFormat.Bmp] is { Width: 33, Height: 17, HasAlpha: false }, "Size and alpha come from the file header.");
        var stored = page.Items.First(item => item.HasAlpha && item.Format == ClassAssetFormat.Png);
        Check((await File.ReadAllBytesAsync(Path.Combine(assetsDirectory, stored.Id + ".png"))).AsSpan().SequenceEqual(transparentPng)
            && File.Exists(Path.Combine(assetsDirectory, byFormat[ClassAssetFormat.Jpeg].Id + ".jpg")),
            "Bytes are stored unchanged under files/class-assets/<id>.<ext>.");
        Check(Directory.GetFiles(assetsDirectory).Length == 5, "No temporary files remain after an import.");
        catDay = await classes.GetVariantAsync(catDay.Id);
        Check(catDay is { AssetCount: 5, OpaqueAssetCount: 3, MaxAssetWidth: 800, MaxAssetHeight: 600 }, "Binding counters follow the import.");
        await using (var thumbnail = await project.Resources.OpenAsync(new ResourceReference("class-assets", stored.Id), ResourceVariant.Thumbnail))
        {
            Check(thumbnail.ContentType == "image/jpeg" && thumbnail.Length > 0, "A class asset thumbnail is served through the resource channel.");
        }
        var again = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(catDay.Id, files[..5]), project.Id, "import-again");
        var againSummary = (await WaitAsync(jobs, project.Id, again.Id, JobStatus.Succeeded)).Result!.Value.Deserialize<ImportClassAssetsResult>(JsonSerializerOptions.Web)!;
        Check(againSummary is { Added: 0, Duplicates: 5 } && (await classes.GetVariantAsync(catDay.Id)).AssetCount == 5, "A repeated import of the same files adds nothing.");
        var other = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(dogDay.Id, [files[0]]), project.Id, "import-other-binding");
        Check((await WaitAsync(jobs, project.Id, other.Id, JobStatus.Succeeded)).Result!.Value.GetProperty("added").GetInt32() == 1,
            "The same file in another binding is not a duplicate.");
        night = await variants.SetArchivedAsync(night.Id, true, night.Revision);
        var refused = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(catNight.Id, [files[0]]), project.Id, "import-archived");
        Check((await WaitAsync(jobs, project.Id, refused.Id, JobStatus.Failed)).Error?.Code == "classes.variant_unavailable",
            "Import into an unavailable binding fails with classes.variant_unavailable.");
        night = await variants.SetArchivedAsync(night.Id, false, night.Revision);

        // Paging with the keyset cursor.
        await RejectsCode("classes.not_found", () => classes.ListAssetsAsync(Guid.NewGuid()));
        await Rejects<ArgumentOutOfRangeException>(() => classes.ListAssetsAsync(catDay.Id, limit: 0));
        await Rejects<ArgumentException>(() => classes.ListAssetsAsync(catDay.Id, after: "-1"));
        var firstPage = await classes.ListAssetsAsync(catDay.Id, limit: 2);
        var secondPage = await classes.ListAssetsAsync(catDay.Id, limit: 2, after: firstPage.NextCursor);
        var lastPage = await classes.ListAssetsAsync(catDay.Id, limit: 2, after: secondPage.NextCursor);
        Check(firstPage.Items.Concat(secondPage.Items).Concat(lastPage.Items).Select(item => item.Id).SequenceEqual(page.Items.Select(item => item.Id))
            && lastPage.NextCursor is null && lastPage.Items.Count == 1, "Pages follow each other without gaps or repeats.");

        // Cancel in the middle: the batches already written stay and the summary says so.
        var many = Enumerable.Range(0, 3 * ImportClassAssetsHandler.BatchSize)
            .Select(index => Source($"many-{index:D4}.png", Png(16, 16, alpha: true, seed: 1000 + index))).ToArray();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        import.Pause = async (point, batch) =>
        {
            if (point == "saved" && batch == 0)
            {
                paused.TrySetResult();
                await hold.Task;
            }
        };
        var cancelled = await jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(dogDay.Id, many), project.Id, "import-cancel");
        await paused.Task.WaitAsync(WaitLimit);

        // While the import runs, deletion is blocked and the review says why.
        var blockedReview = await classes.ReviewDeletionAsync(newcomer.Id);
        Check(blockedReview.Blockers is [{ Code: "jobs.active", Count: 1 }] && blockedReview.Blockers[0].Examples.SequenceEqual(["classes.import_assets"]),
            "Running jobs of the project block deletion and are listed in the review.");
        await RejectsCode("classes.deletion_blocked", () => classes.DeleteAsync(newcomer.Id, blockedReview.Revision));
        await jobs.CancelAsync(project.Id, cancelled.Id);
        hold.SetResult();
        var cancelledJob = await WaitAsync(jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        var cancelledSummary = cancelledJob.Result!.Value.Deserialize<ImportClassAssetsResult>(JsonSerializerOptions.Web)!;
        import.Pause = null;
        Check(cancelledSummary.Added == ImportClassAssetsHandler.BatchSize && cancelledSummary.Total == many.Length
            && (await classes.GetVariantAsync(dogDay.Id)).AssetCount == 1 + ImportClassAssetsHandler.BatchSize,
            "A cancelled import keeps the written batch and reports it in the result.");
        Check(Directory.GetFiles(assetsDirectory).Length == await CountAsync(Database(), "SELECT count(*) FROM class_assets;"),
            "Cancel leaves no files without rows.");

        // Restart: the process dies after a batch is committed but before its checkpoint; the job resumes without
        // duplicates and without counting its own rows as duplicates.
        var restartRoot = Path.Combine(fixture, "catalog-restart");
        Directory.CreateDirectory(restartRoot);
        seedInstalledModel(restartRoot);
        var dying = await createCore(restartRoot, null);
        var dyingProject = await dying.Projects.CreateAsync(new ProjectSettings("Перезапуск", "", "yolo26n", new InputSize(640, 640)));
        await dying.Projects.OpenAsync(dyingProject.Id);
        var restartVariant = await dyingProject.Variants.CreateAsync("Основной", "");
        var restartClass = await dyingProject.Classes.CreateAsync("Объект", "");
        var restartBinding = await dyingProject.Classes.AddVariantAsync(restartClass.Id, restartVariant.Id);
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying).ImportClassAssets.Pause = async (point, batch) =>
        {
            if (point == "inserted" && batch == 1)
            {
                died.TrySetResult();
                await new TaskCompletionSource().Task;
            }
        };
        var interrupted = await dying.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(
            new ImportClassAssetsInput(restartBinding.Id, many), dyingProject.Id, "import-restart");
        await died.Task.WaitAsync(WaitLimit);
        // The application is gone: its deferred writes never land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        var revived = await createCore(restartRoot, null);
        var revivedProject = await revived.Projects.OpenAsync(dyingProject.Id);
        var resumed = await WaitAsync(revived.Jobs, dyingProject.Id, interrupted.Id, JobStatus.Succeeded);
        var resumedSummary = resumed.Result!.Value.Deserialize<ImportClassAssetsResult>(JsonSerializerOptions.Web)!;
        var restartDirectory = Path.Combine(restartRoot, "projects", dyingProject.Id, "files", "class-assets");
        Check(resumedSummary is { Added: 3 * ImportClassAssetsHandler.BatchSize, Duplicates: 0, Rejected: 0 }
            && (await revivedProject.Classes.GetVariantAsync(restartBinding.Id)).AssetCount == many.Length
            && Directory.GetFiles(restartDirectory).Length == many.Length,
            $"A resumed import finishes without duplicates or orphan files: {resumed.Result}.");

        // Deletion: review counts, revision check, cascade and files.
        var review = await variants.ReviewDeletionAsync(day.Id);
        Check(review is { TargetKind: CatalogTargetKind.Variant, TargetName: "ДЕНЬ", Classes: 2, ClassVariants: 2, Blockers.Count: 0 }
            && review.Assets == 5 + 1 + ImportClassAssetsHandler.BatchSize && review.Revision.Length == 22, "A variant review counts its classes, bindings and images.");
        Check((await variants.ReviewDeletionAsync(day.Id)).Revision == review.Revision, "The review revision is stable while nothing changes.");
        dogDay = await classes.GetVariantAsync(dogDay.Id);
        await classes.SetVariantColorAsync(dogDay.Id, "#010203", dogDay.Revision);
        await RejectsCode("variants.deletion_revision_conflict", () => variants.DeleteAsync(day.Id, review.Revision));
        await RejectsCode("variants.not_found", () => variants.ReviewDeletionAsync(Guid.NewGuid()));
        var assetReview = await classes.ReviewAssetDeletionAsync(stored.Id);
        Check(assetReview is { TargetKind: CatalogTargetKind.Asset, Classes: 0, ClassVariants: 0, Assets: 1 }, "An image review counts the image.");
        var thumbnails = Path.Combine(root, "projects", project.Id, "cache", "thumbnails", "v1", "class-assets", stored.Id.ToString("N")[..2]);
        Check(Directory.GetFiles(thumbnails, stored.Id + "-*.jpg").Length == 1, "The thumbnail of the image is cached.");
        var deletedAsset = await classes.DeleteAssetAsync(stored.Id, assetReview.Revision);
        catDay = await classes.GetVariantAsync(catDay.Id);
        Check(deletedAsset is { Assets: 1, CleanupPending: false } && !File.Exists(Path.Combine(assetsDirectory, stored.Id + ".png"))
            && catDay is { AssetCount: 4, OpaqueAssetCount: 3 }, "Deleting an image removes its row and file and recounts the binding.");
        Check(Directory.GetFiles(thumbnails, stored.Id + "-*.jpg").Length == 0, "Cleanup evicts the cached thumbnails of a deleted image.");
        await RejectsCode("classes.not_found", () => classes.DeleteAssetAsync(stored.Id, assetReview.Revision));

        // A busy file stays queued; the next deletion retries it.
        var busyAsset = byFormat[ClassAssetFormat.Jpeg];
        var busyPath = Path.Combine(assetsDirectory, busyAsset.Id + ".jpg");
        var nextAsset = byFormat[ClassAssetFormat.Bmp];
        using (new FileStream(busyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var busy = await classes.DeleteAssetAsync(busyAsset.Id, (await classes.ReviewAssetDeletionAsync(busyAsset.Id)).Revision);
            Check(busy.CleanupPending && File.Exists(busyPath) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 1,
                "A busy file is queued for cleanup and reported as pending.");
        }
        Check(!(await classes.ListAssetsAsync(catDay.Id)).Items.Any(item => item.Id == busyAsset.Id), "The row of a busy file is deleted anyway.");
        var retried = await classes.DeleteAssetAsync(nextAsset.Id, (await classes.ReviewAssetDeletionAsync(nextAsset.Id)).Revision);
        Check(!retried.CleanupPending && !File.Exists(busyPath) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 0,
            "The next deletion retries the queued file.");

        // Cleanup at open: a file busy during deletion is removed when the project is opened again.
        var lockedAsset = byFormat[ClassAssetFormat.Webp];
        var lockedPath = Path.Combine(assetsDirectory, lockedAsset.Id + ".webp");
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check((await classes.DeleteAssetAsync(lockedAsset.Id, (await classes.ReviewAssetDeletionAsync(lockedAsset.Id)).Revision)).CleanupPending,
                "A locked file is left for the cleanup queue.");
        }
        var reopenedCore = await createCore(root, null);
        var reopened = (MautVision.Core.Projects.Project)await reopenedCore.Projects.OpenAsync(project.Id);
        await reopened.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        Check(!File.Exists(lockedPath) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 0, "Opening the project retries the cleanup queue.");

        // Cascade: a project variant goes with its bindings in every class and their images.
        review = await variants.ReviewDeletionAsync(day.Id);
        var dayFiles = (await classes.ListAssetsAsync(dogDay.Id, 500)).Items.Select(item => item.Id).ToArray();
        var deleted = await variants.DeleteAsync(day.Id, review.Revision);
        await ((MautVision.Core.Projects.Project)project).FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        workspace = await classes.GetWorkspaceAsync();
        Check(deleted is { Classes: 2, ClassVariants: 2 } && deleted.Assets == review.Assets
            && workspace.Variants.All(item => item.Id != day.Id) && workspace.ClassVariants.All(item => item.VariantId != day.Id)
            && dayFiles.All(id => !File.Exists(Path.Combine(assetsDirectory, id + ".png"))), "Deleting a project variant removes its bindings and images in all classes.");
        await RejectsCode("variants.not_found", () => variants.DeleteAsync(day.Id, review.Revision));
        var bindingReview = await classes.ReviewVariantDeletionAsync(foxNight.Id);
        Check(bindingReview is { TargetKind: CatalogTargetKind.ClassVariant, TargetName: "Ночь", Classes: 0, ClassVariants: 1, Assets: 0 }, "A binding review names its project variant.");
        await classes.DeleteVariantAsync(foxNight.Id, bindingReview.Revision);
        cat = (await classes.GetWorkspaceAsync()).Classes.Single(item => item.Id == cat.Id);
        var classReview = await classes.ReviewDeletionAsync(cat.Id);
        Check(classReview is { Classes: 1, ClassVariants: 1, Assets: 0 }, "A class review counts its bindings.");
        var orderBefore = (await classes.GetWorkspaceAsync()).ClassOrderRevision;
        await classes.DeleteAsync(cat.Id, classReview.Revision);
        workspace = await classes.GetWorkspaceAsync();
        Check(workspace.Classes.All(item => item.Id != cat.Id) && workspace.ClassVariants.All(item => item.ClassId != cat.Id)
            && workspace.ClassOrderRevision == orderBefore + 1 && workspace.Variants.Any(item => item.Id == night.Id),
            "Deleting a class removes its bindings, changes the order revision and keeps project variants.");
        var archivedReview = await variants.ReviewDeletionAsync(archivedVariant.Id);
        await variants.DeleteAsync(archivedVariant.Id, archivedReview.Revision);
        Check((await variants.ListAsync()).All(item => item.Id != archivedVariant.Id), "Archived objects are deleted the same way.");

        // The helpers count through Check; "checks += await" would read checks before they run.
        await LargeDeletionAsync(fixture, createCore, seedInstalledModel, Check);
        await MeasureAsync(fixture, createCore, seedInstalledModel, Check);
        return checks;
    }

    /// <summary>
    /// Deletion of a binding with 20 000 images returns before its files are removed, the background cleanup finishes, and
    /// a cleanup cut short by exit continues when the project is opened by a new composition.
    /// </summary>
    private static async Task LargeDeletionAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        var root = Path.Combine(fixture, "catalog-large-delete");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Удаление", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var classes = (ClassesManager)project.Classes;
        var directory = Path.Combine(root, "projects", project.Id, "files", "class-assets");
        string Database() => Path.Combine(root, "projects", project.Id, ProjectStore.DatabaseFileName);
        var owner = await classes.CreateAsync("Класс", "");

        // Rows and files are written directly: the import itself is measured elsewhere.
        async Task<(ClassVariant Binding, string[] Paths)> FillAsync(string name, int count)
        {
            var variant = await project.Variants.CreateAsync(name, "");
            var binding = await classes.AddVariantAsync(owner.Id, variant.Id);
            Directory.CreateDirectory(directory);
            var assets = Enumerable.Range(0, count).Select(_ => new ImportedAsset(Guid.NewGuid(), ClassAssetFormat.Png, 16, 16, true, 16,
                Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()))).ToArray();
            var paths = assets.Select(asset => Path.Combine(directory, asset.Id + ".png")).ToArray();
            Parallel.ForEach(paths, path => File.WriteAllBytes(path, new byte[16]));
            foreach (var batch in assets.Chunk(5000))
                await classes.InsertImportedAsync(binding.Id, batch, "large-delete", CancellationToken.None);
            return (binding, paths);
        }

        const int Count = 20_000;
        var (large, largePaths) = await FillAsync("Много", Count);
        var review = await classes.ReviewVariantDeletionAsync(large.Id);
        var watch = Stopwatch.StartNew();
        var result = await classes.DeleteVariantAsync(large.Id, review.Revision);
        var deleteMs = watch.Elapsed.TotalMilliseconds;
        check(result.Assets == Count && result.CleanupPending && deleteMs < 3000 && (await classes.GetWorkspaceAsync()).ClassVariants.Count == 0,
            $"Deleting {Count} images commits the rows and returns before the files are removed ({deleteMs:F0} ms).");
        await project.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        var cleanupMs = watch.Elapsed.TotalMilliseconds;
        check(largePaths.All(path => !File.Exists(path)) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 0,
            "The background cleanup removes every file and empties the queue.");

        // Exit right after the commit: the queue survives and the next open of the project finishes the cleanup.
        var (small, smallPaths) = await FillAsync("После выхода", 2000);
        project.FileCleanup.Suspended = true;
        var suspended = await classes.DeleteVariantAsync(small.Id, (await classes.ReviewVariantDeletionAsync(small.Id)).Revision);
        check(suspended.CleanupPending && smallPaths.All(File.Exists) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == smallPaths.Length,
            "Files whose cleanup did not run stay queued and are reported as pending.");
        var restarted = await createCore(root, null);
        var reopened = (Project)await restarted.Projects.OpenAsync(project.Id);
        await reopened.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        check(smallPaths.All(path => !File.Exists(path)) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 0,
            "Opening the project in a new composition finishes the interrupted cleanup.");
        Console.WriteLine($"Catalog deletion: {Count} images, call {deleteMs:F0} ms, files removed in background after {cleanupMs:F0} ms.");
    }

    /// <summary>Imports 2 000 small PNG files and lists 1 000 images page by page; prints the timings.</summary>
    private static async Task MeasureAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        const int Count = 2000;
        var root = Path.Combine(fixture, "catalog-perf");
        var sources = Path.Combine(fixture, "catalog-perf-sources");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sources);
        seedInstalledModel(root);
        var files = new string[Count];
        Parallel.For(0, Count, index =>
        {
            files[index] = Path.Combine(sources, $"object-{index:D5}.png");
            File.WriteAllBytes(files[index], Png(48, 48, alpha: true, seed: 50_000 + index));
        });
        var core = await createCore(root, null);
        var project = await core.Projects.CreateAsync(new ProjectSettings("Замер", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var variant = await project.Variants.CreateAsync("Вариант", "");
        var owner = await project.Classes.CreateAsync("Класс", "");
        var binding = await project.Classes.AddVariantAsync(owner.Id, variant.Id);
        var watch = Stopwatch.StartNew();
        var job = await core.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id, files), project.Id, "perf-import");
        var finished = await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
        var importMs = watch.Elapsed.TotalMilliseconds;
        check(finished.Result!.Value.GetProperty("added").GetInt32() == Count, "All 2 000 files are imported.");

        var timings = new List<double>();
        string? cursor = null;
        var listed = 0;
        var total = Stopwatch.StartNew();
        while (listed < 1000)
        {
            var pageWatch = Stopwatch.StartNew();
            var page = await project.Classes.ListAssetsAsync(binding.Id, 100, cursor);
            timings.Add(pageWatch.Elapsed.TotalMilliseconds);
            listed += page.Items.Count;
            cursor = page.NextCursor;
        }
        var listMs = total.Elapsed.TotalMilliseconds;
        var workspaceWatch = Stopwatch.StartNew();
        await project.Classes.GetWorkspaceAsync();
        var workspaceMs = workspaceWatch.Elapsed.TotalMilliseconds;
        timings.Sort();
        check(listed == 1000 && timings[^1] < 300, $"Asset pages stay under 300 ms (slowest {timings[^1]:F1} ms).");

        // Large photos: 4 JPEG files of 12 MP with noise, about the size of camera images.
        const int Photos = 4;
        var photos = new string[Photos];
        Parallel.For(0, Photos, index =>
        {
            photos[index] = Path.Combine(sources, $"photo-{index}.jpg");
            File.WriteAllBytes(photos[index], NoisyJpeg(4000, 3000, seed: 70_000 + index));
        });
        var photoVariant = await project.Variants.CreateAsync("Фото", "");
        var photoBinding = await project.Classes.AddVariantAsync(owner.Id, photoVariant.Id);
        var photoWatch = Stopwatch.StartNew();
        var photoJob = await core.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(photoBinding.Id, photos), project.Id, "perf-photos");
        var photoDone = await WaitAsync(core.Jobs, project.Id, photoJob.Id, JobStatus.Succeeded);
        var photoMs = photoWatch.Elapsed.TotalMilliseconds;
        check(photoDone.Result!.Value.GetProperty("added").GetInt32() == Photos, "All 12 MP photos are imported.");
        Console.WriteLine($"Catalog import: {Count} PNG files in {importMs:F0} ms ({Count / (importMs / 1000):F0} files/s); "
            + $"{Photos} JPEG 12 MP ({new FileInfo(photos[0]).Length / 1024 / 1024} MB each) in {photoMs:F0} ms; "
            + $"1000 assets in 10 pages: {listMs:F1} ms total, page {timings[0]:F1}–{timings[^1]:F1} ms; workspace {workspaceMs:F1} ms.");
    }

    private static async Task<JobInfo> WaitAsync(IJobsManager jobs, string projectId, Guid jobId, JobStatus status)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var job = await jobs.GetAsync(projectId, jobId);
            if (job.Status == status) return job;
            if (job.Status is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Succeeded or JobStatus.Blocked or JobStatus.Interrupted)
                throw new InvalidOperationException($"Job {job.OperationId} is {job.Status} ({job.Error?.Code}), expected {status}.");
            if (watch.Elapsed > WaitLimit) throw new TimeoutException($"Job {job.OperationId} is {job.Status}, expected {status}.");
            await Task.Delay(20);
        }
    }

    private static async Task<long> CountAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static SKBitmap Draw(int width, int height, bool alpha, int seed)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(alpha ? SKColors.Transparent : SKColors.White);
        using var paint = new SKPaint { Color = new SKColor((byte)(seed * 37), (byte)(seed * 91 >> 3), (byte)(seed >> 8 ^ seed * 13), 255) };
        canvas.DrawRect(1, 1, width - 2, height - 2, paint);
        // A pixel that differs for every seed keeps generated files distinct.
        bitmap.SetPixel(0, 0, new SKColor((byte)seed, (byte)(seed >> 8), (byte)(seed >> 16), 255));
        return bitmap;
    }

    private static byte[] Png(int width, int height, bool alpha, int seed)
    {
        using var bitmap = Draw(width, height, alpha, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Jpeg(int width, int height, int seed)
    {
        using var bitmap = Draw(width, height, alpha: false, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>A gradient with pixel noise: JPEG entropy data close to a camera photo, unlike a flat fill.</summary>
    private static byte[] NoisyJpeg(int width, int height, int seed) => Noisy(width, height, seed, SKEncodedImageFormat.Jpeg);

    /// <summary>The first half of a file: the header is intact, the pixel data ends early.</summary>
    private static byte[] Truncated(byte[] content) => content[..(content.Length / 2)];

    private static byte[] Noisy(int width, int height, int seed, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var random = new Random(seed);
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var noise = random.Next(-24, 24);
                pixels[offset] = (byte)Math.Clamp(x * 255 / width + noise, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(y * 255 / height + noise, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(128 + noise, 0, 255);
                pixels[offset + 3] = 255;
            }
        }
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Webp(int width, int height, bool alpha, int seed)
    {
        using var bitmap = Draw(width, height, alpha, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        return data.ToArray();
    }

    /// <summary>24-bit BMP; SkiaSharp decodes BMP but does not encode it.</summary>
    private static byte[] Bmp(int width, int height)
    {
        var row = (width * 3 + 3) & ~3;
        var bytes = new byte[54 + row * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.TryWriteBytes(bytes.AsSpan(2), bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(10), 54);
        BitConverter.TryWriteBytes(bytes.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), (short)24);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), row * height);
        for (var index = 54; index < bytes.Length; index++) bytes[index] = (byte)(index * 7);
        return bytes;
    }

    /// <summary>A valid 1×1 GIF: a real image of a format the catalog does not accept.</summary>
    private static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];
}
