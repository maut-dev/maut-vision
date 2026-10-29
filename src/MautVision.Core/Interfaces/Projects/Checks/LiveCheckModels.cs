using System.Text.Json.Serialization;

namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>Live check source: an image, a video or the screen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckMode>))]
public enum LiveCheckMode
{
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("video")] Video,
    [JsonStringEnumMemberName("screen")] Screen,
}

/// <summary>
/// Live check state: Preparing "Preparing", Running "Checking", Cancelling "Stopping…", Succeeded "Done",
/// Failed "Error", Cancelled "Stopped". A live check does not wait for the GPU: a busy GPU means an immediate refusal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckState>))]
public enum LiveCheckState
{
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>
/// Why the check stopped: User — "Stop"; ProjectSwitched — another project was opened; SessionEnded — the UI page
/// was reloaded or the window closed; TimeLimit — the "Stop after, min" limit (the screen check ends as "Done" with
/// a warning).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveCheckStopReason>))]
public enum LiveCheckStopReason
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("project_switched")] ProjectSwitched,
    [JsonStringEnumMemberName("session_ended")] SessionEnded,
    [JsonStringEnumMemberName("time_limit")] TimeLimit,
}

/// <summary>Crop in frame pixels (for the screen — from the top-left corner of the monitor); the size equals the version input.</summary>
public sealed record CheckRegion(int X, int Y, int Width, int Height);

/// <summary>
/// Source file chosen in a native window: absolute path, size and modification time at the time of choosing (a file changed after choosing —
/// <c>checks.source_changed</c>); for a video — frame size, duration and frame rate from ffprobe.
/// </summary>
public sealed record CheckSourceFile(string Path, long Length, DateTimeOffset LastWriteTimeUtc, int? Width = null, int? Height = null, long? DurationMs = null,
    double? Fps = null);

/// <summary>
/// Common fields: "Confidence threshold (0–1)" (default 0.25); "Detections per frame" (null — all model candidates, otherwise ≥ 1);
/// "Device" — "cpu" or a GPU number from the training list. IoU 0.7 and imgsz come from the version.
/// </summary>
public sealed record LiveCheckParameters(double Confidence, int? MaxDetections, string Device);

/// <summary>"Choose image and check…": the image is fed whole (letterbox).</summary>
public sealed record ImageCheckRequest(Guid VersionId, CheckSourceFile Source, LiveCheckParameters Parameters);

/// <summary>
/// Check on a video: StepMs — "Step" (≥ 1 ms, default 1000), frames are read in sequence; Region — an input-sized crop in the frame
/// or null if the video is not larger than the input (the whole frame).
/// </summary>
public sealed record VideoCheckRequest(Guid VersionId, CheckSourceFile Source, LiveCheckParameters Parameters, int StepMs, CheckRegion? Region);

/// <summary>
/// Check from the screen: Monitor — a number from <c>IScreenManager.ListMonitors</c>; Fps — "Frames per second" (0 &lt; fps ≤ 60,
/// default 3); MaxDurationSec — "Stop after" (1 s – 24 h, default 30 min); Region — an input-sized crop inside
/// the monitor or null if the screen is not larger than the input.
/// </summary>
public sealed record ScreenCheckRequest(Guid VersionId, int Monitor, LiveCheckParameters Parameters, double Fps, int MaxDurationSec, CheckRegion? Region);

/// <summary>Detection: class (model output and project class), confidence and box as fractions of the analyzed frame (the crop, if set).</summary>
public sealed record LiveDetection(int ClassIndex, Guid? ClassId, string Name, double Confidence, double X, double Y, double Width, double Height);

/// <summary>
/// Check frame: Ordinal — journal entry number (grows), Index — video frame number (sequential for the screen, 0 for an image),
/// TimeMs — frame time (from the start of the video or the screen check), Width × Height — the analyzed frame, InferenceMs — inference time.
/// </summary>
public sealed record LiveCheckFrame(long Ordinal, long Index, long TimeMs, int Width, int Height, int InferenceMs, IReadOnlyList<LiveDetection> Detections);

/// <summary>"Summary by class": in how many frames the class occurred, how many boxes, the highest confidence.</summary>
public sealed record LiveCheckClassSummary(int ClassIndex, Guid? ClassId, string Name, long Frames, long Detections, double MaxConfidence);

/// <summary>Summary over all checked frames (for a video — over the whole video, regardless of the journal).</summary>
public sealed record LiveCheckSummary(long FramesProcessed, long FramesWithDetections, long Detections, IReadOnlyList<LiveCheckClassSummary> Classes);

