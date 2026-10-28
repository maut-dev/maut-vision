using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;

namespace MautVision.Desktop.Controllers.Jobs.ResponseDto;

/// <summary>
/// The error of a job: Code — what the page translates; Message — the stored text for the log, shown only for a code the page does not know
/// (an older stored code); null when nothing was stored.
/// </summary>
public sealed record JobErrorDto(string Code, string? Message);

/// <summary>Rejected file of an import: name without folders and the reason code.</summary>
public sealed record ImportRejectedFileDto(string Name, string Reason);

/// <summary>Result of classes.import_assets: RejectedFiles holds at most the first 50 rejected files.</summary>
public sealed record ImportClassAssetsResultDto(int Total, int Added, int Duplicates, int Rejected, ImportRejectedFileDto[] RejectedFiles);

/// <summary>
/// Result of images.import: Total — files after expanding folders; RejectedFiles holds at most the first 50 rejected files
/// (name or path inside the folder, reason images.file_invalid or images.file_unreadable).
/// </summary>
public sealed record ImportImagesResultDto(int Total, int Added, int Duplicates, int Rejected, ImportRejectedFileDto[] RejectedFiles);

/// <summary>
/// Skipped source of a background run: SourceIndex — its place among the run's sources (for a video — the frame number),
/// Name — file or video name, Reason — code (the page builds "Size a×b, W×H needed" and others from it and the target size), Width/Height —
/// size of the file or frame for wrong_size and crop_out_of_bounds, TimestampMs — the frame of a video.
/// </summary>
public sealed record BackgroundImportIssueDto(int SourceIndex, string Name, string Reason, int? Width, int? Height, long? TimestampMs);

/// <summary>
/// Result of backgrounds.import: Total — sources (files or video frames), Processed — decided so far ("processed / total"), Added,
/// Duplicates (exact repeats of the set), Similar (skipped similar frames), Rejected; RequestedCount — the goal of the run;
/// TargetSize — the set size; Blur — with the chosen seed or null; Issues — the first 100 skipped sources.
/// </summary>
public sealed record ImportBackgroundsResultDto(int Total, int Processed, int Added, int Duplicates, int Similar, int Rejected, int? RequestedCount,
    InputSizeDto TargetSize, BackgroundBlurResponseDto? Blur,
    BackgroundImportIssueDto[] Issues);

/// <summary>
/// Result of generation.run (also after cancel and failure): RequestedCount, ProducedCount — published ("P / R images"),
/// MissingObjectFrames — frames where some objects found no room ("Frames with missing objects: N"), OutputGroupId — the
/// group of the result (null before it is prepared), Seed.
/// </summary>
public sealed record GenerationRunResultDto(int RequestedCount, int ProducedCount, int MissingObjectFrames, Guid? OutputGroupId, uint Seed);

/// <summary>
/// Status: queued, running, succeeded, failed, cancelled, interrupted, blocked. Progress: 0–100 or null when unknown.
/// Sequence grows with every saved change; the snapshot with the larger Sequence is newer.
/// Result: the summary of the job kind or null; for classes.import_assets — <see cref="ImportClassAssetsResultDto"/>, for images.import — <see cref="ImportImagesResultDto"/>,
/// for backgrounds.import — <see cref="ImportBackgroundsResultDto"/>, for generation.run — <see cref="GenerationRunResultDto"/>, for datasets.build,
/// datasets.verify and datasets.export — DatasetBuildResultDto, DatasetVerifyResultDto and DatasetExportResultDto, for training.run — TrainingRunResultDto.
/// ProgressMessage: what the job is doing (a code of the UI catalog progress with values, a stored text of 1.0.0) or null.
/// Label: what the job works on, given by the feature that started it (a group or dataset name as text, jobLabels.version with number…);
/// null for jobs without one.
/// </summary>
public sealed record JobResponseDto(
    Guid Id,
    string Kind,
    string ProjectId,
    string OperationId,
    string Status,
    double? Progress,
    CodedTextDto? ProgressMessage,
    long? Processed,
    long? Total,
    JobErrorDto? Error,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    long Sequence,
    object? Result,
    CodedTextDto? Label);

/// <summary>An active job; WaitingForGpu — it is queued for the GPU ("Waiting for GPU").</summary>
public sealed record ActiveJobResponseDto(JobResponseDto Job, bool WaitingForGpu);

/// <summary>
/// Active jobs (queued, running, cancelling) in queue order. Total and CountsByKind count all active jobs of the project;
/// NextCursor — the after of the next page, null on the last one.
/// </summary>
public sealed record ActiveJobsPageResponseDto(ActiveJobResponseDto[] Items, int Total, IReadOnlyDictionary<string, int> CountsByKind, Guid? NextCursor);
