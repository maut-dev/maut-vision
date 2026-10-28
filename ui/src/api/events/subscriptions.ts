import { isBaseModelResponseDto } from "../base-models/response-dto/base-model-response-dto";
import { isJobResponseDto } from "../jobs/response-dto/job-response-dto";
import { isProjectImportResponseDto } from "../projects/response-dto/project-lifecycle-response-dto";
import { isProjectResponseDto } from "../projects/response-dto/project-response-dto";
import { nativeBridge } from "../transport/native-bridge";
import type { BaseModelChangedEventDto } from "./event-dto/base-model-changed-event-dto";
import { isBackgroundsChangedDto, type BackgroundsChangedEventDto } from "./event-dto/backgrounds-changed-event-dto";
import { isCloseRequestedDto, type CloseRequestedEventDto } from "./event-dto/close-requested-event-dto";
import { isCatalogChangedDto, type CatalogChangedEventDto } from "./event-dto/catalog-changed-event-dto";
import { isChecksChangedDto, type ChecksChangedEventDto } from "./event-dto/checks-changed-event-dto";
import { isDatasetsChangedDto, type DatasetsChangedEventDto } from "./event-dto/datasets-changed-event-dto";
import { isGenerationChangedDto, type GenerationChangedEventDto } from "./event-dto/generation-changed-event-dto";
import { isImagesChangedDto, type ImagesChangedEventDto } from "./event-dto/images-changed-event-dto";
import type { JobChangedEventDto } from "./event-dto/job-changed-event-dto";
import type { ProjectChangedEventDto } from "./event-dto/project-changed-event-dto";
import type { ProjectImportChangedEventDto } from "./event-dto/project-import-changed-event-dto";
import type { ProjectOpenedEventDto } from "./event-dto/project-opened-event-dto";
import { isProjectRemovedDto, type ProjectRemovedEventDto } from "./event-dto/project-removed-event-dto";
import type { LanguageChangedEventDto } from "./event-dto/language-changed-event-dto";
import { isLanguageResponseDto } from "../settings/response-dto/language-response-dto";
import {
    isTrainingChangedDto, isTrainingPresetsChangedDto, type TrainingChangedEventDto, type TrainingPresetsChangedEventDto,
} from "./event-dto/training-changed-event-dto";

export function subscribeProjectOpened(listener: (event: ProjectOpenedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "project.opened" || !isProjectResponseDto(event.data)) return;
        listener({ kind: "event", event: "project.opened", data: event.data });
    });
}

export function subscribeProjectChanged(listener: (event: ProjectChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "project.changed" || !isProjectResponseDto(event.data)) return;
        listener({ kind: "event", event: "project.changed", data: event.data });
    });
}

/** Проект удалён (в том числе из другого окна): убрать его из библиотеки. */
export function subscribeProjectRemoved(listener: (event: ProjectRemovedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "project.removed" || !isProjectRemovedDto(event.data)) return;
        listener({ kind: "event", event: "project.removed", data: event.data });
    });
}

/** Ход и итог импорта проекта; применять снимок, только если sequence больше известного. */
export function subscribeProjectImportChanged(listener: (event: ProjectImportChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "projectImport.changed" || !isProjectImportResponseDto(event.data)) return;
        listener({ kind: "event", event: "projectImport.changed", data: event.data });
    });
}

/** Снимок задачи после каждого сохранённого изменения; применять, только если sequence больше известного. */
export function subscribeJobChanged(listener: (event: JobChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "job.changed" || !isJobResponseDto(event.data)) return;
        listener({ kind: "event", event: "job.changed", data: event.data });
    });
}

export function subscribeBaseModelChanged(listener: (event: BaseModelChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "baseModel.changed" || !isBaseModelResponseDto(event.data)) return;
        listener({ kind: "event", event: "baseModel.changed", data: event.data });
    });
}

/**
 * Изменения каталога проекта (варианты, классы, привязки, изображения): что изменилось и новые ревизии.
 * Применять, только если catalogRevision больше известного; изменения изображений — по assetsChangedIn.
 */
