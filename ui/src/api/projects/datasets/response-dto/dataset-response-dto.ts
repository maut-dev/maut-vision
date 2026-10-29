import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";
import { isDeletionBlockerResponseDto, type DeletionBlockerResponseDto } from "../../catalog/response-dto/deletion-review-response-dto";
import { isImageBoxDto, type ImageBoxDto } from "../../images/response-dto/image-annotation-response-dto";
import type { DatasetSpecificationDto, DatasetSplit } from "../request-dto/dataset-request-dto";

export type DatasetIntegrityState = "verified" | "corrupt" | "unverified";

/** A class of the version: index — the model output (0..n−1), name — the name at build time ("Saved class order: 0: A, 1: B"). */
export interface DatasetClassEntryDto {
    index: number;
    classId: string;
    name: string;
}

/** A class variant of the version with the name of the project variant at build time — the label of objects and negatives instead of the id. */
export interface DatasetVariantEntryDto {
    classVariantId: string;
    classId: string;
    name: string;
}

/** "Finished frames · Train / Validation / Test". */
export interface DatasetSplitCountsDto {
    train: number;
    val: number;
    test: number;
    total: number;
}

export interface DatasetClassCountDto {
    index: number;
    classId: string;
    name: string;
    boxes: number;
}

export interface DatasetGroupCountDto {
    groupId: string;
    name: string;
    images: number;
}

/** Composition: splits, without boxes, manual and generated, boxes in total and by class (all classes, with zero too), negatives, source groups. */
export interface DatasetCompositionDto {
    images: DatasetSplitCountsDto;
    withoutBoxes: number;
    fromManual: number;
    fromGenerated: number;
    boxes: number;
    negatives: number;
    boxesPerClass: DatasetClassCountDto[];
    groups: DatasetGroupCountDto[];
}

/** Skipped by reason: not reviewed, unfinished generation, without objects (empty ones not included), box or ROI outside the frame. */
export interface DatasetPlanSkipsDto {
    unreviewed: number;
    partialGeneration: number;
    withoutTargets: number;
    invalidGeometry: number;
    total: number;
}

/**
 * A plan warning as a code. code: datasets.insufficient_samples (count 0), datasets.mixed_source_splits (frames),
 * datasets.objects_as_negatives (objects), datasets.split_collision (frames; the build is impossible).
 */
export interface DatasetPlanWarningDto {
    code: string;
    count: number;
}

/**
 * The plan of "Check before building": counts — "Train / Validation / Test", skipped — "skipped N" with reasons, warnings,
 * sufficientSamples; buildable — the button "Build dataset" is available (there are frames, no identical pixels in different splits); hash —
 * is passed to startBuild. The plan goes stale when groups, annotation or the catalog change: startBuild answers datasets.plan_stale.
 */
export interface DatasetPlanResponseDto {
    id: string;
    specification: DatasetSpecificationDto;
    width: number;
    height: number;
    classes: DatasetClassEntryDto[];
    counts: DatasetSplitCountsDto;
    composition: DatasetCompositionDto;
    skipped: DatasetPlanSkipsDto;
    warnings: DatasetPlanWarningDto[];
    sufficientSamples: boolean;
    buildable: boolean;
    hash: string;
    createdAt: string;
}

/**
 * A dataset version. integrityState — "Integrity: verified / corrupt / not verified"; inputSizeMatches — otherwise "The size differs
 * from the project input" (training is unavailable); archivedAt — "name · Archived"; createdAt — "Built …"; specification.groupIds —
 * "Source groups …", specification.seed — "Seed …"; revision — for update and setArchived; deletedAt — only in a passport.
 */
export interface DatasetVersionResponseDto {
    id: string;
    name: string;
    description: string;
    revision: number;
    archivedAt: string | null;
    integrityState: DatasetIntegrityState;
    verifiedAt: string | null;
    width: number;
    height: number;
    inputSizeMatches: boolean;
    specification: DatasetSpecificationDto;
    classMap: DatasetClassEntryDto[];
    variants: DatasetVariantEntryDto[];
    counts: DatasetSplitCountsDto;
    composition: DatasetCompositionDto;
    manifestHash: string;
    planId: string;
    createdAt: string;
    updatedAt: string;
    deletedAt: string | null;
}

