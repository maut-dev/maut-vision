using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Images;

/// <summary>
/// Image groups, images and annotations of an opened project. Files, folders and videos are chosen in native dialogs; the page
/// gets a selection token and starts the import job with it.
/// </summary>
internal sealed class ImagesController(
    IProjectsManager projects,
    IJobsManager jobs,
    ResourceHandles resources,
    FileSelections selections,
    NativeDialogs dialogs,
    IMediaManager media,
    Func<string, string> messageFor)
{
    public async Task<ImageGroupResponseDto[]> ListGroupsAsync(ListImageGroupsRequestDto request, CancellationToken cancellationToken)
        => (await (await Images(request.ProjectId, cancellationToken)).ListGroupsAsync(cancellationToken)).Select(ImagesResponseMapper.Map).ToArray();

    public async Task<ImageGroupResponseDto> GetGroupAsync(GetImageGroupRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).GetGroupAsync(request.GroupId, cancellationToken));

    public async Task<ImageGroupResponseDto> CreateGroupAsync(CreateImageGroupRequestDto request, CancellationToken cancellationToken)
    {
        var split = ImagesResponseMapper.ParseSplit(request.SplitHint);
        return ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).CreateGroupAsync(request.Name, request.Description, split,
            cancellationToken));
    }

    public async Task<ImageGroupResponseDto> UpdateGroupAsync(UpdateImageGroupRequestDto request, CancellationToken cancellationToken)
    {
        var split = ImagesResponseMapper.ParseSplit(request.SplitHint);
        return ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).UpdateGroupAsync(request.GroupId, request.Name,
            request.Description, split, request.Revision, cancellationToken));
    }

    public async Task<ImageGroupResponseDto> SetGroupArchivedAsync(SetImageGroupArchivedRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).SetGroupArchivedAsync(request.GroupId, request.Archived,
            request.Revision, cancellationToken));

    public async Task<ImagePageResponseDto> ListAsync(ListImagesRequestDto request, CancellationToken cancellationToken)
    {
        var filter = new ImageFilter(ImagesResponseMapper.ParseArchive(request.Archive),
            request.ReviewState is null ? null : ImagesResponseMapper.ParseReview(request.ReviewState));
        var page = await (await Images(request.ProjectId, cancellationToken)).ListImagesAsync(request.GroupId, filter, request.Limit ?? 100, request.After,
            cancellationToken);
        return ImagesResponseMapper.Map(request.ProjectId, page, resources);
    }

    public async Task<ImageResponseDto> GetAsync(GetImageRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(request.ProjectId, await (await Images(request.ProjectId, cancellationToken)).GetImageAsync(request.ImageId,
            cancellationToken), resources);

    public async Task<ImageAnnotationResponseDto> GetAnnotationAsync(GetImageAnnotationRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).GetAnnotationAsync(request.ImageId, request.Revision,
            cancellationToken));

    public async Task<ImageAnnotationResponseDto[]> ListAnnotationsAsync(ListImageAnnotationsRequestDto request, CancellationToken cancellationToken)
        => (await (await Images(request.ProjectId, cancellationToken)).ListAnnotationsAsync(request.ImageIds, cancellationToken))
            .Select(ImagesResponseMapper.Map).ToArray();

    public async Task<SavedImageAnnotationResponseDto> SaveAnnotationAsync(SaveImageAnnotationRequestDto request, CancellationToken cancellationToken)
    {
        var input = new AnnotationInput(
            request.Objects.Select(item => new AnnotationObjectInput(item.Id, item.ClassId, item.ClassVariantId,
                new ImageBox(item.Box.X, item.Box.Y, item.Box.Width, item.Box.Height))).ToArray(),
            request.Roi is { } roi ? new ImageBox(roi.X, roi.Y, roi.Width, roi.Height) : null,
            ImagesResponseMapper.ParseReview(request.ReviewState));
        var saved = await (await Images(request.ProjectId, cancellationToken)).SaveAnnotationAsync(request.ImageId, input, request.AnnotationRevision,
            cancellationToken);
        return new SavedImageAnnotationResponseDto(ImagesResponseMapper.Map(request.ProjectId, saved.Image, resources), ImagesResponseMapper.Map(saved.Annotation));
    }

    public async Task<ImageResponseDto[]> SetArchivedAsync(SetImagesArchivedRequestDto request, CancellationToken cancellationToken)
    {
        var changed = await (await Images(request.ProjectId, cancellationToken)).SetImagesArchivedAsync(
            request.Images.Select(item => new ImageRevision(item.Id, item.Revision)).ToArray(), request.Archived, cancellationToken);
        return changed.Select(image => ImagesResponseMapper.Map(request.ProjectId, image, resources)).ToArray();
    }

    public async Task<MovedImagesResponseDto> MoveAsync(MoveImagesRequestDto request, CancellationToken cancellationToken)
    {
        var moved = await (await Images(request.ProjectId, cancellationToken)).MoveImagesAsync(request.SourceGroupId, request.SourceRevision,
            request.TargetGroupId, request.TargetRevision, request.Images.Select(item => new ImageRevision(item.Id, item.Revision)).ToArray(), cancellationToken);
        return new MovedImagesResponseDto(ImagesResponseMapper.Map(moved.Source), ImagesResponseMapper.Map(moved.Target),
            moved.Images.Select(image => ImagesResponseMapper.Map(request.ProjectId, image, resources)).ToArray());
    }

    public async Task<ImageDeletionReviewResponseDto> ReviewGroupDeletionAsync(ReviewImageGroupDeletionRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).ReviewGroupDeletionAsync(request.GroupId, cancellationToken));

    public async Task<ImageDeletionResultResponseDto> DeleteGroupAsync(DeleteImageGroupRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).DeleteGroupAsync(request.GroupId, request.ReviewRevision,
            cancellationToken));

    public async Task<ImageDeletionReviewResponseDto> ReviewDeletionAsync(ReviewImagesDeletionRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).ReviewImagesDeletionAsync(request.GroupId, request.ImageIds,
            cancellationToken));

    public async Task<ImageDeletionResultResponseDto> DeleteAsync(DeleteImagesRequestDto request, CancellationToken cancellationToken)
        => ImagesResponseMapper.Map(await (await Images(request.ProjectId, cancellationToken)).DeleteImagesAsync(request.GroupId, request.ImageIds,
            request.ReviewRevision, cancellationToken));

    public async Task<JobResponseDto[]> ListImportsAsync(ListImageImportsRequestDto request, CancellationToken cancellationToken)
        => (await (await Images(request.ProjectId, cancellationToken)).ListImportsAsync(request.GroupId, request.Limit ?? 50, request.Before, cancellationToken))
            .Select(job => JobResponseMapper.Map(job, messageFor)).ToArray();

    /// <summary>Native multi-select dialog; the answer comes when the user closes it. Files are ordered by name, naturally.</summary>
    public async Task<ImageSelectionResponseDto> SelectFilesAsync(SelectImageFilesRequestDto request, CancellationToken cancellationToken)
    {
        var paths = await dialogs.PickImageFiles("Импорт файлов", cancellationToken);
        if (paths is null || paths.Count == 0) return new ImageSelectionResponseDto("files", null, null, null);
        // The dialog returns the focused file first; the import order follows the names instead.
        var ordered = paths.OrderBy(path => Path.GetFileName(path), ImageNameOrder.Instance).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        return new ImageSelectionResponseDto("files", selections.Register(ordered), ordered.Length, null);
    }

    /// <summary>Native folder dialog; the import job reads the folder's files.</summary>
    public async Task<ImageSelectionResponseDto> SelectFolderAsync(SelectImageFolderRequestDto request, CancellationToken cancellationToken)
    {
        var folder = await dialogs.PickFolder("Импорт из папки", cancellationToken);
        if (string.IsNullOrEmpty(folder)) return new ImageSelectionResponseDto("folder", null, null, null);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return new ImageSelectionResponseDto("folder", selections.Register([folder], FileSelectionKind.Folder), null, name.Length > 0 ? name : folder);
    }

    /// <summary>
    /// Native video dialog, then ffprobe (size, duration); the file is not hashed here — the import job hashes it once.
    /// A cancelled dialog answers with all fields null.
    /// </summary>
    public async Task<VideoSelectionResponseDto> SelectVideoAsync(SelectImageVideoRequestDto request, CancellationToken cancellationToken)
    {
        var path = await dialogs.PickVideo("Кадры из видео", cancellationToken);
        if (string.IsNullOrEmpty(path)) return new VideoSelectionResponseDto(null, null, null, null, null, null);
        var video = await media.ProbeVideoAsync(path, cancellationToken);
        var token = selections.Register([path], FileSelectionKind.Video, video);
        return new VideoSelectionResponseDto(token, Path.GetFileName(path), new FileInfo(path).Length, video.Width, video.Height, video.DurationMs);
    }

    /// <summary>Starts the import job and returns its snapshot at once; progress and the result come through job.changed.</summary>
    public async Task<JobResponseDto> ImportAsync(ImportImagesRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        VideoFrameRange? frames = null;
        // A video range is checked against the probed duration before the token is bound, so a corrected request can use
        // it again; the job probes and checks again.
        var selection = selections.Resolve(request.SelectionToken, request.OperationId, selected =>
        {
            if (selected.Kind != FileSelectionKind.Video) return;
            if (request.StartMs is not { } start || request.IntervalMs is not { } interval || request.Count is not { } count)
                throw new CoreException("video.frames_invalid", "A video import needs startMs, intervalMs and count.");
            frames = new VideoFrameRange(start, interval, count);
            frames.Validate(selected.Video!.DurationMs);
        }) ?? throw new CoreException("images.selection_expired", "File selection is unknown, expired or belongs to another operation.");
        var images = await Images(request.ProjectId, cancellationToken);
        // Checked here for an immediate answer; the job checks again before it writes.
        var group = await images.GetGroupAsync(request.GroupId, cancellationToken);
        if (group.Origin != ImageGroupOrigin.Manual) throw new CoreException("images.group_not_manual", $"Image group {group.Id} is generated.");
        if (group.ArchivedAt is not null) throw new CoreException("images.group_archived", $"Image group {group.Id} is archived.");
        var sources = selection.Kind switch
        {
            FileSelectionKind.Folder => selection.Paths.Select(path => new ImageImportSource(ImageImportSourceKind.Folder, path, request.Recursive)).ToArray(),
            FileSelectionKind.Video => selection.Paths.Select(path => new ImageImportSource(ImageImportSourceKind.Video, path, Frames: frames)).ToArray(),
            _ => selection.Paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray(),
        };
        var job = await jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id, sources, request.SkipDuplicates),
            request.ProjectId, request.OperationId, group.Name, cancellationToken);
        return JobResponseMapper.Map(job, messageFor);
    }

    private async Task<IImagesManager> Images(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Images;
}
