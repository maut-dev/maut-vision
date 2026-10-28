using System.Diagnostics;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Images;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Generation;

/// <summary>
/// One generation run: coordinates the catalog and the backgrounds (sources and their snapshot), the images (output group and
/// published frames) and the generation tables (run row, manifest). Preparation happens once: checks, hard-link snapshot, group
/// in state producing, manifest. Then windows of frames are rendered in parallel while the previous window is published: its
/// files are moved into place and its rows inserted in one transaction strictly in sample order, then the checkpoint (the
/// contiguous prefix producedCount) is saved. A frame depends only on the seed, its index and the manifest, so a run resumed
/// after a restart publishes exactly the frames it would have, and the derived image IDs make a repeated window find its rows.
/// </summary>
internal sealed class GenerationRunHandler(IProjectsManager projects) : IJobHandler<GenerationRunInput>
{
    internal const string Kind = "generation.run";
    private static readonly TimeSpan EventInterval = TimeSpan.FromMilliseconds(500);

    public JobResource Resource => JobResource.Cpu;

    public bool Resumable => true;

    /// <summary>Frame workers inside the one CPU slot the run holds: the CPU slot policy (half of the logical processors).</summary>
    internal int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Frames rendered and published together: one transaction and one checkpoint per window.</summary>
    internal int WindowSize { get; set; } = 64;

    /// <summary>Smoke seam: awaited at "published" (rows committed, checkpoint not saved) and "saved" points of each window.</summary>
    internal Func<string, int, Task>? Pause { get; set; }

    public async Task ExecuteAsync(GenerationRunInput input, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Recipe);
        if (input.RequestedCount < 1) throw new CoreException("generation.count_invalid", $"Job {context.JobId} requests {input.RequestedCount} images.");
        var token = context.CancellationToken;
        var project = (Project)await projects.GetAsync(context.ProjectId, token).ConfigureAwait(false);
        var generation = project.GenerationManager;
        var images = project.ImagesManager;
        var runId = context.JobId;
        var recipe = input.Recipe;
        var requested = input.RequestedCount;
        var checkpoint = context.Checkpoint<GenerationCheckpoint>() ?? new GenerationCheckpoint(0, 0);
        var produced = checkpoint.Produced;
        var missingFrames = checkpoint.MissingFrames;
        var run = await generation.RegisterRunAsync(runId, context.OperationId, token).ConfigureAwait(false);
        Guid? groupId = run.OutputGroupId;
        GenerationRunResult Summary() => new(requested, produced, missingFrames, groupId, input.Seed);
        void Report() => context.ReportProgress(100.0 * produced / requested, null, produced, requested);
        context.SetResult(Summary());
        Report();

