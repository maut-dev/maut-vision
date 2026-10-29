import { nativeBridge } from "../../transport/native-bridge";
import type {
    DeleteDatasetOperationRequestDto, DeleteDatasetRequestDto, GetDatasetOperationRequestDto, GetDatasetPassportRequestDto, GetDatasetPlanRequestDto,
    GetDatasetRequestDto, GetDatasetSampleRequestDto, ListDatasetOperationsRequestDto, ListDatasetSamplesRequestDto, ListDatasetsRequestDto,
    PlanDatasetRequestDto, ReviewDatasetDeletionRequestDto, SelectDatasetExportRequestDto, SetDatasetArchivedRequestDto, StartDatasetBuildRequestDto,
    StartDatasetExportRequestDto, StartDatasetVerifyRequestDto, UpdateDatasetRequestDto,
} from "./request-dto/dataset-request-dto";
import {
    isDatasetOperationDeletionResponseDto, isDatasetOperationPageResponseDto, isDatasetOperationResponseDto,
} from "./response-dto/dataset-operation-response-dto";
import {
    isDatasetDeletionResultResponseDto, isDatasetDeletionReviewResponseDto, isDatasetExportSelectionResponseDto, isDatasetPlanResponseDto,
    isDatasetSamplePageResponseDto, isDatasetSampleResponseDto, isDatasetVersionList, isDatasetVersionResponseDto,
} from "./response-dto/dataset-response-dto";

/** The plan reads all images of the chosen groups (up to 120 000 frames — several seconds); the build start checks the sources. */
const planTimeoutMs = 60_000;
/** "Save as" waits for the user. */
const dialogTimeoutMs = 600_000;

/**
 * Datasets of the open project: immutable YOLO versions, plans and operations.
 *
 * Versions: list (includeArchived — "Show dataset archive"), get, update / setArchived with revision, getPassport — the passport of a deleted
 * version too. Samples: listSamples by split (train | val | test) with a cursor, getSample — for the viewer; imageUrl and thumbnailUrl —
 * the resource channel. Deletion: reviewDeletion (blockers as codes) → delete with revision.
 *
 * Build: plan ("Check before building", the specification from the form; dataset-rules — datasetVariantRoles, ratiosFromPercents,
 * specificationFieldError) → startBuild(plan.id, plan.hash) returns the operation at once. Check: startVerify. Export:
 * selectExportDestination (native "Save as"; token null — the dialog was closed) → startExport with the token. Progress, state and result
 * of all operations — job.changed (kind datasets.build / datasets.verify / datasets.export; parseDatasetBuildResult, parseDatasetVerifyResult,
 * parseDatasetExportResult); "Cancel" — jobsApi.cancel(projectId, operation.job.id). History — listOperations(kind), getOperation,
 * deleteOperation ("Delete from history"). "Set up a repeat" — operation.specification of a build into the form.
 *
 * Events: datasets.changed (subscribeDatasetsChanged) — versions with revisions (null — deleted); job.changed — operations.
 *
 * Errors (codes; texts in the errors catalog): datasets.project_not_open, datasets.unavailable, datasets.revision_conflict,
 * datasets.archived, datasets.corrupt, datasets.name_invalid, datasets.description_invalid, datasets.selection_invalid,
 * datasets.variants_invalid, datasets.roles_invalid, datasets.split_invalid, datasets.seed_invalid (details.field — the form field),
 * datasets.request_invalid, datasets.cursor_invalid, datasets.group_unavailable (details.groupId), datasets.class_unavailable
 * (details.classId), datasets.unselected_class, datasets.unassigned_variant, datasets.variant_role_missing, datasets.plan_stale,
 * datasets.input_size_mismatch, datasets.empty, datasets.split_collision, datasets.export_destination, datasets.export_selection_expired,
 * datasets.operation_active, datasets.deletion_blocked, datasets.deletion_revision_conflict; job errors (job.error):
 * datasets.plan_stale, datasets.source_missing, datasets.source_changed, datasets.split_collision, datasets.build_failed,
 * datasets.export_failed, datasets.corrupt, datasets.disk_full, datasets.out_of_memory, jobs.interrupted.
 */
export const datasetsApi = {
    get available(): boolean { return nativeBridge.available; },
    list(request: ListDatasetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.list", request, isDatasetVersionList, signal);
    },
    get(request: GetDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.get", request, isDatasetVersionResponseDto, signal);
    },
    /** The version passport for models, of a deleted version too (deletedAt). */
    getPassport(request: GetDatasetPassportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getPassport", request, isDatasetVersionResponseDto, signal);
    },
    /** "Edit": the name and the description; an archived version — datasets.archived. */
    update(request: UpdateDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.update", request, isDatasetVersionResponseDto, signal);
    },
    /** "Archive" / "Restore". */
    setArchived(request: SetDatasetArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.setArchived", request, isDatasetVersionResponseDto, signal);
    },
    /** "Check before building": counters, skip reasons, warnings and the hash; copies no files. */
    plan(request: PlanDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.plan", request, isDatasetPlanResponseDto, signal, planTimeoutMs);
    },
    getPlan(request: GetDatasetPlanRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getPlan", request, isDatasetPlanResponseDto, signal);
    },
    /** "Build dataset": checks whether the plan is stale and queues the build; a repeat with the same operationId returns the same build. */
    startBuild(request: StartDatasetBuildRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startBuild", request, isDatasetOperationResponseDto, signal, planTimeoutMs);
    },
    /** "Check integrity". */
    startVerify(request: StartDatasetVerifyRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startVerify", request, isDatasetOperationResponseDto, signal);
    },
    /** "ZIP export", step 1: the "Save as" dialog with "<name>.zip". */
    selectExportDestination(request: SelectDatasetExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.selectExportDestination", request, isDatasetExportSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** "ZIP export", step 2: queues the export to the chosen file (overwriting was confirmed in the dialog). */
    startExport(request: StartDatasetExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startExport", request, isDatasetOperationResponseDto, signal);
    },
    listOperations(request: ListDatasetOperationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.listOperations", request, isDatasetOperationPageResponseDto, signal);
    },
    getOperation(request: GetDatasetOperationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getOperation", request, isDatasetOperationResponseDto, signal);
    },
    /** "Delete from history": only a finished operation; the finished version stays. */
    deleteOperation(request: DeleteDatasetOperationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.deleteOperation", request, isDatasetOperationDeletionResponseDto, signal);
    },
    listSamples(request: ListDatasetSamplesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.listSamples", request, isDatasetSamplePageResponseDto, signal);
    },
    getSample(request: GetDatasetSampleRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getSample", request, isDatasetSampleResponseDto, signal);
    },
    reviewDeletion(request: ReviewDatasetDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.reviewDeletion", request, isDatasetDeletionReviewResponseDto, signal);
    },
    /** Deletes a version; files are deleted in the background, the passport stays. */
    delete(request: DeleteDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.delete", request, isDatasetDeletionResultResponseDto, signal);
    },
};
