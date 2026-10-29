using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>Build input: the plan with its hash and a copy of the specification; prepared by <see cref="IDatasetsManager.PrepareBuildAsync"/>. The version ID is the job ID.</summary>
public sealed record DatasetBuildInput(Guid PlanId, string PlanHash, DatasetSpecification Specification, int Width, int Height);

/// <summary>
/// Version build (CPU, resumable). Preparation: sources are reread and the plan is recalculated — a different hash gives
/// <c>datasets.plan_stale</c>; the version row (building) and all sample rows are written in one transaction. Files: windows of samples
/// in parallel — SHA-256 check of the source file, a hard link (JPEG and PNG without EXIF rotation) or a rotated PNG, a YOLO
/// label; a checkpoint after each window, after a restart the build continues from it. Publishing: a check for identical pixels
/// in different splits (<c>datasets.split_collision</c>), manifest.json and data.yaml, the version ready and verified in one transaction.
/// Cancellation and errors leave neither rows nor files. The result is <see cref="DatasetBuildResult"/>.
/// </summary>
[JobKind("datasets.build")]
public interface IDatasetBuildJob : IJobDefinition<DatasetBuildInput>
{
}

/// <summary>Integrity check input: the version and its name at the time of enqueueing.</summary>
public sealed record DatasetVerifyInput(Guid VersionId, string VersionName);

/// <summary>
/// Integrity check (CPU, not resumable): all version files are read in parallel and compared with SHA-256; all problems
/// are listed. The version becomes verified or corrupt. The result is <see cref="DatasetVerifyResult"/>.
/// </summary>
[JobKind("datasets.verify")]
public interface IDatasetVerifyJob : IJobDefinition<DatasetVerifyInput>
{
}

/// <summary>Export input: the version, its name and the absolute ZIP path chosen in "Save as".</summary>
public sealed record DatasetExportInput(Guid VersionId, string VersionName, string DestinationPath);

/// <summary>
/// ZIP export (CPU, not resumable): data.yaml, manifest.json, dataset-metadata.json, images/ (uncompressed), labels/,
/// negatives/&lt;split&gt;/&lt;id&gt;.json — into a temporary file next to the target, then the target is replaced (overwriting is allowed).
/// Cancellation and errors delete the temporary file, the target does not change. The result is <see cref="DatasetExportResult"/>.
/// </summary>
[JobKind("datasets.export")]
public interface IDatasetExportJob : IJobDefinition<DatasetExportInput>
{
}
