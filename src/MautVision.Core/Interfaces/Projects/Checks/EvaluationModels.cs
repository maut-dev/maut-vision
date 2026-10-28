using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Training;

namespace MautVision.Core.Interfaces.Projects.Checks;

/// <summary>Dataset split for an evaluation: "Validation" or "Test".</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvaluationSplit>))]
public enum EvaluationSplit
{
    [JsonStringEnumMemberName("val")] Validation,
    [JsonStringEnumMemberName("test")] Test,
}

/// <summary>
/// Evaluation state for captions: Queued "Waiting for GPU", Preparing "Preparing", Running "Checking", Cancelling
/// "Stopping…", Succeeded "Done", Failed "Error", Cancelled "Stopped", Interrupted "Interrupted". Derived from the job and the phase.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelEvaluationState>))]
public enum ModelEvaluationState
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("preparing")] Preparing,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("cancelling")] Cancelling,
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>
/// "Check on dataset": model version, dataset version and its split, device ("cpu" or a GPU number from the training list),
/// batch 1–1024 (default 16).
/// </summary>
public sealed record ModelEvaluationRequest(Guid VersionId, Guid DatasetVersionId, EvaluationSplit Split, string Device, int Batch = 16);

/// <summary>
/// Evaluation input prepared by <see cref="IChecksManager.PrepareEvaluationAsync"/>: the version (number, architecture, input, imgsz) and
/// the dataset at the time of enqueueing (DatasetVersionId — the key that blocks dataset deletion), split, device and batch.
/// </summary>
public sealed record ModelEvaluationInput(
    Guid VersionId,
    int VersionNumber,
    string Architecture,
    int Width,
    int Height,
    int ImageSize,
    Guid DatasetVersionId,
    TrainingDatasetReference Dataset,
    EvaluationSplit Split,
    string Device,
    int Batch);

/// <summary>
/// Evaluation of a version on a dataset split: a GPU job (waits in the common queue with training), not resumable — after the application
/// exits it is "Interrupted". Enqueued by ICore.Jobs.EnqueueAsync with the request operationId; the evaluation ID is the job ID.
/// </summary>
[JobKind("checks.evaluate")]
public interface IModelEvaluationJob : IJobDefinition<ModelEvaluationInput>
{
}

/// <summary>Class metrics (model output Index): precision, recall, mAP50, mAP50_95; only classes that have objects in the split.</summary>
public sealed record EvaluationClassMetrics(int Index, Guid? ClassId, string Name, IReadOnlyDictionary<string, double> Metrics);

/// <summary>
/// An error of an evaluation: Code — what the UI translates; Message — for the log; Worker — what the UI shows for a failure the worker
/// reported itself (checks.worker_*: a code of the UI catalog worker with values and the worker's English message), null otherwise.
/// </summary>
public sealed record ModelEvaluationError(string Code, string Message, CodedText? Worker = null);

/// <summary>
/// Evaluation of a model version on a dataset split (Id = Job.Id): Metrics — precision, recall, mAP50, mAP50_95 as in Ultralytics
/// (model.val); ClassMetrics — per class; ImageCount — frames in the split; Environment — worker environment. Revision grows with every
/// saved change.
/// </summary>
public sealed record ModelEvaluation(
    JobInfo Job,
    ModelEvaluationState State,
    Guid VersionId,
    int VersionNumber,
    TrainingDatasetReference Dataset,
    EvaluationSplit Split,
    string Device,
    int Batch,
    int? ImageCount,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<EvaluationClassMetrics> ClassMetrics,
    JsonElement? Environment,
    ModelEvaluationError? Error,
    long Revision)
{
    public Guid Id => Job.Id;
}

/// <summary>"All checks" filter: by version, dataset and split (null — any).</summary>
public sealed record ModelEvaluationFilter(Guid? VersionId = null, Guid? DatasetVersionId = null, EvaluationSplit? Split = null);

/// <summary>Page of evaluations, newest first; Total — all evaluations under the filter.</summary>
public sealed record ModelEvaluationPage(IReadOnlyList<ModelEvaluation> Items, long Total);
