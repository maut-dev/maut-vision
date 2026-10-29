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

/** A project was deleted (also from another window): remove it from the library. */
export function subscribeProjectRemoved(listener: (event: ProjectRemovedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "project.removed" || !isProjectRemovedDto(event.data)) return;
        listener({ kind: "event", event: "project.removed", data: event.data });
    });
}

/** Progress and outcome of a project import; apply a snapshot only when sequence is greater than the known one. */
export function subscribeProjectImportChanged(listener: (event: ProjectImportChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "projectImport.changed" || !isProjectImportResponseDto(event.data)) return;
        listener({ kind: "event", event: "projectImport.changed", data: event.data });
    });
}

/** A job snapshot after each stored change; apply it only when sequence is greater than the known one. */
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
 * Changes of the project catalog (variants, classes, bindings, images): what changed and the new revisions.
 * Apply them only when catalogRevision is greater than the known one; image changes go by assetsChangedIn.
 */
export function subscribeCatalogChanged(listener: (event: CatalogChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "catalog.changed" || !isCatalogChangedDto(event.data)) return;
        listener({ kind: "event", event: "catalog.changed", data: event.data });
    });
}

/**
 * Changes of image groups, images and annotation: what changed and the new revisions, without lists. Apply a group when its
 * revision is greater than the known one (null — deleted); for imagesChangedIn read the group and the loaded pages again.
 */
export function subscribeImagesChanged(listener: (event: ImagesChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "images.changed" || !isImagesChangedDto(event.data)) return;
        listener({ kind: "event", event: "images.changed", data: event.data });
    });
}

/**
 * Changes of background sets and backgrounds: what changed and the new revisions, without lists. Apply a set when its revision is greater
 * than the known one (null — deleted); for itemsChangedIn read the set and the loaded pages again.
 */
export function subscribeBackgroundsChanged(listener: (event: BackgroundsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "backgrounds.changed" || !isBackgroundsChangedDto(event.data)) return;
        listener({ kind: "event", event: "backgrounds.changed", data: event.data });
    });
}

/**
 * Changes of generation: presets with revisions (null — deleted) and runs whose data changed besides the job state
 * (read getRun again). The state and progress of runs come with subscribeJobChanged (kind generation.run).
 */
export function subscribeGenerationChanged(listener: (event: GenerationChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "generation.changed" || !isGenerationChangedDto(event.data)) return;
        listener({ kind: "event", event: "generation.changed", data: event.data });
    });
}

/**
 * Changes of datasets: versions with revisions (null — deleted), without lists; read list / get again. Progress and outcomes of builds,
 * verifications and exports come with subscribeJobChanged (kind datasets.build / datasets.verify / datasets.export).
 */
export function subscribeDatasetsChanged(listener: (event: DatasetsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "datasets.changed" || !isDatasetsChangedDto(event.data)) return;
        listener({ kind: "event", event: "datasets.changed", data: event.data });
    });
}

/** The window is closing: answer systemApi.closeDecision within 1 s (see CloseRequestedEventDto). */
export function subscribeCloseRequested(listener: (event: CloseRequestedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "app.closeRequested" || !isCloseRequestedDto(event.data)) return;
        listener({ kind: "event", event: "app.closeRequested", data: event.data });
    });
}

/**
 * Changes of training: runs (progress, epochs, log, outcome) and model versions with revisions, without data; read getRun /
 * listModelVersions again. The state and progress of runs come with subscribeJobChanged (kind training.run).
 */
export function subscribeTrainingChanged(listener: (event: TrainingChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "training.changed" || !isTrainingChangedDto(event.data)) return;
        listener({ kind: "event", event: "training.changed", data: event.data });
    });
}

/** Training presets were saved or deleted (also from another window): read trainingApi.listPresets again. */
export function subscribeTrainingPresetsChanged(listener: (event: TrainingPresetsChangedEventDto) => void): () => void {
    return nativeBridge.subscribe(event => {
        if (event.event !== "trainingPresets.changed" || !isTrainingPresetsChangedDto(event.data)) return;
        listener({ kind: "event", event: "trainingPresets.changed", data: event.data });
    });
}

/**
 * Changes of checks: dataset evaluations with revisions (read checksApi.getEvaluation again) and snapshots of a live check (apply them by
 * check.sequence). The state and progress of evaluations also come with subscribeJobChanged (kind checks.evaluate).
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