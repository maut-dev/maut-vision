using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Resources;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Image groups, images and annotations: validation, revisions and conflicts, ordering, filters and pages, the import job
/// (formats, EXIF orientation, corrupt files, duplicates by pixels, folders with recursion and links, cancel, restart,
/// changed sources), archive and move rules, deletion with review, blockers and background cleanup, the catalog deletion
/// check for annotated classes and the archived-reference rule; timings of import, gallery page and annotation page.
/// </summary>
internal static class ImagesSmoke
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Func<string, string, Task> linkDirectory, List<string> junctions)
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
            if (error.Code != code) throw new InvalidOperationException($"Expected {code}, got {error.Code}: {error.Message}");
        }

        var root = Path.Combine(fixture, "images-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var sources = Path.Combine(fixture, "images-sources");
        Directory.CreateDirectory(sources);
        string Source(string name, byte[] content)
        {
            var path = Path.Combine(sources, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Изображения", "", "yolo26n", new InputSize(640, 640)));
        string Database() => Path.Combine(root, "projects", project.Id, ProjectStore.DatabaseFileName);
        var imagesDirectory = Path.Combine(root, "projects", project.Id, "files", "images");
        await RejectsCode("images.project_not_open", () => project.Images.ListGroupsAsync());
        Check(!File.Exists(Database()), "Image calls on an unopened project do not open project.db.");
        await core.Projects.OpenAsync(project.Id);
        var images = project.Images;
        var events = new ConcurrentQueue<ImagesChangedEventArgs>();
        core.Projects.ImagesChanged += (_, args) => events.Enqueue(args);
        var jobs = core.Jobs;
        var handler = ((MautVision.Core.Core)core).ImportImages;

        // Groups: validation, trimming, repeated names, revisions, split hint, archive is read-only.
        foreach (var name in new[] { "", "  ", new string('к', 121) })
            await RejectsCode("images.name_invalid", () => images.CreateGroupAsync(name, "", ImageSplitHint.Unassigned));
        await RejectsCode("images.description_invalid", () => images.CreateGroupAsync("Кадры", new string('x', 4001), ImageSplitHint.Unassigned));
        var group = await images.CreateGroupAsync("  Кадры  ", "  Улица  ", ImageSplitHint.Train);
        Check(group is { Name: "Кадры", Description: "Улица", Origin: ImageGroupOrigin.Manual, SplitHint: ImageSplitHint.Train, Revision: 1, ImageCount: 0,
            ArchivedAt: null, GenerationState: null }, "A manual group is created with trimmed text and revision 1.");
        var twin = await images.CreateGroupAsync("Кадры", new string('x', 4000), ImageSplitHint.Unassigned);
        Check(twin.Name == group.Name, "Group names may repeat.");
        await RejectsCode("images.revision_conflict", () => images.UpdateGroupAsync(group.Id, "Улица", "", ImageSplitHint.Test, 5));
        group = await images.UpdateGroupAsync(group.Id, "Улица", "Днём", ImageSplitHint.Validation, group.Revision);
        Check(group is { Name: "Улица", SplitHint: ImageSplitHint.Validation, Revision: 2 }, "A group is changed with its revision.");
        Check((await images.UpdateGroupAsync(group.Id, "Улица", "Днём", ImageSplitHint.Validation, group.Revision)).Revision == 2, "An unchanged update saves nothing.");
        twin = await images.SetGroupArchivedAsync(twin.Id, true, twin.Revision);
        await RejectsCode("images.group_archived", () => images.UpdateGroupAsync(twin.Id, "Другое", "", ImageSplitHint.Test, twin.Revision));
        Check((await images.ListGroupsAsync()).Select(item => item.Id).SequenceEqual([group.Id, twin.Id]), "Groups are listed by creation time with the archive.");
        await RejectsCode("images.group_not_found", () => images.GetGroupAsync(Guid.NewGuid()));

        // Import of every format, EXIF orientation, broken files and duplicates by pixels across formats.
        var bmp = Bmp(33, 17);
        var rotated = WithOrientation(Jpeg(80, 40, seed: 3), 6);
        var files = new[]
        {
            Source("кадр.png", Png(120, 80, alpha: true, seed: 1)),
            Source("photo.jpg", Jpeg(800, 600, seed: 2)),
            Source("rotated.jpg", rotated),
            Source("alpha.webp", Webp(50, 40, seed: 4)),
            Source("bitmap.bmp", bmp),
            Source("bitmap-as.png", PngOf(bmp)),
            Source("copy.png", Png(120, 80, alpha: true, seed: 1)),
            Source("broken.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]),
            Source("truncated.jpg", Noisy(640, 480, seed: 5, SKEncodedImageFormat.Jpeg)[..20_000]),
            Source("animation.gif", Gif()),
            Source("empty.png", []),
            Path.Combine(sources, "missing.png"),
        };
        var input = new ImportImagesInput(group.Id, files.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray());
        var first = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(input, project.Id, "images-formats");
        var done = await WaitAsync(jobs, project.Id, first.Id, JobStatus.Succeeded);
        var summary = Result(done);
        Check(summary is { Total: 12, Added: 5, Duplicates: 2, Rejected: 5 } && done.Processed == 12, $"Import adds every format, skips pixel duplicates and rejects the rest: {done.Result}.");
        Check(summary.RejectedFiles.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => (item.Name, item.Reason)).SequenceEqual(
            [("animation.gif", "images.file_invalid"), ("broken.png", "images.file_invalid"), ("empty.png", "images.file_invalid"),
                ("missing.png", "images.file_unreadable"), ("truncated.jpg", "images.file_invalid")]), "Rejected files carry their name and reason.");
        var page = await images.ListImagesAsync(group.Id, new ImageFilter(Archived: null));
        Check(page.NextCursor is null && page.Items.Select(item => item.DisplayName).SequenceEqual(["кадр.png", "photo.jpg", "rotated.jpg", "alpha.webp", "bitmap.bmp"]),
            "Images are listed in the order of the selection; the PNG with the BMP's pixels is a duplicate.");
        var byName = page.Items.ToDictionary(item => item.DisplayName);
        Check(byName["rotated.jpg"] is { Width: 40, Height: 80, Format: ImageFileFormat.Jpeg } && byName["photo.jpg"] is { Width: 800, Height: 600 }
            && byName["alpha.webp"].Format == ImageFileFormat.Webp && byName["bitmap.bmp"] is { Format: ImageFileFormat.Bmp, Width: 33, Height: 17 },
            "Width and height follow the EXIF orientation.");
        Check(page.Items.All(item => item is { Revision: 1, AnnotationRevision: 1, ObjectCount: 0, ReviewState: ImageReviewState.Unreviewed, OperationId: "images-formats" }
            && item.Lineage.Kind == ImageLineageKind.File && item.Lineage.SourceFamilyId == "file:" + item.PixelHash && item.PixelHash.Length == 64 && item.Sha256.Length == 64),
            "Imported images have revision 1, an empty unreviewed annotation and a file lineage.");
        Check((await File.ReadAllBytesAsync(Path.Combine(imagesDirectory, byName["rotated.jpg"].Id + ".jpg"))).AsSpan().SequenceEqual(rotated)
            && Directory.GetFiles(imagesDirectory).Length == 5, "Files are stored unchanged under files/images/<id>.<ext> without temporary files.");
        Check(byName["rotated.jpg"].PixelHash == OrientedHash(rotated), "The pixel hash of a rotated JPEG is the hash of the frame as displayed.");
        await using (var thumbnail = await project.Resources.OpenAsync(new ResourceReference(ProjectImage.ResourceKind, byName["rotated.jpg"].Id), ResourceVariant.Thumbnail))
        using (var bitmap = SKBitmap.Decode(thumbnail.Stream))
        {
            Check(bitmap.Width == 40 && bitmap.Height == 80, "The thumbnail of a rotated image is oriented like the stored size.");
        }
        group = await images.GetGroupAsync(group.Id);
        Check(group is { ImageCount: 5, Revision: 2 }, "Import changes the counters of the group but not its revision.");
        var annotation = await images.GetAnnotationAsync(byName["photo.jpg"].Id);
        Check(annotation is { Revision: 1, Objects.Count: 0, Roi: null, ReviewState: ImageReviewState.Unreviewed }, "Import creates annotation revision 1 without objects.");

        // All eight EXIF orientations agree with an independent rendering.
        var oriented = Enumerable.Range(1, 8).Select(value => Source($"orientation-{value}.jpg", WithOrientation(Jpeg(24, 10, seed: 40 + value), value))).ToArray();
        var orientationGroup = await images.CreateGroupAsync("Ориентация", "", ImageSplitHint.Unassigned);
        var orientationJob = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(orientationGroup.Id,
            oriented.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray(), SkipDuplicates: false), project.Id, "images-orientation");
        await WaitAsync(jobs, project.Id, orientationJob.Id, JobStatus.Succeeded);
        var orientationPage = await images.ListImagesAsync(orientationGroup.Id, new ImageFilter());
        Check(orientationPage.Items.Count == 8 && orientationPage.Items.Select((item, index) => item.PixelHash == OrientedHash(File.ReadAllBytes(oriented[index]))
            && (index < 4 ? item is { Width: 24, Height: 10 } : item is { Width: 10, Height: 24 })).All(value => value),
            "Every EXIF orientation is hashed in display order and swaps the size when it transposes.");

        // Duplicates are kept when the flag is off.
        var keep = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id, [new(ImageImportSourceKind.File, files[0])],
            SkipDuplicates: false), project.Id, "images-keep");
        Check(Result(await WaitAsync(jobs, project.Id, keep.Id, JobStatus.Succeeded)) is { Added: 1, Duplicates: 0 }, "With skipDuplicates off the same pixels are added again.");
        var skip = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id, [new(ImageImportSourceKind.File, files[0])]),
            project.Id, "images-skip");
        Check(Result(await WaitAsync(jobs, project.Id, skip.Id, JobStatus.Succeeded)) is { Added: 0, Duplicates: 1 }, "With skipDuplicates on they are skipped.");
        var otherGroup = await images.CreateGroupAsync("Другие", "", ImageSplitHint.Unassigned);
        var elsewhere = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(otherGroup.Id, [new(ImageImportSourceKind.File, files[0])]),
            project.Id, "images-elsewhere");
        Check(Result(await WaitAsync(jobs, project.Id, elsewhere.Id, JobStatus.Succeeded)).Added == 1, "The same pixels in another group are not a duplicate.");

        // Folders: natural order by relative path, optional recursion, links, hidden and other extensions skipped.
        var folder = Path.Combine(sources, "folder");
        Source("folder/frame10.png", Png(10, 10, alpha: false, seed: 101));
        Source("folder/frame2.png", Png(10, 10, alpha: false, seed: 102));
        Source("folder/Frame1.PNG", Png(10, 10, alpha: false, seed: 103));
        Source("folder/notes.txt", [1, 2, 3]);
        Source("folder/sub/deep.png", Png(10, 10, alpha: false, seed: 104));
        var hidden = Source("folder/hidden.png", Png(10, 10, alpha: false, seed: 105));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var outside = Path.Combine(fixture, "images-outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "linked.png"), Png(10, 10, alpha: false, seed: 106));
        var link = Path.Combine(folder, "link");
        await linkDirectory(link, outside);
        junctions.Add(link);
        var folderGroup = await images.CreateGroupAsync("Папка", "", ImageSplitHint.Unassigned);
        var flat = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(folderGroup.Id, [new(ImageImportSourceKind.Folder, folder)]),
            project.Id, "images-folder");
        Check(Result(await WaitAsync(jobs, project.Id, flat.Id, JobStatus.Succeeded)) is { Total: 3, Added: 3 }
            && (await images.ListImagesAsync(folderGroup.Id, new ImageFilter())).Items.Select(item => item.DisplayName).SequenceEqual(["Frame1.PNG", "frame2.png", "frame10.png"]),
            "A folder without recursion imports its own images in natural order.");
        var deep = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(folderGroup.Id, [new(ImageImportSourceKind.Folder, folder, Recursive: true)]),
            project.Id, "images-folder-recursive");
        Check(Result(await WaitAsync(jobs, project.Id, deep.Id, JobStatus.Succeeded)) is { Total: 4, Added: 1, Duplicates: 3 }
            && (await images.ListImagesAsync(folderGroup.Id, new ImageFilter())).Items[^1].DisplayName == "sub/deep.png",
            "Recursion adds nested folders with relative names and never follows links or reads hidden files.");
        var missingFolder = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(folderGroup.Id,
            [new(ImageImportSourceKind.Folder, Path.Combine(sources, "nothing"))]), project.Id, "images-folder-missing");
        Check((await WaitAsync(jobs, project.Id, missingFolder.Id, JobStatus.Failed)).Error?.Code == "images.folder_unavailable", "A missing folder fails the job.");
        var archivedTarget = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(twin.Id, [new(ImageImportSourceKind.File, files[1])]),
            project.Id, "images-archived");
        Check((await WaitAsync(jobs, project.Id, archivedTarget.Id, JobStatus.Failed)).Error?.Code == "images.group_archived", "An archived group refuses imports.");

        // A generated group (created directly in storage): only manual groups accept imports; producing groups are read-only.
        var generatedId = Guid.NewGuid();
        var generatedImage = Guid.NewGuid();
        await project.Store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO image_groups (id, name, description, origin, split_hint, generation_run_id, generation_state, created_at, updated_at, revision)
                VALUES ($id, 'Генерация', '', 'generated', 'unassigned', $run, 'producing', 0, 0, 1);
                """;
            command.Parameters.AddWithValue("$id", generatedId.ToString("D"));
            command.Parameters.AddWithValue("$run", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync(token);
            await ImagesManager.InsertImagesAsync(connection, transaction, generatedId,
                [new ImportedImage(generatedImage, "sample-0.png", ImageFileFormat.Png, 640, 640, 100, new string('a', 64), new string('b', 64),
                    new ImageLineage(ImageLineageKind.Generated, "generated:x", GenerationRunId: Guid.NewGuid(), SampleIndex: 0, RecipeRevision: 3))],
                await ImagesManager.ReserveOrdinalsAsync(connection, transaction, generatedId, 1, token), "generation", token);
        });
        var generated = await images.GetGroupAsync(generatedId);
        Check(generated is { Origin: ImageGroupOrigin.Generated, GenerationState: ImageGenerationState.Producing }
            && (await images.GetImageAsync(generatedImage)).Lineage is { Kind: ImageLineageKind.Generated, SampleIndex: 0, RecipeRevision: 3 },
            "Generated groups and lineage are stored.");
        var intoGenerated = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(generatedId, [new(ImageImportSourceKind.File, files[1])]),
            project.Id, "images-generated");
        Check((await WaitAsync(jobs, project.Id, intoGenerated.Id, JobStatus.Failed)).Error?.Code == "images.group_not_manual", "Generated groups refuse imports.");
        await RejectsCode("images.group_busy", () => images.SaveAnnotationAsync(generatedImage, new AnnotationInput([], null, ImageReviewState.Reviewed), 1));
        await project.Store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE image_groups SET generation_state = 'completed' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", generatedId.ToString("D"));
            await command.ExecuteNonQueryAsync(token);
        });
        Check((await images.SaveAnnotationAsync(generatedImage, new AnnotationInput([], null, ImageReviewState.Reviewed), 1)).Annotation.Revision == 2,
            "A completed generated group is editable.");
        generated = await images.GetGroupAsync(generatedId);

        // Pages: keyset cursor, filters, limits.
        await RejectsCode("images.group_not_found", () => images.ListImagesAsync(Guid.NewGuid(), new ImageFilter()));
        await Rejects<ArgumentOutOfRangeException>(() => images.ListImagesAsync(group.Id, new ImageFilter(), limit: 0));
        await Rejects<ArgumentOutOfRangeException>(() => images.ListImagesAsync(group.Id, new ImageFilter(), limit: 201));
        var all = (await images.ListImagesAsync(group.Id, new ImageFilter())).Items;
        var pageOne = await images.ListImagesAsync(group.Id, new ImageFilter(), limit: 2);
        var pageTwo = await images.ListImagesAsync(group.Id, new ImageFilter(), limit: 2, after: pageOne.NextCursor);
        var pageThree = await images.ListImagesAsync(group.Id, new ImageFilter(), limit: 2, after: pageTwo.NextCursor);
        Check(pageOne.Items.Concat(pageTwo.Items).Concat(pageThree.Items).Select(item => item.Id).SequenceEqual(all.Select(item => item.Id))
            && all.Count == 6 && pageThree.NextCursor is null, "Pages follow each other without gaps or repeats.");
        await Rejects<ArgumentException>(() => images.ListImagesAsync(group.Id, new ImageFilter(ReviewState: ImageReviewState.Reviewed), after: pageOne.NextCursor));
        await Rejects<ArgumentException>(() => images.ListImagesAsync(group.Id, new ImageFilter(), after: "not a cursor"));

        // Catalog for annotations: an active binding, an archived class with its binding, a binding of another class.
        var day = await project.Variants.CreateAsync("День", "");
        var night = await project.Variants.CreateAsync("Ночь", "");
        var car = await project.Classes.CreateAsync("Машина", "");
        var person = await project.Classes.CreateAsync("Человек", "");
        var old = await project.Classes.CreateAsync("Старый", "");
        var carDay = await project.Classes.AddVariantAsync(car.Id, day.Id);
        var carNight = await project.Classes.AddVariantAsync(car.Id, night.Id);
        var personDay = await project.Classes.AddVariantAsync(person.Id, day.Id);
        var oldDay = await project.Classes.AddVariantAsync(old.Id, day.Id);

        // Annotations: new IDs, boxes, ROI, review state, revisions, history.
        var photo = byName["photo.jpg"];
        var box = new ImageBox(0.1, 0.2, 0.3, 0.4);
        var saved = await images.SaveAnnotationAsync(photo.Id, new AnnotationInput(
            [new(null, car.Id, carDay.Id, box), new(null, person.Id, null, new ImageBox(0.5, 0.5, 0.5, 0.5)), new(null, old.Id, oldDay.Id, box)],
            new ImageBox(0, 0, 1, 1), ImageReviewState.Reviewed), photo.AnnotationRevision);
        Check(saved.Annotation is { Revision: 2, Objects.Count: 3, ReviewState: ImageReviewState.Reviewed } && saved.Annotation.Roi == new ImageBox(0, 0, 1, 1)
            && saved.Annotation.Objects.All(item => item.Id != Guid.Empty) && saved.Annotation.Objects.Select(item => item.Id).Distinct().Count() == 3
            && saved.Image is { AnnotationRevision: 2, ObjectCount: 3, ReviewState: ImageReviewState.Reviewed, Revision: 2 },
            "Saving assigns object IDs and creates revision 2; the image gets the new revisions and counters.");
        await RejectsCode("images.revision_conflict", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([], null, ImageReviewState.Reviewed), 1));
        var objects = saved.Annotation.Objects;
        AnnotationObjectInput Keep(AnnotationObject item) => new(item.Id, item.ClassId, item.ClassVariantId, item.Box);
        foreach (var bad in new[]
        {
            new ImageBox(-0.1, 0, 0.5, 0.5), new ImageBox(0, 0, 0, 0.5), new ImageBox(0.6, 0, 0.5, 0.5), new ImageBox(0, 0.5, 0.5, 0.5 + 2e-9),
            new ImageBox(double.NaN, 0, 0.1, 0.1),
        })
        {
            await RejectsCode("images.box_invalid", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([new(null, car.Id, null, bad)], null,
                ImageReviewState.Unreviewed), 2));
        }
        await RejectsCode("images.box_invalid", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([], new ImageBox(0.5, 0.5, 0.6, 0.1),
            ImageReviewState.Unreviewed), 2));
        await RejectsCode("images.object_id_invalid", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([new(Guid.NewGuid(), car.Id, null, box)], null,
            ImageReviewState.Unreviewed), 2));
        await RejectsCode("images.object_id_invalid", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([Keep(objects[0]), Keep(objects[0])], null,
            ImageReviewState.Unreviewed), 2));
        await RejectsCode("images.reference_unavailable", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([new(null, car.Id, personDay.Id, box)], null,
            ImageReviewState.Unreviewed), 2));
        await RejectsCode("images.reference_unavailable", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput([new(null, Guid.NewGuid(), null, box)], null,
            ImageReviewState.Unreviewed), 2));
        await images.SaveAnnotationAsync(photo.Id, new AnnotationInput([.. objects.Select(Keep), new(null, car.Id, null, new ImageBox(0.5, 0.5, 0.5, 0.5 + 1e-10))], null,
            ImageReviewState.Unreviewed), 2);
        Check((await images.GetAnnotationAsync(photo.Id)).Objects[3].Box.Height > 0.5, "A box up to 1 + 1e-9 is accepted as is, not clipped.");
        var history = await images.GetAnnotationAsync(photo.Id, 2);
        Check(history.Objects.Select(item => item.Id).SequenceEqual(objects.Select(item => item.Id)), "Earlier annotation revisions stay readable unchanged.");
        await RejectsCode("images.annotation_not_found", () => images.GetAnnotationAsync(photo.Id, 99));
        saved = await images.SaveAnnotationAsync(photo.Id, new AnnotationInput(objects.Select(Keep).ToArray(), null, ImageReviewState.Reviewed), 3);

        // Archived references: unchanged objects keep them, new and changed objects may not.
        old = await project.Classes.SetArchivedAsync(old.Id, true, old.Revision);
        carNight = await project.Classes.SetVariantArchivedAsync(carNight.Id, true, carNight.Revision);
        var moved = objects.Select((item, index) => index == 2 ? Keep(item) with { Box = new ImageBox(0.2, 0.2, 0.1, 0.1) } : Keep(item)).ToArray();
        saved = await images.SaveAnnotationAsync(photo.Id, new AnnotationInput(moved, null, ImageReviewState.Reviewed), saved.Annotation.Revision);
        Check(saved.Annotation.Objects[2].ClassId == old.Id && saved.Annotation.Objects[2].Box.X == 0.2,
            "An object of an archived class keeps its reference while only its box changes.");
        await RejectsCode("images.reference_unavailable", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput(
            [.. moved, new(null, old.Id, null, box)], null, ImageReviewState.Reviewed), saved.Annotation.Revision));
        await RejectsCode("images.reference_unavailable", () => images.SaveAnnotationAsync(photo.Id, new AnnotationInput(
            moved.Select((item, index) => index == 0 ? item with { ClassVariantId = carNight.Id } : item).ToArray(), null, ImageReviewState.Reviewed),
            saved.Annotation.Revision));

        // The current annotations block deletion of the classes and variants they reference; old revisions do not.
        var classReview = await project.Classes.ReviewDeletionAsync(car.Id);
        Check(classReview.Blockers.Any(item => item is { Code: "images.annotations", Count: 1 } && item.Examples.SequenceEqual(["Улица / photo.jpg"])),
            "An annotated class lists the images that reference it.");
        await RejectsCode("classes.deletion_blocked", () => project.Classes.DeleteAsync(car.Id, classReview.Revision));
        Check((await project.Classes.ReviewVariantDeletionAsync(carDay.Id)).Blockers.Any(item => item.Code == "images.annotations")
            && (await project.Variants.ReviewDeletionAsync(day.Id)).Blockers.Any(item => item.Code == "images.annotations")
            && (await project.Classes.ReviewVariantDeletionAsync(carNight.Id)).Blockers.Count == 0,
            "Class variants and project variants referenced by annotations are blocked; others are not.");
        saved = await images.SaveAnnotationAsync(photo.Id, new AnnotationInput([Keep(saved.Annotation.Objects[1])], null, ImageReviewState.Reviewed), saved.Annotation.Revision);
        Check((await project.Classes.ReviewDeletionAsync(car.Id)).Blockers.Count == 0 && (await project.Classes.ReviewDeletionAsync(person.Id)).Blockers.Count == 1,
            "Once the current annotation drops the class, it no longer blocks.");

        // Page annotations for the gallery and the review filter.
        var pageIds = all.Select(item => item.Id).Reverse().ToArray();
        var pageAnnotations = await images.ListAnnotationsAsync([.. pageIds, Guid.NewGuid()]);
        Check(pageAnnotations.Select(item => item.ImageId).SequenceEqual(pageIds) && pageAnnotations.Single(item => item.ImageId == photo.Id).Objects.Count == 1,
            "Page annotations come in the requested order with current objects; unknown IDs are skipped.");
        await RejectsCode("images.selection_invalid", () => images.ListAnnotationsAsync(Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray()));
        Check((await images.ListImagesAsync(group.Id, new ImageFilter(ReviewState: ImageReviewState.Reviewed))).Items.Select(item => item.Id).SequenceEqual([photo.Id])
            && (await images.ListImagesAsync(group.Id, new ImageFilter(ReviewState: ImageReviewState.Unreviewed))).Items.Count == 5, "The review filter splits the group.");

        // Archive of images: revisions, counters, read-only archived images.
        var webp = await images.GetImageAsync(byName["alpha.webp"].Id);
        await RejectsCode("images.revision_conflict", () => images.SetImagesArchivedAsync([new(webp.Id, webp.Revision + 1)], true));
        await RejectsCode("images.selection_invalid", () => images.SetImagesArchivedAsync([new(webp.Id, webp.Revision), new(webp.Id, webp.Revision)], true));
        var archivedImages = await images.SetImagesArchivedAsync([new(webp.Id, webp.Revision)], true);
        Check(archivedImages[0] is { ArchivedAt: not null } && archivedImages[0].Revision == webp.Revision + 1
            && (await images.GetGroupAsync(group.Id)) is { ImageCount: 6, ArchivedImageCount: 1 }, "Archiving images changes their revision and the group counters.");
        Check((await images.ListImagesAsync(group.Id, new ImageFilter())).Items.All(item => item.Id != webp.Id)
            && (await images.ListImagesAsync(group.Id, new ImageFilter(Archived: true))).Items.Single().Id == webp.Id, "The archive filter separates archived images.");
        await RejectsCode("images.image_archived", () => images.SaveAnnotationAsync(webp.Id, new AnnotationInput([], null, ImageReviewState.Reviewed), 1));
        webp = (await images.SetImagesArchivedAsync([new(webp.Id, archivedImages[0].Revision)], false))[0];
        Check(webp.ArchivedAt is null && (await images.GetGroupAsync(group.Id)).ArchivedImageCount == 0, "Restoring an image updates the counters.");
        group = await images.GetGroupAsync(group.Id);
        var first2 = (await images.ListImagesAsync(group.Id, new ImageFilter())).Items[0];
        await RejectsCode("images.group_archived", () => images.MoveImagesAsync(group.Id, group.Revision, twin.Id, twin.Revision, [new(first2.Id, first2.Revision)]));
        orientationGroup = await images.SetGroupArchivedAsync(orientationGroup.Id, true, orientationGroup.Revision);
        var orientedImage = orientationPage.Items[0];
        await RejectsCode("images.group_archived", () => images.SetImagesArchivedAsync([new(orientedImage.Id, orientedImage.Revision)], true));
        await RejectsCode("images.group_archived", () => images.SaveAnnotationAsync(orientedImage.Id, new AnnotationInput([], null, ImageReviewState.Reviewed), 1));
        orientationGroup = await images.SetGroupArchivedAsync(orientationGroup.Id, false, orientationGroup.Revision);

        // Move: same origin, both group revisions, all or nothing, the end of the target order.
        group = await images.GetGroupAsync(group.Id);
        otherGroup = await images.GetGroupAsync(otherGroup.Id);
        var candidates = (await images.ListImagesAsync(group.Id, new ImageFilter())).Items.Take(2).ToArray();
        await RejectsCode("images.revision_conflict", () => images.MoveImagesAsync(group.Id, group.Revision + 1, otherGroup.Id, otherGroup.Revision,
            candidates.Select(item => new ImageRevision(item.Id, item.Revision)).ToArray()));
        await RejectsCode("images.origin_mismatch", () => images.MoveImagesAsync(group.Id, group.Revision, generatedId, generated.Revision,
            candidates.Select(item => new ImageRevision(item.Id, item.Revision)).ToArray()));
        await RejectsCode("images.selection_invalid", () => images.MoveImagesAsync(group.Id, group.Revision, group.Id, group.Revision,
            candidates.Select(item => new ImageRevision(item.Id, item.Revision)).ToArray()));
        var stranger = (await images.ListImagesAsync(folderGroup.Id, new ImageFilter())).Items[0];
        await RejectsCode("images.selection_invalid", () => images.MoveImagesAsync(group.Id, group.Revision, otherGroup.Id, otherGroup.Revision,
            [new(candidates[0].Id, candidates[0].Revision), new(stranger.Id, stranger.Revision)]));
        Check((await images.GetImageAsync(candidates[0].Id)).GroupId == group.Id, "A refused move changes nothing.");
        var result = await images.MoveImagesAsync(group.Id, group.Revision, otherGroup.Id, otherGroup.Revision,
            candidates.Reverse().Select(item => new ImageRevision(item.Id, item.Revision)).ToArray());
        var targetPage = (await images.ListImagesAsync(otherGroup.Id, new ImageFilter())).Items;
        Check(result.Source is { ImageCount: 4 } && result.Source.Revision == group.Revision + 1 && result.Target is { ImageCount: 3 }
            && result.Target.Revision == otherGroup.Revision + 1 && result.Images.All(item => item.GroupId == otherGroup.Id)
            && targetPage.Select(item => item.Id).Skip(1).SequenceEqual(candidates.Select(item => item.Id)),
            "Moved images keep their source order at the end of the target, both groups get new revisions and counters.");
        group = result.Source;
        otherGroup = result.Target;

        // Deletion of selected images: review, revision check, files and thumbnails in the background.
        var victim = targetPage[^1];
        var victimThumbnail = await project.Resources.OpenAsync(new ResourceReference(ProjectImage.ResourceKind, victim.Id), ResourceVariant.Thumbnail);
        await victimThumbnail.DisposeAsync();
        var review = await images.ReviewImagesDeletionAsync(otherGroup.Id, [victim.Id]);
        Check(review is { WholeGroup: false, Images: 1, Blockers.Count: 0, GroupName: "Другие" } && review.Revision.Length == 22, "A selection review counts the images.");
        await RejectsCode("images.image_not_found", () => images.ReviewImagesDeletionAsync(group.Id, [victim.Id]));
        await images.SaveAnnotationAsync(victim.Id, new AnnotationInput([], null, ImageReviewState.Reviewed), victim.AnnotationRevision);
        await RejectsCode("images.deletion_revision_conflict", () => images.DeleteImagesAsync(otherGroup.Id, [victim.Id], review.Revision));
        review = await images.ReviewImagesDeletionAsync(otherGroup.Id, [victim.Id]);
        var removed = await images.DeleteImagesAsync(otherGroup.Id, [victim.Id], review.Revision);
        await project.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        var thumbnails = Path.Combine(root, "projects", project.Id, "cache", "thumbnails", "v1", "images", victim.Id.ToString("N")[..2]);
        Check(removed is { Images: 1, GroupDeleted: false } && !File.Exists(Path.Combine(imagesDirectory, victim.Id + ".jpg"))
            && (!Directory.Exists(thumbnails) || Directory.GetFiles(thumbnails, victim.Id + "-*").Length == 0)
            && (await images.GetGroupAsync(otherGroup.Id)).ImageCount == 2, "Deleting images removes rows, files and thumbnails and updates the counters.");
        await RejectsCode("images.image_not_found", () => images.GetImageAsync(victim.Id));
        Check(await CountAsync(Database(), $"SELECT count(*) FROM image_annotations WHERE image_id = '{victim.Id}';") == 0, "Annotations go with their image.");

        // Import into a group blocks its deletion and archive; other groups are not blocked. Cancel keeps the written batch.
        var many = Enumerable.Range(0, 3 * ImportImagesHandler.BatchSize).Select(index => Source($"many/{index:D4}.png", Png(16, 16, alpha: false, seed: 1000 + index))).ToArray();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, batch) =>
        {
            if (point == "saved" && batch == 0)
            {
                paused.TrySetResult();
                await hold.Task;
            }
        };
        var busyGroup = await images.CreateGroupAsync("Импорт", "", ImageSplitHint.Unassigned);
        var cancelled = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(busyGroup.Id,
            [new(ImageImportSourceKind.Folder, Path.Combine(sources, "many"))]), project.Id, "images-cancel");
        await paused.Task.WaitAsync(WaitLimit);
        var blocked = await images.ReviewGroupDeletionAsync(busyGroup.Id);
        Check(blocked.Blockers is [{ Code: "images.import_active", Count: 1 }] && blocked.WholeGroup, "An import into the group blocks its deletion.");
        await RejectsCode("images.deletion_blocked", () => images.DeleteGroupAsync(busyGroup.Id, blocked.Revision));
        await RejectsCode("images.group_importing", () => images.SetGroupArchivedAsync(busyGroup.Id, true, busyGroup.Revision));
        Check((await images.ReviewGroupDeletionAsync(orientationGroup.Id)).Blockers.Count == 0, "Imports of other groups do not block.");
        var imports = await images.ListImportsAsync(busyGroup.Id);
        Check(imports.Count == 1 && imports[0].Id == cancelled.Id && (await images.ListImportsAsync(group.Id)).Count == 3,
            "The import history lists only the jobs of the group.");
        await jobs.CancelAsync(project.Id, cancelled.Id);
        hold.SetResult();
        var cancelledJob = await WaitAsync(jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        handler.Pause = null;
        Check(Result(cancelledJob) is { Added: ImportImagesHandler.BatchSize, Total: 3 * ImportImagesHandler.BatchSize }
            && (await images.GetGroupAsync(busyGroup.Id)).ImageCount == ImportImagesHandler.BatchSize, "A cancelled import keeps the written batch and reports it.");
        Check(Directory.GetFiles(imagesDirectory).Length == await CountAsync(Database(), "SELECT count(*) FROM images WHERE group_id != '" + generatedId + "';"),
            "Cancel leaves no files without rows.");

        // Whole group deletion: rows, files, history of imports.
        busyGroup = await images.GetGroupAsync(busyGroup.Id);
        var groupReview = await images.ReviewGroupDeletionAsync(busyGroup.Id);
        var groupFiles = (await images.ListImagesAsync(busyGroup.Id, new ImageFilter(), 200)).Items.Select(item => Path.Combine(imagesDirectory, item.Id + ".png")).ToArray();
        var deletedGroup = await images.DeleteGroupAsync(busyGroup.Id, groupReview.Revision);
        await project.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        Check(deletedGroup is { GroupDeleted: true, Images: ImportImagesHandler.BatchSize } && groupFiles.All(path => !File.Exists(path))
            && await CountAsync(Database(), $"SELECT count(*) FROM jobs WHERE id = '{cancelled.Id}';") == 0
            && await CountAsync(Database(), $"SELECT count(*) FROM images WHERE group_id = '{busyGroup.Id}';") == 0,
            "Deleting a group removes its images, files and import history.");
        await RejectsCode("images.group_not_found", () => images.ListImportsAsync(busyGroup.Id));

        // Restart: the process dies after a batch is committed but before its checkpoint; the job resumes without duplicates.
        var restartRoot = Path.Combine(fixture, "images-restart");
        Directory.CreateDirectory(restartRoot);
        seedInstalledModel(restartRoot);
        var dying = await createCore(restartRoot, null);
        var dyingProject = await dying.Projects.CreateAsync(new ProjectSettings("Перезапуск", "", "yolo26n", new InputSize(640, 640)));
        await dying.Projects.OpenAsync(dyingProject.Id);
        var restartGroup = await dyingProject.Images.CreateGroupAsync("Кадры", "", ImageSplitHint.Unassigned);
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying).ImportImages.Pause = async (point, batch) =>
        {
            if (point == "inserted" && batch == 1)
            {
                died.TrySetResult();
                await new TaskCompletionSource().Task;
            }
        };
        var manyFolder = new ImageImportSource(ImageImportSourceKind.Folder, Path.Combine(sources, "many"));
        var interrupted = await dying.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(restartGroup.Id, [manyFolder]),
            dyingProject.Id, "images-restart");
        await died.Task.WaitAsync(WaitLimit);
        // The application is gone: its deferred writes never land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        var revived = await createCore(restartRoot, null);
        var revivedProject = await revived.Projects.OpenAsync(dyingProject.Id);
        var resumed = await WaitAsync(revived.Jobs, dyingProject.Id, interrupted.Id, JobStatus.Succeeded);
        var restartDirectory = Path.Combine(restartRoot, "projects", dyingProject.Id, "files", "images");
        var restartPage = await revivedProject.Images.ListImagesAsync(restartGroup.Id, new ImageFilter(), 200);
        Check(Result(resumed) is { Added: 3 * ImportImagesHandler.BatchSize, Duplicates: 0 } && restartPage.Items.Count == many.Length
            && Directory.GetFiles(restartDirectory).Length == many.Length
            && restartPage.Items.Select(item => item.DisplayName).SequenceEqual(many.Select(Path.GetFileName)), "A resumed import finishes in order without duplicates or orphans.");

        // A folder that changed before the job resumed fails the job instead of importing a different list.
        var changing = Path.Combine(sources, "changing");
        for (var index = 0; index < 2 * ImportImagesHandler.BatchSize; index++) Source($"changing/{index:D3}.png", Png(8, 8, alpha: false, seed: 5000 + index));
        var dying2 = await createCore(restartRoot, null);
        var dyingProject2 = await dying2.Projects.OpenAsync(dyingProject.Id);
        var died2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying2).ImportImages.Pause = async (point, batch) =>
        {
            if (point == "saved" && batch == 0)
            {
                died2.TrySetResult();
                await new TaskCompletionSource().Task;
            }
        };
        var changed = await dying2.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(restartGroup.Id,
            [new(ImageImportSourceKind.Folder, changing)]), dyingProject.Id, "images-changed");
        await died2.Task.WaitAsync(WaitLimit);
        await ((MautVision.Core.Core)dying2).EndForTestingAsync();
        Source("changing/extra.png", Png(8, 8, alpha: false, seed: 9999));
        var revived2 = await createCore(restartRoot, null);
        await revived2.Projects.OpenAsync(dyingProject.Id);
        var failed = await WaitAsync(revived2.Jobs, dyingProject.Id, changed.Id, JobStatus.Failed);
        Check(failed.Error?.Code == "images.import_sources_changed" && Result(failed).Added == ImportImagesHandler.BatchSize,
            "A changed folder fails the resumed import and keeps what was added.");

        // Events: what changed, without lists; imports are throttled.
        var eventList = events.ToArray();
        Check(eventList.All(item => item.ProjectId == project.Id)
            && eventList.Any(item => item.Groups.Any(change => change.Id == group.Id && change.Revision is not null))
            && eventList.Any(item => item.Groups.Any(change => change.Id == busyGroup.Id && change.Revision is null))
            && eventList.Any(item => item.Images.Any(change => change.Id == victim.Id && change.Revision is null))
            && eventList.Any(item => item.Images.Any(change => change.Id == photo.Id) && item.ImagesChangedIn.Contains(group.Id))
            && eventList.Any(item => item.ImagesChangedIn.Contains(folderGroup.Id)), "images.changed names groups, images and groups whose images changed.");

        await MeasureAsync(fixture, createCore, seedInstalledModel, Check);
        return checks;
    }

    /// <summary>
    /// Imports 1080p JPEG frames and measures the rate against the 500 frames per minute target; fills a group with
    /// 20 000 rows and times the first gallery page and an annotation page.
    /// </summary>
    private static async Task MeasureAsync(string fixture, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        const int Frames = 120;
        var root = Path.Combine(fixture, "images-perf");
        var sources = Path.Combine(fixture, "images-perf-sources");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sources);
        seedInstalledModel(root);
        var frames = new string[Frames];
        Parallel.For(0, Frames, index =>
        {
            frames[index] = Path.Combine(sources, $"frame-{index:D4}.jpg");
            File.WriteAllBytes(frames[index], Noisy(1920, 1080, 80_000 + index, SKEncodedImageFormat.Jpeg));
        });
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Замер", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var group = await project.Images.CreateGroupAsync("Кадры", "", ImageSplitHint.Unassigned);
        var watch = Stopwatch.StartNew();
        var job = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            [new(ImageImportSourceKind.Folder, sources)]), project.Id, "perf-frames");
        var finished = await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded);
        var importMs = watch.Elapsed.TotalMilliseconds;
        var perMinute = Frames / (importMs / 60_000);
        check(Result(finished).Added == Frames && perMinute >= 500, $"1080p JPEG import reaches 500 frames per minute ({perMinute:F0}).");

        // 20 000 rows written through the real insert, then the gallery page and the annotation page.
        const int Rows = 20_000;
        var large = await project.Images.CreateGroupAsync("Много", "", ImageSplitHint.Unassigned);
        await project.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var first = await ImagesManager.ReserveOrdinalsAsync(connection, transaction, large.Id, Rows, token);
            var rows = Enumerable.Range(0, Rows).Select(index => new ImportedImage(Guid.NewGuid(), $"frame-{index}.jpg", ImageFileFormat.Jpeg, 1920, 1080, 300_000,
                new string('a', 64), index.ToString("x64"), new ImageLineage(ImageLineageKind.File, "file:" + index))).ToArray();
            await ImagesManager.InsertImagesAsync(connection, transaction, large.Id, rows, first, "perf-rows", token);
        });
        SqliteConnection.ClearAllPools();
        var pageWatch = Stopwatch.StartNew();
        var page = await project.Images.ListImagesAsync(large.Id, new ImageFilter(), 100);
        var pageMs = pageWatch.Elapsed.TotalMilliseconds;
        pageWatch.Restart();
        var deep = await project.Images.ListImagesAsync(large.Id, new ImageFilter(ReviewState: ImageReviewState.Unreviewed), 100, null);
        var filteredMs = pageWatch.Elapsed.TotalMilliseconds;
        pageWatch.Restart();
        var annotations = await project.Images.ListAnnotationsAsync(page.Items.Select(item => item.Id).ToArray());
        var annotationsMs = pageWatch.Elapsed.TotalMilliseconds;
        check(page.Items.Count == 100 && deep.Items.Count == 100 && annotations.Count == 100 && pageMs < 300 && annotationsMs < 50,
            $"Gallery page ({pageMs:F1} ms) and annotation page ({annotationsMs:F1} ms) of a 20 000 image group stay within targets.");
        Console.WriteLine($"Images import: {Frames} JPEG 1920x1080 in {importMs:F0} ms ({perMinute:F0} frames/min, {Frames / (importMs / 1000):F1}/s); "
            + $"20 000-image group: first page {pageMs:F1} ms, filtered page {filteredMs:F1} ms, annotation page {annotationsMs:F1} ms.");
    }

    private static ImportImagesResult Result(JobInfo job) => job.Result!.Value.Deserialize<ImportImagesResult>(JsonSerializerOptions.Web)!;

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

    private static async Task<long> CountAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The pixel hash of a JPEG computed independently: decode, draw with the thumbnail orientation matrix, hash.</summary>
    internal static string OrientedHash(byte[] jpeg)
    {
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, codec.Info.ColorSpace);
        using var stored = new SKBitmap(info);
        if (codec.GetPixels(info, stored.GetPixels()) != SKCodecResult.Success) throw new InvalidOperationException("Fixture decode failed.");
        var transposed = ImagePixels.IsTransposed(codec.EncodedOrigin);
        using var oriented = new SKBitmap(info.WithSize(transposed ? info.Height : info.Width, transposed ? info.Width : info.Height));
        using (var canvas = new SKCanvas(oriented))
        {
            canvas.SetMatrix(ThumbnailGenerator.Orientation(codec.EncodedOrigin, info.Width, info.Height));
            using var image = SKImage.FromBitmap(stored);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        return ImagePixels.Hash(oriented);
    }

    /// <summary>Inserts an EXIF APP1 segment with the given Orientation right after the JPEG SOI marker.</summary>
    internal static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        var exif = new byte[2 + 2 + 6 + 26];
        exif[0] = 0xFF;
        exif[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(2), (ushort)(exif.Length - 2));
        "Exif\0\0"u8.CopyTo(exif.AsSpan(4));
        var tiff = exif.AsSpan(10);
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[10..], 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[12..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[14..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[18..], (ushort)orientation);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[22..], 0);
        return [.. jpeg.AsSpan(0, 2), .. exif, .. jpeg.AsSpan(2)];
    }

    private static SKBitmap Draw(int width, int height, bool alpha, int seed)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(alpha ? SKColors.Transparent : SKColors.White);
        using var paint = new SKPaint { Color = new SKColor((byte)(seed * 37), (byte)(seed * 91 >> 3), (byte)(seed >> 8 ^ seed * 13), 255) };
        canvas.DrawRect(1, 1, width / 2f, height - 2, paint);
        bitmap.SetPixel(0, 0, new SKColor((byte)seed, (byte)(seed >> 8), (byte)(seed >> 16), 255));
        return bitmap;
    }

    internal static byte[] Png(int width, int height, bool alpha, int seed)
    {
        using var bitmap = Draw(width, height, alpha, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static byte[] Jpeg(int width, int height, int seed)
    {
        using var bitmap = Draw(width, height, alpha: false, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    internal static byte[] Webp(int width, int height, int seed)
    {
        using var bitmap = Draw(width, height, alpha: true, seed);
        using var data = bitmap.Encode(SKEncodedImageFormat.Webp, 90);
        return data.ToArray();
    }

    /// <summary>A PNG with exactly the pixels of another file (decoded as opaque RGBA).</summary>
    private static byte[] PngOf(byte[] image)
    {
        using var codec = SKCodec.Create(new MemoryStream(image));
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidOperationException("Fixture decode failed.");
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

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

    /// <summary>24-bit BMP; SkiaSharp decodes BMP but does not encode it.</summary>
    internal static byte[] Bmp(int width, int height)
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

    private static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B,
    ];
}
