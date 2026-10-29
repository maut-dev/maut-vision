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

/** Video samples are read by seeking to frames (up to 20 seeks): the answer may come later than the usual 10 s. */
const previewTimeoutMs = 30_000;

/**
 * Background sets of the open project and their backgrounds.
 * The initial state comes from listSets and list (pages in the order of adding, the removed filter); changes come with the
 * backgrounds.changed event (subscribeBackgroundsChanged): sets are sets with revisions (null: deleted), items are backgrounds of explicit operations,
 * itemsChangedIn are sets whose contents or counters changed (reread the set and its pages). The matchesProjectInput flag
 * depends on the project input: reread the sets after project.changed.
 *
 * Adding (the backgrounds.import job; progress and result in job.changed, parseImportBackgroundsResult; the set history is listRuns):
 * - "Ready frames": selectFiles (exact/larger/smaller/unreadable counters at once), then import without sourceIndices and crops;
 * - "Cut from images": selectFiles, previewSource per file (the frame), previewImport (samples), then import with sourceIndices and crops;
 * - "Cut from video": selectVideo, previewVideoFrame (the example shot, "Show another shot"), previewImport, then import with region,
 *   startMs, intervalMs, count, skipSimilar (videoImportError and availableVideoFrames check before sending).
 * The blur is shared by all ways; the same operationId for previewImport and import gives the same radii and pixels.
 * Permanent deletion: reviewSetDeletion/deleteSet or reviewDeletion/delete with the revision of the contents.
 *
 * Errors: backgrounds.project_not_open, backgrounds.name_invalid, backgrounds.description_invalid, backgrounds.set_not_found,
 * backgrounds.item_not_found, backgrounds.revision_conflict, backgrounds.set_archived, backgrounds.set_importing (archiving a set
 * during preparation), backgrounds.selection_invalid, backgrounds.selection_expired, backgrounds.too_many_sources,
 * backgrounds.sources_empty, backgrounds.blur_invalid, backgrounds.crop_invalid, backgrounds.count_invalid,
 * backgrounds.video_too_small, backgrounds.region_invalid, backgrounds.image_unreadable (previewSource),
 * backgrounds.deletion_revision_conflict, backgrounds.deletion_blocked, jobs.project_not_open; video: video.ffmpeg_missing,
 * video.unsupported, video.unreadable, video.frames_invalid, video.range_invalid, video.frame_unavailable; job errors:
 * backgrounds.import_sources_changed, backgrounds.no_usable_media and the codes above. Skip reasons are in the result (issues) and the samples
 * (reason): backgrounds.wrong_size, backgrounds.crop_out_of_bounds, backgrounds.image_unreadable, backgrounds.similar,
 * backgrounds.duplicate, video.frame_unavailable. Wrong enumeration values and indices give bridge.validation.
 */
export const backgroundsApi = {
    get available(): boolean { return nativeBridge.available; },
    /** All sets of the project, archived included, by creation time. */
    listSets(request: ListBackgroundSetsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.listSets", request, isBackgroundSetList, signal);
    },
    getSet(request: GetBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.getSet", request, isBackgroundSetResponseDto, signal);
    },
    /** "Create set": the size is the current project input. */
    createSet(request: CreateBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.createSet", request, isBackgroundSetResponseDto, signal);
    },
    /** The name and description with the set revision. */
    updateSet(request: UpdateBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.updateSet", request, isBackgroundSetResponseDto, signal);
    },
    /** "Archive" / "Restore"; an archived set is read-only, a set with running preparation cannot be archived. */
    setSetArchived(request: SetBackgroundSetArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.setSetArchived", request, isBackgroundSetResponseDto, signal);
    },
    /** A page of backgrounds in the order of adding; removed: active (the default), removed, all. */
    list(request: ListBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.list", request, isBackgroundPageResponseDto, signal);
    },
    get(request: GetBackgroundRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.get", request, isBackgroundResponseDto, signal);
    },
    /** "Remove" / "Restore" up to 400 backgrounds by their revision; all or nothing, running preparation does not interfere. */
    setRemoved(request: SetBackgroundsRemovedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.setRemoved", request, isBackgroundList, signal);
    },
    /** The preparation history of this set, newest first; the result with skip reasons is parseImportBackgroundsResult; cancelling is jobsApi.cancel. */
    listRuns(request: ListBackgroundRunsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.listRuns", request, isJobList, signal);
    },
    reviewSetDeletion(request: ReviewBackgroundSetDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.reviewSetDeletion", request, isBackgroundDeletionReviewResponseDto, signal);
    },
    /** Deletes the set, its backgrounds and preparation history; files in the background. */
    deleteSet(request: DeleteBackgroundSetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.deleteSet", request, isBackgroundDeletionResultResponseDto, signal);
    },
    /** The deletion review of selected (up to 400) backgrounds of one set. */
    reviewDeletion(request: ReviewBackgroundsDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.reviewDeletion", request, isBackgroundDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.delete", request, isBackgroundDeletionResultResponseDto, signal);
    },
    /**
     * The native dialog for choosing PNG/JPEG/WebP/BMP and reading the headers; the answer comes when the user closes the dialog.
     * A closed dialog gives selectionToken: null.
     */
    selectFiles(request: SelectBackgroundFilesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.selectFiles", request, isBackgroundFileSelectionResponseDto, signal, null);
    },
    /** The native video dialog and the video properties (like imagesApi.selectVideo); a closed dialog gives selectionToken: null (isSelectedVideo). */
    selectVideo(signal?: AbortSignal) {
        const request: SelectBackgroundVideoRequestDto = {};
        return nativeBridge.request("backgrounds.selectVideo", request, isVideoSelectionResponseDto, signal, null);
    },
    /** A file of the selection reduced to 1600 px (scaled decoding) for the frame of "Cut backgrounds from images". */
    previewSource(request: PreviewBackgroundSourceRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewSource", request, isBackgroundSourcePreviewResponseDto, signal, previewTimeoutMs);
    },
    /** A frame of the chosen video at a mark, for the crop frame. */
    previewVideoFrame(request: PreviewBackgroundVideoFrameRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewVideoFrame", request, isBackgroundSourcePreviewResponseDto, signal, previewTimeoutMs);
    },
    /**
     * Up to 6 samples of a run with the same fields and operationId import will have: the same frames, radii and pixels. For a video the samples
     * are spread over the first count frames, and with skipSimilar a similar frame is replaced with the next one.
     */
    previewImport(request: ImportBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.previewImport", request, isBackgroundImportPreviewResponseDto, signal, previewTimeoutMs);
    },
    /** Starts a preparation job and returns its snapshot at once; a repeat with the same operationId returns the same job. */
    import(request: ImportBackgroundsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("backgrounds.import", request, isJobResponseDto, signal);
    },
};
