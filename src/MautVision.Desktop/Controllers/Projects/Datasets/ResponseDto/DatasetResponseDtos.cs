using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;

/// <summary>Version class: Index — model output (0..n−1), Name — the name at build time ("Saved class order: 0: A, 1: B").</summary>
public sealed record DatasetClassEntryDto(int Index, Guid ClassId, string Name);

/// <summary>Version class variant: name of the project variant at build time (for object and negative captions).</summary>
public sealed record DatasetVariantEntryDto(Guid ClassVariantId, Guid ClassId, string Name);

/// <summary>Frames by split: "Train / Validation / Test".</summary>
public sealed record DatasetSplitCountsDto(int Train, int Val, int Test, int Total);

public sealed record DatasetClassCountDto(int Index, Guid ClassId, string Name, int Boxes);

public sealed record DatasetGroupCountDto(Guid GroupId, string Name, int Images);

/// <summary>Composition: splits, frames without boxes, manual and generated, boxes in total and by class, negatives, source groups.</summary>
public sealed record DatasetCompositionDto(DatasetSplitCountsDto Images, int WithoutBoxes, int FromManual, int FromGenerated, int Boxes, int Negatives,
    DatasetClassCountDto[] BoxesPerClass, DatasetGroupCountDto[] Groups);

/// <summary>Skipped frames by reason: not reviewed, unfinished generation, without objects (empty ones not included), box outside the frame.</summary>
public sealed record DatasetPlanSkipsDto(int Unreviewed, int PartialGeneration, int WithoutTargets, int InvalidGeometry, int Total);

/// <summary>Plan warning: code, count (of frames or objects) and ready text.</summary>
public sealed record DatasetPlanWarningDto(string Code, int Count);

/// <summary>
/// Plan ("Check before build"): Counts — Train/Validation/Test, Skipped — skipped frames by reason, Warnings, SufficientSamples;
/// Buildable — "Build dataset" is available (there are frames and no identical pixels in different splits); Hash — passed to startBuild.
/// </summary>
public sealed record DatasetPlanResponseDto(
    Guid Id,
    DatasetSpecificationDto Specification,
    int Width,
    int Height,
    DatasetClassEntryDto[] Classes,
    DatasetSplitCountsDto Counts,
    DatasetCompositionDto Composition,
    DatasetPlanSkipsDto Skipped,
    DatasetPlanWarningDto[] Warnings,
    bool SufficientSamples,
    bool Buildable,
    string Hash,
    DateTimeOffset CreatedAt);

/// <summary>
/// Dataset version. IntegrityState: verified | corrupt | unverified ("Integrity: verified / broken / not verified");
/// InputSizeMatches — the size equals the project input (otherwise "Size differs from the project input", training is unavailable); Revision —
/// for editing and archiving; DeletedAt — only in the passport of a deleted version.
/// </summary>
public sealed record DatasetVersionResponseDto(
    Guid Id,
    string Name,
    string Description,
    long Revision,
    DateTimeOffset? ArchivedAt,
    string IntegrityState,
    DateTimeOffset? VerifiedAt,
    int Width,
    int Height,
    bool InputSizeMatches,
    DatasetSpecificationDto Specification,
    DatasetClassEntryDto[] ClassMap,
    DatasetVariantEntryDto[] Variants,
    DatasetSplitCountsDto Counts,
    DatasetCompositionDto Composition,
    string ManifestHash,
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt);

