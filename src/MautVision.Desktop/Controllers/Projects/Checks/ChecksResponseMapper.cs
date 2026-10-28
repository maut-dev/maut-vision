using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Core.Interfaces.Screen;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Checks.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Training;

namespace MautVision.Desktop.Controllers.Projects.Checks;

internal static class ChecksResponseMapper
{
    /// <summary>Detections of one frame in an event: the most confident ones; the journal and snapshots carry all.</summary>
    internal const int EventDetections = 100;

    internal static ModelEvaluationResponseDto Map(ModelEvaluation evaluation) => new(
        evaluation.Id,
        JobResponseMapper.Map(evaluation.Job),
        StateKey(evaluation.State),
        evaluation.VersionId,
        evaluation.VersionNumber,
        TrainingResponseMapper.Map(evaluation.Dataset),
        SplitKey(evaluation.Split),
        evaluation.Device,
        evaluation.Batch,
        evaluation.ImageCount,
        new Dictionary<string, double>(evaluation.Metrics),
        evaluation.ClassMetrics.Select(item => new EvaluationClassMetricsDto(item.Index, item.ClassId, item.Name, new Dictionary<string, double>(item.Metrics))).ToArray(),
        evaluation.Environment,
        evaluation.Error is { } error ? new CheckErrorDto(error.Code, WorkerErrors.OwnMessage("checks", error.Code, error.Message)) : null,
        evaluation.Revision);

    internal static LiveCheckResponseDto Map(LiveCheck check, string? previewUrl, int detectionLimit = int.MaxValue) => new(
        check.Id,
        check.Mode switch { LiveCheckMode.Image => "image", LiveCheckMode.Video => "video", _ => "screen" },
        check.State switch
        {
            LiveCheckState.Preparing => "preparing",
            LiveCheckState.Running => "running",
            LiveCheckState.Cancelling => "cancelling",
            LiveCheckState.Succeeded => "succeeded",
            LiveCheckState.Failed => "failed",
            _ => "cancelled",
        },
        check.VersionId,
        check.VersionNumber,
        new LiveCheckSettingsDto(check.Settings.Confidence, check.Settings.MaxDetections, check.Settings.Device, check.Settings.ImageSize, Map(check.Settings.Region),
            check.Settings.StepMs, check.Settings.Monitor, check.Settings.Fps, check.Settings.MaxDurationSec),
        new LiveCheckSourceDto(check.Source.Name, check.Source.Width, check.Source.Height, check.Source.DurationMs, check.Source.Fps, check.Source.Monitor),
        new LiveCheckProgressDto(check.Progress.FramesProcessed, check.Progress.FramesTotal, check.Progress.PositionMs, check.Progress.DurationMs, check.Progress.Fps,
            check.Progress.ElapsedMs, check.Progress.FirstFrameMs),
        new LiveCheckSummaryDto(check.Summary.FramesProcessed, check.Summary.FramesWithDetections, check.Summary.Detections, check.Summary.Classes
            .Select(item => new LiveCheckClassSummaryDto(item.ClassIndex, item.ClassId, item.Name, item.Frames, item.Detections, item.MaxConfidence)).ToArray()),
        check.LastFrame is { } last ? Map(last, detectionLimit) : null,
        check.Preview is { } preview && previewUrl is not null
            ? new LiveCheckPreviewDto(preview.Number, preview.FrameIndex, preview.TimeMs, preview.Width, preview.Height, previewUrl)
            : null,
        Map(check.Journal),
        check.RecentFrames.Select(frame => Map(frame, int.MaxValue)).ToArray(),
        check.Warnings.Select(warning => new LiveCheckWarningDto(warning.Code, warning.Message)).ToArray(),
        check.Error is { } error ? new CheckErrorDto(error.Code, WorkerErrors.OwnMessage("checks", error.Code, error.Message)) : null,
        check.StopReason switch
        {
            null => null,
            LiveCheckStopReason.User => "user",
            LiveCheckStopReason.ProjectSwitched => "project_switched",
            LiveCheckStopReason.SessionEnded => "session_ended",
            _ => "time_limit",
        },
        check.StartedAt,
        check.FinishedAt,
        check.Sequence);

    internal static LiveCheckFrameDto Map(LiveCheckFrame frame, int detectionLimit) => new(frame.Ordinal, frame.Index, frame.TimeMs, frame.Width, frame.Height,
        frame.InferenceMs, frame.Detections.OrderByDescending(item => item.Confidence).Take(detectionLimit)
            .Select(item => new LiveDetectionDto(item.ClassIndex, item.ClassId, item.Name, item.Confidence, item.X, item.Y, item.Width, item.Height)).ToArray(),
        frame.Detections.Count);

    internal static LiveCheckJournalInfoDto Map(LiveCheckJournalInfo journal) => new(journal.Total, journal.Kept, journal.Truncated, journal.FirstOrdinal,
        journal.LastOrdinal);

    internal static MonitorResponseDto Map(MonitorInfo monitor) => new(monitor.Index, monitor.Left, monitor.Top, monitor.Width, monitor.Height, monitor.IsPrimary,
        monitor.Name);

    internal static CheckRegionDto? Map(CheckRegion? region) => region is null ? null : new CheckRegionDto(region.X, region.Y, region.Width, region.Height);

    internal static CheckRegion? Map(CheckRegionDto? region) => region is null ? null : new CheckRegion(region.X, region.Y, region.Width, region.Height);

    internal static ChecksChangedEventDto Map(ChecksChangedEventArgs args, LiveCheckResponseDto? check) => new(args.ProjectId,
        args.Evaluations.Select(change => new EvaluationChangeDto(change.Id, change.Revision)).ToArray(), check);

    internal static string StateKey(ModelEvaluationState state) => state switch
    {
        ModelEvaluationState.Queued => "queued",
        ModelEvaluationState.Preparing => "preparing",
        ModelEvaluationState.Running => "running",
        ModelEvaluationState.Cancelling => "cancelling",
        ModelEvaluationState.Succeeded => "succeeded",
        ModelEvaluationState.Failed => "failed",
        ModelEvaluationState.Cancelled => "cancelled",
        _ => "interrupted",
    };

    internal static string SplitKey(EvaluationSplit split) => split == EvaluationSplit.Test ? "test" : "val";

    /// <summary>val | test; anything else is checks.evaluation_parameters with the field split.</summary>
    internal static EvaluationSplit ParseSplit(string? value) => value switch
    {
        "val" => EvaluationSplit.Validation,
        "test" => EvaluationSplit.Test,
        _ => throw new Core.Interfaces.CoreException("checks.evaluation_parameters", "Split must be val or test.")
        {
            Details = new Dictionary<string, string> { ["field"] = "split" },
        },
    };
}
