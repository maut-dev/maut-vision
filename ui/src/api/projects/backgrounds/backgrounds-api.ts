import { isJobResponseDto, type JobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { nativeBridge } from "../../transport/native-bridge";
import { isVideoSelectionResponseDto } from "../images/response-dto/video-selection-response-dto";
import type { CreateBackgroundSetRequestDto } from "./request-dto/create-background-set-request-dto";
import type { DeleteBackgroundSetRequestDto } from "./request-dto/delete-background-set-request-dto";
import type { DeleteBackgroundsRequestDto } from "./request-dto/delete-backgrounds-request-dto";
import type { GetBackgroundRequestDto } from "./request-dto/get-background-request-dto";
import type { GetBackgroundSetRequestDto } from "./request-dto/get-background-set-request-dto";
import type { ImportBackgroundsRequestDto } from "./request-dto/import-backgrounds-request-dto";
import type { ListBackgroundRunsRequestDto } from "./request-dto/list-background-runs-request-dto";
import type { ListBackgroundSetsRequestDto } from "./request-dto/list-background-sets-request-dto";
import type { ListBackgroundsRequestDto } from "./request-dto/list-backgrounds-request-dto";
import type { PreviewBackgroundSourceRequestDto } from "./request-dto/preview-background-source-request-dto";
import type { PreviewBackgroundVideoFrameRequestDto } from "./request-dto/preview-background-video-frame-request-dto";
import type { ReviewBackgroundSetDeletionRequestDto } from "./request-dto/review-background-set-deletion-request-dto";
import type { ReviewBackgroundsDeletionRequestDto } from "./request-dto/review-backgrounds-deletion-request-dto";
import type { SelectBackgroundFilesRequestDto } from "./request-dto/select-background-files-request-dto";
import type { SelectBackgroundVideoRequestDto } from "./request-dto/select-background-video-request-dto";
import type { SetBackgroundSetArchivedRequestDto } from "./request-dto/set-background-set-archived-request-dto";
import type { SetBackgroundsRemovedRequestDto } from "./request-dto/set-backgrounds-removed-request-dto";
import type { UpdateBackgroundSetRequestDto } from "./request-dto/update-background-set-request-dto";
import { isBackgroundDeletionResultResponseDto, isBackgroundDeletionReviewResponseDto } from "./response-dto/background-deletion-response-dto";
import { isBackgroundImportPreviewResponseDto } from "./response-dto/background-import-preview-response-dto";
import { isBackgroundList, isBackgroundPageResponseDto, isBackgroundResponseDto } from "./response-dto/background-response-dto";
import { isBackgroundFileSelectionResponseDto, isBackgroundSourcePreviewResponseDto } from "./response-dto/background-selection-response-dto";
import { isBackgroundSetList, isBackgroundSetResponseDto } from "./response-dto/background-set-response-dto";

const isJobList = (value: unknown): value is JobResponseDto[] => Array.isArray(value) && value.every(isJobResponseDto);

/** Примеры видео читаются переходом к кадрам (до 20 перемоток): ответ может прийти позже обычных 10 с. */
const previewTimeoutMs = 30_000;

/**
 * Наборы фонов открытого проекта и их фоны.
 * Начальное состояние — listSets и list (страницы по порядку добавления, фильтр removed); изменения сообщает событие
 * backgrounds.changed (subscribeBackgroundsChanged): sets — наборы с ревизиями (null — удалён), items — фоны явных операций,
 * itemsChangedIn — наборы, у которых изменился состав или счётчики (перечитать набор и страницы). Флаг matchesProjectInput
 * зависит от входа проекта: после project.changed перечитать наборы.
 *
 * Добавление (задача backgrounds.import; прогресс и итог — job.changed, parseImportBackgroundsResult; история набора — listRuns):
 * - «Готовые кадры»: selectFiles (сразу счётчики exact/larger/smaller/unreadable) → import без sourceIndices и crops;
 * - «Нарезать из изображений»: selectFiles → previewSource по файлу (рамка) → previewImport (примеры) → import с sourceIndices и crops;
 * - «Нарезать из видео»: selectVideo → previewVideoFrame (кадр-пример, «Показать другой кадр») → previewImport → import с region,
 *   startMs, intervalMs, count, skipSimilar (videoImportError и availableVideoFrames проверяют до отправки).
 * Размытие (blur) общее для всех способов; одинаковый operationId у previewImport и import даёт одинаковые радиусы и пиксели.
 * Удаление навсегда: reviewSetDeletion/deleteSet или reviewDeletion/delete с revision состава.
 *
 * Ошибки: backgrounds.project_not_open, backgrounds.name_invalid, backgrounds.description_invalid, backgrounds.set_not_found,
 * backgrounds.item_not_found, backgrounds.revision_conflict, backgrounds.set_archived, backgrounds.set_importing (архив набора во
 * время подготовки), backgrounds.selection_invalid, backgrounds.selection_expired, backgrounds.too_many_sources,
 * backgrounds.sources_empty, backgrounds.blur_invalid, backgrounds.crop_invalid, backgrounds.count_invalid,
 * backgrounds.video_too_small, backgrounds.region_invalid, backgrounds.image_unreadable (previewSource),
 * backgrounds.deletion_revision_conflict, backgrounds.deletion_blocked, jobs.project_not_open; видео: video.ffmpeg_missing,
 * video.unsupported, video.unreadable, video.frames_invalid, video.range_invalid, video.frame_unavailable; ошибки задачи:
 * backgrounds.import_sources_changed, backgrounds.no_usable_media и коды выше. Причины пропуска — в итоге (issues) и примерах
 * (reason): backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable, backgrounds.similar,
 * backgrounds.duplicate, video.frame_unavailable. Неверные значения перечислений и индексов — bridge.validation.
 */
export const backgroundsApi = {
    get available(): boolean { return nativeBridge.available; },
    /** Все наборы проекта, включая архивные, по времени создания. */
    listSets(request: ListBackgroundSetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.listSets", request, isBackgroundSetList, signal);
    },
    getSet(request: GetBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.getSet", request, isBackgroundSetResponseDto, signal);
    },
    /** «Создать набор»: размер — текущий вход проекта. */
    createSet(request: CreateBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.createSet", request, isBackgroundSetResponseDto, signal);
    },
    /** Название и описание с ревизией набора. */
    updateSet(request: UpdateBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.updateSet", request, isBackgroundSetResponseDto, signal);
    },
    /** «В архив» / «Восстановить»; архивный набор только для чтения, набор с идущей подготовкой в архив не перенести. */
    setSetArchived(request: SetBackgroundSetArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.setSetArchived", request, isBackgroundSetResponseDto, signal);
    },
    /** Страница фонов по порядку добавления; removed: active (по умолчанию), removed, all. */
    list(request: ListBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.list", request, isBackgroundPageResponseDto, signal);
    },
    get(request: GetBackgroundRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.get", request, isBackgroundResponseDto, signal);
    },
    /** «Убрать» / «Восстановить» до 400 фонов по их revision; всё или ничего, идущая подготовка не мешает. */
    setRemoved(request: SetBackgroundsRemovedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.setRemoved", request, isBackgroundList, signal);
    },
    /** История подготовки этого набора, новые первыми; итог с причинами пропуска — parseImportBackgroundsResult; отмена — jobsApi.cancel. */
    listRuns(request: ListBackgroundRunsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.listRuns", request, isJobList, signal);
    },
    reviewSetDeletion(request: ReviewBackgroundSetDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.reviewSetDeletion", request, isBackgroundDeletionReviewResponseDto, signal);
    },
    /** Удаляет набор, его фоны и историю подготовки; файлы — в фоне. */
    deleteSet(request: DeleteBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.deleteSet", request, isBackgroundDeletionResultResponseDto, signal);
    },
    /** Состав удаления выбранных (до 400) фонов одного набора. */
    reviewDeletion(request: ReviewBackgroundsDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.reviewDeletion", request, isBackgroundDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.delete", request, isBackgroundDeletionResultResponseDto, signal);
    },
    /**
     * Нативный диалог выбора PNG/JPEG/WebP/BMP и чтение заголовков; ответ приходит, когда пользователь закроет диалог.
     * Закрытый диалог — selectionToken: null.
     */
    selectFiles(request: SelectBackgroundFilesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.selectFiles", request, isBackgroundFileSelectionResponseDto, signal, null);
    },
    /** Нативный диалог выбора видео и его свойства (как imagesApi.selectVideo); закрытый диалог — selectionToken: null (isSelectedVideo). */
    selectVideo(signal?: AbortSignal) {
        const request: SelectBackgroundVideoRequestDto = {};
        return nativeBridge.request("backgrounds.selectVideo", request, isVideoSelectionResponseDto, signal, null);
    },
    /** Файл выбора, уменьшенный до 1600 px (масштабированное декодирование), для рамки «Нарезка фонов из изображений». */
    previewSource(request: PreviewBackgroundSourceRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewSource", request, isBackgroundSourcePreviewResponseDto, signal, previewTimeoutMs);
    },
    /** Кадр выбранного видео на отметке для рамки. */
    previewVideoFrame(request: PreviewBackgroundVideoFrameRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewVideoFrame", request, isBackgroundSourcePreviewResponseDto, signal, previewTimeoutMs);
    },
    /**
     * До 6 примеров запуска с теми же полями и operationId, что будет у import: те же рамки, радиусы и пиксели. У видео примеры
     * распределены по первым count кадрам и при skipSimilar похожий кадр заменяется следующим.
     */
    previewImport(request: ImportBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewImport", request, isBackgroundImportPreviewResponseDto, signal, previewTimeoutMs);
    },
    /** Ставит задачу подготовки и сразу возвращает её снимок; повтор с тем же operationId вернёт ту же задачу. */
    import(request: ImportBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.import", request, isJobResponseDto, signal);
    },
};
