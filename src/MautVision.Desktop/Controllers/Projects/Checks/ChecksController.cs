using System.Collections.Concurrent;
using System.IO;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Screen;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Checks.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Checks;

/// <summary>
/// Checks of an opened project: evaluations on datasets (the job checks.evaluate; state through job.changed, results through
/// checks.changed), live checks by image, video and screen (snapshots and checks.changed), monitors and the screenshot. Chosen files
/// stay in the host behind selection tokens; previews and screenshots are transient resources, never bytes in a message.
/// </summary>
internal sealed class ChecksController(IProjectsManager projects, IJobsManager jobs, IScreenManager screen, ResourceHandles resources, FileSelections selections,
    NativeDialogs dialogs, IMediaManager media, Func<string, string> messageFor)
{
    internal const int DefaultPreviewSide = 1600;

    // A repeated start of the same operation (a timed-out request sent again) returns the check it started.
    private readonly ConcurrentDictionary<string, Guid> startedChecks = new(StringComparer.Ordinal);

    public async Task<ModelEvaluationResponseDto> StartEvaluationAsync(StartModelEvaluationRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);
        var checks = await Checks(request.ProjectId, cancellationToken);
        var input = await checks.PrepareEvaluationAsync(new ModelEvaluationRequest(request.VersionId, request.DatasetVersionId,
            ChecksResponseMapper.ParseSplit(request.Split), request.Device, request.Batch ?? 16), request.OperationId, cancellationToken);
        var job = await jobs.EnqueueAsync<IModelEvaluationJob, ModelEvaluationInput>(input, request.ProjectId, request.OperationId,
            $"{JobLabels.Version(input.VersionNumber)} · {input.Dataset.Name}", cancellationToken);
        return ChecksResponseMapper.Map(await checks.GetEvaluationAsync(job.Id, cancellationToken), messageFor);
    }

    public async Task<ModelEvaluationResponseDto> CancelEvaluationAsync(CancelModelEvaluationRequestDto request, CancellationToken cancellationToken)
    {
        var checks = await Checks(request.ProjectId, cancellationToken);
        await checks.GetEvaluationAsync(request.EvaluationId, cancellationToken);
        await jobs.CancelAsync(request.ProjectId, request.EvaluationId, cancellationToken);
        return ChecksResponseMapper.Map(await checks.GetEvaluationAsync(request.EvaluationId, cancellationToken), messageFor);
    }

    public async Task<ModelEvaluationResponseDto> GetEvaluationAsync(GetModelEvaluationRequestDto request, CancellationToken cancellationToken)
        => ChecksResponseMapper.Map(await (await Checks(request.ProjectId, cancellationToken)).GetEvaluationAsync(request.EvaluationId, cancellationToken), messageFor);

    public async Task<ModelEvaluationPageResponseDto> ListEvaluationsAsync(ListModelEvaluationsRequestDto request, CancellationToken cancellationToken)
    {
        var filter = new ModelEvaluationFilter(request.VersionId, request.DatasetVersionId, request.Split is null ? null : ChecksResponseMapper.ParseSplit(request.Split));
        var page = await (await Checks(request.ProjectId, cancellationToken)).ListEvaluationsAsync(filter, request.Limit ?? 50, request.Before, cancellationToken);
        return new ModelEvaluationPageResponseDto(page.Items.Select(item => ChecksResponseMapper.Map(item, messageFor)).ToArray(), page.Total);
    }

    public async Task<ModelEvaluationResponseDto[]> ListLatestEvaluationsAsync(ListLatestModelEvaluationsRequestDto request, CancellationToken cancellationToken)
        => (await (await Checks(request.ProjectId, cancellationToken)).ListLatestEvaluationsAsync(request.DatasetVersionId,
            request.Split is null ? null : ChecksResponseMapper.ParseSplit(request.Split), cancellationToken))
            .Select(item => ChecksResponseMapper.Map(item, messageFor)).ToArray();

    public async Task<CheckFileSelectionResponseDto> SelectImageAsync(SelectCheckImageRequestDto request, CancellationToken cancellationToken)
    {
        await Checks(request.ProjectId, cancellationToken);
        var path = await dialogs.PickImageFile("Изображение для проверки", cancellationToken);
        if (string.IsNullOrEmpty(path)) return new CheckFileSelectionResponseDto(null, null, null);
        var stamp = FileStamp.Of(path);
        return new CheckFileSelectionResponseDto(selections.Register([path], FileSelectionKind.Files, null, [stamp]), Path.GetFileName(path), stamp.Length);
    }

    /// <summary>The video, its probe and a frame from its middle for the region of the input size.</summary>
    public async Task<CheckVideoSelectionResponseDto> SelectVideoAsync(SelectCheckVideoRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var path = await dialogs.PickVideo("Видео для проверки", cancellationToken);
        if (string.IsNullOrEmpty(path)) return new CheckVideoSelectionResponseDto(null, null, null, null, null, null, null, null);
        var video = await media.ProbeVideoAsync(path, cancellationToken);
        var stamp = FileStamp.Of(path);
        var token = selections.Register([path], FileSelectionKind.Video, video, [stamp]);
        var frame = await FrameAsync(project, path, video.DurationMs / 2, DefaultPreviewSide, cancellationToken);
        return new CheckVideoSelectionResponseDto(token, Path.GetFileName(path), stamp.Length, video.Width, video.Height, video.DurationMs,
            video.FrameRate > 0 ? video.FrameRate : null, frame);
    }

    /// <summary>«Показать другой кадр».</summary>
    public async Task<CheckVideoFrameResponseDto> PreviewVideoFrameAsync(PreviewCheckVideoFrameRequestDto request, CancellationToken cancellationToken)
    {
        var project = await projects.GetAsync(request.ProjectId, cancellationToken);
        var selection = selections.Peek(request.SelectionToken);
        if (selection is not { Kind: FileSelectionKind.Video, Paths.Count: 1 }) throw SelectionExpired();
        return await FrameAsync(project, selection.Paths[0], request.TimestampMs, request.MaxSide ?? DefaultPreviewSide, cancellationToken);
    }

    public Task<MonitorResponseDto[]> ListMonitorsAsync(ListMonitorsRequestDto request, CancellationToken cancellationToken)
        => Task.FromResult(screen.ListMonitors().Select(ChecksResponseMapper.Map).ToArray());

    /// <summary>«Снять экран»: the PNG goes to the page as a transient resource.</summary>
    public async Task<ScreenShotResponseDto> CaptureScreenAsync(CaptureScreenRequestDto request, CancellationToken cancellationToken)
    {
        await Checks(request.ProjectId, cancellationToken);
        var shot = await screen.CaptureAsync(request.Monitor, cancellationToken);
        return new ScreenShotResponseDto(ChecksResponseMapper.Map(shot.Monitor), resources.IssueTransient(request.ProjectId, shot.Content, shot.ContentType),
            shot.Monitors.Select(ChecksResponseMapper.Map).ToArray());
    }

    public Task<LiveCheckResponseDto> StartImageAsync(StartImageCheckRequestDto request, CancellationToken cancellationToken)
        => StartOnceAsync(request.ProjectId, request.OperationId, async checks =>
        {
            var source = Source(request.SelectionToken, FileSelectionKind.Files);
            return await checks.StartImageCheckAsync(new ImageCheckRequest(request.VersionId, source, Parameters(request.Parameters)), cancellationToken);
        }, cancellationToken);

    public Task<LiveCheckResponseDto> StartVideoAsync(StartVideoCheckRequestDto request, CancellationToken cancellationToken)
        => StartOnceAsync(request.ProjectId, request.OperationId, async checks =>
        {
            var source = Source(request.SelectionToken, FileSelectionKind.Video);
            return await checks.StartVideoCheckAsync(new VideoCheckRequest(request.VersionId, source, Parameters(request.Parameters), request.StepMs ?? 1000,
                ChecksResponseMapper.Map(request.Region)), cancellationToken);
        }, cancellationToken);

    public Task<LiveCheckResponseDto> StartScreenAsync(StartScreenCheckRequestDto request, CancellationToken cancellationToken)
        => StartOnceAsync(request.ProjectId, request.OperationId, checks => checks.StartScreenCheckAsync(new ScreenCheckRequest(request.VersionId, request.Monitor,
            Parameters(request.Parameters), request.Fps ?? 3, request.MaxDurationSec ?? 1800, ChecksResponseMapper.Map(request.Region)), cancellationToken),
            cancellationToken);

    public async Task<LiveCheckResponseDto> StopAsync(StopCheckRequestDto request, CancellationToken cancellationToken)
    {
        var checks = await Checks(request.ProjectId, cancellationToken);
        return Map(request.ProjectId, checks, await checks.StopCheckAsync(request.CheckId, LiveCheckStopReason.User, cancellationToken));
    }

    public async Task<LiveCheckResponseDto> GetAsync(GetCheckRequestDto request, CancellationToken cancellationToken)
    {
        var checks = await Checks(request.ProjectId, cancellationToken);
        return Map(request.ProjectId, checks, await checks.GetCheckAsync(request.CheckId, request.JournalLimit ?? 40, cancellationToken));
    }

    public async Task<LiveCheckResponseDto[]> ListAsync(ListChecksRequestDto request, CancellationToken cancellationToken)
    {
        var checks = await Checks(request.ProjectId, cancellationToken);
        return (await checks.ListChecksAsync(cancellationToken)).Select(check => Map(request.ProjectId, checks, check)).ToArray();
    }

    public async Task<LiveCheckJournalPageResponseDto> ListJournalAsync(ListCheckJournalRequestDto request, CancellationToken cancellationToken)
    {
        var page = await (await Checks(request.ProjectId, cancellationToken)).ListJournalAsync(request.CheckId, request.After, request.Before, request.Limit ?? 100,
            cancellationToken);
        return new LiveCheckJournalPageResponseDto(page.Items.Select(frame => ChecksResponseMapper.Map(frame, int.MaxValue)).ToArray(),
            ChecksResponseMapper.Map(page.Journal));
    }

    private async Task<LiveCheckResponseDto> StartOnceAsync(string projectId, string operationId, Func<IChecksManager, Task<LiveCheck>> start,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var checks = await Checks(projectId, cancellationToken);
        if (startedChecks.TryGetValue(operationId, out var existing)) return Map(projectId, checks, await checks.GetCheckAsync(existing, 0, cancellationToken));
        var check = await start(checks);
        // Repeats come within seconds of the first request; old operations are not kept for long.
        if (startedChecks.Count > 256) startedChecks.Clear();
        startedChecks[operationId] = check.Id;
        return Map(projectId, checks, check);
    }

    /// <summary>A snapshot with a fresh URL of its preview (the bytes stay in the host for the token lifetime).</summary>
    private LiveCheckResponseDto Map(string projectId, IChecksManager checks, LiveCheck check)
    {
        var preview = check.Preview is null ? null : checks.GetPreview(check.Id);
        var url = preview is null ? null : resources.IssueTransient(projectId, preview.Content, preview.ContentType);
        return ChecksResponseMapper.Map(check with { Preview = preview?.Info ?? check.Preview }, url, messageFor);
    }

    private async Task<CheckVideoFrameResponseDto> FrameAsync(IProject project, string path, long timestampMs, int maxSide, CancellationToken cancellationToken)
    {
        var frame = await project.Backgrounds.PreviewVideoFrameAsync(path, timestampMs, maxSide, cancellationToken);
        return new CheckVideoFrameResponseDto(timestampMs, frame.Width, frame.Height, frame.Preview.Width, frame.Preview.Height,
            resources.IssueTransient(project.Id, frame.Preview.Content, frame.Preview.ContentType));
    }

    /// <summary>The chosen file with its size and time at the choice; a selection can be checked several times (new operations).</summary>
    private CheckSourceFile Source(string token, FileSelectionKind kind)
    {
        var selection = selections.Peek(token);
        if (selection is null || selection.Kind != kind || selection.Paths.Count != 1 || selection.Stamps is not [var stamp]) throw SelectionExpired();
        return new CheckSourceFile(selection.Paths[0], stamp.Length, stamp.LastWriteTimeUtc, selection.Video?.Width, selection.Video?.Height,
            selection.Video?.DurationMs, selection.Video is { FrameRate: > 0 } video ? video.FrameRate : null);
    }

    private static LiveCheckParameters Parameters(CheckParametersDto parameters) => new(parameters.Confidence ?? 0.25, parameters.MaxDetections, parameters.Device);

    private static CoreException SelectionExpired() => new("checks.selection_expired", "The chosen file is unknown or expired.");

    private async Task<IChecksManager> Checks(string projectId, CancellationToken cancellationToken)
        => (await projects.GetAsync(projectId, cancellationToken)).Checks;
}