/// <summary>YOLO box: index, class, class variant, center and size as fractions of the frame.</summary>
public sealed record DatasetBoxDto(int Index, Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>Negative: the same geometry without an index (drawn dashed in #f6b44c).</summary>
public sealed record DatasetNegativeDto(Guid ClassId, Guid? ClassVariantId, double CenterX, double CenterY, double Width, double Height);

/// <summary>
/// Sample: Id — ID of the source image, Split — train | val | test, Format — jpeg | png (the sample file), Roi — area as fractions
/// from the top-left corner or null; ImageUrl and ThumbnailUrl — resources of the sample file (valid for 10 minutes for this page).
/// </summary>
public sealed record DatasetSampleResponseDto(
    Guid Id,
    string Split,
    string Format,
    int Width,
    int Height,
    long ByteSize,
    string ImageHash,
    string LabelHash,
    string PixelHash,
    long AnnotationRevision,
    ImageBoxResponseDto? Roi,
    DatasetBoxDto[] Boxes,
    DatasetNegativeDto[] Negatives,
    Guid GroupId,
    string ImageUrl,
    string ThumbnailUrl);

public sealed record DatasetSamplePageResponseDto(DatasetSampleResponseDto[] Items, string? NextCursor);

/// <summary>
/// Operation: Job — job snapshot (datasets.build, datasets.verify, datasets.export) with processed/total progress and the result;
/// Kind — build | verify | export; VersionId and VersionName — the version (for a build — the one being created); VersionAvailable — "Open dataset";
/// PlanId and Specification — for a build ("Configure repeat"); FileName — ZIP name for an export.
/// </summary>
public sealed record DatasetOperationResponseDto(
    JobResponseDto Job,
    string Kind,
    Guid VersionId,
    string VersionName,
    bool VersionAvailable,
    Guid? PlanId,
    DatasetSpecificationDto? Specification,
    string? FileName);

/// <summary>Operations of a kind, newest first; Total — "Checks (N)".</summary>
public sealed record DatasetOperationPageResponseDto(DatasetOperationResponseDto[] Items, long Total);

public sealed record DatasetOperationDeletionResponseDto(Guid OperationId);

/// <summary>"Save as" selection: Token — for startExport, FileName — the chosen file name; both null if the window was closed.</summary>
public sealed record DatasetExportSelectionResponseDto(string? Token, string? FileName);

/// <summary>What a deletion removes: samples, history records; non-empty Blockers — the deletion is unavailable (the page builds the dialog lines from codes).</summary>
public sealed record DatasetDeletionReviewResponseDto(Guid VersionId, string Name, long Samples, long Operations, DeletionBlockerResponseDto[] Blockers,
    string Revision);

/// <summary>Deleted; CleanupPending — version files are still being deleted in the background. The version passport is kept for models.</summary>
public sealed record DatasetDeletionResultResponseDto(Guid VersionId, long Samples, long Operations, bool CleanupPending);

/// <summary>Build result (job.result): frames in total and done, of them hard links, copies and PNG after conversion; version splits.</summary>
public sealed record DatasetBuildResultDto(Guid VersionId, int Total, int Produced, int Linked, int Copied, int Converted, DatasetSplitCountsDto Counts);

/// <summary>Integrity problem: sample and split (null for manifest.json and data.yaml), Role — manifest | dataYaml | image | label, code and text.</summary>
public sealed record DatasetIntegrityIssueDto(Guid? SampleId, string? Split, string Role, string Code);

/// <summary>
/// Integrity check result (job.result): "Files checked: CheckedFiles of TotalFiles", IssueCount — "Problems found (n)",
/// Issues — the first 500; State — verified | corrupt (null until finished or when cancelled); Bytes and ElapsedMs — for the speed.
/// </summary>
public sealed record DatasetVerifyResultDto(Guid VersionId, int TotalFiles, int CheckedFiles, int IssueCount, DatasetIntegrityIssueDto[] Issues, string? State,
    long Bytes, long ElapsedMs);

/// <summary>Export result (job.result): "Images exported: ExportedImages of TotalImages", file name, archive size, duration.</summary>
public sealed record DatasetExportResultDto(Guid VersionId, string FileName, int TotalImages, int ExportedImages, long Bytes, long ElapsedMs);

/// <summary>Changed or deleted (Revision = null) version.</summary>
public sealed record DatasetEntityChangeDto(Guid Id, long? Revision);

/// <summary>The datasets.changed event: versions with new revisions (appeared, changed, checked) or deleted ones; reread get / list.</summary>
public sealed record DatasetsChangedEventDto(string ProjectId, DatasetEntityChangeDto[] Versions);
