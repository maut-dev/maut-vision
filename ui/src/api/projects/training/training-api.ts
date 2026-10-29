import { isJobResponseDto } from "../../jobs/response-dto/job-response-dto";
import { nativeBridge } from "../../transport/native-bridge";
import type {
    CancelTrainingRunRequestDto, DeleteModelVersionRequestDto, DeleteTrainingPresetRequestDto, GetModelVersionRequestDto, GetTrainingRunRequestDto,
    GetTrainingRuntimeRequestDto, ListGpusRequestDto, ListModelVersionsRequestDto, ListTrainingEpochsRequestDto, ListTrainingLogRequestDto,
    ListTrainingPresetsRequestDto, ListTrainingRunsRequestDto, ReviewModelVersionDeletionRequestDto, SaveTrainingPresetRequestDto,
    SelectModelExportRequestDto, SetWorkingModelVersionRequestDto, StartModelExportRequestDto, StartTrainingRunRequestDto, TailTrainingLogRequestDto,
} from "./request-dto/training-request-dto";
import {
    isModelExportSelectionResponseDto, isModelVersionDeletionResponseDto, isModelVersionDeletionReviewResponseDto,
} from "./response-dto/model-version-deletion-response-dto";
import { isModelVersionList, isModelVersionResponseDto } from "./response-dto/model-version-response-dto";
import {
    isTrainingEpochPageResponseDto, isTrainingLogPageResponseDto, isTrainingRunPageResponseDto, isTrainingRunResponseDto,
} from "./response-dto/training-run-response-dto";
import {
    isGpuInventoryResponseDto, isTrainingPresetDeletionResponseDto, isTrainingPresetList, isTrainingPresetResponseDto, isTrainingRuntimeResponseDto,
} from "./response-dto/training-runtime-response-dto";

/** The start does not prepare the job folder at once, but it checks the dataset and the base model (catalog read, version check). */
const startTimeoutMs = 60_000;
/** nvidia-smi answers within 5 s. */
const gpuTimeoutMs = 15_000;
/** The native "Save as" waits for the user. */
const dialogTimeoutMs = 600_000;

/**
 * Training of the open project (tabs "Settings", "Current run", "Training history") and model versions.
 *
 * Settings: listPresets / savePreset (the same name overwrites) / deletePreset — presets are shared by all projects; listGpus — the
 * "Device" field (deviceOptions in training-parameters.ts); getRuntime — the Python worker environment (training.runtime_not_configured /
 * runtime_missing); parameters, labels, groups, ranges and tuned values — training-parameters.ts. Datasets to choose from —
 * datasetsApi.list.
 *
 * Run: startRun returns the run at once (state queued / preparing); cancelRun — "Cancel training". Progress: job.changed (kind
 * training.run: progress, progressMessage progress.epoch, job state) and training.changed (subscribeTrainingChanged: runs with a revision —
 * read getRun again; versions — read listModelVersions again). Epoch history — listEpochs by pages (after = nextAfter); the log —
 * tailLog (the last messages), then listLog with after = nextAfter for new lines and the whole history by pages.
 *
 * Versions: listModelVersions, getModelVersion (files[].url — chart images), setWorkingModelVersion ("Make working"),
 * reviewModelVersionDeletion → deleteModelVersion (the shared deletion dialog; a working version or one under a check or an export cannot
 * be deleted — training.version_in_use, details.reason: working | evaluation | check | export; its evaluations go with it;
 * training.changed reports the version with revision = null). ZIP: selectModelExportDestination (native "Save as", token null — the
 * dialog was closed) → startModelExport — the job training.export (job.changed: progress, result — isModelExportResultDto; errors
 * training.artifact_corrupt with details.name, training.export_failed, training.disk_full, training.export_selection_expired).
 *
 * Errors (codes; texts in the errors catalog): training.project_not_open, training.run_not_found, training.version_not_found,
 * training.parameter_invalid (details.field — the parameter key), training.preset_name_invalid, training.preset_not_found,
 * training.runtime_not_configured, training.runtime_missing, training.base_model_not_installed (details.baseModelId),
 * training.input_size_invalid, datasets.unavailable, datasets.archived, datasets.corrupt, datasets.input_size_mismatch,
 * datasets.classes_invalid, jobs.project_not_open. Run errors (run.error, job.error): training.worker_* (worker message),
 * training.base_weights_missing, base_models.installation_active, training.protocol_*, training.result_*, training.artifact_*,
 * training.disk_full, training.out_of_memory, training.execution_failed, jobs.interrupted.
 */
export const trainingApi = {
    get available(): boolean { return nativeBridge.available; },
    listPresets(request: ListTrainingPresetsRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.listPresets", request, isTrainingPresetList, signal);
    },
    /** "Save as preset…". */
    savePreset(request: SaveTrainingPresetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.savePreset", request, isTrainingPresetResponseDto, signal);
    },
    /** "Delete preset". */
    deletePreset(request: DeleteTrainingPresetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.deletePreset", request, isTrainingPresetDeletionResponseDto, signal);
    },
    listGpus(request: ListGpusRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.listGpus", request, isGpuInventoryResponseDto, signal, gpuTimeoutMs);
    },
    getRuntime(request: GetTrainingRuntimeRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.getRuntime", request, isTrainingRuntimeResponseDto, signal);
    },
    /** "Start training" / "Queue": one GPU job at a time, the others wait in the queue. */
    startRun(request: StartTrainingRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.startRun", request, isTrainingRunResponseDto, signal, startTimeoutMs);
    },
    /** "Cancel training": idempotent; a running run becomes cancelled after the worker stops (job.changed). */
    cancelRun(request: CancelTrainingRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.cancelRun", request, isTrainingRunResponseDto, signal);
    },
    listRuns(request: ListTrainingRunsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.listRuns", request, isTrainingRunPageResponseDto, signal);
    },
    getRun(request: GetTrainingRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.getRun", request, isTrainingRunResponseDto, signal);
    },
    listEpochs(request: ListTrainingEpochsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.listEpochs", request, isTrainingEpochPageResponseDto, signal);
    },
    listLog(request: ListTrainingLogRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.listLog", request, isTrainingLogPageResponseDto, signal);
    },
    tailLog(request: TailTrainingLogRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.tailLog", request, isTrainingLogPageResponseDto, signal);
    },
    listModelVersions(request: ListModelVersionsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.listModelVersions", request, isModelVersionList, signal);
    },
    getModelVersion(request: GetModelVersionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.getModelVersion", request, isModelVersionResponseDto, signal);
    },
    /** "Make working". */
    setWorkingModelVersion(request: SetWorkingModelVersionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.setWorkingModelVersion", request, isModelVersionResponseDto, signal);
    },
    /** What deleting a version removes, for the shared dialog: evaluations, file size, blockers. */
    reviewModelVersionDeletion(request: ReviewModelVersionDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.reviewModelVersionDeletion", request, isModelVersionDeletionReviewResponseDto, signal);
    },
    /** "Delete version". */
    deleteModelVersion(request: DeleteModelVersionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.deleteModelVersion", request, isModelVersionDeletionResponseDto, signal);
    },
    /** "Export ZIP", step 1: "Save as" with model-vN.zip. */
    selectModelExportDestination(request: SelectModelExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.selectModelExportDestination", request, isModelExportSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** "Export ZIP", step 2: the job training.export at once; progress and result — job.changed. */
    startModelExport(request: StartModelExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.startModelExport", request, isJobResponseDto, signal, startTimeoutMs);
    },
};
