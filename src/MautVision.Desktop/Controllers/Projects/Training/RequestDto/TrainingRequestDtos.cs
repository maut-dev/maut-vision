using System.Text.Json;

namespace MautVision.Desktop.Controllers.Projects.Training.RequestDto;

/// <summary>Presets are shared by all projects: projectId is not needed.</summary>
public sealed record ListTrainingPresetsRequestDto;

/// <summary>"Save as preset…": Parameters — worker protocol keys (epochs, batch, …); the same name overwrites the preset.</summary>
public sealed record SaveTrainingPresetRequestDto(string Name, Dictionary<string, JsonElement> Parameters);

/// <summary>"Delete preset".</summary>
public sealed record DeleteTrainingPresetRequestDto(Guid PresetId);

/// <summary>GPUs for the "Device" field; Refresh — poll nvidia-smi again.</summary>
public sealed record ListGpusRequestDto(bool? Refresh = null);

/// <summary>State of the Python worker runtime.</summary>
public sealed record GetTrainingRuntimeRequestDto;

/// <summary>
/// "Start training" / "Add to queue": dataset version, parameters (a missing key — the chosen value), the preset for
/// the passport; OperationId — new per action (a repeat returns the same run).
/// </summary>
public sealed record StartTrainingRunRequestDto(string ProjectId, Guid DatasetVersionId, Dictionary<string, JsonElement> Parameters, string? PresetName,
    string OperationId);

/// <summary>"Cancel training": idempotent; a queued run is cancelled at once, a running one after the worker stops.</summary>
public sealed record CancelTrainingRunRequestDto(string ProjectId, Guid RunId);

/// <summary>Runs, newest first; Limit 1–200 (default 50), Before — ID of the last run of the previous page.</summary>
public sealed record ListTrainingRunsRequestDto(string ProjectId, int? Limit = null, Guid? Before = null);

public sealed record GetTrainingRunRequestDto(string ProjectId, Guid RunId);

/// <summary>Epoch history in ascending order; After — number of the last received epoch, Limit 1–1000 (default 500).</summary>
public sealed record ListTrainingEpochsRequestDto(string ProjectId, Guid RunId, int? After = null, int? Limit = null);

/// <summary>Journal in ascending order; After — sequence of the last received entry, Limit 1–1000 (default 200).</summary>
public sealed record ListTrainingLogRequestDto(string ProjectId, Guid RunId, long? After = null, int? Limit = null);

/// <summary>The latest journal entries; Limit 1–1000 (default 50).</summary>
public sealed record TailTrainingLogRequestDto(string ProjectId, Guid RunId, int? Limit = null);

public sealed record ListModelVersionsRequestDto(string ProjectId);

public sealed record GetModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Make working".</summary>
public sealed record SetWorkingModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>What a version deletion removes, for the common deletion dialog.</summary>
public sealed record ReviewModelVersionDeletionRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Delete version": the working version and a version being checked or exported now cannot be deleted (training.version_in_use).</summary>
public sealed record DeleteModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Export ZIP", step 1: "Save as" with the name model-vN.zip; the path stays in the host.</summary>
public sealed record SelectModelExportRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Export ZIP", step 2: DestinationToken — from selectModelExportDestination; OperationId — new per action.</summary>
public sealed record StartModelExportRequestDto(string ProjectId, Guid VersionId, string DestinationToken, string OperationId);