/** A YOLO box in frame fractions: the center (centerX, centerY) and the size; index — the version class (drawn #5fb3ff). */
export interface DatasetBoxDto {
    index: number;
    classId: string;
    classVariantId: string | null;
    centerX: number;
    centerY: number;
    width: number;
    height: number;
}

/** A negative: the same geometry without an index (drawn #f6b44c dashed). */
export interface DatasetNegativeDto {
    classId: string;
    classVariantId: string | null;
    centerX: number;
    centerY: number;
    width: number;
    height: number;
}

/**
 * A sample: id — the ID of the source image; "W × H · n objects · m negatives" — width, height, boxes.length, negatives.length; roi —
 * the area (#69d3a2) in fractions from the top left corner or null; imageUrl and thumbnailUrl — the sample file (an already oriented frame).
 */
export interface DatasetSampleResponseDto {
    id: string;
    split: DatasetSplit;
    format: "jpeg" | "png";
    width: number;
    height: number;
    byteSize: number;
    imageHash: string;
    labelHash: string;
    pixelHash: string;
    annotationRevision: number;
    roi: ImageBoxDto | null;
    boxes: DatasetBoxDto[];
    negatives: DatasetNegativeDto[];
    groupId: string;
    imageUrl: ResourceUrl;
    thumbnailUrl: ResourceUrl;
}

/** A page of samples of a split; nextCursor — null on the last page. */
export interface DatasetSamplePageResponseDto {
    items: DatasetSampleResponseDto[];
    nextCursor: string | null;
}

/** "Save as": token and fileName — both null when the dialog was closed without a choice. */
export interface DatasetExportSelectionResponseDto {
    token: string | null;
    fileName: string | null;
}

/** What deletion removes: samples, history records; a non-empty blockers means deletion is unavailable (lines — blockerText). */
export interface DatasetDeletionReviewResponseDto {
    versionId: string;
    name: string;
    samples: number;
    operations: number;
    blockers: DeletionBlockerResponseDto[];
    revision: string;
}

/** Deleted; cleanupPending — files are still being deleted in the background. The version passport stays for models. */
export interface DatasetDeletionResultResponseDto {
    versionId: string;
    samples: number;
    operations: number;
    cleanupPending: boolean;
}

const isFiniteNumber = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

const isText = (value: unknown): value is string => typeof value === "string";

const splits: readonly string[] = ["train", "val", "test"] satisfies DatasetSplit[];

export const isDatasetSplit = (value: unknown): value is DatasetSplit => typeof value === "string" && splits.includes(value);

export function isDatasetSpecificationDto(value: unknown): value is DatasetSpecificationDto {
    return isRecord(value) && isText(value.name) && isText(value.description)
        && [value.groupIds, value.classIds, value.targetVariantIds, value.negativeVariantIds].every(ids => Array.isArray(ids) && ids.every(isId))
        && ["target", "negative", "reject"].includes(value.unassignedVariantRole as string) && ["negative", "reject"].includes(value.unselectedClassRole as string)
        && typeof value.includeReviewedEmpty === "boolean" && typeof value.allowPartialGeneration === "boolean"
        && isCount(value.seed) && isFiniteNumber(value.valRatio) && isFiniteNumber(value.testRatio);
}

function isClassEntry(value: unknown): value is DatasetClassEntryDto {
    return isRecord(value) && isCount(value.index) && isId(value.classId) && isText(value.name);
}

export function isDatasetSplitCountsDto(value: unknown): value is DatasetSplitCountsDto {
    return isRecord(value) && isCount(value.train) && isCount(value.val) && isCount(value.test) && isCount(value.total);
}

function isCompositionDto(value: unknown): value is DatasetCompositionDto {
    return isRecord(value) && isDatasetSplitCountsDto(value.images) && isCount(value.withoutBoxes) && isCount(value.fromManual)
        && isCount(value.fromGenerated) && isCount(value.boxes) && isCount(value.negatives)
        && Array.isArray(value.boxesPerClass) && value.boxesPerClass.every(item => isRecord(item) && isCount(item.index) && isId(item.classId)
            && isText(item.name) && isCount(item.boxes))
        && Array.isArray(value.groups) && value.groups.every(item => isRecord(item) && isId(item.groupId) && isText(item.name) && isCount(item.images));
}