export function subscribeCatalogChanged(listener: (event: CatalogChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "catalog.changed" || !isCatalogChangedDto(event.data)) return;
        listener({ kind: "event", event: "catalog.changed", data: event.data });
    });
}

/**
 * Изменения групп изображений, изображений и разметки: что изменилось и новые ревизии, без списков. Группу применять, если
 * revision больше известного (null — удалена); для imagesChangedIn перечитать группу и загруженные страницы.
 */
export function subscribeImagesChanged(listener: (event: ImagesChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "images.changed" || !isImagesChangedDto(event.data)) return;
        listener({ kind: "event", event: "images.changed", data: event.data });
    });
}

/**
 * Изменения наборов фонов и фонов: что изменилось и новые ревизии, без списков. Набор применять, если revision больше известного
 * (null — удалён); для itemsChangedIn перечитать набор и загруженные страницы.
 */
export function subscribeBackgroundsChanged(listener: (event: BackgroundsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "backgrounds.changed" || !isBackgroundsChangedDto(event.data)) return;
        listener({ kind: "event", event: "backgrounds.changed", data: event.data });
    });
}

/**
 * Изменения генерации: пресеты с ревизиями (null — удалён) и запуски, у которых изменились данные помимо состояния задачи
 * (перечитать getRun). Состояние и прогресс запусков — subscribeJobChanged (kind generation.run).
 */
export function subscribeGenerationChanged(listener: (event: GenerationChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "generation.changed" || !isGenerationChangedDto(event.data)) return;
        listener({ kind: "event", event: "generation.changed", data: event.data });
    });
}

/**
 * Изменения датасетов: версии с ревизиями (null — удалена), без списков; перечитать list / get. Прогресс и итоги сборки,
 * проверки и экспорта — subscribeJobChanged (kind datasets.build / datasets.verify / datasets.export).
 */
export function subscribeDatasetsChanged(listener: (event: DatasetsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "datasets.changed" || !isDatasetsChangedDto(event.data)) return;
        listener({ kind: "event", event: "datasets.changed", data: event.data });
    });
}

/** Окно закрывается: ответить systemApi.closeDecision за 1 с (см. CloseRequestedEventDto). */
export function subscribeCloseRequested(listener: (event: CloseRequestedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "app.closeRequested" || !isCloseRequestedDto(event.data)) return;
        listener({ kind: "event", event: "app.closeRequested", data: event.data });
    });
}

/**
 * Изменения обучения: запуски (ход, эпохи, журнал, итог) и версии моделей с ревизиями, без данных; перечитать getRun /
 * listModelVersions. Состояние и прогресс запусков — subscribeJobChanged (kind training.run).
 */
export function subscribeTrainingChanged(listener: (event: TrainingChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "training.changed" || !isTrainingChangedDto(event.data)) return;
        listener({ kind: "event", event: "training.changed", data: event.data });
    });
}

/** Пресеты обучения сохранены или удалены (в том числе из другого окна): перечитать trainingApi.listPresets. */
export function subscribeTrainingPresetsChanged(listener: (event: TrainingPresetsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "trainingPresets.changed" || !isTrainingPresetsChangedDto(event.data)) return;
        listener({ kind: "event", event: "trainingPresets.changed", data: event.data });
    });
}

/**
 * Изменения проверок: оценки на датасетах с ревизиями (перечитать checksApi.getEvaluation) и снимки живой проверки (применять по
 * check.sequence). Состояние и прогресс оценок — также subscribeJobChanged (kind checks.evaluate).
 */
export function subscribeChecksChanged(listener: (event: ChecksChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "checks.changed" || !isChecksChangedDto(event.data)) return;
        listener({ kind: "event", event: "checks.changed", data: event.data });
    });
}

/** The interface language changed (this page or the host): switch the catalogs. */
export function subscribeLanguageChanged(listener: (event: LanguageChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "settings.languageChanged" || !isLanguageResponseDto(event.data)) return;
        listener({ kind: "event", event: "settings.languageChanged", data: event.data });
    });
}