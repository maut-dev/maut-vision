using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Datasets;

namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// State of a run for screen labels: Queued "Waiting for the GPU", Preparing "Preparing", Training "Training", Cancelling
/// "Cancelling…", Succeeded "Done", Failed "Error", Cancelled "Cancelled", Interrupted "Interrupted". Derived from the job and the phase of the run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrainingRunState>))]
public enum TrainingRunState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("training")] Training,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>
/// The dataset of a run and of a model version: VersionId — the dataset version (its passport stays after deletion), Name — the name at the
/// time of the run, ManifestHash — SHA-256 of manifest.json, Classes — classes 0..n−1 of the dataset, Counts — frames per split.
/// </summary>
public sealed record TrainingDatasetReference(Guid VersionId, string Name, string ManifestHash, IReadOnlyList<DatasetClassEntry> Classes, DatasetSplitCounts Counts);

/// <summary>"Start training": the dataset version, parameters (keys of the worker protocol, a missing one is a chosen value) and the preset, if selected.</summary>
public sealed record TrainingRunRequest(Guid DatasetVersionId, IReadOnlyDictionary<string, JsonElement> Parameters, string? PresetName = null);

/// <summary>
/// Progress of training: Stage — the worker stage (validating, loading_model, training, finalizing, exporting); Epoch of Epochs and Batch of
/// Batches; Metrics — losses and metrics of the last epoch ("live metrics").
/// </summary>
public sealed record TrainingRunProgress(string? Stage, int? Epoch, int? Epochs, int? Batch, int? Batches, IReadOnlyDictionary<string, double> Metrics);

/// <summary>An error of a run: Code — what the UI translates; Message — for the log; Worker — what the UI shows for a failure the worker reported itself (training.worker_*), null otherwise.</summary>
public sealed record TrainingRunError(string Code, string Message, CodedText? Worker = null);

/// <summary>
/// A training run is the job <see cref="ITrainingRunJob"/> (Id = Job.Id). Architecture and ImageSize — the model and the project input at the
/// time of the run; Parameters — requested (complete, with chosen values); EffectiveParams — actual ones from the worker (null
/// until they come); Environment — the worker environment (python, torch, ultralytics, cuda, deviceName); EpochCount and LogCount — records
/// of the epoch history and of the log; ResultVersionId — the model version after "Done". Revision grows with saved changes of the run.
/// </summary>
public sealed record TrainingRun(
    JobInfo Job,
    TrainingRunState State,
    TrainingDatasetReference Dataset,
    string Architecture,
    InputSize Input,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string? PresetName,
    JsonElement? EffectiveParams,
    TrainingRunProgress Progress,
    int EpochCount,
    long LogCount,
    JsonElement? Environment,
    TrainingRunError? Error,
    Guid? ResultVersionId,
    long Revision)
{
    public Guid Id => Job.Id;
}

/// <summary>A page of runs, newest first; Total — all runs of the project.</summary>
public sealed record TrainingRunPage(IReadOnlyList<TrainingRun> Items, long Total);

/// <summary>An epoch of the history: training losses, metrics and validation losses, lr and fitness (null without validation in this epoch).</summary>
public sealed record TrainingEpoch(int Epoch, IReadOnlyDictionary<string, double> Losses, IReadOnlyDictionary<string, double> Metrics, double? Lr, double? Fitness);

/// <summary>A page of the epoch history in ascending order; NextAfter — the epoch number for the next page (null — the last one).</summary>
public sealed record TrainingEpochPage(IReadOnlyList<TrainingEpoch> Items, int Total, int? NextAfter);

/// <summary>Kind of a log record: log — a console line of training, stage — a worker stage, warning — a warning, system — a message of the application.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrainingLogKind>))]
public enum TrainingLogKind
{
    [JsonStringEnumMemberName("log")] Log,
    [JsonStringEnumMemberName("stage")] Stage,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("system")] System,
}

/// <summary>
/// A log record. Message — a console line or a stage as text; a system line or a warning as a code of the UI catalogs trainingLog or
/// worker with values (the text of a warning is the worker's English message); records of version 1.0.0 are texts.
/// </summary>
public sealed record TrainingLogEntry(long Sequence, DateTimeOffset At, TrainingLogKind Kind, CodedText Message);