export function isDatasetPlanResponseDto(value: unknown): value is DatasetPlanResponseDto {
    return isRecord(value) && isId(value.id) && isDatasetSpecificationDto(value.specification) && isCount(value.width) && isCount(value.height)
        && Array.isArray(value.classes) && value.classes.every(isClassEntry) && isDatasetSplitCountsDto(value.counts) && isCompositionDto(value.composition)
        && isRecord(value.skipped) && isCount(value.skipped.unreviewed) && isCount(value.skipped.partialGeneration) && isCount(value.skipped.withoutTargets)
        && isCount(value.skipped.invalidGeometry) && isCount(value.skipped.total)
        && Array.isArray(value.warnings) && value.warnings.every(item => isRecord(item) && isText(item.code) && isCount(item.count))
        && typeof value.sufficientSamples === "boolean" && typeof value.buildable === "boolean" && isText(value.hash) && value.hash.length > 0
        && isDate(value.createdAt);
}

export function isDatasetVersionResponseDto(value: unknown): value is DatasetVersionResponseDto {
    return isRecord(value) && isId(value.id) && isText(value.name) && isText(value.description) && isRevision(value.revision)
        && isDateOrNull(value.archivedAt) && ["verified", "corrupt", "unverified"].includes(value.integrityState as string)
        && isDateOrNull(value.verifiedAt) && isCount(value.width) && isCount(value.height) && typeof value.inputSizeMatches === "boolean"
        && isDatasetSpecificationDto(value.specification) && Array.isArray(value.classMap) && value.classMap.every(isClassEntry)
        && Array.isArray(value.variants) && value.variants.every(item => isRecord(item) && isId(item.classVariantId) && isId(item.classId) && isText(item.name))
        && isDatasetSplitCountsDto(value.counts) && isCompositionDto(value.composition) && isText(value.manifestHash) && isId(value.planId)
        && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.deletedAt);
}

export function isDatasetVersionList(value: unknown): value is DatasetVersionResponseDto[] {
    return Array.isArray(value) && value.every(isDatasetVersionResponseDto);
}

function isGeometry(value: Record<string, unknown>): boolean {
    return isFiniteNumber(value.centerX) && isFiniteNumber(value.centerY) && isFiniteNumber(value.width) && isFiniteNumber(value.height)
        && isId(value.classId) && (value.classVariantId === null || isId(value.classVariantId));
}

export function isDatasetSampleResponseDto(value: unknown): value is DatasetSampleResponseDto {
    return isRecord(value) && isId(value.id) && isDatasetSplit(value.split) && (value.format === "jpeg" || value.format === "png")
        && isCount(value.width) && isCount(value.height) && isCount(value.byteSize) && isText(value.imageHash) && isText(value.labelHash)
        && isText(value.pixelHash) && isRevision(value.annotationRevision) && (value.roi === null || isImageBoxDto(value.roi))
        && Array.isArray(value.boxes) && value.boxes.every(item => isRecord(item) && isCount(item.index) && isGeometry(item))
        && Array.isArray(value.negatives) && value.negatives.every(item => isRecord(item) && isGeometry(item))
        && isId(value.groupId) && isResourceUrl(value.imageUrl) && isResourceUrl(value.thumbnailUrl);
}

export function isDatasetSamplePageResponseDto(value: unknown): value is DatasetSamplePageResponseDto {
    return isRecord(value) && Array.isArray(value.items) && value.items.every(isDatasetSampleResponseDto)
        && (value.nextCursor === null || (isText(value.nextCursor) && value.nextCursor.length > 0));
}

export function isDatasetExportSelectionResponseDto(value: unknown): value is DatasetExportSelectionResponseDto {
    return isRecord(value) && ((value.token === null && value.fileName === null) || (isText(value.token) && isText(value.fileName)));
}

export function isDatasetDeletionReviewResponseDto(value: unknown): value is DatasetDeletionReviewResponseDto {
    return isRecord(value) && isId(value.versionId) && isText(value.name) && isCount(value.samples) && isCount(value.operations)
        && Array.isArray(value.blockers) && value.blockers.every(isDeletionBlockerResponseDto) && isText(value.revision) && value.revision.length > 0;
}

export function isDatasetDeletionResultResponseDto(value: unknown): value is DatasetDeletionResultResponseDto {
    return isRecord(value) && isId(value.versionId) && isCount(value.samples) && isCount(value.operations) && typeof value.cleanupPending === "boolean";
}
