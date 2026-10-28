using System.Text.Json;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Checks.RequestDto;
using MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Checks.ResponseDto;

/// <summary>Class metrics (output Index): precision, recall, mAP50, mAP50_95.</summary>
public sealed record EvaluationClassMetricsDto(int Index, Guid? ClassId, string Name, Dictionary<string, double> Metrics);

/// <summary>
/// Error of a check: Code — what the page translates; WorkerMessage — for a failure the worker reported itself (checks.worker_*), a code
/// of the UI catalog worker with values and the worker's English message as the fallback; null otherwise.
/// </summary>
public sealed record CheckErrorDto(string Code, CodedTextDto? WorkerMessage);

/// <summary>
/// Evaluation on a dataset: State — queued "Waiting for GPU" | preparing "Preparing" | running "Checking" | cancelling "Stopping…" |
/// succeeded "Done" | failed "Error" | cancelled "Stopped" | interrupted "Interrupted"; Job — snapshot of the checks.evaluate job;
/// Split — val | test; Metrics — precision, recall, mAP50, mAP50_95; ImageCount — frames in the split; Revision — to compare with checks.changed.
/// </summary>
public sealed record ModelEvaluationResponseDto(
    Guid Id,
    JobResponseDto Job,
    string State,
    Guid VersionId,
    int VersionNumber,
    TrainingDatasetDto Dataset,
    string Split,
    string Device,
    int Batch,
    int? ImageCount,
    Dictionary<string, double> Metrics,
    EvaluationClassMetricsDto[] ClassMetrics,
    JsonElement? Environment,
    CheckErrorDto? Error,
    long Revision);

public sealed record ModelEvaluationPageResponseDto(ModelEvaluationResponseDto[] Items, long Total);

/// <summary>Token — the selection (null — the window was closed); the path does not reach the UI.</summary>
public sealed record CheckFileSelectionResponseDto(string? Token, string? Name, long? ByteSize);

/// <summary>Frame of the selected video: video frame size and reduced preview size (the crop is set in frame coordinates), Url — resource channel.</summary>
public sealed record CheckVideoFrameResponseDto(long TimestampMs, int Width, int Height, int PreviewWidth, int PreviewHeight, string Url);

/// <summary>The selected video: frame size, duration, frame rate and a frame from the middle (null if the window was closed).</summary>
public sealed record CheckVideoSelectionResponseDto(string? Token, string? Name, long? ByteSize, int? Width, int? Height, long? DurationMs, double? Fps,
    CheckVideoFrameResponseDto? Frame);

/// <summary>Monitor in physical pixels: "Monitor {Index}" ({Width} × {Height}), IsPrimary — the primary one.</summary>
public sealed record MonitorResponseDto(int Index, int Left, int Top, int Width, int Height, bool IsPrimary, string Name);

/// <summary>Monitor screenshot (PNG by Url) and all monitors at the time of the screenshot.</summary>
public sealed record ScreenShotResponseDto(MonitorResponseDto Monitor, string Url, MonitorResponseDto[] Monitors);

/// <summary>Parameters as passed to the worker: StepMs — for a video; Monitor, Fps, MaxDurationSec — for the screen.</summary>
public sealed record LiveCheckSettingsDto(double Confidence, int? MaxDetections, string Device, int ImageSize, CheckRegionDto? Region, int? StepMs, int? Monitor,
    double? Fps, int? MaxDurationSec);

/// <summary>File name (null for the screen), frame size, video duration and frame rate, monitor.</summary>
public sealed record LiveCheckSourceDto(string? Name, int? Width, int? Height, long? DurationMs, double? Fps, int? Monitor);

/// <summary>
/// "m:ss.s of m:ss.s · frames checked X of ~Y" (video), "Frames N · X fps · m:ss.s from the start" (screen); FirstFrameMs — from the start
/// of the worker to the first frame.
/// </summary>
public sealed record LiveCheckProgressDto(long FramesProcessed, long? FramesTotal, long? PositionMs, long? DurationMs, double? Fps, long ElapsedMs, long? FirstFrameMs);

/// <summary>A row of "Summary by class": Class / Frames / Boxes / Max confidence.</summary>
public sealed record LiveCheckClassSummaryDto(int ClassIndex, Guid? ClassId, string Name, long Frames, long Detections, double MaxConfidence);

public sealed record LiveCheckSummaryDto(long FramesProcessed, long FramesWithDetections, long Detections, LiveCheckClassSummaryDto[] Classes);

/// <summary>Detection: box as fractions of the analyzed frame, caption "name NN%".</summary>
public sealed record LiveDetectionDto(int ClassIndex, Guid? ClassId, string Name, double Confidence, double X, double Y, double Width, double Height);

/// <summary>
/// Frame: Ordinal — journal entry (0 for the last frame without detections), Index — frame number, TimeMs — its time; Detections in
/// an event — at most the first 100 by confidence, DetectionCount — all of them.
/// </summary>
public sealed record LiveCheckFrameDto(long Ordinal, long Index, long TimeMs, int Width, int Height, int InferenceMs, LiveDetectionDto[] Detections, int DetectionCount);

/// <summary>The latest frame preview (JPEG by Url); Number grows with every new preview.</summary>
public sealed record LiveCheckPreviewDto(long Number, long FrameIndex, long TimeMs, int Width, int Height, string Url);

/// <summary>Journal: Total — frames with detections in total, Kept — stored, Truncated — the video journal is truncated (the first 5,000).</summary>
public sealed record LiveCheckJournalInfoDto(long Total, int Kept, bool Truncated, long FirstOrdinal, long LastOrdinal);

public sealed record LiveCheckJournalPageResponseDto(LiveCheckFrameDto[] Items, LiveCheckJournalInfoDto Journal);

public sealed record LiveCheckWarningDto(string Code, CodedTextDto Message);

/// <summary>
/// Live check: Mode — image | video | screen; State — preparing "Preparing" | running "Checking" | cancelling "Stopping…" |
/// succeeded "Done" | failed "Error" | cancelled "Stopped"; StopReason — user | project_switched | session_ended | time_limit;
/// LastFrame — "On screen now"; RecentFrames — the last journal page (empty in events); Sequence — apply the snapshot only
/// if it is greater than the known one.
/// </summary>
public sealed record LiveCheckResponseDto(
    Guid Id,
    string Mode,
    string State,
    Guid VersionId,
    int VersionNumber,
    LiveCheckSettingsDto Settings,
    LiveCheckSourceDto Source,
    LiveCheckProgressDto Progress,
    LiveCheckSummaryDto Summary,
    LiveCheckFrameDto? LastFrame,
    LiveCheckPreviewDto? Preview,
    LiveCheckJournalInfoDto Journal,
    LiveCheckFrameDto[] RecentFrames,
    LiveCheckWarningDto[] Warnings,
    CheckErrorDto? Error,
    string? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

public sealed record EvaluationChangeDto(Guid Id, long? Revision);

/// <summary>checks.changed: evaluations with a new revision (reread getEvaluation) and/or a live check snapshot (at most 5 times per second).</summary>
public sealed record ChecksChangedEventDto(string ProjectId, EvaluationChangeDto[] Evaluations, LiveCheckResponseDto? Check);
