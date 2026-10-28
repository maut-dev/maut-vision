import { backgroundsWord } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundOriginKind, BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { formatRadius } from "./blur-form";

export const originLabels: Record<BackgroundOriginKind, string> = {
    "uploaded": "Готовое изображение",
    "image-crop": "Фрагмент изображения",
    "video-frame": "Кадр видео",
};

const number = (value: number) => value.toLocaleString("ru-RU");

/** «N фонов · W × H[ · В архиве]». */
export function setSummary(set: BackgroundSetResponseDto): string {
    return `${number(set.itemCount)} ${backgroundsWord(set.itemCount)} · ${set.targetSize.width} × ${set.targetSize.height}${set.archivedAt ? " · В архиве" : ""}`;
}

/** «Размер набора W × H отличается от входа проекта». */
export function sizeMismatchText(set: BackgroundSetResponseDto): string {
    return `Размер набора ${set.targetSize.width} × ${set.targetSize.height} отличается от входа проекта`;
}

/** Подпись карточки: «W × H · вырезан из x L, y T · размытие r px». */
export function backgroundMeta(item: BackgroundResponseDto): string {
    const crop = item.origin.crop ? ` · вырезан из x ${item.origin.crop.left}, y ${item.origin.crop.top}` : "";
    const blur = item.origin.blurRadius !== null ? ` · размытие ${formatRadius(item.origin.blurRadius)} px` : "";
    return `${item.width} × ${item.height}${crop}${blur}`;
}

/** Строки сведений просмотрщика. */
export function backgroundDetails(item: BackgroundResponseDto): string[] {
    const { origin } = item;
    const lines = [`Источник: ${originLabels[origin.kind]} · ${(item.byteSize / 1024).toLocaleString("ru-RU", { minimumFractionDigits: 1, maximumFractionDigits: 1 })} КБ`];
    if (origin.kind === "video-frame" && origin.timestampMs !== null) {
        lines.push(`${origin.videoName ?? origin.sourceName} · ${(origin.timestampMs / 1000).toFixed(3)} с${origin.frameIndex !== null ? ` · кадр ${origin.frameIndex}` : ""}`);
    }
    if (origin.crop) lines.push(`Область: x ${origin.crop.left}, y ${origin.crop.top} · ${origin.crop.width} × ${origin.crop.height} px`);
    if (origin.blurRadius !== null) lines.push(`Размытие: ${formatRadius(origin.blurRadius)} px`);
    return lines;
}

/** «размытие a–b px, seed S». */
export function blurSummary(blur: { minRadius: number; maxRadius: number; seed: number }): string {
    return `размытие ${formatRadius(blur.minRadius)}–${formatRadius(blur.maxRadius)} px, seed ${blur.seed}`;
}
