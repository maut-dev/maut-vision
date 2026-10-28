using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Media;
using MautVision.Core.Projects.Images;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Frames from video: probe values (H.264, VP9, rotation, a Cyrillic name), errors (missing ffmpeg, unsupported and broken
/// files, invalid ranges), the frame rule against ffmpeg's own seek, the pass and seek paths and concurrent readers, import
/// with names, sizes, lineage and family, a video shorter than its container, cancel without leftover ffmpeg processes,
/// resume after a crash without duplicates; timings for a 1080p H.264 clip. Test videos are generated with the pinned ffmpeg.
/// </summary>
internal static class VideoSmoke
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

        var ffmpeg = Path.Combine(ffmpegDirectory, "ffmpeg.exe");
        Check(File.Exists(ffmpeg) && File.Exists(Path.Combine(ffmpegDirectory, "ffprobe.exe")), "The pinned ffmpeg build is present (tools/get-ffmpeg.ps1).");
        var sources = Path.Combine(fixture, "video-sources");
        Directory.CreateDirectory(sources);
        string Clip(string name) => Path.Combine(sources, name);
        const string Pattern = "testsrc2=size=640x360:rate=25:duration=10";
        await Task.WhenAll(
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", Pattern, "-c:v", "libopenh264", "-b:v", "2M", "-g", "25", Clip("h264.mp4")),
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", Pattern, "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-b:v", "1M", Clip("vp9.webm")),
            // The container lasts 5 s (audio), the picture only 2 s; Matroska has no per-stream duration, so the probe sees 5 s.
            FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=duration=5",
                "-c:v", "libopenh264", "-c:a", "aac", Clip("short-picture.mkv")));
        await FfmpegAsync(ffmpeg, "-display_rotation", "90", "-i", Clip("h264.mp4"), "-c", "copy", Clip("rotated.mp4"));
        File.Copy(Clip("h264.mp4"), Clip("кадры улицы.mp4"));
        await File.WriteAllBytesAsync(Clip("broken.mp4"), RandomNumberGenerator.GetBytes(4096));
        await File.WriteAllTextAsync(Clip("notes.txt"), "not a video");

        var root = Path.Combine(fixture, "video-library");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var media = core.Media;

        // Probe: size, duration, rate and codec without hashing the file; rotation swaps the size; errors.
        var h264 = await media.ProbeVideoAsync(Clip("h264.mp4"));
        Check(h264 == new VideoInfo(640, 360, 10_000, 25, "h264"), $"H.264 probe gives size, duration, rate and codec ({h264}).");
        var vp9 = await media.ProbeVideoAsync(Clip("vp9.webm"));
        Check(vp9 is { Width: 640, Height: 360, Codec: "vp9", FrameRate: 25 } && Math.Abs(vp9.DurationMs - 10_000) <= 50, $"VP9 WebM probe ({vp9}).");
        var rotated = await media.ProbeVideoAsync(Clip("rotated.mp4"));
        Check(rotated is { Width: 360, Height: 640 }, $"A rotated video reports the displayed size ({rotated}).");
        Check((await media.ProbeVideoAsync(Clip("кадры улицы.mp4"))).DurationMs == 10_000, "A Cyrillic file name is read.");
        Check((await media.ProbeVideoAsync(Clip("short-picture.mkv"))).DurationMs is >= 4_900 and <= 5_100, "Matroska without a stream duration uses the container duration.");
        await RejectsCode("video.unreadable", () => media.ProbeVideoAsync(Clip("broken.mp4")));
        await RejectsCode("video.unreadable", () => media.ProbeVideoAsync(Clip("missing.mp4")));
        await RejectsCode("video.unsupported", () => media.ProbeVideoAsync(Clip("notes.txt")));
        var bare = new MediaManager(null, NullLogger<MediaManager>.Instance);
        await RejectsCode("video.ffmpeg_missing", () => bare.ProbeVideoAsync(Clip("h264.mp4")));
        await RejectsCode("video.ffmpeg_missing", () => new MediaManager(sources, NullLogger<MediaManager>.Instance).ProbeVideoAsync(Clip("h264.mp4")));

        // Ranges: start ≥ 0, interval ≥ 1 ms, count ≥ 1, last timestamp strictly before the end.
        foreach (var invalid in new[] { new VideoFrameRange(-1, 1000, 1), new VideoFrameRange(0, 0, 1), new VideoFrameRange(0, 1000, 0) })
            await RejectsCode("video.frames_invalid", () => { invalid.Validate(10_000); return Task.CompletedTask; });
        await RejectsCode("video.range_invalid", () => { new VideoFrameRange(0, 1000, 11).Validate(10_000); return Task.CompletedTask; });
        await RejectsCode("video.range_invalid", () => { new VideoFrameRange(9_000, 1000, 2).Validate(10_000); return Task.CompletedTask; });
        await RejectsCode("video.range_invalid", () => { new VideoFrameRange(long.MaxValue / 2, long.MaxValue / 2, 3).Validate(10_000); return Task.CompletedTask; });
        new VideoFrameRange(0, 1000, 10).Validate(10_000);
        new VideoFrameRange(9_999, 1, 1).Validate(10_000);
        Check(true, "The last frame may be 1 ms before the end.");

        // Frames are written losslessly: opaque as RGB, with alpha as RGBA; the alpha check covers vector and tail positions.
        var rgba = new byte[33 * 7 * 4];
        Random.Shared.NextBytes(rgba);
        for (var index = 3; index < rgba.Length; index += 4) rgba[index] = 255;
        Check(ImagePixels.IsOpaque(rgba), "A frame with every alpha 255 is opaque.");
        foreach (var position in new[] { 3, rgba.Length - 1 })
        {
            rgba[position] = 254;
            Check(!ImagePixels.IsOpaque(rgba), $"Alpha below 255 at byte {position} is found.");
            using var png = ImagePixels.EncodePng(rgba, 33, 7);
            Check(ImagePixels.Decode(png.AsStream()) is { } decoded && decoded.PixelHash == ImagePixels.Hash(rgba, 33, 7), "A PNG frame with alpha decodes to its pixels.");
            rgba[position] = 255;
        }
        using (var opaquePng = ImagePixels.EncodePng(rgba, 33, 7))
        {
            Check(ImagePixels.Decode(opaquePng.AsStream())?.PixelHash == ImagePixels.Hash(rgba, 33, 7), "An opaque PNG frame decodes to its pixels.");
        }

        // The frame of a timestamp is ffmpeg's own accurate-seek frame, also on exact frame times (1.000 s at 25 fps).
        var internalMedia = (MediaManager)media;
        var exact = new VideoFrameRange(0, 1000, 5);
        var expected = new string[exact.Count];
        for (var index = 0; index < exact.Count; index++) expected[index] = await SeekHashAsync(ffmpeg, Clip("h264.mp4"), exact.TimestampMs(index));
        await using (var pass = new VideoFrameReader(internalMedia, Clip("h264.mp4"), exact))
        {
            var hashes = new List<string>();
            for (var index = 0; index < exact.Count; index++)
            {
                using var frame = await pass.ReadAsync(index, CancellationToken.None);
                hashes.Add(ImagePixels.Hash(frame!.Buffer, frame.Width, frame.Height));
            }
            Check(hashes.SequenceEqual(expected) && pass.Runs == 1 && hashes.Distinct().Count() == exact.Count,
                "One pass gives the frames of ffmpeg's seek at each timestamp, exact frame times included.");
        }
        // Concurrent readers asking slightly out of order get the same frames from one pass.
        await using (var concurrent = new VideoFrameReader(internalMedia, Clip("h264.mp4"), exact))
        {
            var frames = await Task.WhenAll(Enumerable.Range(0, exact.Count).Reverse().Select(index => Task.Run(() => concurrent.ReadAsync(index, CancellationToken.None))));
            var hashes = frames.Reverse().Select(frame => ImagePixels.Hash(frame!.Buffer, frame.Width, frame.Height)).ToArray();
            foreach (var frame in frames) frame!.Dispose();
            Check(hashes.SequenceEqual(expected) && concurrent.Runs <= 2, $"Concurrent out-of-order reads get the same frames ({concurrent.Runs} runs).");
        }
        // Resume: a pass started at a later index gives the same frame as the pass from the beginning.
        await using (var midway = new VideoFrameReader(internalMedia, Clip("h264.mp4"), exact))
        {
            using var frame = await midway.ReadAsync(3, CancellationToken.None);
            Check(ImagePixels.Hash(frame!.Buffer, frame.Width, frame.Height) == expected[3], "A pass started mid-range gives the same frame.");
        }
        // A step above 3 s seeks per frame: the same frames as a pass over the same timestamps.
        await using (var seeking = new VideoFrameReader(internalMedia, Clip("h264.mp4"), new VideoFrameRange(1000, 4000, 2)))
        {
            using var first = await seeking.ReadAsync(0, CancellationToken.None);
            using var later = await seeking.ReadAsync(1, CancellationToken.None);
            Check(ImagePixels.Hash(first!.Buffer, 640, 360) == expected[1] && ImagePixels.Hash(later!.Buffer, 640, 360) == await SeekHashAsync(ffmpeg, Clip("h264.mp4"), 5000)
                && seeking.Runs == 2, "A step above 3 s reads each frame by its own seek.");
        }
        await using (var turned = new VideoFrameReader(internalMedia, Clip("rotated.mp4"), new VideoFrameRange(0, 1000, 1)))
        {
            using var frame = await turned.ReadAsync(0, CancellationToken.None);
            Check(frame is { Width: 360, Height: 640 }, "Frames of a rotated video come upright.");
        }

        // Import: names, native size, PNG files that decode to the stored pixel hash, lineage and family.
        var project = await core.Projects.CreateAsync(new ProjectSettings("Видео", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var images = project.Images;
        var jobs = core.Jobs;
        var directory = Path.Combine(root, "projects", project.Id, "files", "images");
        var family = "video:" + Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Clip("h264.mp4"))));
        var group = await images.CreateGroupAsync("Кадры", "", ImageSplitHint.Unassigned);
        var job = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            [new(ImageImportSourceKind.Video, Clip("h264.mp4"), Frames: exact)]), project.Id, "video-import");
        var done = await WaitAsync(jobs, project.Id, job.Id, JobStatus.Succeeded);
        var page = await images.ListImagesAsync(group.Id, new ImageFilter(), 100);
        Check(Result(done) is { Total: 5, Added: 5, Rejected: 0 } && page.Items.Select(image => image.DisplayName)
            .SequenceEqual(Enumerable.Range(0, 5).Select(index => $"h264.mp4-frame-{index * 1000}.png")), "Frames are named <video>-frame-<ms>.png in order.");
        Check(page.Items.All(image => image is { Format: ImageFileFormat.Png, Width: 640, Height: 360 }), "Frames keep the native size as PNG.");
        Check(page.Items.Select(image => image.PixelHash).SequenceEqual(expected), "Imported frames are the frames at the exact timestamps.");
        Check(page.Items.Select((image, index) => image.Lineage == new ImageLineage(ImageLineageKind.Video, family, "h264.mp4", index * 1000L, index)).All(same => same),
            "Lineage names the video, timestamp, frame index and the SHA-256 family of the file.");
        var stored = page.Items.Select(image => Path.Combine(directory, image.Id + ".png")).ToArray();
        Check(stored.Zip(page.Items).All(pair =>
        {
            var bytes = File.ReadAllBytes(pair.First);
            using var memory = new MemoryStream(bytes);
            return ImagePixels.Decode(memory) is { Format: ImageFileFormat.Png } decoded && decoded.PixelHash == pair.Second.PixelHash
                && bytes.Length == pair.Second.ByteSize && Convert.ToHexStringLower(SHA256.HashData(bytes)) == pair.Second.Sha256;
        }), "Stored PNG files decode to the stored pixel hash, size and SHA-256.");

        // A second import of the same video (another group, a Cyrillic copy) has the same family; repeats are skipped by pixels.
        var again = await images.CreateGroupAsync("Ещё раз", "", ImageSplitHint.Unassigned);
        var second = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(again.Id,
            [new(ImageImportSourceKind.Video, Clip("кадры улицы.mp4"), Frames: new VideoFrameRange(500, 1500, 4)),
             new(ImageImportSourceKind.Video, Clip("h264.mp4"), Frames: new VideoFrameRange(2000, 1000, 2))]), project.Id, "video-again");
        var secondDone = await WaitAsync(jobs, project.Id, second.Id, JobStatus.Succeeded);
        var againPage = await images.ListImagesAsync(again.Id, new ImageFilter(), 100);
        Check(Result(secondDone) is { Total: 6, Added: 5, Duplicates: 1 } && againPage.Items.All(image => image.Lineage.SourceFamilyId == family)
            && againPage.Items[0].DisplayName == "кадры улицы.mp4-frame-500.png" && againPage.Items[0].Lineage.VideoName == "кадры улицы.mp4",
            "The same video gives the same family in another import; a repeated frame is skipped.");

        // VP9 and a picture shorter than its container: frames past the picture are rejected, the rest is kept.
        var mixed = await images.CreateGroupAsync("WebM", "", ImageSplitHint.Unassigned);
        var webm = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(mixed.Id,
            [new(ImageImportSourceKind.Video, Clip("vp9.webm"), Frames: new VideoFrameRange(0, 2500, 4)),
             new(ImageImportSourceKind.Video, Clip("short-picture.mkv"), Frames: new VideoFrameRange(0, 1000, 4))]), project.Id, "video-webm");
        var webmDone = Result(await WaitAsync(jobs, project.Id, webm.Id, JobStatus.Succeeded));
        Check(webmDone is { Total: 8, Added: 6, Rejected: 2 } && webmDone.RejectedFiles.All(file => file.Reason == "video.frame_unavailable")
            && webmDone.RejectedFiles.Select(file => file.Name).SequenceEqual(["short-picture.mkv-frame-2000.png", "short-picture.mkv-frame-3000.png"]),
            "VP9 frames import; timestamps past the picture are rejected as video.frame_unavailable.");

        // Job errors: range past the end, broken video, missing ffmpeg.
        foreach (var (operation, source, code) in new (string, ImageImportSource, string)[]
        {
            ("video-range", new(ImageImportSourceKind.Video, Clip("h264.mp4"), Frames: new VideoFrameRange(0, 1000, 11)), "video.range_invalid"),
            ("video-broken", new(ImageImportSourceKind.Video, Clip("broken.mp4"), Frames: new VideoFrameRange(0, 1000, 1)), "video.unreadable"),
            ("video-noframes", new(ImageImportSourceKind.Video, Clip("h264.mp4")), "video.frames_invalid"),
        })
        {
            var failing = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(mixed.Id, [source]), project.Id, operation);
            Check((await WaitAsync(jobs, project.Id, failing.Id, JobStatus.Failed)).Error?.Code == code, $"An import job fails with {code}.");
        }

        // Cancel: the job's ffmpeg process is killed, none is left behind.
        var longClip = Clip("long.mp4");
        await FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=20", "-c:v", "libopenh264", "-g", "50", longClip);
        var handler = ((MautVision.Core.Core)core).ImportImages;
        var before = FfmpegProcesses();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Pause = async (point, batch) =>
        {
            if (point == "saved" && batch == 0)
            {
                paused.TrySetResult();
                await hold.Task;
            }
        };
        var cancelGroup = await images.CreateGroupAsync("Отмена", "", ImageSplitHint.Unassigned);
        var cancelled = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(cancelGroup.Id,
            [new(ImageImportSourceKind.Video, longClip, Frames: new VideoFrameRange(0, 40, 400))]), project.Id, "video-cancel");
        await paused.Task.WaitAsync(WaitLimit);
        var running = FfmpegProcesses().Except(before).ToArray();
        Check(running.Length == 1, $"One ffmpeg process decodes the job's video ({running.Length}).");
        await jobs.CancelAsync(project.Id, cancelled.Id);
        hold.SetResult();
        var cancelledJob = await WaitAsync(jobs, project.Id, cancelled.Id, JobStatus.Cancelled);
        handler.Pause = null;
        Check(Result(cancelledJob).Added == ImportImagesHandler.BatchSize && running.All(Exited), "Cancel keeps the written batch and leaves no ffmpeg process.");
        Check(Directory.GetFiles(directory, "*.import").Length == 0, "Cancel leaves no temporary frame files.");

        // Restart: the process dies after a batch is committed but before its checkpoint; the resumed job reads from the
        // checkpoint again and finishes without duplicates.
        var restartRoot = Path.Combine(fixture, "video-restart");
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
        var beforeRestart = FfmpegProcesses();
        var range = new VideoFrameRange(0, 80, 200);
        var interrupted = await dying.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(restartGroup.Id,
            [new(ImageImportSourceKind.Video, longClip, Frames: range)], SkipDuplicates: false), dyingProject.Id, "video-restart");
        await died.Task.WaitAsync(WaitLimit);
        // The application is gone: its deferred writes never land on top of the recovery.
        await ((MautVision.Core.Core)dying).EndForTestingAsync();
        // The "dead" process would have taken its ffmpeg with it.
        foreach (var orphan in FfmpegProcesses().Except(beforeRestart)) Kill(orphan);
        var revived = await createCore(restartRoot, null);
        var revivedProject = await revived.Projects.OpenAsync(dyingProject.Id);
        var resumed = await WaitAsync(revived.Jobs, dyingProject.Id, interrupted.Id, JobStatus.Succeeded);
        var restartPage = await revivedProject.Images.ListImagesAsync(restartGroup.Id, new ImageFilter(), 200);
        var restartDirectory = Path.Combine(restartRoot, "projects", dyingProject.Id, "files", "images");
        Check(Result(resumed) is { Total: 200, Added: 200, Duplicates: 0 } && restartPage.Items.Count == 200 && Directory.GetFiles(restartDirectory).Length == 200
            && restartPage.Items.Select(image => image.Lineage.TimestampMs).SequenceEqual(Enumerable.Range(0, 200).Select(index => (long?)(index * 80L)))
            && restartPage.Items.Select(image => image.Lineage.SourceFamilyId).Distinct().Count() == 1,
            "A resumed video import finishes in order without duplicates or orphans.");
        await using (var check = new VideoFrameReader(internalMedia, longClip, range))
        {
            var sample = new[] { 63, 64, 100, 199 };
            var same = true;
            foreach (var index in sample)
            {
                using var frame = await check.ReadAsync(index, CancellationToken.None);
                same &= ImagePixels.Hash(frame!.Buffer, frame.Width, frame.Height) == restartPage.Items[index].PixelHash;
            }
            Check(same, "Frames written before and after the restart are the frames of their timestamps.");
        }

        // A video replaced before a resumed run changes the plan digest.
        var (_, digest) = ImportPlan.Expand([new(ImageImportSourceKind.Video, longClip, Frames: range)], CancellationToken.None);
        File.SetLastWriteTimeUtc(longClip, DateTime.UtcNow.AddMinutes(1));
        var (_, changed) = ImportPlan.Expand([new(ImageImportSourceKind.Video, longClip, Frames: range)], CancellationToken.None);
        Check(digest != changed, "A replaced video changes the import plan digest.");

        await MeasureAsync(fixture, ffmpeg, internalMedia, createCore, seedInstalledModel, Check);
        return checks;
    }

    /// <summary>
    /// 1080p H.264: import throughput of the pass path; reading 12 frames 3 s apart by one pass and by a seek per frame; the
    /// alternative pipe of PNG images decoded in C# against PAM frames encoded in C#; PNG zlib levels.
    /// </summary>
    private static async Task MeasureAsync(string fixture, string ffmpeg, MediaManager media, Func<string, ILoggerFactory?, Task<ICore>> createCore,
        Action<string> seedInstalledModel, Action<bool, string> check)
    {
        var clip = Path.Combine(fixture, "video-sources", "h1080.mp4");
        await FfmpegAsync(ffmpeg, "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=40,noise=alls=12:allf=t", "-c:v", "libopenh264",
            "-b:v", "12M", "-g", "60", clip);
        var root = Path.Combine(fixture, "video-perf");
        Directory.CreateDirectory(root);
        seedInstalledModel(root);
        var core = await createCore(root, null);
        var project = await core.Projects.CreateAsync(new ProjectSettings("Замер видео", "", "yolo26n", new InputSize(640, 640)));
        await core.Projects.OpenAsync(project.Id);
        var group = await project.Images.CreateGroupAsync("1080p", "", ImageSplitHint.Unassigned);
        const int Frames = 120;
        var watch = Stopwatch.StartNew();
        var job = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            [new(ImageImportSourceKind.Video, clip, Frames: new VideoFrameRange(0, 250, Frames))]), project.Id, "perf-video");
        var finished = Result(await WaitAsync(core.Jobs, project.Id, job.Id, JobStatus.Succeeded));
        var importRate = Frames / watch.Elapsed.TotalSeconds;
        check(finished.Added == Frames, $"1080p video import adds {Frames} frames ({importRate:F1} frames/s).");

        async Task<double> ReadRate(VideoFrameRange range, long seekAboveMs = VideoFrameReader.SeekIntervalMs)
        {
            await using var reader = new VideoFrameReader(media, clip, range, seekAboveMs);
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < range.Count; index++) (await reader.ReadAsync(index, CancellationToken.None))!.Dispose();
            return range.Count / timer.Elapsed.TotalSeconds;
        }
        // One pass against a seek per frame over the same timestamps (GOP 2 s).
        var paths = new List<string>();
        foreach (var step in new[] { 3000L, 6000L, 12000L })
        {
            var range = new VideoFrameRange(0, step, (int)(39_000 / step) + 1);
            paths.Add($"step {step / 1000} s: pass {await ReadRate(range, long.MaxValue):F1}, seek {await ReadRate(range, 0):F1}");
        }
        var denseRate = await ReadRate(new VideoFrameRange(0, 250, Frames));

        // Pipe formats for the same 120 frames with 6 workers: PAM + hash + PNG encode in C#, or PNG from ffmpeg + full decode in C#.
        var pamTimer = Stopwatch.StartNew();
        await using (var reader = new VideoFrameReader(media, clip, new VideoFrameRange(0, 250, Frames)))
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, Frames), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (index, token) =>
            {
                using var frame = await reader.ReadAsync(index, token);
                ImagePixels.Hash(frame!.Buffer, frame.Width, frame.Height);
                using var png = ImagePixels.EncodePng(frame.Buffer, frame.Width, frame.Height);
            });
        }
        var pamRate = Frames / pamTimer.Elapsed.TotalSeconds;
        var pngRate = await PngPipeRateAsync(ffmpeg, clip, Frames, 0);
        var pngBoundedRate = await PngPipeRateAsync(ffmpeg, clip, Frames, 6);

        // PNG filters and zlib levels for one noisy 1080p frame.
        var levels = new List<string>();
        await using (var reader = new VideoFrameReader(media, clip, new VideoFrameRange(5000, 1000, 1)))
        {
            using var frame = await reader.ReadAsync(0, CancellationToken.None);
            var original = ImagePixels.PngOptions;
            // Skia's default, all filters at level 3 and the chosen Paeth filter at level 1.
            foreach (var (filters, level) in new[] { (SkiaSharp.SKPngEncoderFilterFlags.AllFilters, 6), (SkiaSharp.SKPngEncoderFilterFlags.AllFilters, 3),
                (SkiaSharp.SKPngEncoderFilterFlags.Paeth, 1) })
            {
                ImagePixels.PngOptions = new SkiaSharp.SKPngEncoderOptions(filters, level);
                var timer = Stopwatch.StartNew();
                long size = 0;
                for (var repeat = 0; repeat < 5; repeat++)
                {
                    using var png = ImagePixels.EncodePng(frame!.Buffer, frame.Width, frame.Height);
                    size = png.Size;
                }
                levels.Add($"{filters}/{level}: {timer.Elapsed.TotalMilliseconds / 5:F0} ms, {size / 1024} KB");
            }
            ImagePixels.PngOptions = original;
        }
        Console.WriteLine($"Video 1080p H.264: import {importRate:F1} frames/s; frames/s by {string.Join("; ", paths)}; "
            + $"dense read (250 ms) {denseRate:F1} frames/s; PAM+encode {pamRate:F1} frames/s vs PNG pipe+decode {pngRate:F1} frames/s (encoder threads auto), {pngBoundedRate:F1} frames/s (6); PNG {string.Join("; ", levels)}.");
    }

    /// <summary>The alternative design: ffmpeg encodes PNG into the pipe, C# splits the stream at IEND and decodes each image.</summary>
    private static async Task<double> PngPipeRateAsync(string ffmpeg, string clip, int frames, int encoderThreads)
    {
        var timer = Stopwatch.StartNew();
        var start = Start(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", MediaManager.DecoderThreads.ToString(), "-ss", "0",
            "-i", clip, "-map", "0:v:0", "-an", "-vf", "select=gte(t*1000+0.5\\,selected_n*250)", "-fps_mode", "passthrough", "-frames:v", frames.ToString(),
            "-threads", encoderThreads.ToString(), "-c:v", "png", "-f", "image2pipe", "pipe:1");
        using var process = Process.Start(start)!;
        var decoded = new ConcurrentBag<Task>();
        var output = new BufferedStream(process.StandardOutput.BaseStream, 1 << 20);
        using var gate = new SemaphoreSlim(6);
        var signature = new byte[8];
        while (await output.ReadAtLeastAsync(signature, 8, throwOnEndOfStream: false) == 8)
        {
            var image = new MemoryStream();
            image.Write(signature);
            var header = new byte[8];
            while (true)
            {
                await output.ReadExactlyAsync(header);
                var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
                var body = new byte[length + 4];
                await output.ReadExactlyAsync(body);
                image.Write(header);
                image.Write(body);
                if (header.AsSpan(4).SequenceEqual("IEND"u8)) break;
            }
            await gate.WaitAsync();
            image.Position = 0;
            decoded.Add(Task.Run(() =>
            {
                try { ImagePixels.Decode(image); }
                finally { gate.Release(); }
            }));
        }
        await Task.WhenAll(decoded);
        await process.WaitForExitAsync();
        return frames / timer.Elapsed.TotalSeconds;
    }

    /// <summary>Pixel hash of the frame ffmpeg itself gives for an accurate seek to the timestamp.</summary>
    private static async Task<string> SeekHashAsync(string ffmpeg, string clip, long timestampMs)
    {
        var start = Start(ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-ss", (timestampMs / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            "-i", clip, "-map", "0:v:0", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
        using var process = Process.Start(start)!;
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || memory.Length != 640 * 360 * 4) throw new InvalidOperationException("Reference frame failed.");
        return ImagePixels.Hash(memory.GetBuffer().AsSpan(0, (int)memory.Length), 640, 360);
    }

    private static async Task FfmpegAsync(string ffmpeg, params string[] arguments)
    {
        var start = Start(ffmpeg, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", .. arguments]);
        start.RedirectStandardOutput = false;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg fixture failed: " + await errors);
    }

    private static ProcessStartInfo Start(string ffmpeg, params string[] arguments)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static int[] FfmpegProcesses() => Process.GetProcessesByName("ffmpeg").Select(process =>
    {
        using (process) return process.Id;
    }).ToArray();

    private static bool Exited(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Kill(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
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
}
