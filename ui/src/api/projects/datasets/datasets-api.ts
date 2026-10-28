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

/** План читает все изображения выбранных групп (до 120 000 кадров — несколько секунд); старт сборки сверяет источники. */
const planTimeoutMs = 60_000;
/** «Сохранить как» ждёт пользователя. */
const dialogTimeoutMs = 600_000;

/**
 * Датасеты открытого проекта: неизменяемые версии YOLO, планы и операции.
 *
 * Версии: list (includeArchived — «Показать архив датасетов»), get, update / setArchived с revision, getPassport — паспорт и удалённой
 * версии. Образцы: listSamples по части (train | val | test) с курсором, getSample — для просмотрщика; imageUrl и thumbnailUrl —
 * канал ресурсов. Удаление: reviewDeletion (blockers с текстами) → delete с revision.
 *
 * Сборка: plan («Проверить перед сборкой», specification из формы; dataset-rules — datasetVariantRoles, ratiosFromPercents,
 * specificationFieldError) → startBuild(plan.id, plan.hash) возвращает операцию сразу. Проверка: startVerify. Экспорт:
 * selectExportDestination (нативное «Сохранить как»; token null — окно закрыто) → startExport с token. Прогресс, состояние и итог
 * всех операций — job.changed (kind datasets.build / datasets.verify / datasets.export; parseDatasetBuildResult, parseDatasetVerifyResult,
 * parseDatasetExportResult); «Отменить» — jobsApi.cancel(projectId, operation.job.id). История — listOperations(kind), getOperation,
 * deleteOperation («Удалить из истории»). «Настроить повтор» — operation.specification сборки в форму.
 *
 * События: datasets.changed (subscribeDatasetsChanged) — версии с ревизиями (null — удалена); job.changed — операции.
 *
 * Ошибки (тексты приходят готовыми в error.message): datasets.project_not_open, datasets.unavailable, datasets.revision_conflict,
 * datasets.archived, datasets.corrupt, datasets.name_invalid, datasets.description_invalid, datasets.selection_invalid,
 * datasets.variants_invalid, datasets.roles_invalid, datasets.split_invalid, datasets.seed_invalid (details.field — поле формы),
 * datasets.request_invalid, datasets.cursor_invalid, datasets.group_unavailable (details.groupId), datasets.class_unavailable
 * (details.classId), datasets.unselected_class, datasets.unassigned_variant, datasets.variant_role_missing, datasets.plan_stale,
 * datasets.input_size_mismatch, datasets.empty, datasets.split_collision, datasets.export_destination, datasets.export_selection_expired,
 * datasets.operation_active, datasets.deletion_blocked, datasets.deletion_revision_conflict; ошибки задач (job.error):
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
    /** Паспорт версии для моделей, в том числе удалённой (deletedAt). */
    getPassport(request: GetDatasetPassportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getPassport", request, isDatasetVersionResponseDto, signal);
    },
    /** «Изменить»: название и описание; архивная версия — datasets.archived. */
    update(request: UpdateDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.update", request, isDatasetVersionResponseDto, signal);
    },
    /** «В архив» / «Восстановить». */
    setArchived(request: SetDatasetArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.setArchived", request, isDatasetVersionResponseDto, signal);
    },
    /** «Проверить перед сборкой»: счётчики, причины пропуска, предупреждения и hash; файлов не копирует. */
    plan(request: PlanDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.plan", request, isDatasetPlanResponseDto, signal, planTimeoutMs);
    },
    getPlan(request: GetDatasetPlanRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getPlan", request, isDatasetPlanResponseDto, signal);
    },
    /** «Собрать датасет»: проверяет, не устарел ли план, и ставит сборку; повтор с тем же operationId вернёт ту же сборку. */
    startBuild(request: StartDatasetBuildRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startBuild", request, isDatasetOperationResponseDto, signal, planTimeoutMs);
    },
    /** «Проверить целостность». */
    startVerify(request: StartDatasetVerifyRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startVerify", request, isDatasetOperationResponseDto, signal);
    },
    /** «Экспорт ZIP», шаг 1: окно «Сохранить как» с «<название>.zip». */
    selectExportDestination(request: SelectDatasetExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.selectExportDestination", request, isDatasetExportSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** «Экспорт ZIP», шаг 2: ставит экспорт в выбранный файл (перезапись подтверждена в окне). */
    startExport(request: StartDatasetExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.startExport", request, isDatasetOperationResponseDto, signal);
    },
    listOperations(request: ListDatasetOperationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.listOperations", request, isDatasetOperationPageResponseDto, signal);
    },
    getOperation(request: GetDatasetOperationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.getOperation", request, isDatasetOperationResponseDto, signal);
    },
    /** «Удалить из истории»: только завершённая операция; готовая версия остаётся. */
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
    /** Удаляет версию; файлы удаляются в фоне, паспорт остаётся. */
    delete(request: DeleteDatasetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("datasets.delete", request, isDatasetDeletionResultResponseDto, signal);
    },
};
