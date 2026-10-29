import type { BackgroundCropRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundSourceFileResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { FramePosition } from "./crop-frame";

/** The decision on a file of "Cutting backgrounds from images". */
export type CropDecision =
    | { kind: "crop"; left: number; top: number }
    | { kind: "whole" }
    | { kind: "skip" }
    | { kind: "small"; width: number; height: number }
    /** message: why the file could not be read; none when its header was unreadable. */
    | { kind: "invalid"; message?: string };

/**
 * A decision without the user by the file header: exactly the set size is taken whole, a smaller one is skipped, an unreadable one
 * goes to the "Not read" list; a larger file (null) needs a frame.
 */
export function automaticDecision(file: BackgroundSourceFileResponseDto): CropDecision | null {
    switch (file.fit) {
        case "exact":
            return { kind: "whole" };
        case "smaller":
            return { kind: "small", width: file.width ?? 0, height: file.height ?? 0 };
        case "unreadable":
            return { kind: "invalid" };
        case "larger":
            return null;
    }
}

export interface CropPlan {
    /** Files of the run in the order of choice: taken by a frame and whole. */
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

/** The earlier frame decision for a file reached again with "Back". */
export function previousRegion(decision: CropDecision | undefined): FramePosition | undefined {
    return decision?.kind === "crop" ? { left: decision.left, top: decision.top } : undefined;
}