/// <summary>
/// Progress: FramesProcessed of FramesTotal (for a video — an estimate by step), PositionMs and DurationMs — position in the video, Fps — actual
/// screen frame rate over the latest frames, ElapsedMs — since the check started, FirstFrameMs — from the worker start to the first frame.
/// </summary>
public sealed record LiveCheckProgress(long FramesProcessed, long? FramesTotal, long? PositionMs, long? DurationMs, double? Fps, long ElapsedMs, long? FirstFrameMs);

/// <summary>What is checked: file name (null for the screen), source frame size (for the screen — the captured area), video duration and frame rate, monitor.</summary>
public sealed record LiveCheckSource(string? Name, int? Width, int? Height, long? DurationMs, double? Fps, int? Monitor);

/// <summary>The latest preview: Number grows with every new preview; bytes — <see cref="IChecksManager.GetPreview"/>.</summary>
public sealed record LiveCheckPreviewInfo(long Number, long FrameIndex, long TimeMs, int Width, int Height);

/// <summary>
/// Journal of "frames with detections": Total — how many such frames there were, Kept — how many are stored (screen — a ring of the last 2,000, video —
/// the first 5,000), Truncated — the video journal is truncated, FirstOrdinal and LastOrdinal — bounds of the stored entries (0 if empty).
/// </summary>
public sealed record LiveCheckJournalInfo(long Total, int Kept, bool Truncated, long FirstOrdinal, long LastOrdinal);

/// <summary>Journal page in ascending Ordinal order.</summary>
public sealed record LiveCheckJournalPage(IReadOnlyList<LiveCheckFrame> Items, LiveCheckJournalInfo Journal);

/// <summary>A warning of the worker: Code and Message for the log; Text — what the UI shows (a code of the UI catalog worker with values).</summary>
public sealed record LiveCheckWarning(string Code, string Message, CodedText Text);

/// <summary>
/// An error of a check: Code — what the UI translates; Message — for the log; Worker — what the UI shows for a failure the worker reported
/// itself (checks.worker_*), null otherwise.
/// </summary>
public sealed record LiveCheckError(string Code, string Message, CodedText? Worker = null);

/// <summary>Check parameters as passed to the worker: StepMs — for a video, Monitor, Fps, MaxDurationSec — for the screen.</summary>
public sealed record LiveCheckSettings(double Confidence, int? MaxDetections, string Device, int ImageSize, CheckRegion? Region, int? StepMs, int? Monitor,
    double? Fps, int? MaxDurationSec);

/// <summary>
/// Live check snapshot: LastFrame — "On screen now" (the last frame, including an empty one); RecentFrames — the last page
/// of the journal (only from <see cref="IChecksManager.GetCheckAsync"/>, empty in events); Sequence grows with every change — of two
/// snapshots, the one with the greater value is newer.
/// </summary>
public sealed record LiveCheck(
    Guid Id,
    LiveCheckMode Mode,
    LiveCheckState State,
    Guid VersionId,
    int VersionNumber,
    LiveCheckSettings Settings,
    LiveCheckSource Source,
    LiveCheckProgress Progress,
    LiveCheckSummary Summary,
    LiveCheckFrame? LastFrame,
    LiveCheckPreviewInfo? Preview,
    LiveCheckJournalInfo Journal,
    IReadOnlyList<LiveCheckFrame> RecentFrames,
    IReadOnlyList<LiveCheckWarning> Warnings,
    LiveCheckError? Error,
    LiveCheckStopReason? StopReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence);

/// <summary>Bytes of the latest preview (JPEG) and its description.</summary>
public sealed record LiveCheckPreview(LiveCheckPreviewInfo Info, byte[] Content, string ContentType);

/// <summary>Changed or deleted (Revision = null) evaluation.</summary>
public sealed record EvaluationChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project checks: Evaluations — evaluations with a new revision (reread); Check — live check snapshot (without
/// the journal), at most 5 times per second, state transitions immediately; Preview — bytes of the latest check preview, so that the host issues
/// a resource channel URL for it (bytes are not passed to the UI). The state of evaluation jobs — JobChanged.
/// </summary>
public sealed class ChecksChangedEventArgs(string projectId, IReadOnlyList<EvaluationChange> evaluations, LiveCheck? check, LiveCheckPreview? preview = null)
    : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<EvaluationChange> Evaluations { get; } = evaluations;
    public LiveCheck? Check { get; } = check;
    public LiveCheckPreview? Preview { get; } = preview;
}
