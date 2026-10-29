import { isJobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { nativeBridge } from "../../transport/native-bridge";
import type { AddClassVariantRequestDto } from "./request-dto/add-class-variant-request-dto";
import type { CreateClassRequestDto } from "./request-dto/create-class-request-dto";
import type { DeleteClassAssetRequestDto } from "./request-dto/delete-class-asset-request-dto";
import type { DeleteClassRequestDto } from "./request-dto/delete-class-request-dto";
import type { DeleteClassVariantRequestDto } from "./request-dto/delete-class-variant-request-dto";
import type { GetCatalogWorkspaceRequestDto } from "./request-dto/get-catalog-workspace-request-dto";
import type { ImportClassAssetsRequestDto } from "./request-dto/import-class-assets-request-dto";
import type { ListClassAssetsRequestDto } from "./request-dto/list-class-assets-request-dto";
import type { ReorderClassesRequestDto } from "./request-dto/reorder-classes-request-dto";
import type { ReviewClassAssetDeletionRequestDto } from "./request-dto/review-class-asset-deletion-request-dto";
import type { ReviewClassDeletionRequestDto } from "./request-dto/review-class-deletion-request-dto";
import type { ReviewClassVariantDeletionRequestDto } from "./request-dto/review-class-variant-deletion-request-dto";
import type { SelectClassAssetFilesRequestDto } from "./request-dto/select-class-asset-files-request-dto";
import type { SetClassArchivedRequestDto } from "./request-dto/set-class-archived-request-dto";
import type { SetClassVariantArchivedRequestDto } from "./request-dto/set-class-variant-archived-request-dto";
import type { SetClassVariantColorRequestDto } from "./request-dto/set-class-variant-color-request-dto";
import type { UpdateClassRequestDto } from "./request-dto/update-class-request-dto";
import { isCatalogWorkspaceResponseDto } from "./response-dto/catalog-workspace-response-dto";
import { isClassAssetFileSelectionResponseDto, isClassAssetPageResponseDto } from "./response-dto/class-asset-response-dto";
import { isClassOrderResponseDto, isClassResponseDto } from "./response-dto/class-response-dto";
import { isClassVariantResponseDto } from "./response-dto/class-variant-response-dto";
import { isDeletionResultResponseDto, isDeletionReviewResponseDto } from "./response-dto/deletion-review-response-dto";

/**
 * Classes of the open project, their variants (bindings of project variants) and source images.
 * The initial state comes from getWorkspace and listAssets; changes come with the catalog.changed event.
 * Import: selectAssetFiles (a native dialog), then importAssets (a job; progress and result in job.changed).
 * Errors: classes.project_not_open, classes.name_invalid, classes.description_invalid, classes.name_duplicate,
 * classes.not_found, classes.revision_conflict, classes.archived (editing an archived class),
 * classes.variant_archived (the color of an archived class variant), classes.order_revision_conflict, classes.order_invalid,
 * classes.class_unavailable, classes.variant_unavailable, classes.project_variant_unavailable,
 * classes.duplicate_variant_binding, classes.color_invalid, classes.deletion_revision_conflict,
 * classes.deletion_blocked, classes.selection_expired, jobs.project_not_open.
 */
export const classesApi = {
    get available(): boolean { return nativeBridge.available; },
    getWorkspace(request: GetCatalogWorkspaceRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.getWorkspace", request, isCatalogWorkspaceResponseDto, signal);
    },
    create(request: CreateClassRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.create", request, isClassResponseDto, signal);
    },
    update(request: UpdateClassRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.update", request, isClassResponseDto, signal);
    },
    setArchived(request: SetClassArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.setArchived", request, isClassResponseDto, signal);
    },
    /** A new full order of all classes, archived included, with classOrderRevision from the snapshot. */
    reorder(request: ReorderClassesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.reorder", request, isClassOrderResponseDto, signal);
    },
    reviewDeletion(request: ReviewClassDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.reviewDeletion", request, isDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteClassRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.delete", request, isDeletionResultResponseDto, signal);
    },
    addVariant(request: AddClassVariantRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.addVariant", request, isClassVariantResponseDto, signal);
    },
    /** color: "#rrggbb", or null to return to the default color. */
    setVariantColor(request: SetClassVariantColorRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.setVariantColor", request, isClassVariantResponseDto, signal);
    },
    setVariantArchived(request: SetClassVariantArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.setVariantArchived", request, isClassVariantResponseDto, signal);
    },
    reviewVariantDeletion(request: ReviewClassVariantDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.reviewVariantDeletion", request, isDeletionReviewResponseDto, signal);
    },
    deleteVariant(request: DeleteClassVariantRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.deleteVariant", request, isDeletionResultResponseDto, signal);
    },
    listAssets(request: ListClassAssetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.listAssets", request, isClassAssetPageResponseDto, signal);
    },
    reviewAssetDeletion(request: ReviewClassAssetDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.reviewAssetDeletion", request, isDeletionReviewResponseDto, signal);
    },
    deleteAsset(request: DeleteClassAssetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.deleteAsset", request, isDeletionResultResponseDto, signal);
    },
    /** Shows the native dialog for choosing PNG/JPEG/WebP/BMP; the answer comes when the user closes the dialog. */
    selectAssetFiles(signal?: AbortSignal) {
        const request: SelectClassAssetFilesRequestDto = {};
        return nativeBridge.request("classes.selectAssetFiles", request, isClassAssetFileSelectionResponseDto, signal, null);
    },
    /** Starts an import job and returns its snapshot at once; a repeat with the same operationId returns the same job. */
    importAssets(request: ImportClassAssetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.importAssets", request, isJobResponseDto, signal);
    },
};
