import { nativeBridge } from "../../transport/native-bridge";
import type {
    CancelModelEvaluationRequestDto, CaptureScreenRequestDto, GetCheckRequestDto, GetModelEvaluationRequestDto, ListCheckJournalRequestDto, ListChecksRequestDto,
    ListLatestModelEvaluationsRequestDto, ListModelEvaluationsRequestDto, ListMonitorsRequestDto, PreviewCheckVideoFrameRequestDto, SelectCheckImageRequestDto,
    SelectCheckVideoRequestDto, StartImageCheckRequestDto, StartModelEvaluationRequestDto, StartScreenCheckRequestDto, StartVideoCheckRequestDto, StopCheckRequestDto,
} from "./request-dto/checks-request-dto";
import {
    isCheckFileSelectionResponseDto, isCheckVideoFrameResponseDto, isCheckVideoSelectionResponseDto, isMonitorList, isScreenShotResponseDto,
} from "./response-dto/check-source-response-dto";
import { isLiveCheckJournalPageResponseDto, isLiveCheckList, isLiveCheckResponseDto } from "./response-dto/live-check-response-dto";
import { isModelEvaluationList, isModelEvaluationPageResponseDto, isModelEvaluationResponseDto } from "./response-dto/model-evaluation-response-dto";

/** The native picker waits for the user. */
const dialogTimeoutMs = 600_000;
/** A start checks the version and the dataset; a video runs ffprobe and a sample frame; a 4K screenshot is encoded to PNG. */
const slowTimeoutMs = 60_000;

/**
 * Checks of the model versions of the open project (the Checks section; evaluations also in Analytics).
 *
 * Dataset: startEvaluation — a checks.evaluate job in the shared GPU queue (job.changed: the state; checks.changed: evaluations with a revision —
 * read getEvaluation again); cancelEvaluation — Cancel; listEvaluations — all checks (filters: version, dataset, split);
 * listLatestEvaluations — the comparison matrix (the latest successful evaluation of a version on a "dataset · split" pair; no record — not checked).
 * Core checks the compatibility too (classes and their order, the frame size, frames in the split): checks.evaluation_classes_mismatch,
 * checks.evaluation_input_mismatch, checks.evaluation_split_empty with their values in error.details.
 *
 * Live checks (one per application, they take the GPU; a busy one is refused at once with checks.gpu_busy_training | gpu_busy_evaluation |
 * gpu_busy_prediction): selectImage → startImage; selectVideo (a frame from the middle) / previewVideoFrame (Show another frame) →
 * startVideo; listMonitors / captureScreen (Take a screenshot, the frame in monitor coordinates) → startScreen. Snapshots: start*, get (with the last
 * page of the journal), list; events: subscribeChecksChanged (a snapshot without the journal, at most 5 times a second, apply by sequence;
 * the preview is preview.url of the resource channel); new journal rows: listJournal({after: the last ordinal}). stop — Stop.
 * Leaving the section, the UI calls stop itself; switching the project and reloading the page stop the check in the host.
 *
 * Errors: checks.prediction_parameters and checks.evaluation_parameters (details.field), checks.prediction_source, checks.source_changed,
 * checks.selection_expired, checks.region_invalid, checks.runtime_*, screen.monitor_not_found, screen.capture_*, training.version_not_found,
 * training.artifact_corrupt; execution errors (check.error, evaluation.error) — checks.worker_* with the worker message, checks.protocol_*,
 * checks.result_*.
 */
export const checksApi = {
    get available(): boolean { return nativeBridge.available; },
    startEvaluation(request: StartModelEvaluationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startEvaluation", request, isModelEvaluationResponseDto, signal, slowTimeoutMs);
    },
    cancelEvaluation(request: CancelModelEvaluationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.cancelEvaluation", request, isModelEvaluationResponseDto, signal);
    },
    getEvaluation(request: GetModelEvaluationRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.getEvaluation", request, isModelEvaluationResponseDto, signal);
    },
    listEvaluations(request: ListModelEvaluationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.listEvaluations", request, isModelEvaluationPageResponseDto, signal);
    },
    listLatestEvaluations(request: ListLatestModelEvaluationsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.listLatestEvaluations", request, isModelEvaluationList, signal);
    },
    /** Choose an image and check…, step 1. */
    selectImage(request: SelectCheckImageRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.selectImage", request, isCheckFileSelectionResponseDto, signal, dialogTimeoutMs);
    },
    selectVideo(request: SelectCheckVideoRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.selectVideo", request, isCheckVideoSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** Show another frame. */
    previewVideoFrame(request: PreviewCheckVideoFrameRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.previewVideoFrame", request, isCheckVideoFrameResponseDto, signal, slowTimeoutMs);
    },
    listMonitors(request: ListMonitorsRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("checks.listMonitors", request, isMonitorList, signal);
    },
    /** Take a screenshot. */
    captureScreen(request: CaptureScreenRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.captureScreen", request, isScreenShotResponseDto, signal, slowTimeoutMs);
    },
    startImage(request: StartImageCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startImage", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    startVideo(request: StartVideoCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startVideo", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    /** Start the screen check. */
    startScreen(request: StartScreenCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startScreen", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    /** Stop. */
    stop(request: StopCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.stop", request, isLiveCheckResponseDto, signal);
    },
    get(request: GetCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.get", request, isLiveCheckResponseDto, signal);
    },
    list(request: ListChecksRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.list", request, isLiveCheckList, signal);
    },
    listJournal(request: ListCheckJournalRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.listJournal", request, isLiveCheckJournalPageResponseDto, signal);
    },
};
