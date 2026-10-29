/** The role of an object without a class variant: "Object", "Negative", "Refuse" (the plan is refused with datasets.unassigned_variant). */
export type DatasetUnassignedVariantRole = "target" | "negative" | "reject";

/** The role of an object of a class not chosen for training: "Negative" or "Refuse" (datasets.unselected_class). */
export type DatasetUnselectedClassRole = "negative" | "reject";

/** A dataset split — YOLO folders. In the UI: "Train", "Validation", "Test". */
export type DatasetSplit = "train" | "val" | "test";

/** The kind of a dataset operation: build, integrity check, ZIP export. */
export type DatasetOperationKind = "build" | "verify" | "export";

/**
 * The build specification ("Prepare a build"). groupIds — "Image groups"; classIds — "Classes to train" (indices 0..n−1 are
 * assigned by the host in catalog order, not list order); targetVariantIds / negativeVariantIds — class variants (bindings),
 * usually targets are available variants of the chosen classes, negatives — archived ones (datasetVariantRoles). valRatio and testRatio —
 * fractions 0..1 (not percents): valRatio > 0, testRatio ≥ 0, the sum < 1; seed — an integer 0–4 294 967 295.
 */
export interface DatasetSpecificationDto {
    name: string;
    description: string;
    groupIds: string[];
    classIds: string[];
    targetVariantIds: string[];
    negativeVariantIds: string[];
    unassignedVariantRole: DatasetUnassignedVariantRole;
    unselectedClassRole: DatasetUnselectedClassRole;
    includeReviewedEmpty: boolean;
    allowPartialGeneration: boolean;
    seed: number;
    valRatio: number;
    testRatio: number;
}

/** Finished versions, newest first; includeArchived — "Show dataset archive" (true by default). */
export interface ListDatasetsRequestDto {
    projectId: string;
    includeArchived?: boolean;
}

export interface GetDatasetRequestDto {
    projectId: string;
    versionId: string;
}

/** The version passport for models — after its deletion too (deletedAt). */
export interface GetDatasetPassportRequestDto {
    projectId: string;
    versionId: string;
}

/** "Edit": a name of 1–120, a description up to 4000; revision — the one the user saw. */
export interface UpdateDatasetRequestDto {
    projectId: string;
    versionId: string;
    name: string;
    description: string;
    revision: number;
}

/** "Archive" / "Restore". */
export interface SetDatasetArchivedRequestDto {
    projectId: string;
    versionId: string;
    archived: boolean;
    revision: number;
}

/** "Check before building"; operationId — new for each press (a repeat of the request returns the same plan). */
export interface PlanDatasetRequestDto {
    projectId: string;
    specification: DatasetSpecificationDto;
    operationId: string;
}

export interface GetDatasetPlanRequestDto {
    projectId: string;
    planId: string;
}

/** "Build dataset": plan.id and plan.hash from the plan answer; operationId — new per action. */
export interface StartDatasetBuildRequestDto {
    projectId: string;
    planId: string;
    planHash: string;
    operationId: string;
}

/** "Check integrity". */
export interface StartDatasetVerifyRequestDto {
    projectId: string;
    versionId: string;
    operationId: string;
}

/** "ZIP export", step 1: the native "Save as" dialog with the name "<name>.zip" (overwriting is confirmed in the dialog). */
export interface SelectDatasetExportRequestDto {
    projectId: string;
    versionId: string;
}

/** "ZIP export", step 2: destinationToken from selectExportDestination (one token — one operation). */
export interface StartDatasetExportRequestDto {
    projectId: string;
    versionId: string;
    destinationToken: string;
    operationId: string;
}

/** Operations of a kind, newest first; limit 1–200 (50 by default), before — job.id of the last operation of the previous page. */
export interface ListDatasetOperationsRequestDto {
    projectId: string;
    kind: DatasetOperationKind;
    limit?: number;
    before?: string;
}

/** operationId — job.id of the operation (for a build it is also the version ID). */
export interface GetDatasetOperationRequestDto {
    projectId: string;
    operationId: string;
}

/** "Delete from history": only a finished operation (datasets.operation_active), the version stays. */
export interface DeleteDatasetOperationRequestDto {
    projectId: string;
    operationId: string;
}

/** Samples of a split by ID; limit 1–200 (100 by default), after — nextCursor of the previous page of the same split. */
export interface ListDatasetSamplesRequestDto {
    projectId: string;
    versionId: string;
    split: DatasetSplit;
    limit?: number;
    after?: string;
}

/** A sample by ID (the ID of the source image) — for the viewer when it moves across all samples. */
export interface GetDatasetSampleRequestDto {
    projectId: string;
    versionId: string;
    sampleId: string;
}

export interface ReviewDatasetDeletionRequestDto {
    projectId: string;
    versionId: string;
}

/** "Delete dataset" with revision from reviewDeletion. */
export interface DeleteDatasetRequestDto {
    projectId: string;
    versionId: string;
    revision: string;
}