/// <summary>Log records in ascending Sequence; Total — all records; NextAfter — for the next page (null — the last one).</summary>
public sealed record TrainingLogPage(IReadOnlyList<TrainingLogEntry> Items, long Total, long? NextAfter);

/// <summary>
/// A model output class: Index — the output number; ClassId — the project class (null for the merged singleCls class); SourceClassIds —
/// project classes it represents (one, or all selected with singleCls).
/// </summary>
public sealed record ModelClass(int Index, Guid? ClassId, string Name, IReadOnlyList<Guid> SourceClassIds);

/// <summary>Kind of a version file: weights_pt (best.pt), onnx (best.onnx), plot (a chart or an example), log (the training log).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelFileKind>))]
public enum ModelFileKind
{
    [JsonStringEnumMemberName("weights_pt")] WeightsPt,
    [JsonStringEnumMemberName("onnx")] Onnx,
    [JsonStringEnumMemberName("plot")] Plot,
    [JsonStringEnumMemberName("log")] Log,
}

/// <summary>
/// A version file in files/models/&lt;versionId&gt;/: Name — the path inside the version folder (best.pt, plots/results.png); ResourceId —
/// a resource of kind <see cref="ModelVersion.ResourceKind"/> (the resource channel serves images).
/// </summary>
public sealed record ModelVersionFile(ModelFileKind Kind, string Name, long ByteSize, string Sha256, Guid ResourceId);

/// <summary>
/// A model version (the passport of the "Models" screen): Number — the number in the project from 1; RunId — the run; Dataset — the dataset and its passport;
/// Parameters — requested, EffectiveParams — actual; EpochsCompleted and StoppedEarly — early stop; BestEpoch — the epoch with the
/// highest fitness; Classes — in the order of model outputs; Metrics — validation of best.pt; Files with SHA-256; Notes — warnings of the
/// worker (codes of the UI catalog worker; a version of 1.0.0 has texts "code: message"); Environment — the training environment; IsWorking — "Working" (the first version automatically).
/// </summary>
public sealed record ModelVersion(
    Guid Id,
    int Number,
    Guid RunId,
    TrainingDatasetReference Dataset,
    string Architecture,
    InputSize Input,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    JsonElement EffectiveParams,
    int EpochsCompleted,
    bool StoppedEarly,
    int? BestEpoch,
    IReadOnlyList<ModelClass> Classes,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<ModelVersionFile> Files,
    IReadOnlyList<CodedText> Notes,
    JsonElement Environment,
    bool IsWorking,
    long Revision,
    DateTimeOffset CreatedAt)
{
    /// <summary>Kind of the material in the resource channel (folder files/models): <c>new ResourceReference(ResourceKind, file.ResourceId)</c>.</summary>
    public const string ResourceKind = "models";
}

/// <summary>
/// What deleting a version removes: Evaluations — its evaluations on datasets (deleted with it), Bytes — files of the version; Blockers —
/// why it cannot be deleted now: <c>training.version_working</c> (the working version), <c>checks.evaluation_active</c>
/// (an evaluation is queued or running), <c>checks.live_active</c> (a live check is running), <c>training.export_active</c> (a ZIP export).
/// </summary>
public sealed record ModelVersionDeletionReview(Guid VersionId, int Number, bool IsWorking, int Evaluations, long Bytes,
    IReadOnlyList<Catalog.DeletionBlocker> Blockers);

/// <summary>The version is deleted; Evaluations — deleted evaluations; CleanupPending — the files folder is still being deleted in the background.</summary>
public sealed record ModelVersionDeletionResult(Guid VersionId, int Number, int Evaluations, bool CleanupPending);

/// <summary>A changed or deleted (Revision = null) training entity.</summary>
public sealed record TrainingEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the training of the project: Runs — runs with a new revision (progress, epochs, log, actual parameters, outcome; merged
/// at most once per 500 ms); Versions — model versions (appeared, "Working" changed, deleted — Revision = null). The state of the job comes with
/// JobChanged.
/// </summary>
public sealed class TrainingChangedEventArgs(string projectId, IReadOnlyList<TrainingEntityChange> runs, IReadOnlyList<TrainingEntityChange> versions) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<TrainingEntityChange> Runs { get; } = runs;
    public IReadOnlyList<TrainingEntityChange> Versions { get; } = versions;
}
