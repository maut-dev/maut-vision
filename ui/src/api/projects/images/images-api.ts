import { isJobResponseDto, type JobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { nativeBridge } from "../../transport/native-bridge";
import type { CreateImageGroupRequestDto } from "./request-dto/create-image-group-request-dto";
import type { DeleteImageGroupRequestDto } from "./request-dto/delete-image-group-request-dto";
import type { DeleteImagesRequestDto } from "./request-dto/delete-images-request-dto";
import type { GetImageAnnotationRequestDto } from "./request-dto/get-image-annotation-request-dto";
import type { GetImageGroupRequestDto } from "./request-dto/get-image-group-request-dto";
import type { GetImageRequestDto } from "./request-dto/get-image-request-dto";
import type { ImportImagesRequestDto } from "./request-dto/import-images-request-dto";
import type { ListImageAnnotationsRequestDto } from "./request-dto/list-image-annotations-request-dto";
import type { ListImageGroupsRequestDto } from "./request-dto/list-image-groups-request-dto";
import type { ListImageImportsRequestDto } from "./request-dto/list-image-imports-request-dto";
import type { ListImagesRequestDto } from "./request-dto/list-images-request-dto";
import type { MoveImagesRequestDto } from "./request-dto/move-images-request-dto";
import type { ReviewImageGroupDeletionRequestDto } from "./request-dto/review-image-group-deletion-request-dto";
import type { ReviewImagesDeletionRequestDto } from "./request-dto/review-images-deletion-request-dto";
import type { SaveImageAnnotationRequestDto } from "./request-dto/save-image-annotation-request-dto";
import type { SelectImageFilesRequestDto } from "./request-dto/select-image-files-request-dto";
import type { SelectImageFolderRequestDto } from "./request-dto/select-image-folder-request-dto";
import type { SelectImageVideoRequestDto } from "./request-dto/select-image-video-request-dto";
import type { SetImageGroupArchivedRequestDto } from "./request-dto/set-image-group-archived-request-dto";
import type { SetImagesArchivedRequestDto } from "./request-dto/set-images-archived-request-dto";
import type { UpdateImageGroupRequestDto } from "./request-dto/update-image-group-request-dto";
import { isImageAnnotationList, isImageAnnotationResponseDto, isSavedImageAnnotationResponseDto } from "./response-dto/image-annotation-response-dto";
import { isImageDeletionResultResponseDto, isImageDeletionReviewResponseDto } from "./response-dto/image-deletion-response-dto";
import { isImageGroupList, isImageGroupResponseDto } from "./response-dto/image-group-response-dto";
import { isImageList, isImagePageResponseDto, isImageResponseDto, isImageSelectionResponseDto, isMovedImagesResponseDto } from "./response-dto/image-response-dto";
import { isVideoSelectionResponseDto } from "./response-dto/video-selection-response-dto";

const isJobList = (value: unknown): value is JobResponseDto[] => Array.isArray(value) && value.every(isJobResponseDto);

/**
 * Группы изображений открытого проекта, изображения и разметка.
 * Начальное состояние — listGroups, list (страницы по порядку импорта) и listAnnotations (рамки страницы); изменения сообщает
 * событие images.changed (subscribeImagesChanged): groups — группы с ревизиями (null — удалена), images — изменённые
 * изображения, imagesChangedIn — группы, у которых изменился состав или счётчики (перечитать группу и страницы).
 * Импорт: selectFiles, selectFolder или selectVideo (нативный диалог) → import (задача images.import; прогресс и итог —
 * job.changed, parseImportImagesResult); для видео import получает startMs, intervalMs и count (videoFrameRangeError
 * проверяет их до отправки); история импорта группы — listImports.
 * Удаление навсегда: reviewGroupDeletion/deleteGroup или reviewDeletion/delete с revision состава.
 * Ошибки: images.project_not_open, images.name_invalid, images.description_invalid, images.group_not_found,
 * images.image_not_found, images.annotation_not_found, images.revision_conflict, images.group_archived, images.group_busy
 * (генерация наполняет группу), images.group_importing (архив группы во время импорта), images.group_not_manual,
 * images.origin_mismatch, images.selection_invalid, images.image_archived, images.box_invalid, images.object_id_invalid,
 * images.reference_unavailable, images.annotation_invalid, images.deletion_revision_conflict, images.deletion_blocked,
 * images.selection_expired, jobs.project_not_open; ошибки задачи импорта: images.folder_unavailable,
 * images.import_sources_changed; видео (selectVideo, import и задача): video.ffmpeg_missing, video.unsupported,
 * video.unreadable, video.frames_invalid, video.range_invalid; причины отклонения файлов: images.file_invalid,
 * images.file_unreadable, video.frame_unavailable (кадр за концом видеодорожки).
 * Неверные значения перечислений и пределов — bridge.validation.
 */
export const imagesApi = {
    get available(): boolean { return nativeBridge.available; },
    /** Все группы проекта, включая архивные, по времени создания. */
    listGroups(request: ListImageGroupsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listGroups", request, isImageGroupList, signal);
    },
    getGroup(request: GetImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.getGroup", request, isImageGroupResponseDto, signal);
    },
    /** Ручная группа («Исходные изображения»). */
    createGroup(request: CreateImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.createGroup", request, isImageGroupResponseDto, signal);
    },
    updateGroup(request: UpdateImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.updateGroup", request, isImageGroupResponseDto, signal);
    },
    /** Архивная группа только для чтения; группу с идущим импортом в архив не перенести (images.group_importing). */
    setGroupArchived(request: SetImageGroupArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.setGroupArchived", request, isImageGroupResponseDto, signal);
    },
    /** Страница изображений группы по порядку импорта с фильтрами archive и reviewState. */
    list(request: ListImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.list", request, isImagePageResponseDto, signal);
    },
    get(request: GetImageRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.get", request, isImageResponseDto, signal);
    },
    /** Текущая или указанная ревизия разметки. */
    getAnnotation(request: GetImageAnnotationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.getAnnotation", request, isImageAnnotationResponseDto, signal);
    },
    /** Текущая разметка до 200 изображений (рамки на миниатюрах страницы), в порядке запроса; отсутствующие пропускаются. */
    listAnnotations(request: ListImageAnnotationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listAnnotations", request, isImageAnnotationList, signal);
    },
    /**
     * Новая ревизия разметки. Новые объекты — id: null; новые и изменённые по классу или варианту объекты должны ссылаться
     * на активные класс и вариант этого класса, неизменённые могут сохранить архивную ссылку.
     */
    saveAnnotation(request: SaveImageAnnotationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.saveAnnotation", request, isSavedImageAnnotationResponseDto, signal);
    },
    /** В архив / восстановить до 400 изображений с их revision; всё или ничего. */
    setArchived(request: SetImagesArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.setArchived", request, isImageList, signal);
    },
    /** Переместить до 400 активных изображений в группу того же происхождения; ревизии обеих групп и изображений. */
    move(request: MoveImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.move", request, isMovedImagesResponseDto, signal);
    },
    reviewGroupDeletion(request: ReviewImageGroupDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.reviewGroupDeletion", request, isImageDeletionReviewResponseDto, signal);
    },
    /** Удаляет группу, её изображения, разметку и историю импорта. */
    deleteGroup(request: DeleteImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.deleteGroup", request, isImageDeletionResultResponseDto, signal);
    },
    /** Состав удаления выбранных (до 400) изображений одной группы. */
    reviewDeletion(request: ReviewImagesDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.reviewDeletion", request, isImageDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.delete", request, isImageDeletionResultResponseDto, signal);
    },
    /** История импорта группы, новые первыми; итог с ошибками по файлам — parseImportImagesResult. */
    listImports(request: ListImageImportsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listImports", request, isJobList, signal);
    },
    /** Нативный диалог выбора PNG/JPEG/WebP/BMP; ответ приходит, когда пользователь закроет диалог. */
    selectFiles(signal?: AbortSignal) {
        const request: SelectImageFilesRequestDto = {};
        return nativeBridge.request("images.selectFiles", request, isImageSelectionResponseDto, signal, null);
    },
    /** Нативный диалог выбора папки; ответ приходит, когда пользователь закроет диалог. */
    selectFolder(signal?: AbortSignal) {
        const request: SelectImageFolderRequestDto = {};
        return nativeBridge.request("images.selectFolder", request, isImageSelectionResponseDto, signal, null);
    },
    /**
     * Нативный диалог выбора видео (MP4, MKV, AVI, MOV, WebM, M4V) и чтение его свойств; ответ приходит, когда пользователь
     * закроет диалог. Закрытый диалог — selectionToken: null (isSelectedVideo).
     */
    selectVideo(signal?: AbortSignal) {
        const request: SelectImageVideoRequestDto = {};
        return nativeBridge.request("images.selectVideo", request, isVideoSelectionResponseDto, signal, null);
    },
    /** Ставит задачу импорта и сразу возвращает её снимок; повтор с тем же operationId вернёт ту же задачу. */
    import(request: ImportImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.import", request, isJobResponseDto, signal);
    },
};
