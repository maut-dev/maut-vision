using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SkiaSharp;

/// <summary>
/// Background sets: the blur radius port, the signature kernels (vector against scalar), sets and their validation,
/// the three ways to add (ready frames, crops of images, one region of a video), crop bounds and wrong sizes, blur
/// determinism, similar frames against the run and the set, exact repeats, the requested count, remove and restore by item
/// revision during a running import, the size mismatch flag, cancel and restart, previews and samples equal to the run,
/// deletion review, blockers and two-phase cleanup; timings of the hot paths and the run throughput.
/// </summary>
internal static class BackgroundsSmoke
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(120);

    internal static async Task<int> RunAsync(string fixture, string ffmpegDirectory, Func<string, ILoggerFactory?, Task<ICore>> createCore,
        Action<string> seedInstalledModel)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task RejectsCode(string code, Func<Task> action)
        {
            try { await action(); }
            catch (CoreException error) when (error.Code == code) { checks++; return; }
            catch (CoreException error) { throw new InvalidOperationException($"Expected {code}, got {error.Code}: {error.Message}"); }
            throw new InvalidOperationException($"Expected CoreException {code}.");
        }

        CheckKernels(Check);

        var root = Path.Combine(fixture, "backgrounds-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var sources = Path.Combine(fixture, "backgrounds-sources");
        Directory.CreateDirectory(sources);
        string Source(string name, byte[] content)
        {
            var path = Path.Combine(sources, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Фоны", "", "yolo26n", new InputSize(320, 256)));
        string Database() => Path.Combine(root, "projects", project.Id, ProjectStore.DatabaseFileName);
        var directory = Path.Combine(root, "projects", project.Id, "files", "backgrounds");
        await RejectsCode("backgrounds.project_not_open", () => project.Backgrounds.ListSetsAsync());
        await core.Projects.OpenAsync(project.Id);
        var backgrounds = project.Backgrounds;
        var events = new ConcurrentQueue<BackgroundsChangedEventArgs>();
        core.Projects.BackgroundsChanged += (_, args) => events.Enqueue(args);
        var jobs = core.Jobs;
        var handler = ((MautVision.Core.Core)core).ImportBackgrounds;
        async Task<JobInfo> RunAsync(ImportBackgroundsInput input, string operation, JobStatus status = JobStatus.Succeeded)
        {
            var job = await jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(input, project.Id, operation);
            return await WaitAsync(jobs, project.Id, job.Id, status);
        }
        async Task<IReadOnlyList<BackgroundItem>> Items(Guid setId, bool? removed = false)
            => (await backgrounds.ListItemsAsync(setId, new BackgroundFilter(removed), 200)).Items;

        // Sets: validation, trimming, revisions, archive is read-only.
        foreach (var name in new[] { "", "  ", new string('ф', 121) })
            await RejectsCode("backgrounds.name_invalid", () => backgrounds.CreateSetAsync(name, ""));
        await RejectsCode("backgrounds.description_invalid", () => backgrounds.CreateSetAsync("Набор", new string('x', 4001)));
        var set = await backgrounds.CreateSetAsync("  Улица  ", "  Днём  ");
        Check(set is { Name: "Улица", Description: "Днём", Revision: 1, ContentRevision: 1, ItemCount: 0, RemovedCount: 0, ArchivedAt: null, MatchesProjectInput: true }
            && set.TargetSize == new InputSize(320, 256), "A set copies the project input size and starts at revision 1.");
        Check(events.Any(change => change.Sets.Any(item => item.Id == set.Id && item.Revision == 1)), "Creation raises BackgroundsChanged with the set.");
        await RejectsCode("backgrounds.revision_conflict", () => backgrounds.UpdateSetAsync(set.Id, "Двор", "", 7));
        set = await backgrounds.UpdateSetAsync(set.Id, "Двор", "", set.Revision);
        Check(set is { Name: "Двор", Description: "", Revision: 2 }, "A set is renamed with its revision.");
        Check((await backgrounds.UpdateSetAsync(set.Id, "Двор", "", set.Revision)).Revision == 2, "An unchanged update saves nothing.");
        var archived = await backgrounds.SetSetArchivedAsync(set.Id, true, set.Revision);
        await RejectsCode("backgrounds.set_archived", () => backgrounds.UpdateSetAsync(set.Id, "Ещё", "", archived.Revision));
        var archivedRun = await RunAsync(new ImportBackgroundsInput(set.Id, [new BackgroundFileSource(Path.Combine(sources, "none.png"))]), "bg-archived",
            JobStatus.Failed);
        Check(archivedRun.Error?.Code == "backgrounds.set_archived", "A run into an archived set fails with backgrounds.set_archived.");
        set = await backgrounds.SetSetArchivedAsync(set.Id, false, archived.Revision);
        Check(set is { ArchivedAt: null, Revision: 4 } && (await backgrounds.ListSetsAsync()).Single().Id == set.Id, "Archive and restore change the revision.");
        await RejectsCode("backgrounds.set_not_found", () => backgrounds.GetSetAsync(Guid.NewGuid()));
        await RejectsCode("backgrounds.item_not_found", () => backgrounds.GetItemAsync(Guid.NewGuid()));

        // Input validation, shared by the job, the samples and Desktop.
        await RejectsCode("backgrounds.sources_empty", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(set.Id, []), "bg-check"));
        foreach (var blur in new[] { new BackgroundBlur(-1, 2), new BackgroundBlur(3, 2), new BackgroundBlur(0, 1001), new BackgroundBlur(double.NaN, 2) })
            await RejectsCode("backgrounds.blur_invalid", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(set.Id, [new("C:\\a.png")], Blur: blur), "bg-check"));
        await RejectsCode("backgrounds.crop_invalid", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(set.Id, [new("C:\\a.png", new BackgroundCrop(-1, 0))]), "bg-check"));
        await RejectsCode("backgrounds.count_invalid", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(set.Id, [new("C:\\a.png")], RequestedCount: 2), "bg-check"));
        await RejectsCode("backgrounds.count_invalid", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(set.Id, [new("C:\\a.png")], RequestedCount: 0), "bg-check"));

        // Ready frames: headers before the run, then the run with every kind of skipped file.
        var distinct = Enumerable.Range(0, 4).Select(index => Source($"ready/кадр-{index}.png", Png(320, 256, seed: index))).ToArray();
        var copy = Source("ready/копия.png", File.ReadAllBytes(distinct[0]));
        var near = Source("ready/почти.png", Png(320, 256, seed: 1, jitter: 1));
        var small = Source("ready/малый.png", Png(300, 200, seed: 9));
        var large = Source("ready/большой.jpg", Jpeg(800, 600, seed: 10));
        var broken = Source("ready/битый.png", RandomNumberGenerator.GetBytes(2000));
        var inspected = await backgrounds.InspectFilesAsync(set.Id, [distinct[0], small, large, broken]);
        Check(inspected.Select(item => item.Fit).SequenceEqual([BackgroundSourceFit.Exact, BackgroundSourceFit.Smaller, BackgroundSourceFit.Larger, BackgroundSourceFit.Unreadable])
            && inspected[2] is { Name: "большой.jpg", Width: 800, Height: 600 } && inspected[3] is { Width: null, Height: null },
            "File headers give sizes and fit before the run.");
        var ready = new ImportBackgroundsInput(set.Id, [.. distinct.Select(path => new BackgroundFileSource(path)), new(copy), new(near), new(small), new(broken)]);
        var readyRun = await RunAsync(ready, "bg-ready");
        var readyResult = Result(readyRun);
        var readyItems = await Items(set.Id);
        Check(readyResult is { Total: 8, Processed: 8, Added: 5, Duplicates: 1, Similar: 0, Rejected: 2, Blur: null } && readyItems.Count == 5
            && readyResult.Issues.Any(issue => issue is { Reason: "backgrounds.duplicate", Name: "копия.png", SourceIndex: 4 })
            && readyResult.Issues.Any(issue => issue is { Reason: "backgrounds.wrong_size", Name: "малый.png", Width: 300, Height: 200 })
            && readyResult.Issues.Any(issue => issue is { Reason: "backgrounds.image_unreadable", Name: "битый.png" }),
            "Ready frames: exact size is taken, a copy is a duplicate, other sizes and broken files are skipped with reasons.");
        Check(readyItems.All(item => item is { Width: 320, Height: 256, Revision: 1, RemovedAt: null } && item.Origin is { Kind: BackgroundOriginKind.Uploaded, Crop: null, BlurRadius: null }
            && item.Origin.OperationId == "bg-ready") && readyItems[0].Origin.SourceName == "кадр-0.png"
            && readyItems[0].Origin.SourceHash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(distinct[0]))),
            "Ready backgrounds keep their order, origin and source hash.");
        Check(readyItems.All(item =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, item.Id + ".png"));
            using var memory = new MemoryStream(bytes);
            return Directory.GetFiles(directory).Length == 5 && ImagePixels.Decode(memory) is { Format: ImageFileFormat.Png, Width: 320, Height: 256 } decoded
                && decoded.PixelHash == item.PixelHash && bytes.Length == item.ByteSize && Convert.ToHexStringLower(SHA256.HashData(bytes)) == item.Sha256;
        }), "The only file of a background is files/backgrounds/<id>.png of the set size, matching its hashes.");
        Check(readyItems[0].PixelHash == PixelHash(distinct[0]), "A ready frame is stored with its own pixels.");
        set = await backgrounds.GetSetAsync(set.Id);
        Check(set is { ItemCount: 5, Revision: 4 } && set.ContentRevision > 1, "A run changes the counters and the content revision, not the set revision.");
        Check(events.Any(change => change.ItemsChangedIn.Contains(set.Id)), "A run raises ItemsChangedIn for its set.");
        var runs = await backgrounds.ListRunsAsync(set.Id);
        Check(runs.Count == 2 && runs[0].Id == readyRun.Id && runs.All(run => run.Kind == "backgrounds.import"), "The run history lists the runs of the set, newest first.");

        // Exact repeats and similar frames against the set; similar frames against the run.
        var again = Result(await RunAsync(new ImportBackgroundsInput(set.Id, [new(distinct[2])]), "bg-again"));
        Check(again is { Added: 0, Duplicates: 1 }, "Pixels already in the set are an exact repeat.");
        var nearAgain = Source("ready/почти-2.png", Png(320, 256, seed: 3, jitter: 2));
        var similarSet = Result(await RunAsync(new ImportBackgroundsInput(set.Id, [new(nearAgain)], SkipSimilar: true), "bg-similar-set"));
        Check(similarSet is { Added: 0, Similar: 1 } && similarSet.Issues.Single().Reason == "backgrounds.similar", "A frame similar to a background of the set is skipped.");
        var fresh = Source("ready/новый.png", Png(320, 256, seed: 20));
        var freshNear = Source("ready/новый-2.png", Png(320, 256, seed: 20, jitter: 2));
        var similarRun = Result(await RunAsync(new ImportBackgroundsInput(set.Id, [new(fresh), new(freshNear)], SkipSimilar: true), "bg-similar-run"));
        Check(similarRun is { Added: 1, Similar: 1 } && similarRun.Issues.Single().SourceIndex == 1, "A frame similar to one taken earlier by the run is skipped.");
        var unusable = await RunAsync(new ImportBackgroundsInput(set.Id, [new(small), new(broken)]), "bg-unusable", JobStatus.Failed);
        Check(unusable.Error?.Code == "backgrounds.no_usable_media" && Result(unusable) is { Rejected: 2, Added: 0 }, "A run where every source is rejected fails with backgrounds.no_usable_media.");

        // Crops of images: each file its own crop, exact-size files whole, smaller ones skipped, crop bounds.
        var cropSet = await backgrounds.CreateSetAsync("Нарезка", "");
        var photo = Source("crop/фото.jpg", Jpeg(800, 600, seed: 30));
        var cropInput = new ImportBackgroundsInput(cropSet.Id, [
            new(photo, new BackgroundCrop(0, 0)), new(photo, new BackgroundCrop(480, 344)), new(photo, new BackgroundCrop(481, 0)),
            new(distinct[3]), new(small)]);
        var cropResult = Result(await RunAsync(cropInput, "bg-crop"));
        var cropItems = await Items(cropSet.Id);
        Check(cropResult is { Added: 3, Rejected: 2 } && cropResult.Issues.Any(issue => issue is { Reason: "backgrounds.crop_out_of_bounds", SourceIndex: 2, Width: 800, Height: 600 })
            && cropResult.Issues.Any(issue => issue is { Reason: "backgrounds.wrong_size", SourceIndex: 4 }), "A crop past the frame and a smaller file are skipped with sizes.");
        Check(cropItems[0].Origin is { Kind: BackgroundOriginKind.ImageCrop, Crop: { Left: 0, Top: 0, Width: 320, Height: 256 } }
            && cropItems[1].Origin.Crop is { Left: 480, Top: 344 } && cropItems[2].Origin is { Kind: BackgroundOriginKind.Uploaded, Crop: null },
            "Crops record their area; an exact-size file is taken whole.");
        Check(cropItems[1].PixelHash == CutHash(photo, 480, 344, 320, 256), "A crop stores exactly the pixels of its region of the decoded file.");

        // Blur: deterministic radii from the seed (the port of the previous formula), same pixels for the same seed.
        var blurA = await backgrounds.CreateSetAsync("Размытие А", "");
        var blurB = await backgrounds.CreateSetAsync("Размытие Б", "");
        var blurSources = distinct.Select(path => new BackgroundFileSource(path)).ToArray();
        var seeded = new BackgroundBlur(0, 4, 12345);
        var firstBlur = Result(await RunAsync(new ImportBackgroundsInput(blurA.Id, blurSources, Blur: seeded), "bg-blur-a"));
        var secondBlur = Result(await RunAsync(new ImportBackgroundsInput(blurB.Id, blurSources, Blur: seeded), "bg-blur-b"));
        var blurredA = await Items(blurA.Id);
        var blurredB = await Items(blurB.Id);
        Check(firstBlur.Blur == seeded && secondBlur.Blur == seeded && blurredA.Select(item => item.Origin.BlurRadius).SequenceEqual([0.98, 1.7, 1.04, 0.7])
            && blurredB.Select(item => item.Origin.BlurRadius).SequenceEqual([0.98, 1.7, 1.04, 0.7]), "The same seed gives the reference radii.");
        Check(blurredA.Select(item => item.PixelHash).SequenceEqual(blurredB.Select(item => item.PixelHash))
            && blurredA[0].PixelHash != readyItems[0].PixelHash, "The same seed gives the same pixels; blurred pixels differ from the sharp frame.");
        var defaultSeed = Result(await RunAsync(new ImportBackgroundsInput(blurA.Id, [new(fresh)], Blur: new BackgroundBlur(0.5, 32)), "bg-blur-default"));
        Check(defaultSeed.Blur?.Seed == BlurPlan.DefaultSeed(project.Id, "bg-blur-default"), "Without a seed the run uses the seed of the project and operation.");
        var sharpSet = await backgrounds.CreateSetAsync("Почти резко", "");
        await RunAsync(new ImportBackgroundsInput(sharpSet.Id, [new(distinct[0])], Blur: new BackgroundBlur(0, 0.2, 1)), "bg-blur-sharp");
        var sharp = (await Items(sharpSet.Id)).Single();
        Check(sharp.Origin.BlurRadius == 0 && sharp.PixelHash == readyItems[0].PixelHash, "A radius below 0.3 px leaves the frame sharp.");
        var noBlur = Result(await RunAsync(new ImportBackgroundsInput(sharpSet.Id, [new(distinct[1])], Blur: new BackgroundBlur(0, 0)), "bg-blur-none"));
        Check(noBlur.Blur is null && (await Items(sharpSet.Id))[1].Origin.BlurRadius is null, "A maximum radius of 0 means no blur.");

        // Previews: a scaled source frame, samples equal to the run of the same operation.
        var poster = Source("crop/плакат.jpg", Jpeg(3000, 2000, seed: 40));
        var posterPreview = await backgrounds.PreviewFileAsync(poster);
        Check(posterPreview is { Width: 3000, Height: 2000, Preview: { Width: 1600, Height: 1067, ContentType: "image/jpeg" } }
            && posterPreview.Preview.Content.Length > 1000, "A large source is previewed at 1600 px with its original size.");
        await RejectsCode("backgrounds.image_unreadable", () => backgrounds.PreviewFileAsync(broken));
        var sampleSet = await backgrounds.CreateSetAsync("Примеры", "");
        var sampleInput = new ImportBackgroundsInput(sampleSet.Id, [.. distinct.Select(path => new BackgroundFileSource(path)), new(small), new(photo, new BackgroundCrop(10, 20))],
            Blur: new BackgroundBlur(1, 3));
        var preview = await backgrounds.PreviewImportAsync(sampleInput, "bg-samples");
        Check(preview.Samples.Count == 6 && preview.Blur?.Seed == BlurPlan.DefaultSeed(project.Id, "bg-samples") && preview.TargetSize == new InputSize(320, 256)
            && preview.Samples[4] is { Reason: "backgrounds.wrong_size", ActualWidth: 300, Image: null } && preview.Samples[5].Crop is { Left: 10, Top: 20 },
            "Samples cover the sources with their reasons and crops.");
        await RunAsync(sampleInput, "bg-samples");
        var sampled = await Items(sampleSet.Id);
        Check(preview.Samples.Where(sample => sample.Image is not null).Zip(sampled).All(pair =>
        {
            using var memory = new MemoryStream(pair.First.Image!.Content);
            return ImagePixels.Decode(memory)?.PixelHash == pair.Second.PixelHash && pair.First.BlurRadius == pair.Second.Origin.BlurRadius;
        }), "Samples have the pixels and radii the run of the same operation stores.");
        var duplicateSamples = await backgrounds.PreviewImportAsync(new ImportBackgroundsInput(sampleSet.Id, [new(distinct[0]), new(distinct[1])],
            Blur: new BackgroundBlur(1, 3)), "bg-samples");
        Check(duplicateSamples.Samples.All(sample => sample is { Reason: "backgrounds.duplicate", Image: null }), "Samples report exact repeats of the set.");

        // Video: one region for the whole video, the requested count, similar frames of a still video.
        var ffmpeg = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
        var clip = Path.Combine(sources, "улица.mp4");
        var still = Path.Combine(sources, "пауза.mp4");
        var tiny = Path.Combine(sources, "маленькое.mp4");
        await Task.WhenAll(
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=10", "-c:v", "libopenh264", "-g", "25", clip),
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "color=c=gray:size=640x360:rate=25:duration=4", "-c:v", "libopenh264", still),
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=256x192:rate=25:duration=2", "-c:v", "libopenh264", tiny));
        Check(BackgroundVideoSource.AvailableFrames(10_000, 0, 1000) == 10 && BackgroundVideoSource.AvailableFrames(10_000, 500, 3000) == 4
            && BackgroundVideoSource.AvailableFrames(10_000, 10_000, 1000) == 0 && BackgroundVideoSource.AvailableFrames(10_000, 9_999, 1) == 1,
            "Available frames follow ⌊(duration − 1 − start) / step⌋ + 1.");
        var frame = await backgrounds.PreviewVideoFrameAsync(clip, 5000);
        Check(frame is { Width: 640, Height: 360, Preview: { Width: 640, Height: 360, ContentType: "image/jpeg" } }, "A video frame is previewed for the region.");
        await RejectsCode("video.range_invalid", () => backgrounds.PreviewVideoFrameAsync(clip, 10_000));
        var videoSet = await backgrounds.CreateSetAsync("Видео", "");
        var region = new BackgroundCrop(100, 50);
        VideoFrameRange All(long durationMs, long intervalMs) => new(0, intervalMs, BackgroundVideoSource.AvailableFrames(durationMs, 0, intervalMs));
        await RejectsCode("backgrounds.video_too_small", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(videoSet.Id, [],
            new BackgroundVideoSource(tiny, All(2000, 500), region)), "bg-video-check"));
        await RejectsCode("backgrounds.region_invalid", () => backgrounds.PreviewImportAsync(new ImportBackgroundsInput(videoSet.Id, [],
            new BackgroundVideoSource(clip, All(10_000, 1000), new BackgroundCrop(400, 0))), "bg-video-check"));
        var tooSmall = await RunAsync(new ImportBackgroundsInput(videoSet.Id, [], new BackgroundVideoSource(tiny, All(2000, 500), region)), "bg-video-small", JobStatus.Failed);
        Check(tooSmall.Error?.Code == "backgrounds.video_too_small", "A run on a video smaller than the set fails.");
        var videoInput = new ImportBackgroundsInput(videoSet.Id, [], new BackgroundVideoSource(clip, All(10_000, 1000), region), RequestedCount: 4,
            Blur: new BackgroundBlur(0, 2, 99));
        var videoSamples = await backgrounds.PreviewImportAsync(videoInput, "bg-video");
        var videoRun = Result(await RunAsync(videoInput, "bg-video"));
        var videoItems = await Items(videoSet.Id);
        var videoHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(clip)));
        Check(videoRun is { Total: 10, Processed: 4, Added: 4, RequestedCount: 4 } && videoItems.Count == 4, "The run stops when the requested count is added.");
        Check(videoItems.Select((item, index) => item.Origin is { Kind: BackgroundOriginKind.VideoFrame, VideoName: "улица.mp4", SourceName: "улица.mp4" }
            && item.Origin.TimestampMs == index * 1000L && item.Origin.FrameIndex == index && item.Origin.SourceHash == videoHash
            && item.Origin.Crop == new BackgroundCropArea(100, 50, 320, 256)).All(same => same), "Video backgrounds record the frame, the region and the video hash.");
        Check(videoSamples.Samples.Count == 4 && videoSamples.Samples.Select(sample => sample.TimestampMs).SequenceEqual([0L, 1000L, 2000L, 3000L])
            && videoSamples.Samples.Zip(videoItems).All(pair =>
            {
                using var memory = new MemoryStream(pair.First.Image!.Content);
                return ImagePixels.Decode(memory)?.PixelHash == pair.Second.PixelHash;
            }), "Video samples are read by seeking and equal the frames the run stores.");
        var stillSet = await backgrounds.CreateSetAsync("Пауза", "");
        var stillSamples = await backgrounds.PreviewImportAsync(new ImportBackgroundsInput(stillSet.Id, [], new BackgroundVideoSource(still, All(4000, 1000), region),
            RequestedCount: 3, SkipSimilar: true), "bg-still");
        Check(stillSamples.Samples.Count == 3 && stillSamples.Samples[0].Image is not null && stillSamples.Samples.Skip(1).All(sample => sample.Reason == "backgrounds.similar"),
            "Samples of a still video honour skipping similar frames.");
        var stillRun = Result(await RunAsync(new ImportBackgroundsInput(stillSet.Id, [], new BackgroundVideoSource(still, All(4000, 1000), region), RequestedCount: 3,
            SkipSimilar: true), "bg-still"));
        Check(stillRun is { Total: 4, Processed: 4, Added: 1, Similar: 3 }, "A still video gives one background when similar frames are skipped.");
        var stillExact = await backgrounds.CreateSetAsync("Пауза без пропуска", "");
        var exactRun = Result(await RunAsync(new ImportBackgroundsInput(stillExact.Id, [], new BackgroundVideoSource(still, All(4000, 1000), region)), "bg-still-exact"));
        Check(exactRun is { Added: 1, Duplicates: 3, Similar: 0 }, "Without skipping similar frames identical frames are exact repeats.");
        var (_, digest) = BackgroundImportPlan.Expand(videoInput);
        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(1));
        Check(BackgroundImportPlan.Expand(videoInput).Digest != digest, "A replaced video changes the run digest.");

        // Resource channel: the original PNG and its thumbnail.
        await using (var original = await project.Resources.OpenAsync(new ResourceReference(BackgroundItem.ResourceKind, readyItems[0].Id), ResourceVariant.Original))
        await using (var thumbnail = await project.Resources.OpenAsync(new ResourceReference(BackgroundItem.ResourceKind, readyItems[0].Id), ResourceVariant.Thumbnail))
        {
            Check(original.ContentType == "image/png" && original.Length == readyItems[0].ByteSize && thumbnail.ContentType == "image/jpeg",
                "Backgrounds are a resource kind with originals and thumbnails.");
        }

        // A long run: remove and restore by item revision while it runs, blockers, history; then cancel keeps the written batch.
        var many = Enumerable.Range(0, 3 * ImportBackgroundsHandler.BatchSize).Select(index => new BackgroundFileSource(Source($"many/{index:D3}.png", Png(320, 256, seed: 1000 + index)))).ToArray();
        var busy = await backgrounds.CreateSetAsync("Большой", "");
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
        var running = await jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(busy.Id, many), project.Id, "bg-long");
        await paused.Task.WaitAsync(WaitLimit);
        var firstBatch = await Items(busy.Id);
        var removed = await backgrounds.SetItemsRemovedAsync([new(firstBatch[0].Id, 1), new(firstBatch[1].Id, 1)], removed: true);
        Check(removed.All(item => item is { Revision: 2, RemovedAt: not null }), "Backgrounds are removed by their own revisions while a run fills the set.");
        await RejectsCode("backgrounds.revision_conflict", () => backgrounds.SetItemsRemovedAsync([new(firstBatch[0].Id, 1)], removed: false));
        var restored = await backgrounds.SetItemsRemovedAsync([new(firstBatch[1].Id, 2)], removed: false);
        Check(restored.Single() is { Revision: 3, RemovedAt: null }, "A removed background is restored with its revision.");
        await RejectsCode("backgrounds.selection_invalid", () => backgrounds.SetItemsRemovedAsync([], removed: true));
        var blockedReview = await backgrounds.ReviewSetDeletionAsync(busy.Id);
        Check(blockedReview.Blockers is [{ Code: "backgrounds.import_active", Count: 1 }] && blockedReview.WholeSet, "A run into the set blocks its deletion.");
        await RejectsCode("backgrounds.deletion_blocked", () => backgrounds.DeleteSetAsync(busy.Id, blockedReview.Revision));
        await RejectsCode("backgrounds.set_importing", () => backgrounds.SetSetArchivedAsync(busy.Id, true, busy.Revision));
        Check((await backgrounds.ReviewSetDeletionAsync(cropSet.Id)).Blockers.Count == 0, "Runs into other sets do not block.");
        hold.SetResult();
        var longRun = Result(await WaitAsync(jobs, project.Id, running.Id, JobStatus.Succeeded));
        handler.Pause = null;
        busy = await backgrounds.GetSetAsync(busy.Id);
        Check(longRun.Added == many.Length && busy is { ItemCount: 95, RemovedCount: 1 } && (await Items(busy.Id, removed: true)).Single().Id == firstBatch[0].Id,
            "The run finishes without conflicts and the counters keep the removal.");
        var firstPage = await backgrounds.ListItemsAsync(busy.Id, new BackgroundFilter(null), 50);
        var secondPage = await backgrounds.ListItemsAsync(busy.Id, new BackgroundFilter(null), 50, firstPage.NextCursor);
        Check(firstPage.Items.Count == 50 && secondPage.Items.Count == 46 && secondPage.NextCursor is null
            && firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).Distinct().Count() == 96, "Pages over all backgrounds follow the order of addition.");
        await Rejects<ArgumentException>(() => backgrounds.ListItemsAsync(busy.Id, new BackgroundFilter(), 50, firstPage.NextCursor));
        var active = await backgrounds.ListActiveItemsAsync(busy.Id, 60);
        var activeRest = await backgrounds.ListActiveItemsAsync(busy.Id, 60, active.NextCursor);
        Check(active.Items.Count == 60 && activeRest.Items.Count == 35 && activeRest.NextCursor is null && active.ContentRevision == busy.ContentRevision
            && active.TargetSize == busy.TargetSize && active.Items.All(item => item.RemovedAt is null), "Generation reads active backgrounds with the content revision.");

        var cancelHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, batch) =>
        {
            if (point == "saved" && batch == 0)
            {
                cancelPaused.TrySetResult();
                await cancelHold.Task;
            }
        };
        var cancelSet = await backgrounds.CreateSetAsync("Отмена", "");
        var cancelled = await jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(cancelSet.Id, many), project.Id, "bg-cancel");
        await cancelPaused.Task.WaitAsync(WaitLimit);
        await jobs.CancelAsync(project.Id, cancelled.Id);
        cancelHold.SetResult();
        var cancelledJob = await WaitAsync(jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        handler.Pause = null;
        Check(Result(cancelledJob) is { Added: ImportBackgroundsHandler.BatchSize, Processed: ImportBackgroundsHandler.BatchSize }
            && (await backgrounds.GetSetAsync(cancelSet.Id)).ItemCount == ImportBackgroundsHandler.BatchSize, "A cancelled run keeps the written batch and reports it.");
        Check(Directory.GetFiles(directory, "*.import").Length == 0
            && Directory.GetFiles(directory).Length == await CountAsync(Database(), "SELECT count(*) FROM background_items;"), "Cancel leaves no files without rows.");

        // Size mismatch: the project input changes, existing sets keep their size and are flagged.
        await project.UpdateAsync(new ProjectSettings(project.Name, "", "yolo26n", new InputSize(320, 320)), project.Revision);
        var mismatched = await backgrounds.GetSetAsync(set.Id);
        var resized = await backgrounds.CreateSetAsync("Новый размер", "");
        Check(!mismatched.MatchesProjectInput && mismatched.TargetSize == new InputSize(320, 256) && resized.MatchesProjectInput
            && resized.TargetSize == new InputSize(320, 320) && (await backgrounds.ListSetsAsync()).Count(item => !item.MatchesProjectInput) >= 10,
            "Sets of another size than the project input are flagged; new sets take the new size.");
        var oldSize = Result(await RunAsync(new ImportBackgroundsInput(mismatched.Id, [new(fresh)]), "bg-old-size"));
        Check(oldSize.Duplicates == 1, "A flagged set still uses its own size.");

        // Deletion: review with revision, two phases (rows now, files in the background), thumbnails go with the files.
        var victims = (await Items(cropSet.Id)).Take(2).ToArray();
        await using (var thumbnail = await project.Resources.OpenAsync(new ResourceReference(BackgroundItem.ResourceKind, victims[0].Id), ResourceVariant.Thumbnail))
        {
        }
        await RejectsCode("backgrounds.selection_invalid", () => backgrounds.ReviewItemsDeletionAsync(cropSet.Id, []));
        await RejectsCode("backgrounds.item_not_found", () => backgrounds.ReviewItemsDeletionAsync(set.Id, [victims[0].Id]));
        var review = await backgrounds.ReviewItemsDeletionAsync(cropSet.Id, victims.Select(item => item.Id).ToArray());
        Check(review is { WholeSet: false, Items: 2, SetName: "Нарезка", Blockers.Count: 0 } && review.Revision.Length == 22, "A selection review counts the backgrounds.");
        await backgrounds.SetItemsRemovedAsync([new(victims[1].Id, victims[1].Revision)], removed: true);
        await RejectsCode("backgrounds.deletion_revision_conflict", () => backgrounds.DeleteItemsAsync(cropSet.Id, victims.Select(item => item.Id).ToArray(), review.Revision));
        review = await backgrounds.ReviewItemsDeletionAsync(cropSet.Id, victims.Select(item => item.Id).ToArray());
        project.FileCleanup.Suspended = true;
        var deleted = await backgrounds.DeleteItemsAsync(cropSet.Id, victims.Select(item => item.Id).ToArray(), review.Revision);
        var victimFiles = victims.Select(item => Path.Combine(directory, item.Id + ".png")).ToArray();
        Check(deleted is { Items: 2, SetDeleted: false } && victimFiles.All(File.Exists)
            && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup WHERE kind = 'backgrounds';") == 2
            && (await backgrounds.GetSetAsync(cropSet.Id)) is { ItemCount: 1, RemovedCount: 0 }, "Rows go first; their files wait in the cleanup queue.");
        await RejectsCode("backgrounds.item_not_found", () => backgrounds.GetItemAsync(victims[0].Id));
        project.FileCleanup.Suspended = false;
        await project.FileCleanup.Request()!.WaitAsync(WaitLimit);
        var thumbnails = Path.Combine(root, "projects", project.Id, "cache", "thumbnails", "v1", "backgrounds", victims[0].Id.ToString("N")[..2]);
        Check(victimFiles.All(path => !File.Exists(path)) && await CountAsync(Database(), "SELECT count(*) FROM file_cleanup;") == 0
            && (!Directory.Exists(thumbnails) || Directory.GetFiles(thumbnails, victims[0].Id + "-*").Length == 0), "The second phase removes files and thumbnails.");
        var setReview = await backgrounds.ReviewSetDeletionAsync(busy.Id);
        var busyFiles = (await Items(busy.Id, null)).Select(item => Path.Combine(directory, item.Id + ".png")).ToArray();
        var setDeleted = await backgrounds.DeleteSetAsync(busy.Id, setReview.Revision);
        await project.FileCleanup.CleanupTask.WaitAsync(WaitLimit);
        Check(setDeleted is { SetDeleted: true, Items: 96 } && busyFiles.All(path => !File.Exists(path))
            && await CountAsync(Database(), $"SELECT count(*) FROM jobs WHERE id = '{running.Id}';") == 0
            && await CountAsync(Database(), $"SELECT count(*) FROM background_items WHERE set_id = '{busy.Id}';") == 0, "Deleting a set removes its backgrounds, files and history.");
        await RejectsCode("backgrounds.set_not_found", () => backgrounds.ListRunsAsync(busy.Id));
        Check(events.Any(change => change.Sets.Any(item => item.Id == busy.Id && item.Revision is null)), "Deletion raises the set with a null revision.");

        // Restart: the process dies after a batch is committed but before its checkpoint; the run resumes without repeats.
        var restartRoot = Path.Combine(fixture, "backgrounds-restart");
        Directory.CreateDirectory(restartRoot);
        seedInstalledModel(restartRoot);
        var dying = await createCore(restartRoot, null);
        var dyingProject = await dying.Projects.CreateAsync(new ProjectSettings("Перезапуск", "", "yolo26n", new InputSize(320, 256)));
        await dying.Projects.OpenAsync(dyingProject.Id);
        var restartSet = await dyingProject.Backgrounds.CreateSetAsync("Кадры", "");
        var died = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((MautVision.Core.Core)dying).ImportBackgrounds.Pause = async (point, batch) =>
        {
            if (point == "inserted" && batch == 1)
            {
                died.TrySetResult();
                await new TaskCompletionSource().Task;
            }
        };
        var interrupted = await dying.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(restartSet.Id, many,
            Blur: new BackgroundBlur(0, 3)), dyingProject.Id, "bg-restart");
        await died.Task.WaitAsync(WaitLimit);
        // The application is gone: its deferred writes never land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        var revived = await createCore(restartRoot, null);
        var revivedProject = await revived.Projects.OpenAsync(dyingProject.Id);
        var resumed = Result(await WaitAsync(revived.Jobs, dyingProject.Id, interrupted.Id, JobStatus.Succeeded));
        var restartItems = (await revivedProject.Backgrounds.ListItemsAsync(restartSet.Id, new BackgroundFilter(), 200)).Items;
        var restartDirectory = Path.Combine(restartRoot, "projects", dyingProject.Id, "files", "backgrounds");
        Check(resumed is { Total: 96, Added: 96, Duplicates: 0 } && restartItems.Count == 96 && Directory.GetFiles(restartDirectory).Length == 96
            && restartItems.Select(item => item.Origin.SourceName).SequenceEqual(many.Select(source => Path.GetFileName(source.Path)))
            && restartItems.Select(item => item.Origin.BlurRadius).SequenceEqual(Enumerable.Range(0, 96).Select(index =>
                (double?)BlurPlan.Radius(new BackgroundBlur(0, 3, BlurPlan.DefaultSeed(dyingProject.Id, "bg-restart")), index))),
            "A resumed run finishes in order without repeats, with the radii of its sources.");

        await MeasureAsync(fixture, ffmpeg, createCore, seedInstalledModel, Check);
        return checks;

        async Task Rejects<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }

    /// <summary>The radius formula against reference values; vector kernels against the scalar loop.</summary>
    private static void CheckKernels(Action<bool, string> check)
    {
        double[] Radii(uint seed, double min, double max, params int[] indexes)
            => indexes.Select(index => BlurPlan.Radius(new BackgroundBlur(min, max, seed), index)).ToArray();
        check(Radii(0, 0, 4, 0, 1, 2, 3, 4, 5, 99, 1000).SequenceEqual([2.9, 1.19, 0, 2.52, 2.74, 3.22, 2.64, 2.19])
            && Radii(12345, 0, 4, 0, 1, 2, 3, 4, 5, 99, 1000).SequenceEqual([0.98, 1.7, 1.04, 0.7, 2.38, 1.07, 1.77, 0.9])
            && Radii(uint.MaxValue, 0, 4, 0, 1, 2, 3).SequenceEqual([1.26, 1.07, 1.5, 3.79])
            && Radii(7, 0.5, 32, 0, 1, 2, 3).SequenceEqual([19.57, 20.59, 1.21, 7.4]),
            "Blur radii match the reference values for the same seed and index.");
        check(BlurPlan.DefaultSeed("11111111-2222-4333-8444-555555555555", "operation-1") == 3745209340u
            && BlurPlan.Resolve("p", "o", new BackgroundBlur(0, 0)) is null && BlurPlan.Resolve("p", "o", new BackgroundBlur(1, 2, 5))?.Seed == 5,
            "The default seed is the first big-endian uint32 of SHA-256(project:operation); a zero maximum means no blur.");

        var random = new Random(7);
        var same = true;
        foreach (var length in new[] { 1024, 1000, 31, 17, 0 })
        {
            for (var round = 0; round < 50; round++)
            {
                var left = new byte[length];
                var right = new byte[length];
                random.NextBytes(left);
                random.NextBytes(right);
                if (round % 5 == 0) Array.Fill(right, (byte)255);
                if (round % 7 == 0) left.CopyTo(right, 0);
                same &= SignatureDistance.Sum(left, right) == SignatureDistance.ScalarSum(left, right);
            }
        }
        check(same, $"The vector sum of differences equals the scalar loop (AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}).");
        using (var gray = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            gray.Erase(new SKColor(100, 150, 200));
            var signature = BackgroundSignature.Compute(gray.GetPixelSpan(), 64, 48);
            var luma = (54 * 100 + 183 * 150 + 19 * 200 + 128) / 256;
            check(signature.Length == 1024 && signature.All(value => value == luma), "A uniform frame has a uniform signature of its Rec. 709 luma.");
        }
        var index = new SimilarityIndex();
        var baseSignature = Enumerable.Range(0, 1024).Select(value => (byte)(value % 200)).ToArray();
        index.Add(baseSignature);
        var close = baseSignature.Select(value => (byte)(value + 3)).ToArray();
        var far = baseSignature.Select(value => (byte)(value + 4)).ToArray();
        check(index.IsSimilar(close) && !index.IsSimilar(far), "A mean difference of 3/255 is similar, 4/255 is not.");
    }

    /// <summary>
    /// Hot paths: the similarity check against 10 000 signatures (vector and scalar), loading them from project.db, a signature,
    /// and run throughput for 1080p screenshot crops and 1080p video frames with and without blur.
    /// </summary>
    private static async Task MeasureAsync(string fixture, string ffmpeg, Func<string, ILoggerFactory?, Task<ICore>> createCore, Action<string> seedInstalledModel,
        Action<bool, string> check)
    {
        var root = Path.Combine(fixture, "backgrounds-perf");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var project = (Project)await core.Projects.CreateAsync(new ProjectSettings("Замер фонов", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var backgrounds = project.BackgroundsManager;

        // 10 000 backgrounds of a set with signatures of distinct frames (shapes drawn at 64×48), inserted through the product's SQL.
        const int Stored = 10_000;
        var set = await backgrounds.CreateSetAsync("10 000", "");
        static byte[] SignatureOf(int seed)
        {
            using var small = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(small)) Draw(canvas, 64, 48, seed, 0);
            return BackgroundSignature.Compute(small.GetPixelSpan(), 64, 48);
        }
        var signatures = Enumerable.Range(0, Stored).Select(index => SignatureOf(50_000 + index)).ToArray();
        await project.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var rows = signatures.Select((signature, index) => new ImportedBackground(Guid.NewGuid(), 640, 640, 1, "x", "p" + index, signature,
                new BackgroundOrigin(BackgroundOriginKind.Uploaded, "perf", "perf.png", "x", null, null, null, null, null, "perf"))).ToArray();
            await BackgroundsManager.InsertItemsAsync(connection, transaction, set.Id, rows, 1, token);
        });
        var load = Stopwatch.StartNew();
        var loaded = await backgrounds.LoadSignaturesAsync(set.Id, CancellationToken.None);
        var loadMs = load.Elapsed.TotalMilliseconds;
        check(loaded.Count == Stored, "10 000 signatures load from project.db.");
        var probe = SignatureOf(7);
        var packed = signatures.SelectMany(value => value).ToArray();
        double Timed(bool vectors, Action action)
        {
            SignatureDistance.UseVectors = vectors;
            try
            {
                action();
                var timer = Stopwatch.StartNew();
                for (var repeat = 0; repeat < 10; repeat++) action();
                return timer.Elapsed.TotalMilliseconds / 10;
            }
            finally
            {
                SignatureDistance.UseVectors = true;
            }
        }
        void FullSums()
        {
            for (var offset = 0; offset < packed.Length; offset += BackgroundSignature.Length)
                SignatureDistance.Sum(packed.AsSpan(offset, BackgroundSignature.Length), probe);
        }
        var fullScalarMs = Timed(false, FullSums);
        var fullVectorMs = Timed(true, FullSums);
        var searchScalarMs = Timed(false, () => loaded.IsSimilar(probe));
        var searchVectorMs = Timed(true, () => loaded.IsSimilar(probe));
        SignatureDistance.UseVectors = false;
        var scalarIndex = SignatureDistance.FindWithin(packed, signatures[Stored - 1], BackgroundSignature.SimilarLimit);
        var scalarMiss = SignatureDistance.FindWithin(packed, probe, BackgroundSignature.SimilarLimit);
        SignatureDistance.UseVectors = true;
        check(scalarIndex == SignatureDistance.FindWithin(packed, signatures[Stored - 1], BackgroundSignature.SimilarLimit) && scalarIndex <= Stored - 1
            && scalarMiss == SignatureDistance.FindWithin(packed, probe, BackgroundSignature.SimilarLimit),
            "The vector and scalar searches give the same answer among 10 000 signatures.");

        using var frame = new SKBitmap(new SKImageInfo(640, 640, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(frame)) Draw(canvas, 640, 640, 5, 0);
        var signatureTimer = Stopwatch.StartNew();
        for (var repeat = 0; repeat < 50; repeat++) BackgroundSignature.Compute(frame.GetPixelSpan(), 640, 640);
        var signatureMs = signatureTimer.Elapsed.TotalMilliseconds / 50;
        double BlurMs(double radius)
        {
            using var image = RgbaImage.Rent(640, 640);
            frame.GetPixelSpan().CopyTo(image.Buffer);
            BackgroundPixels.Blur(image, radius);
            var timer = Stopwatch.StartNew();
            for (var repeat = 0; repeat < 10; repeat++) BackgroundPixels.Blur(image, radius);
            return timer.Elapsed.TotalMilliseconds / 10;
        }
        var blur3 = BlurMs(3);
        var blur32 = BlurMs(32);

        // Throughput: 1080p screenshots cut to 640×640, and 1080p video frames, without and with blur.
        const int Screens = 48;
        var screens = Path.Combine(fixture, "backgrounds-screens");
        Directory.CreateDirectory(screens);
        var paths = Enumerable.Range(0, Screens).Select(index => Path.Combine(screens, $"screen-{index:D2}.png")).ToArray();
        Parallel.For(0, Screens, index =>
        {
            using var bitmap = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap)) Draw(canvas, 1920, 1080, 100 + index, 6);
            using var png = ImagePixels.EncodePng(bitmap.GetPixelSpan().ToArray(), 1920, 1080);
            File.WriteAllBytes(paths[index], png.ToArray());
        });
        var cropSources = paths.Select((path, index) => new BackgroundFileSource(path, new BackgroundCrop(index * 20 % 1280, index * 9 % 440))).ToArray();
        async Task<double> RateAsync(ImportBackgroundsInput input, string operation, int frames)
        {
            var timer = Stopwatch.StartNew();
            var job = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(input, project.Id, operation);
            var done = Result(await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded));
            check(done.Added == frames, $"{operation} adds {frames} backgrounds ({done.Added}).");
            return frames / timer.Elapsed.TotalSeconds;
        }
        var sharpRate = await RateAsync(new ImportBackgroundsInput((await backgrounds.CreateSetAsync("Скриншоты", "")).Id, cropSources), "perf-screens", Screens);
        var blurRate = await RateAsync(new ImportBackgroundsInput((await backgrounds.CreateSetAsync("Скриншоты, размытие", "")).Id, cropSources,
            Blur: new BackgroundBlur(2, 6, 1)), "perf-screens-blur", Screens);
        var clip = Path.Combine(fixture, "backgrounds-sources", "h1080.mp4");
        await FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=20,noise=alls=12:allf=t", "-c:v", "libopenh264", "-b:v", "12M",
            "-g", "60", clip);
        const int Frames = 60;
        var range = new VideoFrameRange(0, 250, Frames);
        var videoSharp = await RateAsync(new ImportBackgroundsInput((await backgrounds.CreateSetAsync("Видео 1080p", "")).Id, [],
            new BackgroundVideoSource(clip, range, new BackgroundCrop(640, 220))), "perf-video", Frames);
        var videoBlur = await RateAsync(new ImportBackgroundsInput((await backgrounds.CreateSetAsync("Видео 1080p, размытие", "")).Id, [],
            new BackgroundVideoSource(clip, range, new BackgroundCrop(640, 220)), Blur: new BackgroundBlur(2, 6, 1)), "perf-video-blur", Frames);
        Console.WriteLine($"Backgrounds: similarity vs {Stored} signatures: full sums {fullScalarMs:F2} ms scalar / {fullVectorMs:F2} ms vector, search with early stop {searchScalarMs:F2} / {searchVectorMs:F2} ms per frame, load {loadMs:F0} ms; "
            + $"signature 640x640 {signatureMs:F2} ms; blur 640x640 r3 {blur3:F1} ms, r32 {blur32:F1} ms; 1080p screenshot crops {sharpRate:F1} / {blurRate:F1} frames/s "
            + $"(sharp / blur 2–6 px); 1080p video frames {videoSharp:F1} / {videoBlur:F1} frames/s.");
    }

    private static ImportBackgroundsResult Result(JobInfo job) => job.Result!.Value.Deserialize<ImportBackgroundsResult>(JsonSerializerOptions.Web)!;

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

    /// <summary>Pixel hash of an image file as the background decoder sees it (sRGB, oriented).</summary>
    private static string PixelHash(string path)
    {
        using var data = BackgroundPixels.ReadFile(path);
        using var image = BackgroundPixels.Decode(data)!;
        return ImagePixels.Hash(image.Buffer.AsSpan(0, image.Length), image.Width, image.Height);
    }

    private static string CutHash(string path, int left, int top, int width, int height)
    {
        using var data = BackgroundPixels.ReadFile(path);
        using var image = BackgroundPixels.Decode(data)!;
        var rows = new byte[width * height * 4];
        for (var y = 0; y < height; y++) image.Buffer.AsSpan(((top + y) * image.Width + left) * 4, width * 4).CopyTo(rows.AsSpan(y * width * 4));
        return ImagePixels.Hash(rows, width, height);
    }

    /// <summary>A frame of large random shapes: different seeds differ clearly; jitter adds ±jitter noise for nearly equal copies.</summary>
    private static byte[] Png(int width, int height, int seed, int jitter = 0)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap)) Draw(canvas, width, height, seed, 0);
        if (jitter > 0)
        {
            var random = new Random(seed * 31 + jitter);
            var pixels = bitmap.GetPixelSpan();
            for (var index = 0; index < pixels.Length; index++)
            {
                if (index % 4 == 3) continue;
                pixels[index] = (byte)Math.Clamp(pixels[index] + random.Next(-jitter, jitter + 1), 0, 255);
            }
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Jpeg(int width, int height, int seed)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) Draw(canvas, width, height, seed, 0);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static void Draw(SKCanvas canvas, int width, int height, int seed, int noise)
    {
        var random = new Random(seed);
        canvas.Clear(new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        using var paint = new SKPaint();
        for (var shape = 0; shape < 8; shape++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawRect(random.Next(width), random.Next(height), width / 3f + random.Next(width / 2), height / 3f + random.Next(height / 2), paint);
        }
        for (var dot = 0; dot < noise * 2000; dot++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawRect(random.Next(width), random.Next(height), 3, 3, paint);
        }
    }

    private static async Task FfmpegAsync(string ffmpeg, params string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg fixture failed: " + await errors);
    }
}
