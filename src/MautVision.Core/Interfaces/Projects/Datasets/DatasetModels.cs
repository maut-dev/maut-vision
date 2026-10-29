using System.Text.Json.Serialization;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Images;

namespace MautVision.Core.Interfaces.Projects.Datasets;

/// <summary>Dataset split: YOLO folders <c>train</c>, <c>val</c>, <c>test</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetSplit>))]
public enum DatasetSplit
{
    [JsonStringEnumMemberName("train")] Train,
    [JsonStringEnumMemberName("val")] Validation,
    [JsonStringEnumMemberName("test")] Test,
}

/// <summary>Role of an object without a class variant: object, negative or build refusal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetUnassignedVariantRole>))]
public enum DatasetUnassignedVariantRole
{
    [JsonStringEnumMemberName("target")] Target,
    [JsonStringEnumMemberName("negative")] Negative,
    [JsonStringEnumMemberName("reject")] Reject,
}

/// <summary>Role of an object of a class not selected for training: negative or build refusal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetUnselectedClassRole>))]
public enum DatasetUnselectedClassRole
{
    [JsonStringEnumMemberName("negative")] Negative,
    [JsonStringEnumMemberName("reject")] Reject,
}

/// <summary>Verified — the hash of all files is checked (at build or by an integrity check); Corrupt — the check found problems.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatasetIntegrityState>))]
public enum DatasetIntegrityState
{
    [JsonStringEnumMemberName("unverified")] Unverified,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("corrupt")] Corrupt,
}

/// <summary>
/// Build specification. GroupIds — image groups; ClassIds — classes for training (indexes follow the catalog order);
/// TargetVariantIds and NegativeVariantIds — class variants (bindings) whose objects become objects or negatives.
/// Seed — uint32; ValRatio &gt; 0, TestRatio ≥ 0, sum &lt; 1.
/// </summary>
public sealed record DatasetSpecification(
    string Name,
    string Description,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyList<Guid> ClassIds,
    IReadOnlyList<Guid> TargetVariantIds,
    IReadOnlyList<Guid> NegativeVariantIds,
    DatasetUnassignedVariantRole UnassignedVariantRole,
    DatasetUnselectedClassRole UnselectedClassRole,
    bool IncludeReviewedEmpty,
    bool AllowPartialGeneration,
    uint Seed,
    double ValRatio,
    double TestRatio);

/// <summary>Version class: Index — model output (0..n−1 in catalog order), Name — the name at build time.</summary>
public sealed record DatasetClassEntry(int Index, Guid ClassId, string Name);

/// <summary>Version class variant (binding): name of the project variant at build time, for displaying objects and negatives.</summary>
public sealed record DatasetVariantEntry(Guid ClassVariantId, Guid ClassId, string Name);

public sealed record DatasetSplitCounts(int Train, int Validation, int Test)
{
    public int Total => Train + Validation + Test;
}

/// <summary>Boxes of a class in the version (in index order, including zeros).</summary>
public sealed record DatasetClassCount(int Index, Guid ClassId, string Name, int Boxes);

/// <summary>Frames of a source group in the version.</summary>
public sealed record DatasetGroupCount(Guid GroupId, string Name, int Images);

/// <summary>
/// Composition: frames by split, frames without boxes, manual and generated, boxes in total and by class, negatives, source groups.
/// </summary>
public sealed record DatasetComposition(
    DatasetSplitCounts Images,
    int WithoutBoxes,
    int FromManual,
    int FromGenerated,
    int Boxes,
    int Negatives,
    IReadOnlyList<DatasetClassCount> BoxesPerClass,
    IReadOnlyList<DatasetGroupCount> Groups);

/// <summary>
/// Skipped frames by reason: Unreviewed — the annotation is not reviewed; PartialGeneration — an unfinished generation without
/// permission; WithoutTargets — no objects and empty frames are not included; InvalidGeometry — a box or ROI outside [0, 1].
/// </summary>
public sealed record DatasetPlanSkips(int Unreviewed, int PartialGeneration, int WithoutTargets, int InvalidGeometry)
{
    public int Total => Unreviewed + PartialGeneration + WithoutTargets + InvalidGeometry;
}

