namespace MautVision.Desktop.Controllers.Projects.Checks.RequestDto;

/// <summary>
/// "Check on dataset": version, dataset version, Split — val ("Validation") | test ("Test"), Device — "cpu" or a GPU number,
/// Batch 1–1024 (default 16); OperationId — new per action (a repeat returns the same evaluation).
/// </summary>
public sealed record StartModelEvaluationRequestDto(string ProjectId, Guid VersionId, Guid DatasetVersionId, string Split, string Device, int? Batch,
    string OperationId);

/// <summary>"Cancel": idempotent; a queued evaluation is cancelled at once, a running one after the worker stops.</summary>
public sealed record CancelModelEvaluationRequestDto(string ProjectId, Guid EvaluationId);

public sealed record GetModelEvaluationRequestDto(string ProjectId, Guid EvaluationId);

/// <summary>"All checks", newest first; filters by version, dataset and split (val | test); Limit 1–200 (default 50).</summary>
public sealed record ListModelEvaluationsRequestDto(string ProjectId, Guid? VersionId = null, Guid? DatasetVersionId = null, string? Split = null, int? Limit = null,
    Guid? Before = null);

/// <summary>Comparison matrix: the latest successful evaluation of each version on a "dataset · split" pair (null — all pairs).</summary>
public sealed record ListLatestModelEvaluationsRequestDto(string ProjectId, Guid? DatasetVersionId = null, string? Split = null);

/// <summary>"Choose image and check…": one PNG, JPEG, WebP or BMP image; the path stays in the host.</summary>
public sealed record SelectCheckImageRequestDto(string ProjectId);

/// <summary>Video selection (MP4, MKV, AVI, MOV, WebM, M4V); the answer carries a frame from the middle for the crop.</summary>
public sealed record SelectCheckVideoRequestDto(string ProjectId);

/// <summary>"Show another frame": a frame of the selected video at TimestampMs, reduced to MaxSide (default 1600).</summary>
public sealed record PreviewCheckVideoFrameRequestDto(string ProjectId, string SelectionToken, long TimestampMs, int? MaxSide = null);

/// <summary>Monitors in physical pixels; Index — the number for the screen check.</summary>
public sealed record ListMonitorsRequestDto;

/// <summary>"Capture screen": PNG of the monitor for the crop (resource channel).</summary>
public sealed record CaptureScreenRequestDto(string ProjectId, int Monitor);

/// <summary>Common fields: Confidence — "Confidence threshold" (null — 0.25), MaxDetections — null "Automatic", Device — "cpu" or a GPU number.</summary>
public sealed record CheckParametersDto(double? Confidence, int? MaxDetections, string Device);

/// <summary>Crop in frame pixels (for the screen — from the monitor corner), the size of the version input.</summary>
public sealed record CheckRegionDto(int X, int Y, int Width, int Height);

/// <summary>Check on an image: SelectionToken — from selectImage; OperationId — new per action (a repeat returns the same check).</summary>
public sealed record StartImageCheckRequestDto(string ProjectId, Guid VersionId, string SelectionToken, CheckParametersDto Parameters, string OperationId);

/// <summary>Check on a video: StepMs — "Step" (null — 1000 ms), Region — null if the video is not larger than the input.</summary>
public sealed record StartVideoCheckRequestDto(string ProjectId, Guid VersionId, string SelectionToken, CheckParametersDto Parameters, int? StepMs,
    CheckRegionDto? Region, string OperationId);

/// <summary>Check from the screen: Fps — "Frames per second" (null — 3), MaxDurationSec — "Stop after" (null — 30 min), Region — null if the screen is not larger than the input.</summary>
public sealed record StartScreenCheckRequestDto(string ProjectId, Guid VersionId, int Monitor, CheckParametersDto Parameters, double? Fps, int? MaxDurationSec,
    CheckRegionDto? Region, string OperationId);

/// <summary>"Stop": idempotent.</summary>
public sealed record StopCheckRequestDto(string ProjectId, Guid CheckId);

/// <summary>Check snapshot with the last page of the journal (JournalLimit 0–200, default 40).</summary>
public sealed record GetCheckRequestDto(string ProjectId, Guid CheckId, int? JournalLimit = null);

/// <summary>Live checks of the project in memory: the running one and the latest finished ones.</summary>
public sealed record ListChecksRequestDto(string ProjectId);

/// <summary>Journal of frames with detections: After — read new ones after Ordinal, Before — page back; Limit 1–200 (default 100).</summary>
public sealed record ListCheckJournalRequestDto(string ProjectId, Guid CheckId, long? After = null, long? Before = null, int? Limit = null);
