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

/** Старт готовит папку задачи не сразу, но проверяет датасет и базовую модель (чтение каталога, проверка версии). */
const startTimeoutMs = 60_000;
/** nvidia-smi отвечает до 5 с. */
const gpuTimeoutMs = 15_000;
/** Нативное «Сохранить как» ждёт пользователя. */
const dialogTimeoutMs = 600_000;

/**
 * Обучение открытого проекта (вкладки «Настройки», «Текущий запуск», «История обучения») и версии моделей.
 *
 * Настройки: listPresets / savePreset (то же имя — перезапись) / deletePreset — пресеты общие для всех проектов; listGpus — поле
 * «Устройство» (deviceOptions в training-parameters.ts); getRuntime — среда Python worker (training.runtime_not_configured /
 * runtime_missing); параметры, подписи, группы, диапазоны и подобранные значения — training-parameters.ts. Датасеты для выбора —
 * datasetsApi.list.
 *
 * Запуск: startRun возвращает запуск сразу (state queued / preparing); cancelRun — «Отменить обучение». Ход: job.changed (kind
 * training.run: progress, progressMessage progress.epoch «Эпоха X из Y», state задачи) и training.changed (subscribeTrainingChanged: runs с revision —
 * перечитать getRun; versions — перечитать listModelVersions). История эпох — listEpochs страницами (after = nextAfter); журнал —
 * tailLog (последние сообщения), затем listLog с after = nextAfter для дочитывания новых и всей истории страницами.
 *
 * Версии: listModelVersions, getModelVersion (files[].url — картинки графиков), setWorkingModelVersion («Сделать рабочей»),
 * reviewModelVersionDeletion → deleteModelVersion (общий диалог удаления; рабочую, проверяемую или выгружаемую версию удалить нельзя —
 * training.version_in_use, details.reason: working | evaluation | check | export; её оценки уходят вместе с ней; training.changed
 * сообщает версию с revision = null). ZIP: selectModelExportDestination (нативное «Сохранить как», token null — окно закрыто) →
 * startModelExport — задача training.export (job.changed: progress, result — isModelExportResultDto; ошибки training.artifact_corrupt
 * с details.name, training.export_failed, training.disk_full, training.export_selection_expired).
 *
 * Ошибки (тексты приходят готовыми в error.message): training.project_not_open, training.run_not_found, training.version_not_found,
 * training.parameter_invalid (details.field — ключ параметра), training.preset_name_invalid, training.preset_not_found,
 * training.runtime_not_configured, training.runtime_missing, training.base_model_not_installed (details.baseModelId),
 * training.input_size_invalid, datasets.unavailable, datasets.archived, datasets.corrupt, datasets.input_size_mismatch,
 * datasets.classes_invalid, jobs.project_not_open. Ошибки запуска (run.error, job.error): training.worker_* (сообщение worker),
 * training.base_weights_missing, base_models.installation_active, training.protocol_*, training.result_*, training.artifact_*,
 * training.disk_full, training.out_of_memory, training.execution_failed, jobs.interrupted.
 */
export const trainingApi = {
    get available(): boolean { return nativeBridge.available; },
    listPresets(request: ListTrainingPresetsRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.listPresets", request, isTrainingPresetList, signal);
    },
    /** «Сохранить как пресет…». */
    savePreset(request: SaveTrainingPresetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.savePreset", request, isTrainingPresetResponseDto, signal);
    },
    /** «Удалить пресет». */
    deletePreset(request: DeleteTrainingPresetRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.deletePreset", request, isTrainingPresetDeletionResponseDto, signal);
    },
    listGpus(request: ListGpusRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.listGpus", request, isGpuInventoryResponseDto, signal, gpuTimeoutMs);
    },
    getRuntime(request: GetTrainingRuntimeRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("training.getRuntime", request, isTrainingRuntimeResponseDto, signal);
    },
    /** «Начать обучение» / «Поставить в очередь»: одна GPU-задача одновременно, остальные ждут в очереди. */
    startRun(request: StartTrainingRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.startRun", request, isTrainingRunResponseDto, signal, startTimeoutMs);
    },
    /** «Отменить обучение»: идемпотентно; запуск в работе станет cancelled после остановки worker (job.changed). */
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
    /** «Сделать рабочей». */
    setWorkingModelVersion(request: SetWorkingModelVersionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.setWorkingModelVersion", request, isModelVersionResponseDto, signal);
    },
    /** Состав удаления версии для общего диалога: оценки, размер файлов, причины запрета. */
    reviewModelVersionDeletion(request: ReviewModelVersionDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.reviewModelVersionDeletion", request, isModelVersionDeletionReviewResponseDto, signal);
    },
    /** «Удалить версию». */
    deleteModelVersion(request: DeleteModelVersionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.deleteModelVersion", request, isModelVersionDeletionResponseDto, signal);
    },
    /** «Выгрузить ZIP», шаг 1: «Сохранить как» с model-vN.zip. */
    selectModelExportDestination(request: SelectModelExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.selectModelExportDestination", request, isModelExportSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** «Выгрузить ZIP», шаг 2: задача training.export сразу; ход и итог — job.changed. */
    startModelExport(request: StartModelExportRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("training.startModelExport", request, isJobResponseDto, signal, startTimeoutMs);
    },
};
