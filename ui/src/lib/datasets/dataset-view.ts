import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { datasetOverlayColors, datasetSplitLabels } from "@/api/projects/datasets/dataset-rules";
import type { DatasetSplit } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import {
    parseDatasetBuildResult,
    parseDatasetExportResult,
    parseDatasetVerifyResult,
    type DatasetIntegrityIssueDto,
    type DatasetOperationResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import type {
    DatasetPlanSkipsDto,
    DatasetSampleResponseDto,
    DatasetVersionResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { Box } from "@/lib/annotation/geometry";

export const datasetSplits: readonly DatasetSplit[] = ["train", "val", "test"];

export const count = (value: number) => value.toLocaleString("ru-RU");

/** Поиск по списку датасетов: название и описание без учёта регистра. */
export function matchesSearch(version: DatasetVersionResponseDto, query: string): boolean {
    const needle = query.trim().toLocaleLowerCase("ru-RU");
    return !needle || version.name.toLocaleLowerCase("ru-RU").includes(needle) || version.description.toLocaleLowerCase("ru-RU").includes(needle);
}

/** Причины пропуска плана с ненулевыми счётчиками: «не проверены: 3» и т. д. */
export function skipReasons(skipped: DatasetPlanSkipsDto): { key: keyof DatasetPlanSkipsDto; label: string; count: number }[] {
    const reasons: { key: keyof DatasetPlanSkipsDto; label: string }[] = [
        { key: "unreviewed", label: "не проверены" },
        { key: "partialGeneration", label: "незавершённая генерация" },
        { key: "withoutTargets", label: "без объектов (пустые кадры не включены)" },
        { key: "invalidGeometry", label: "рамка или ROI вне кадра" },
    ];
    return reasons.map(reason => ({ ...reason, count: skipped[reason.key] })).filter(reason => reason.count > 0);
}

/** «0: A, 1: B». */
export function classOrderText(version: Pick<DatasetVersionResponseDto, "classMap">): string {
    return [...version.classMap].sort((left, right) => left.index - right.index).map(entry => `${entry.index}: ${entry.name}`).join(", ");
}

export function classLabel(version: DatasetVersionResponseDto, classId: string, index?: number): string {
    const entry = version.classMap.find(item => item.classId === classId && (index === undefined || item.index === index));
    return entry ? `${entry.index}. ${entry.name}` : index === undefined ? "Класс вне датасета" : `${index}. Класс`;
}

/** Название варианта на момент сборки или null (объект без варианта). */
export function variantName(version: DatasetVersionResponseDto, classVariantId: string | null): string | null {
    if (!classVariantId) return null;
    return version.variants.find(item => item.classVariantId === classVariantId)?.name ?? "вариант удалён";
}

/** Центр и размер YOLO → левый верхний угол. */
export function centerBox(box: { centerX: number; centerY: number; width: number; height: number }): Box {
    return { x: box.centerX - box.width / 2, y: box.centerY - box.height / 2, width: box.width, height: box.height };
}

export type SampleOverlayKind = "object" | "negative" | "roi";

export interface SampleOverlay {
    id: string;
    kind: SampleOverlayKind;
    label: string;
    color: string;
    box: Box;
}

/** Рамки образца: объекты #5fb3ff, негативы #f6b44c (пунктиром), ROI #69d3a2; подписи — класс и вариант. */
export function sampleOverlays(sample: DatasetSampleResponseDto, version: DatasetVersionResponseDto): SampleOverlay[] {
    const withVariant = (label: string, id: string | null) => {
        const name = variantName(version, id);
        return name ? `${label} · ${name}` : label;
    };
    const result: SampleOverlay[] = sample.boxes.map((box, ordinal) => ({
        id: `object-${ordinal}`, kind: "object", color: datasetOverlayColors.box,
        label: withVariant(classLabel(version, box.classId, box.index), box.classVariantId), box: centerBox(box),
    }));
    result.push(...sample.negatives.map((box, ordinal): SampleOverlay => ({
        id: `negative-${ordinal}`, kind: "negative", color: datasetOverlayColors.negative,
        label: withVariant(`Негатив · ${classLabel(version, box.classId)}`, box.classVariantId), box: centerBox(box),
    })));
    if (sample.roi) result.push({ id: "roi", kind: "roi", color: datasetOverlayColors.roi, label: "Область ROI", box: { ...sample.roi } });
    return result;
}

export function boxPixels(box: Box, width: number, height: number): string {
    const number = (value: number) => Number(value.toFixed(2)).toLocaleString("ru-RU");
    return `x ${number(box.x * width)}, y ${number(box.y * height)}, ширина ${number(box.width * width)}, высота ${number(box.height * height)} px`;
}

export const splitLabel = (split: DatasetSplit) => datasetSplitLabels[split];

/** Операция ещё идёт: её можно отменить, но не удалить из истории. */
export function isOperationActive(job: JobResponseDto): boolean {
    return job.status === "queued" || job.status === "running" || job.status === "blocked";
}

export function duration(job: JobResponseDto): string | null {
    if (!job.startedAt || !job.finishedAt) return null;
    const seconds = Math.max(0, Math.round((Date.parse(job.finishedAt) - Date.parse(job.startedAt)) / 1000));
    return Number.isFinite(seconds) ? `${Math.floor(seconds / 60)} мин ${seconds % 60} с` : null;
}

export const operationTitles = { build: "Сборка датасета", verify: "Проверка целостности", export: "Экспорт ZIP" } as const;

/**
 * Ход операции по последнему снимку задачи: итог задачи важнее processed/total. Подпись — «Собрано изображений / Проверено
 * файлов / Экспортировано изображений: p из t».
 */
export function operationProgress(operation: DatasetOperationResponseDto): { label: string; processed: number; total: number | null; percent: number | null } {
    const { job } = operation;
    let processed = job.processed ?? 0;
    let total = job.total;
    let label = "Собрано изображений";
    if (operation.kind === "build") {
        const result = parseDatasetBuildResult(job);
        if (result) { processed = result.produced; total = result.total; }
    } else if (operation.kind === "verify") {
        label = "Проверено файлов";
        const result = parseDatasetVerifyResult(job);
        if (result) { processed = result.checkedFiles; total = result.totalFiles; }
    } else {
        label = "Экспортировано изображений";
        const result = parseDatasetExportResult(job);
        if (result) { processed = result.exportedImages; total = result.totalImages; }
    }
    const percent = total ? Math.min(100, Math.floor(100 * processed / total)) : job.status === "succeeded" ? 100 : job.progress;
    return { label, processed, total, percent };
}

export const issueRoleLabels: Record<DatasetIntegrityIssueDto["role"], string> = {
    manifest: "Описание датасета",
    dataYaml: "Описание датасета",
    image: "Изображение",
    label: "Разметка",
};

/** Текст состояния операции после завершения без ошибки host (отмена, прерывание). */
export function operationEndText(operation: DatasetOperationResponseDto): string | null {
    const { job } = operation;
    if (isOperationActive(job) || job.status === "succeeded") return null;
    if (job.error) return job.error.message;
    if (job.status === "cancelled") return operation.kind === "build" ? "Сборка отменена. Исходные изображения не изменены." : "Операция отменена. Для нового запуска нажмите «Повторить».";
    if (job.status === "interrupted") return "Операция прервана. Создайте новый запуск.";
    return null;
}
