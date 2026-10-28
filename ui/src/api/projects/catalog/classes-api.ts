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
 * Классы открытого проекта, их варианты (привязки вариантов проекта) и исходные изображения.
 * Начальное состояние — getWorkspace и listAssets; изменения сообщает событие catalog.changed.
 * Импорт: selectAssetFiles (нативный диалог) → importAssets (задача; прогресс и итог — в job.changed).
 * Ошибки: classes.project_not_open, classes.name_invalid, classes.description_invalid, classes.name_duplicate,
 * classes.not_found, classes.revision_conflict, classes.archived (правка архивного класса),
 * classes.variant_archived (цвет архивного варианта класса), classes.order_revision_conflict, classes.order_invalid,
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
    /** Новая полная перестановка всех классов, включая архивные, с classOrderRevision из снимка. */
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
    /** color: "#rrggbb" или null — вернуть цвет по умолчанию. */
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
    /** Показывает нативный диалог выбора PNG/JPEG/WebP/BMP; ответ приходит, когда пользователь закроет диалог. */
    selectAssetFiles(signal?: AbortSignal) {
        const request: SelectClassAssetFilesRequestDto = {};
        return nativeBridge.request("classes.selectAssetFiles", request, isClassAssetFileSelectionResponseDto, signal, null);
    },
    /** Ставит задачу импорта и сразу возвращает её снимок; повтор с тем же operationId вернёт ту же задачу. */
    importAssets(request: ImportClassAssetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("classes.importAssets", request, isJobResponseDto, signal);
    },
};
