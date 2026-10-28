using System.Text.Json;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

public sealed record TrainingPresetResponseDto(Guid Id, string Name, Dictionary<string, JsonElement> Parameters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TrainingPresetDeletionResponseDto(Guid PresetId);

/// <summary>GPU: "GPU {Index} — {Name}, {MemoryTotalMb / 1024} GB"; device = String(Index).</summary>
public sealed record GpuResponseDto(int Index, string Name, long MemoryTotalMb, string DriverVersion);

/// <summary>DriverAvailable = false — there is no driver: only "Processor" and manual device input.</summary>
public sealed record GpuInventoryResponseDto(bool DriverAvailable, GpuResponseDto[] Gpus, DateTimeOffset CheckedAt);

/// <summary>Mode — packaged | interpreter; Code — why the runtime is unavailable (null if it is available).</summary>
public sealed record TrainingRuntimeResponseDto(bool Configured, bool Available, string? Mode, string? Code);

/// <summary>Dataset of the run and the version: version, name at run time, manifest hash, classes 0..n−1, frames by split.</summary>
public sealed record TrainingDatasetDto(Guid VersionId, string Name, string ManifestHash, DatasetClassEntryDto[] Classes, DatasetSplitCountsDto Counts);

/// <summary>Progress: worker stage, "Epoch X of Y", "Batch a of b", live metrics of the last epoch (losses and quality).</summary>
public sealed record TrainingRunProgressDto(string? Stage, int? Epoch, int? Epochs, int? Batch, int? Batches, Dictionary<string, double> Metrics);

/// <summary>
/// Error of a run: Code — what the page translates; WorkerMessage — for a failure the worker reported itself (training.worker_*), a code
/// of the UI catalog worker with values and the worker's English message as the fallback (a run of 1.0.0 has only its stored text);
/// null otherwise.
/// </summary>
public sealed record TrainingRunErrorDto(string Code, CodedTextDto? WorkerMessage);

/// <summary>
/// Run: State — queued "Waiting for GPU" | preparing "Preparing" | training "Training" | cancelling "Cancelling…" | succeeded "Done" |
/// failed "Error" | cancelled "Cancelled" | interrupted "Interrupted"; Job — snapshot of the training.run job (progress 0–100, "Epoch X of Y");
/// Parameters — requested (all keys), EffectiveParams — actual (null until they arrive); Environment — worker environment;
/// EpochCount and LogCount — history and journal entries; ResultVersionId — the model version after "Done"; Revision — to compare with training.changed.
/// </summary>
public sealed record TrainingRunResponseDto(
    Guid Id,
    JobResponseDto Job,
    string State,
    TrainingDatasetDto Dataset,
    string Architecture,
    InputSizeDto Input,
    int ImageSize,
    Dictionary<string, JsonElement> Parameters,
    string? PresetName,
    JsonElement? EffectiveParams,
    TrainingRunProgressDto Progress,
    int EpochCount,
    long LogCount,
    JsonElement? Environment,
    TrainingRunErrorDto? Error,
    Guid? ResultVersionId,
    long Revision);

public sealed record TrainingRunPageResponseDto(TrainingRunResponseDto[] Items, long Total);

/// <summary>Epoch: losses — box_loss, cls_loss, dfl_loss; metrics — precision, recall, mAP50, mAP50_95, valBoxLoss, valClsLoss, valDflLoss.</summary>
public sealed record TrainingEpochDto(int Epoch, Dictionary<string, double> Losses, Dictionary<string, double> Metrics, double? Lr, double? Fitness);

public sealed record TrainingEpochPageResponseDto(TrainingEpochDto[] Items, int Total, int? NextAfter);

/// <summary>
/// Kind — log (the training console) | stage | warning | system. Message — a console line or a stage as text; a system line (code
/// trainingLog.*) or a warning (code worker.*) with values; lines of 1.0.0 as text.
/// </summary>
public sealed record TrainingLogEntryDto(long Sequence, DateTimeOffset At, string Kind, CodedTextDto Message);

public sealed record TrainingLogPageResponseDto(TrainingLogEntryDto[] Items, long Total, long? NextAfter);

/// <summary>Model output class; ClassId = null for the merged singleCls class, SourceClassIds — the project classes it represents.</summary>
public sealed record ModelClassDto(int Index, Guid? ClassId, string Name, Guid[] SourceClassIds);

/// <summary>Version file: Kind — weights_pt | onnx | plot | log; Name — path in the version folder; Url — for images (resource channel).</summary>
public sealed record ModelVersionFileDto(string Kind, string Name, long ByteSize, string Sha256, string? Url);

/// <summary>Short passport of a model version (the "Models" screen).</summary>
public sealed record ModelVersionResponseDto(
    Guid Id,
    int Number,
    Guid RunId,
    TrainingDatasetDto Dataset,
    string Architecture,
    InputSizeDto Input,
    int ImageSize,
    Dictionary<string, JsonElement> Parameters,
    JsonElement EffectiveParams,
    int EpochsCompleted,
    bool StoppedEarly,
    int? BestEpoch,
    ModelClassDto[] Classes,
    Dictionary<string, double> Metrics,
    ModelVersionFileDto[] Files,
    CodedTextDto[] Notes,
    JsonElement Environment,
    bool IsWorking,
    long Revision,
    DateTimeOffset CreatedAt);

/// <summary>
/// What a version deletion removes: Evaluations — its evaluations on datasets (deleted together with it), Bytes — file size; non-empty Blockers —
/// the deletion is unavailable (training.version_working, checks.evaluation_active, checks.live_active, training.export_active; the page builds the lines from codes).
/// </summary>
public sealed record ModelVersionDeletionReviewResponseDto(Guid VersionId, int Number, bool IsWorking, int Evaluations, long Bytes,
    Catalog.ResponseDto.DeletionBlockerResponseDto[] Blockers);

/// <summary>The version is deleted; CleanupPending — the file folder is still being deleted in the background.</summary>
public sealed record ModelVersionDeletionResponseDto(Guid VersionId, int Number, int Evaluations, bool CleanupPending);

/// <summary>Token — the chosen save location (null — the window was closed), FileName — name of the chosen file.</summary>
public sealed record ModelExportSelectionResponseDto(string? Token, string? FileName);

/// <summary>Result of the training.export job (job.result): "Version N exported: model-vN.zip · 1.2 MB".</summary>
public sealed record ModelExportResultDto(Guid VersionId, int VersionNumber, string FileName, long Bytes, string? Sha256);

/// <summary>Result of the training.run job (job.result): the created version.</summary>
public sealed record TrainingRunResultDto(Guid VersionId);

public sealed record TrainingEntityChangeDto(Guid Id, long? Revision);

/// <summary>training.changed: runs and versions with new revisions (reread getRun / getModelVersion), without data.</summary>
public sealed record TrainingChangedEventDto(string ProjectId, TrainingEntityChangeDto[] Runs, TrainingEntityChangeDto[] Versions);

/// <summary>trainingPresets.changed: presets were saved or deleted — reread listPresets.</summary>
public sealed record TrainingPresetsChangedEventDto(Guid[] PresetIds);