/// <summary>
/// Plan warning: Code and Count. <c>datasets.insufficient_samples</c> — no frames in train, val or (with a test ratio) test;
/// <c>datasets.mixed_source_splits</c> — one source falls into different splits (frames in such sources);
/// <c>datasets.objects_as_negatives</c> — objects of archived or deleted classes and variants that became negatives;
/// <c>datasets.split_collision</c> — frames with identical pixels in different splits (the build is impossible).
/// </summary>
public sealed record DatasetPlanWarning(string Code, int Count);

/// <summary>
/// Result of "Check before build". Classes — indexes of the selected classes; Composition — frames by split and composition;
/// Skipped — skipped frames by reason; Warnings; SufficientSamples; Hash — passed to the build. The plan copies no files and
/// becomes stale if the selected groups, annotation or catalog change (the build answers <c>datasets.plan_stale</c>).
/// </summary>
public sealed record DatasetPlan(
    Guid Id,
    DatasetSpecification Specification,
    int Width,
    int Height,
    IReadOnlyList<DatasetClassEntry> Classes,
    DatasetComposition Composition,
    DatasetPlanSkips Skipped,
    IReadOnlyList<DatasetPlanWarning> Warnings,
    bool SufficientSamples,
    string Hash,
    DateTimeOffset CreatedAt)
{
    public DatasetSplitCounts Counts => Composition.Images;
}

/// <summary>
/// Immutable dataset version (only the name, description and archive change). Specification — build specification, ClassMap —
/// class indexes, Variants — variant names, Composition — composition, ManifestHash — SHA-256 of manifest.json. DeletedAt —
/// only in the passport of a deleted version (<see cref="IDatasetsManager.GetPassportAsync"/>).
/// </summary>
public sealed record DatasetVersion(
    Guid Id,
    string Name,
    string Description,
    long Revision,
    DateTimeOffset? ArchivedAt,
    DatasetIntegrityState IntegrityState,
    DateTimeOffset? VerifiedAt,
    int Width,
    int Height,
    DatasetSpecification Specification,
    IReadOnlyList<DatasetClassEntry> ClassMap,
    IReadOnlyList<DatasetVariantEntry> Variants,
    DatasetComposition Composition,
    string ManifestHash,
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    public DatasetSplitCounts Counts => Composition.Images;
}

