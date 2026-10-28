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

/** Нативное окно выбора ждёт пользователя. */
const dialogTimeoutMs = 600_000;
/** Старт проверяет версию и датасет; видео — ffprobe и кадр-пример; снимок 4K кодируется в PNG. */
const slowTimeoutMs = 60_000;

/**
 * Проверки версий моделей открытого проекта (раздел «Проверки»; оценки — также «Аналитика»).
 *
 * Датасет: startEvaluation — задача checks.evaluate в общей GPU-очереди (job.changed: состояние; checks.changed: evaluations с revision —
 * перечитать getEvaluation); cancelEvaluation — «Отменить»; listEvaluations — «Все проверки» (фильтры версия, датасет, часть);
 * listLatestEvaluations — матрица сравнения (последняя успешная оценка версии на паре «датасет · часть»; нет записи — «не проверялась»).
 * Совместимость (классы и их порядок, размер кадров, кадры в части) проверяется и в Core: checks.evaluation_classes_mismatch,
 * checks.evaluation_input_mismatch, checks.evaluation_split_empty — тексты спецификации приходят в error.message.
 *
 * Живые проверки (одна на приложение, занимают видеокарту; занята — сразу checks.gpu_busy_training | gpu_busy_evaluation |
 * gpu_busy_prediction): selectImage → startImage; selectVideo (кадр из середины) / previewVideoFrame («Показать другой кадр») →
 * startVideo; listMonitors / captureScreen («Снять экран», рамка в координатах монитора) → startScreen. Снимок — start*, get (с последней
 * страницей журнала), list; события — subscribeChecksChanged (снимок без журнала, не чаще 5 раз в секунду, применять по sequence;
 * превью — preview.url канала ресурсов); новые строки журнала — listJournal({after: последний ordinal}). stop — «Остановить».
 * Уход из раздела UI вызывает stop сам; смена проекта и перезагрузка страницы останавливают проверку в host.
 *
 * Ошибки: checks.prediction_parameters и checks.evaluation_parameters (details.field), checks.prediction_source, checks.source_changed,
 * checks.selection_expired, checks.region_invalid, checks.runtime_*, screen.monitor_not_found, screen.capture_*, training.version_not_found,
 * training.artifact_corrupt; ошибки исполнения (check.error, evaluation.error) — checks.worker_* с сообщением worker, checks.protocol_*,
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
    /** «Выбрать изображение и проверить…», шаг 1. */
    selectImage(request: SelectCheckImageRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.selectImage", request, isCheckFileSelectionResponseDto, signal, dialogTimeoutMs);
    },
    selectVideo(request: SelectCheckVideoRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.selectVideo", request, isCheckVideoSelectionResponseDto, signal, dialogTimeoutMs);
    },
    /** «Показать другой кадр». */
    previewVideoFrame(request: PreviewCheckVideoFrameRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.previewVideoFrame", request, isCheckVideoFrameResponseDto, signal, slowTimeoutMs);
    },
    listMonitors(request: ListMonitorsRequestDto = {}, signal?: AbortSignal) {
        return nativeBridge.request("checks.listMonitors", request, isMonitorList, signal);
    },
    /** «Снять экран». */
    captureScreen(request: CaptureScreenRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.captureScreen", request, isScreenShotResponseDto, signal, slowTimeoutMs);
    },
    startImage(request: StartImageCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startImage", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    startVideo(request: StartVideoCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startVideo", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    /** «Запустить проверку с экрана». */
    startScreen(request: StartScreenCheckRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("checks.startScreen", request, isLiveCheckResponseDto, signal, slowTimeoutMs);
    },
    /** «Остановить». */
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
