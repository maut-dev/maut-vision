using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Backgrounds.RequestDto;
using MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;
using MautVision.Desktop.Resources;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds;

/// <summary>
/// Background sets and their backgrounds of an opened project. Files and videos are chosen in native dialogs; the page gets a
/// selection token, previews frames and samples with it and starts the run. Previews are transient resources: encoded images
/// held in memory for the page, never project files.
/// </summary>
internal sealed class BackgroundsController(
    IProjectsManager projects,
    IJobsManager jobs,
    ResourceHandles resources,
    FileSelections selections,
    NativeDialogs dialogs,
    IMediaManager media)
{
    /// <summary>Files of one "Cut from images" run: their indexes and crops must fit one bridge message.</summary>
    internal const int MaxCropSources = 500;
    private const int DefaultPreviewSide = 1600;

    public async Task<BackgroundSetResponseDto[]> ListSetsAsync(ListBackgroundSetsRequestDto request, CancellationToken cancellationToken)
        => (await (await Backgrounds(request.ProjectId, cancellationToken)).ListSetsAsync(cancellationToken)).Select(BackgroundsResponseMapper.Map).ToArray();

    public async Task<BackgroundSetResponseDto> GetSetAsync(GetBackgroundSetRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).GetSetAsync(request.SetId, cancellationToken));

    public async Task<BackgroundSetResponseDto> CreateSetAsync(CreateBackgroundSetRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).CreateSetAsync(request.Name, request.Description,
            cancellationToken));

    public async Task<BackgroundSetResponseDto> UpdateSetAsync(UpdateBackgroundSetRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).UpdateSetAsync(request.SetId, request.Name,
            request.Description, request.Revision, cancellationToken));

    public async Task<BackgroundSetResponseDto> SetSetArchivedAsync(SetBackgroundSetArchivedRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).SetSetArchivedAsync(request.SetId, request.Archived,
            request.Revision, cancellationToken));

    public async Task<BackgroundPageResponseDto> ListAsync(ListBackgroundsRequestDto request, CancellationToken cancellationToken)
    {
        var filter = new BackgroundFilter(BackgroundsResponseMapper.ParseRemoved(request.Removed));
        var page = await (await Backgrounds(request.ProjectId, cancellationToken)).ListItemsAsync(request.SetId, filter, request.Limit ?? 100, request.After,
            cancellationToken);
        return BackgroundsResponseMapper.Map(request.ProjectId, page, resources);
    }

    public async Task<BackgroundResponseDto> GetAsync(GetBackgroundRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(request.ProjectId, await (await Backgrounds(request.ProjectId, cancellationToken)).GetItemAsync(request.ItemId,
            cancellationToken), resources);

    public async Task<BackgroundResponseDto[]> SetRemovedAsync(SetBackgroundsRemovedRequestDto request, CancellationToken cancellationToken)
    {
        var changed = await (await Backgrounds(request.ProjectId, cancellationToken)).SetItemsRemovedAsync(
            request.Items.Select(item => new BackgroundRevision(item.Id, item.Revision)).ToArray(), request.Removed, cancellationToken);
        return changed.Select(item => BackgroundsResponseMapper.Map(request.ProjectId, item, resources)).ToArray();
    }

    public async Task<JobResponseDto[]> ListRunsAsync(ListBackgroundRunsRequestDto request, CancellationToken cancellationToken)
        => (await (await Backgrounds(request.ProjectId, cancellationToken)).ListRunsAsync(request.SetId, request.Limit ?? 50, request.Before, cancellationToken))
            .Select(job => JobResponseMapper.Map(job)).ToArray();

    public async Task<BackgroundDeletionReviewResponseDto> ReviewSetDeletionAsync(ReviewBackgroundSetDeletionRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).ReviewSetDeletionAsync(request.SetId, cancellationToken));

    public async Task<BackgroundDeletionResultResponseDto> DeleteSetAsync(DeleteBackgroundSetRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).DeleteSetAsync(request.SetId, request.ReviewRevision,
            cancellationToken));

    public async Task<BackgroundDeletionReviewResponseDto> ReviewDeletionAsync(ReviewBackgroundsDeletionRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).ReviewItemsDeletionAsync(request.SetId, request.ItemIds,
            cancellationToken));

    public async Task<BackgroundDeletionResultResponseDto> DeleteAsync(DeleteBackgroundsRequestDto request, CancellationToken cancellationToken)
        => BackgroundsResponseMapper.Map(await (await Backgrounds(request.ProjectId, cancellationToken)).DeleteItemsAsync(request.SetId, request.ItemIds,
            request.ReviewRevision, cancellationToken));

    /// <summary>
    /// Native multi-select dialog, then the headers of the chosen files against the set size: before the run the page shows how
    /// many files fit and how many will be skipped. Files are ordered by name, naturally.
    /// </summary>
    public async Task<BackgroundFileSelectionResponseDto> SelectFilesAsync(SelectBackgroundFilesRequestDto request, CancellationToken cancellationToken)
    {
        var backgrounds = await Backgrounds(request.ProjectId, cancellationToken);
        var set = await backgrounds.GetSetAsync(request.SetId, cancellationToken);
        var paths = await dialogs.PickImageFiles(Strings.DialogAddBackgrounds, cancellationToken);
        if (paths is null || paths.Count == 0)
            return new BackgroundFileSelectionResponseDto(null, set.TargetSize.Width, set.TargetSize.Height, [], 0, 0, 0, 0);
        var ordered = paths.OrderBy(path => Path.GetFileName(path), ImageNameOrder.Instance).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var inspected = await backgrounds.InspectFilesAsync(set.Id, ordered, cancellationToken);
        var files = inspected.Select((file, index) => new BackgroundSourceFileResponseDto(index, file.Name, file.Width, file.Height,
            BackgroundsResponseMapper.FitKey(file.Fit))).ToArray();
        int Count(BackgroundSourceFit fit) => inspected.Count(file => file.Fit == fit);
        return new BackgroundFileSelectionResponseDto(selections.Register(ordered), set.TargetSize.Width, set.TargetSize.Height, files,
            Count(BackgroundSourceFit.Exact), Count(BackgroundSourceFit.Larger), Count(BackgroundSourceFit.Smaller), Count(BackgroundSourceFit.Unreadable));
    }

    /// <summary>Native video dialog, then ffprobe (size, duration); the file is not hashed here. A closed dialog answers with nulls.</summary>
    public async Task<VideoSelectionResponseDto> SelectVideoAsync(SelectBackgroundVideoRequestDto request, CancellationToken cancellationToken)
    {
        var path = await dialogs.PickVideo(Strings.DialogBackgroundVideo, cancellationToken);
        if (string.IsNullOrEmpty(path)) return new VideoSelectionResponseDto(null, null, null, null, null, null);
        var video = await media.ProbeVideoAsync(path, cancellationToken);
        var token = selections.Register([path], FileSelectionKind.Video, video);
        return new VideoSelectionResponseDto(token, Path.GetFileName(path), new FileInfo(path).Length, video.Width, video.Height, video.DurationMs);
    }

    /// <summary>A file of the selection scaled to at most 1600 px (scaled decode) for placing its crop.</summary>
    public async Task<BackgroundSourcePreviewResponseDto> PreviewSourceAsync(PreviewBackgroundSourceRequestDto request, CancellationToken cancellationToken)
    {
        var selection = Peek(request.SelectionToken, FileSelectionKind.Files);
        if (request.SourceIndex < 0 || request.SourceIndex >= selection.Paths.Count) throw new ArgumentException("Source index is outside the selection.");
        var path = selection.Paths[request.SourceIndex];
        var preview = await (await Backgrounds(request.ProjectId, cancellationToken)).PreviewFileAsync(path, request.MaxSide ?? DefaultPreviewSide, cancellationToken);
        return Map(request.ProjectId, request.SourceIndex, null, Path.GetFileName(path), preview);
    }

    /// <summary>A frame of the selected video at a timestamp, for placing the one region of the run.</summary>
    public async Task<BackgroundSourcePreviewResponseDto> PreviewVideoFrameAsync(PreviewBackgroundVideoFrameRequestDto request, CancellationToken cancellationToken)
    {
        var selection = Peek(request.SelectionToken, FileSelectionKind.Video);
        var path = selection.Paths[0];
        var preview = await (await Backgrounds(request.ProjectId, cancellationToken)).PreviewVideoFrameAsync(path, request.TimestampMs,
            request.MaxSide ?? DefaultPreviewSide, cancellationToken);
        return Map(request.ProjectId, null, request.TimestampMs, Path.GetFileName(path), preview);
    }

    /// <summary>Up to 6 samples of the run this request would start (same operation, so the same radii and pixels).</summary>
    public async Task<BackgroundImportPreviewResponseDto> PreviewImportAsync(ImportBackgroundsRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var selection = selections.Peek(request.SelectionToken) ?? throw SelectionExpired();
        var (input, positions) = BuildInput(request, selection);
        var preview = await (await Backgrounds(request.ProjectId, cancellationToken)).PreviewImportAsync(input, request.OperationId, cancellationToken);
        var samples = preview.Samples.Select(sample => new BackgroundSampleResponseDto(
            positions is null ? sample.SourceIndex : positions[sample.SourceIndex],
            sample.SourceName,
            sample.TimestampMs,
            sample.Reason,
            sample.ActualWidth,
            sample.ActualHeight,
            BackgroundsResponseMapper.Map(sample.Crop),
            sample.BlurRadius,
            sample.Image is { } image ? resources.IssueTransient(request.ProjectId, image.Content, image.ContentType) : null)).ToArray();
        return new BackgroundImportPreviewResponseDto(BackgroundsResponseMapper.Map(preview.TargetSize), BackgroundsResponseMapper.Map(preview.Blur), samples);
    }

    /// <summary>Starts the run and returns its snapshot at once; progress and the result come through job.changed.</summary>
    public async Task<JobResponseDto> ImportAsync(ImportBackgroundsRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        ImportBackgroundsInput? input = null;
        // The input is checked before the token is bound, so a corrected request of a new operation can use it again.
        var selection = selections.Resolve(request.SelectionToken, request.OperationId, selected =>
        {
            input = BuildInput(request, selected).Input;
            input.Validate();
        });
        if (selection is null || input is null) throw SelectionExpired();
        // Checked here for an immediate answer; the run checks again before it writes.
        var set = await (await Backgrounds(request.ProjectId, cancellationToken)).GetSetAsync(request.SetId, cancellationToken);
        if (set.ArchivedAt is not null) throw new CoreException("backgrounds.set_archived", $"Background set {set.Id} is archived.");
        var job = await jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(input, request.ProjectId, request.OperationId, JobLabels.Name(set.Name), cancellationToken);
        return JobResponseMapper.Map(job);
    }

    /// <summary>
    /// The run input of a request: the included files with their crops, or the video with its frames from StartMs every
    /// IntervalMs up to the end (the run stops at Count). Positions maps a source of the run back to its index in the selection.
    /// </summary>
    private static (ImportBackgroundsInput Input, int[]? Positions) BuildInput(ImportBackgroundsRequestDto request, FileSelection selection)
    {
        var blur = request.Blur is null ? null : new BackgroundBlur(request.Blur.MinRadius, request.Blur.MaxRadius, request.Blur.Seed switch
        {
            null => null,
            >= 0 and <= uint.MaxValue => (uint)request.Blur.Seed.Value,
            _ => throw new CoreException("backgrounds.blur_invalid", "Blur seed is outside the uint32 range."),
        });
        if (selection.Kind == FileSelectionKind.Video)
        {
            if (request.SourceIndices is not null || request.Crops is not null) throw new ArgumentException("A video run takes a region, not files.");
            if (request.Region is not { } region || request.StartMs is not { } start || request.IntervalMs is not { } interval || request.Count is not { } count)
                throw new ArgumentException("A video run needs region, startMs, intervalMs and count.");
            if (start < 0 || interval < 1) throw new CoreException("video.frames_invalid", $"Video frames from {start} every {interval} ms are invalid.");
            var available = BackgroundVideoSource.AvailableFrames(selection.Video!.DurationMs, start, interval);
            if (available == 0) throw new CoreException("video.range_invalid", $"The start {start} ms is not before the end of the video.");
            if (count < 1 || count > available) throw new CoreException("backgrounds.count_invalid", $"Requested {count} backgrounds of {available} frames.");
            var video = new BackgroundVideoSource(selection.Paths[0], new VideoFrameRange(start, interval, available), new BackgroundCrop(region.Left, region.Top));
            return (new ImportBackgroundsInput(request.SetId, [], video, count, request.SkipSimilar, blur), null);
        }
        if (selection.Kind != FileSelectionKind.Files) throw new ArgumentException("Backgrounds are added from files or a video.");
        if (request.Region is not null || request.StartMs is not null || request.IntervalMs is not null) throw new ArgumentException("A file run takes no video fields.");
        var indexes = request.SourceIndices ?? Enumerable.Range(0, selection.Paths.Count).ToArray();
        if (indexes.Any(index => index < 0 || index >= selection.Paths.Count) || indexes.Distinct().Count() != indexes.Length)
            throw new ArgumentException("Source indexes must be distinct indexes of the selection.");
        var included = indexes.ToHashSet();
        var crops = new Dictionary<int, BackgroundCrop>();
        foreach (var crop in request.Crops ?? [])
        {
            if (!included.Contains(crop.SourceIndex) || !crops.TryAdd(crop.SourceIndex, new BackgroundCrop(crop.Left, crop.Top)))
                throw new ArgumentException("Each crop names one included source.");
        }
        if (crops.Count > 0 && indexes.Length > MaxCropSources)
            throw new CoreException("backgrounds.too_many_sources", $"A crop run takes at most {MaxCropSources} files.");
        var files = indexes.Select(index => new BackgroundFileSource(selection.Paths[index], crops.GetValueOrDefault(index))).ToArray();
        return (new ImportBackgroundsInput(request.SetId, files, null, request.Count, request.SkipSimilar, blur), indexes);
    }

    private BackgroundSourcePreviewResponseDto Map(string projectId, int? sourceIndex, long? timestampMs, string name, BackgroundSourcePreview preview)
        => new(sourceIndex, timestampMs, name, preview.Width, preview.Height,
            resources.IssueTransient(projectId, preview.Preview.Content, preview.Preview.ContentType), preview.Preview.Width, preview.Preview.Height);

    private FileSelection Peek(string token, FileSelectionKind kind)
    {
        var selection = selections.Peek(token) ?? throw SelectionExpired();
        return selection.Kind == kind ? selection : throw new ArgumentException($"The selection holds {selection.Kind}, not {kind}.");
    }

    private static CoreException SelectionExpired() => new("backgrounds.selection_expired", "File selection is unknown, expired or belongs to another operation.");

    private async Task<IBackgroundsManager> Backgrounds(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Backgrounds;
}