        var files = await generation.GetFilesAsync(token).ConfigureAwait(false);
        var completed = false;
        try
        {
            GenerationManifest manifest;
            GenerationSnapshot snapshot;
            if (run.Manifest is null)
            {
                manifest = await generation.LoadManifestAsync(recipe, token).ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                (snapshot, var copied) = GenerationSnapshot.Create(files, runId, manifest, token);
                var group = await images.CreateGeneratedGroupAsync(JobItemIds.Derive(runId, -1), input.OutputGroupName, runId, CancellationToken.None)
                    .ConfigureAwait(false);
                groupId = group.Id;
                await generation.SavePreparedAsync(runId, group.Id, manifest, CancellationToken.None).ConfigureAwait(false);
                context.Logger.LogInformation("Generation run {RunId} of project {ProjectId} prepared in {Elapsed} ms: {Assets} images, {Backgrounds} backgrounds, {Copied} copied.",
                    runId, context.ProjectId, watch.ElapsedMilliseconds, manifest.Bindings.Sum(binding => binding.Assets.Count), manifest.Backgrounds.Count, copied);
                context.SetResult(Summary());
            }
            else
            {
                if (run.Released) throw new CoreException("generation.interrupted", $"Generation run {runId} lost its snapshot before it resumed.");
                manifest = run.Manifest;
                snapshot = GenerationSnapshot.Open(files, runId);
            }
            var outputGroup = groupId!.Value;
            var imagesDirectory = files.CreateDirectory(ProjectImage.ResourceKind);
            if (ProjectStore.ValidateEntry(imagesDirectory) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Image directory must be a directory.");
            using var renderer = new GenerationRenderer(manifest.Layout(recipe), input.Seed,
                (binding, asset) => snapshot.AssetPath(manifest.Bindings[binding].Assets[asset]), index => snapshot.BackgroundPath(manifest.Backgrounds[index]));

            var lastEvent = Stopwatch.StartNew();
            var unannounced = false;
            var batch = 0;
            Task<RenderWindow>? pending = produced < requested ? RenderAsync(produced) : null;
            try
            {
                while (pending is not null)
                {
                    var window = await pending.ConfigureAwait(false);
                    pending = null;
                    try
                    {
                        var next = window.Start + window.Frames.Length;
                        // The next window renders while this one is published.
                        if (next < requested && !token.IsCancellationRequested) pending = RenderAsync(next);
                        // From here the window is published without observing cancellation: it is short and leaves no half-written state.
                        var rows = new List<ImportedImage>(window.Frames.Length);
                        for (var offset = 0; offset < window.Frames.Length; offset++)
                        {
                            var frame = window.Frames[offset]!;
                            var sampleIndex = window.Start + offset;
                            var id = ImageId(runId, sampleIndex);
                            File.Move(window.Temporaries[offset]!, Path.Combine(imagesDirectory, id.ToString("D") + ".png"), overwrite: true);
                            window.Temporaries[offset] = null;
                            rows.Add(new ImportedImage(id, "generated-" + sampleIndex, ImageFileFormat.Png, recipe.Width, recipe.Height, frame.Png.Size, frame.Sha256,
                                frame.PixelHash, new ImageLineage(ImageLineageKind.Generated, "generation:" + runId.ToString("D"), GenerationRunId: runId,
                                    SampleIndex: sampleIndex, RecipeRevision: recipe.Revision),
                                frame.Plan.Objects.Select((item, index) => new AnnotationObject(ObjectId(id, index), manifest.Bindings[item.Binding].ClassId,
                                    manifest.Bindings[item.Binding].Id, renderer.Box(item))).ToArray()));
                        }
                        var raise = lastEvent.Elapsed >= EventInterval;
                        await images.InsertGeneratedAsync(outputGroup, runId, rows, context.OperationId, raise, CancellationToken.None).ConfigureAwait(false);
                        if (raise) lastEvent.Restart();
                        unannounced = !raise;
                        produced = next;
                        missingFrames += window.Frames.Count(frame => frame!.Plan.Missing > 0);
                        if (Pause is { } pause) await pause("published", batch).ConfigureAwait(false);
                        await context.SaveCheckpointAsync(new GenerationCheckpoint(produced, missingFrames)).ConfigureAwait(false);
                        context.SetResult(Summary());
                        Report();
                        if (Pause is { } paused) await paused("saved", batch).ConfigureAwait(false);
                        batch++;
                        token.ThrowIfCancellationRequested();
                    }
                    finally
                    {
                        window.Dispose();
                    }
                }
            }
            finally
            {
                // A window still rendering is awaited so that no worker runs after the run ends; its files are removed.
                if (pending is not null)
                {
                    try
                    {
                        (await pending.ConfigureAwait(false)).Dispose();
                    }
                    catch (Exception)
                    {
                        // The run already ends with its own outcome.
                    }
                }
                if (unannounced) images.NotifyImagesChanged(outputGroup);
            }
            completed = true;
            context.Logger.LogInformation("Generation run {RunId} of project {ProjectId} published {Produced} images; {Missing} frames miss objects.",
                runId, context.ProjectId, produced, missingFrames);

            async Task<RenderWindow> RenderAsync(int start)
            {
                var count = Math.Min(WindowSize, requested - start);
                var window = new RenderWindow(start, new RenderedFrame?[count], new string?[count]);
                try
                {
                    await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = token }, (offset, _) =>
                    {
                        var frame = renderer.Render(start + offset);
                        window.Frames[offset] = frame;
                        var temporary = Path.Combine(imagesDirectory, "." + ImageId(runId, start + offset).ToString("D") + ".generate");
                        window.Temporaries[offset] = temporary;
                        using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1);
                        output.Write(frame.Png.AsSpan());
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
                    return window;
                }
                catch
                {
                    window.Dispose();
                    throw;
                }
            }
        }
        catch (Exception error) when (error is not (CoreException or OperationCanceledException or JobDataException))
        {
            // A full disk and a lack of memory are named, other failures by the phase they hit.
            context.Logger.LogError(error, "Generation run {RunId} of project {ProjectId} failed.", runId, context.ProjectId);
            throw new CoreException(FailureCode(error, prepared: groupId is not null), error.Message, error);
        }
        finally
        {
            // Success, cancel and failure end here; a process that dies does not, and the run resumes with its group and snapshot.
            // A failed preparation has no group yet; its partial snapshot goes as well.
            if (groupId is { } group)
            {
                await images.FinishGeneratedGroupAsync(group, completed ? ImageGenerationState.Completed : ImageGenerationState.Partial, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            await generation.ReleaseSnapshotAsync(runId, CancellationToken.None).ConfigureAwait(false);
            context.SetResult(Summary());
        }
    }

    /// <summary>generation.disk_full, generation.out_of_memory, otherwise generation.preparation_failed or generation.execution_failed.</summary>
    internal static string FailureCode(Exception error, bool prepared)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL; SQLITE_FULL, SQLITE_NOMEM.
            if (current is IOException io && (io.HResult & 0xFFFF) is 39 or 112) return "generation.disk_full";
            if (current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 13 }) return "generation.disk_full";
            if (current is OutOfMemoryException or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 7 })
                return "generation.out_of_memory";
        }
        return prepared ? "generation.execution_failed" : "generation.preparation_failed";
    }

    /// <summary>ID of the image of a sample, derived from the run, so a repeated window finds its rows.</summary>
    internal static Guid ImageId(Guid runId, int sampleIndex) => JobItemIds.Derive(runId, sampleIndex);

    /// <summary>ID of an object of a generated image, derived from the image and its position.</summary>
    private static Guid ObjectId(Guid imageId, int index) => JobItemIds.Derive(imageId, index);

    /// <summary>Rendered frames of a window with their temporary files (null once moved into place).</summary>
    private sealed record RenderWindow(int Start, RenderedFrame?[] Frames, string?[] Temporaries) : IDisposable
    {
        public void Dispose()
        {
            foreach (var frame in Frames) frame?.Dispose();
            foreach (var temporary in Temporaries)
            {
                if (temporary is null) continue;
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A leftover run file is never served: resources resolve only <id>.png of existing rows.
                }
            }
        }
    }
}
