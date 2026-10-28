using System.Text.Json;
using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// The input of a training run prepared by <see cref="ITrainingManager.PrepareRunAsync"/>: the dataset (DatasetVersionId — the key of
/// the deletion lock), the model and the project input at the time of the run, complete requested parameters and the preset.
/// </summary>
public sealed record TrainingRunInput(
    Guid DatasetVersionId,
    TrainingDatasetReference Dataset,
    string Architecture,
    int Width,
    int Height,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string? PresetName);

/// <summary>
/// A training run: a GPU job (one GPU job at a time, the queue survives restarts), not resumable — after the application ends,
/// a published result is accepted when the project is opened, otherwise the run is "Interrupted".
/// Enqueued by ICore.Jobs.EnqueueAsync with the operationId of the request; JobInfo.Progress — epochs and batches, ProgressMessage — progress.epoch.
/// </summary>
[JobKind("training.run")]
public interface ITrainingRunJob : IJobDefinition<TrainingRunInput>
{
}

/// <summary>The outcome of a successful run (JobInfo.Result): the created model version.</summary>
public sealed record TrainingRunResult(Guid VersionId);

/// <summary>The input of a ZIP export of a model version: the version, its number and the absolute archive path chosen in "Save as".</summary>
public sealed record ModelExportInput(Guid VersionId, int VersionNumber, string DestinationPath);

/// <summary>
/// Export of a version to ZIP (CPU, not resumable): weights/best.pt and weights/best.onnx (if any) checked by SHA-256 and size
/// (<c>training.artifact_corrupt</c> on a mismatch), manifest.json, classes.json, README.md (English). The archive is reproducible: a fixed
/// date of entries and their order. It is written to a temporary file next to the target and replaces it at the end; cancellation and an error delete the temporary
/// file. Progress — bytes of the weights; the outcome — <see cref="ModelExportResult"/>.
/// </summary>
[JobKind("training.export")]
public interface IModelExportJob : IJobDefinition<ModelExportInput>
{
}

/// <summary>The outcome of an export: "Version N exported: model-vN.zip · 1.2 MB"; Sha256 — of the whole archive.</summary>
public sealed record ModelExportResult(Guid VersionId, int VersionNumber, string FileName, long Bytes, string? Sha256);