/// <summary>YOLO box: class index, class, class variant and center/size as fractions of the frame.</summary>
public sealed record DatasetBox(int Index, Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>Negative: an object that is not trained (the same geometry, without an index).</summary>
public sealed record DatasetNegative(Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>
/// Version sample: Id — ID of the source image; file <c>images/&lt;split&gt;/&lt;id&gt;.jpg|.png</c> (JPEG and PNG without rotation are
/// a hard link to the source file, otherwise a rotated PNG), label <c>labels/&lt;split&gt;/&lt;id&gt;.txt</c>. ImageHash and LabelHash —
/// SHA-256 of the files, PixelHash — hash of the frame pixels. ResourceId — a resource of kind <see cref="ResourceKind"/> (original and thumbnail).
/// </summary>
public sealed record DatasetSample(
    Guid Id,
    DatasetSplit Split,
    ImageFileFormat Format,
    int Width,
    int Height,
    long ByteSize,
    string ImageHash,
    string LabelHash,
    string PixelHash,
    long AnnotationRevision,
    ImageBox? Roi,
    IReadOnlyList<DatasetBox> Boxes,
    IReadOnlyList<DatasetNegative> Negatives,
    Guid GroupId,
    Guid ResourceId)
{
    /// <summary>Material kind in the resource channel (folder files/datasets): <c>new ResourceReference(ResourceKind, sample.ResourceId)</c>.</summary>
    public const string ResourceKind = "datasets";
}

/// <summary>Page of samples of one split by ID; NextCursor — null on the last page.</summary>
public sealed record DatasetSamplePage(IReadOnlyList<DatasetSample> Items, string? NextCursor);

[JsonConverter(typeof(JsonStringEnumConverter<DatasetOperationKind>))]
public enum DatasetOperationKind
{
    [JsonStringEnumMemberName("build")] Build,
    [JsonStringEnumMemberName("verify")] Verify,
    [JsonStringEnumMemberName("export")] Export,
}

/// <summary>
/// Dataset operation — a Jobs job. Build: VersionId = job ID, Specification and PlanId — for "Configure repeat". Verify and
/// Export: VersionId — the checked or exported version; FileName — ZIP name. VersionName — version name (at the time of
/// enqueueing, if the version is deleted); VersionAvailable — the version is ready and not deleted ("Open dataset").
/// </summary>
public sealed record DatasetOperation(
    JobInfo Job,
    DatasetOperationKind Kind,
    Guid VersionId,
    string VersionName,
    bool VersionAvailable,
    Guid? PlanId,
    DatasetSpecification? Specification,
    string? FileName);

/// <summary>Operations of one kind, newest first; Total — all operations of this kind in the project.</summary>
public sealed record DatasetOperationPage(IReadOnlyList<DatasetOperation> Items, long Total);

/// <summary>Build result (in JobInfo.Result): frames in total and done, of them hard links, copies and PNG after conversion.</summary>
public sealed record DatasetBuildResult(Guid VersionId, int Total, int Produced, int Linked, int Copied, int Converted, DatasetSplitCounts Counts);

[JsonConverter(typeof(JsonStringEnumConverter<DatasetFileRole>))]
public enum DatasetFileRole
{
    [JsonStringEnumMemberName("manifest")] Manifest,
    [JsonStringEnumMemberName("dataYaml")] DataYaml,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("label")] Label,
}

/// <summary>
/// Integrity problem: SampleId and Split — the sample (null for manifest.json and data.yaml), Role — the file, Code —
/// <c>datasets.file_missing</c>, <c>datasets.file_changed</c> (content differs) or <c>datasets.file_unreadable</c>.
/// </summary>
public sealed record DatasetIntegrityIssue(Guid? SampleId, DatasetSplit? Split, DatasetFileRole Role, string Code);

/// <summary>
/// Integrity check result (in JobInfo.Result): files in total and checked, IssueCount — all problems, Issues — the first
/// <see cref="MaxIssues"/>; State — verified or corrupt (null until the check finishes); Bytes — bytes read; ElapsedMs.
/// </summary>
public sealed record DatasetVerifyResult(Guid VersionId, int TotalFiles, int CheckedFiles, int IssueCount, IReadOnlyList<DatasetIntegrityIssue> Issues,
    DatasetIntegrityState? State, long Bytes, long ElapsedMs)
{
    public const int MaxIssues = 500;
}

/// <summary>Export result (in JobInfo.Result): file name, images in total and written, archive bytes, duration.</summary>
public sealed record DatasetExportResult(Guid VersionId, string FileName, int TotalImages, int ExportedImages, long Bytes, long ElapsedMs);

/// <summary>
/// What a version deletion removes: samples, history records (build, checks and exports of the version) and blockers (a non-empty list —
/// the deletion is unavailable now). Revision is passed to the deletion. The version passport is kept for models.
/// </summary>
public sealed record DatasetDeletionReview(Guid VersionId, string Name, long Samples, long Operations, IReadOnlyList<DeletionBlocker> Blockers, string Revision);

/// <summary>Rows are deleted; CleanupPending — version files are still being deleted in the background.</summary>
public sealed record DatasetDeletionResult(Guid VersionId, long Samples, long Operations, bool CleanupPending);

/// <summary>
/// Version for training: a YOLO folder (Directory) with data.yaml and manifest.json, classes 0..n−1, frame size and splits.
/// Paths are absolute; for <see cref="IDatasetsManager.MaterializeAsync"/> — inside the job folder.
/// </summary>
public sealed record DatasetTrainingSource(
    Guid VersionId,
    string Name,
    int Width,
    int Height,
    IReadOnlyList<DatasetClassEntry> Classes,
    DatasetSplitCounts Counts,
    string ManifestHash,
    string Directory,
    string DataYamlPath,
    string ManifestPath);

/// <summary>Changed or deleted (Revision = null) version.</summary>
public sealed record DatasetEntityChange(Guid Id, long? Revision);

/// <summary>
/// What changed in the project datasets: Versions — versions with new revisions (appeared, changed name, archive or
/// integrity) or deleted ones. State and progress of build, check and export — the JobChanged event.
/// </summary>
public sealed class DatasetsChangedEventArgs(string projectId, IReadOnlyList<DatasetEntityChange> versions) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<DatasetEntityChange> Versions { get; } = versions;
}

/// <summary>
/// Counters of ready versions: Ready — not archived and not corrupt, Archived — archived (including corrupt), Corrupt — corrupt
/// outside the archive. Versions being built and deleted ones are not counted.
/// </summary>
public sealed record DatasetsSummary(int Ready, int Archived, int Corrupt);
