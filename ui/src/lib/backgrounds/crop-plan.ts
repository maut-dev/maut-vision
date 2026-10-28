import type { BackgroundCropRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundSourceFileResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { FramePosition } from "./crop-frame";

/** Решение по файлу «Нарезки фонов из изображений». */
export type CropDecision =
    | { kind: "crop"; left: number; top: number }
    | { kind: "whole" }
    | { kind: "skip" }
    | { kind: "small"; width: number; height: number }
    | { kind: "invalid"; message: string };

export const unreadableText = "Не удалось прочитать изображение";

/**
 * Решение без участия пользователя по заголовку файла: ровно размера набора — целиком, меньше — пропуск, нечитаемый —
 * в список «Не прочитано»; больший файл (null) требует рамки.
 */
export function automaticDecision(file: BackgroundSourceFileResponseDto): CropDecision | null {
    switch (file.fit) {
        case "exact":
            return { kind: "whole" };
        case "smaller":
            return { kind: "small", width: file.width ?? 0, height: file.height ?? 0 };
        case "unreadable":
            return { kind: "invalid", message: unreadableText };
        case "larger":
            return null;
    }
}

export interface CropPlan {
    /** Файлы запуска по порядку выбора: взятые по рамке и целиком. */
    sourceIndices: number[];
    crops: BackgroundCropRequestDto[];
    counts: Record<CropDecision["kind"], number>;
}

export function cropPlan(decisions: ReadonlyMap<number, CropDecision>): CropPlan {
    const counts: Record<CropDecision["kind"], number> = { crop: 0, whole: 0, skip: 0, small: 0, invalid: 0 };
    const sourceIndices: number[] = [];
    const crops: BackgroundCropRequestDto[] = [];
    for (const index of [...decisions.keys()].sort((left, right) => left - right)) {
        const decision = decisions.get(index)!;
        counts[decision.kind]++;
        if (decision.kind === "whole") sourceIndices.push(index);
        if (decision.kind === "crop") {
            sourceIndices.push(index);
            crops.push({ sourceIndex: index, left: decision.left, top: decision.top });
        }
    }
    return { sourceIndices, crops, counts };
}

/** Прежнее решение рамкой для файла, к которому вернулись «Назад». */
export function previousRegion(decision: CropDecision | undefined): FramePosition | undefined {
    return decision?.kind === "crop" ? { left: decision.left, top: decision.top } : undefined;
}
