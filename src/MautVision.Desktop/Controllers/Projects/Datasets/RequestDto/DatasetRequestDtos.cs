namespace MautVision.Desktop.Controllers.Projects.Datasets.RequestDto;

/// <summary>
/// Build specification from the form. UnassignedVariantRole: target | negative | reject; UnselectedClassRole: negative | reject;
/// Seed — 0–4 294 967 295; ValRatio &gt; 0, TestRatio ≥ 0, sum &lt; 1 (fractions, not percent). TargetVariantIds and NegativeVariantIds —
/// class variants (bindings); usually the targets are active variants of the selected classes and the negatives are archived ones.
/// </summary>
public sealed record DatasetSpecificationDto(
    string Name,
    string Description,
    Guid[] GroupIds,
    Guid[] ClassIds,
    Guid[] TargetVariantIds,
    Guid[] NegativeVariantIds,
    string UnassignedVariantRole,
    string UnselectedClassRole,
    bool IncludeReviewedEmpty,
    bool AllowPartialGeneration,
    long Seed,
    double ValRatio,
    double TestRatio);

/// <summary>Ready versions, newest first; IncludeArchived — "Show dataset archive" (default yes).</summary>
public sealed record ListDatasetsRequestDto(string ProjectId, bool? IncludeArchived = null);

public sealed record GetDatasetRequestDto(string ProjectId, Guid VersionId);

/// <summary>Version passport (also of a deleted one) for models.</summary>
public sealed record GetDatasetPassportRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Edit": name and description with the revision the user saw.</summary>
public sealed record UpdateDatasetRequestDto(string ProjectId, Guid VersionId, string Name, string Description, long Revision);

/// <summary>"Archive" / "Restore".</summary>
public sealed record SetDatasetArchivedRequestDto(string ProjectId, Guid VersionId, bool Archived, long Revision);

/// <summary>"Check before build": OperationId — new per click (a repeated request returns the same plan).</summary>
public sealed record PlanDatasetRequestDto(string ProjectId, DatasetSpecificationDto Specification, string OperationId);

public sealed record GetDatasetPlanRequestDto(string ProjectId, Guid PlanId);

/// <summary>"Build dataset": the plan and its hash from the plan answer; OperationId — new per action (a repeat returns the same build).</summary>
public sealed record StartDatasetBuildRequestDto(string ProjectId, Guid PlanId, string PlanHash, string OperationId);

/// <summary>"Check integrity".</summary>
public sealed record StartDatasetVerifyRequestDto(string ProjectId, Guid VersionId, string OperationId);

/// <summary>"Export ZIP", step 1: native "Save as" with the name "&lt;name&gt;.zip"; the answer is a selection token.</summary>
public sealed record SelectDatasetExportRequestDto(string ProjectId, Guid VersionId);

/// <summary>"Export ZIP", step 2: DestinationToken — from selectExportDestination; OperationId — new per action.</summary>
public sealed record StartDatasetExportRequestDto(string ProjectId, Guid VersionId, string DestinationToken, string OperationId);

/// <summary>Operations of kind build | verify | export, newest first; Limit 1–200 (default 50), Before — ID of the last one of the previous page.</summary>
public sealed record ListDatasetOperationsRequestDto(string ProjectId, string Kind, int? Limit = null, Guid? Before = null);

public sealed record GetDatasetOperationRequestDto(string ProjectId, Guid OperationId);

/// <summary>"Remove from history" of a finished operation.</summary>
public sealed record DeleteDatasetOperationRequestDto(string ProjectId, Guid OperationId);

/// <summary>Samples of split train | val | test by ID; Limit 1–200 (default 100), After — NextCursor of the previous page.</summary>
public sealed record ListDatasetSamplesRequestDto(string ProjectId, Guid VersionId, string Split, int? Limit = null, string? After = null);

public sealed record GetDatasetSampleRequestDto(string ProjectId, Guid VersionId, Guid SampleId);

public sealed record ReviewDatasetDeletionRequestDto(string ProjectId, Guid VersionId);

/// <summary>Deletion with the Revision from reviewDeletion.</summary>
public sealed record DeleteDatasetRequestDto(string ProjectId, Guid VersionId, string Revision);
