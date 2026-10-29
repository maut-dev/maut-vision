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
 * Image groups of the open project, images and annotations.
 * The initial state comes from listGroups, list (pages in import order) and listAnnotations (boxes of a page); changes come with
 * the images.changed event (subscribeImagesChanged): groups are groups with revisions (null: deleted), images are changed
 * images, imagesChangedIn are groups whose contents or counters changed (reread the group and its pages).
 * Import: selectFiles, selectFolder or selectVideo (a native dialog), then import (the images.import job; progress and result in
 * job.changed, parseImportImagesResult); for a video import gets startMs, intervalMs and count (videoFrameRangeError
 * checks them before sending); the import history of a group is listImports.
 * Permanent deletion: reviewGroupDeletion/deleteGroup or reviewDeletion/delete with the revision of the contents.
 * Errors: images.project_not_open, images.name_invalid, images.description_invalid, images.group_not_found,
 * images.image_not_found, images.annotation_not_found, images.revision_conflict, images.group_archived, images.group_busy
 * (generation is filling the group), images.group_importing (archiving a group during an import), images.group_not_manual,
 * images.origin_mismatch, images.selection_invalid, images.image_archived, images.box_invalid, images.object_id_invalid,
 * images.reference_unavailable, images.annotation_invalid, images.deletion_revision_conflict, images.deletion_blocked,
 * images.selection_expired, jobs.project_not_open; errors of the import job: images.folder_unavailable,
 * images.import_sources_changed; video (selectVideo, import and the job): video.ffmpeg_missing, video.unsupported,
 * video.unreadable, video.frames_invalid, video.range_invalid; reasons a file is rejected: images.file_invalid,
 * images.file_unreadable, video.frame_unavailable (a frame past the end of the video track).
 * Wrong enumeration values and limits give bridge.validation.
 */
export const imagesApi = {
    get available(): boolean { return nativeBridge.available; },
    /** All groups of the project, archived included, by creation time. */
    listGroups(request: ListImageGroupsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listGroups", request, isImageGroupList, signal);
    },
    getGroup(request: GetImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.getGroup", request, isImageGroupResponseDto, signal);
    },
    /** A manual group ("Source images"). */
    createGroup(request: CreateImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.createGroup", request, isImageGroupResponseDto, signal);
    },
    updateGroup(request: UpdateImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.updateGroup", request, isImageGroupResponseDto, signal);
    },
    /** An archived group is read-only; a group with a running import cannot be archived (images.group_importing). */
    setGroupArchived(request: SetImageGroupArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.setGroupArchived", request, isImageGroupResponseDto, signal);
    },
    /** A page of images of a group in import order with the archive and reviewState filters. */
    list(request: ListImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.list", request, isImagePageResponseDto, signal);
    },
    get(request: GetImageRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.get", request, isImageResponseDto, signal);
    },
    /** The current or the given annotation revision. */
    getAnnotation(request: GetImageAnnotationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.getAnnotation", request, isImageAnnotationResponseDto, signal);
    },
    /** The current annotation of up to 200 images (boxes on the thumbnails of a page) in the requested order; missing ones are skipped. */
    listAnnotations(request: ListImageAnnotationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listAnnotations", request, isImageAnnotationList, signal);
    },
    /**
     * A new annotation revision. New objects have id: null; new objects and objects with a changed class or variant must refer to
     * an active class and a variant of that class; unchanged ones may keep an archived reference.
     */
    saveAnnotation(request: SaveImageAnnotationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.saveAnnotation", request, isSavedImageAnnotationResponseDto, signal);
    },
    /** Archive or restore up to 400 images with their revision; all or nothing. */
    setArchived(request: SetImagesArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.setArchived", request, isImageList, signal);
    },
    /** Move up to 400 active images to a group of the same origin; revisions of both groups and the images. */
    move(request: MoveImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.move", request, isMovedImagesResponseDto, signal);
    },
    reviewGroupDeletion(request: ReviewImageGroupDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.reviewGroupDeletion", request, isImageDeletionReviewResponseDto, signal);
    },
    /** Deletes the group, its images, annotations and import history. */
    deleteGroup(request: DeleteImageGroupRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.deleteGroup", request, isImageDeletionResultResponseDto, signal);
    },
    /** The deletion review of selected (up to 400) images of one group. */
    reviewDeletion(request: ReviewImagesDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.reviewDeletion", request, isImageDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.delete", request, isImageDeletionResultResponseDto, signal);
    },
    /** The import history of a group, newest first; the result with errors per file is parseImportImagesResult. */
    listImports(request: ListImageImportsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.listImports", request, isJobList, signal);
    },
    /** The native dialog for choosing PNG/JPEG/WebP/BMP; the answer comes when the user closes the dialog. */
    selectFiles(signal?: AbortSignal) {
        const request: SelectImageFilesRequestDto = {};
        return nativeBridge.request("images.selectFiles", request, isImageSelectionResponseDto, signal, null);
    },
    /** The native folder dialog; the answer comes when the user closes the dialog. */
    selectFolder(signal?: AbortSignal) {
        const request: SelectImageFolderRequestDto = {};
        return nativeBridge.request("images.selectFolder", request, isImageSelectionResponseDto, signal, null);
    },
    /**
     * The native dialog for choosing a video (MP4, MKV, AVI, MOV, WebM, M4V) and reading its properties; the answer comes when the user
     * closes the dialog. A closed dialog gives selectionToken: null (isSelectedVideo).
     */
    selectVideo(signal?: AbortSignal) {
        const request: SelectImageVideoRequestDto = {};
        return nativeBridge.request("images.selectVideo", request, isVideoSelectionResponseDto, signal, null);
    },
    /** Starts an import job and returns its snapshot at once; a repeat with the same operationId returns the same job. */
    import(request: ImportImagesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("images.import", request, isJobResponseDto, signal);
    },
};
