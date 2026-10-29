import { isRecord } from "../../../transport/envelopes";
import { isCount, isDate, isDateOrNull, isId, isRevision } from "../../catalog/catalog-guards";

/** The purpose: "Unassigned / Training / Validation / Test". */
export type ImageSplitHint = "unassigned" | "train" | "validation" | "test";

export type ImageGroupOrigin = "manual" | "generated";

export type ImageGenerationState = "producing" | "completed" | "partial";

export const imageSplitHints: readonly ImageSplitHint[] = ["unassigned", "train", "validation", "test"];

const origins: readonly string[] = ["manual", "generated"] satisfies ImageGroupOrigin[];
const generationStates: readonly string[] = ["producing", "completed", "partial"] satisfies ImageGenerationState[];

/**
 * An image group. imageCount counts all images, archived included; archivedImageCount the archived ones ("Frames: N").
 * revision grows when group fields change, on archiving and on moving images; an import changes only the counters (their
 * change comes in images.changed, imagesChangedIn). generationState is only set on generated groups;
 * while producing the group is read-only: "Generation is filling this group. Editing becomes available when it finishes."
 */
export interface ImageGroupResponseDto {
    id: string;
    name: string;
    description: string;
    origin: ImageGroupOrigin;
    splitHint: ImageSplitHint;
    revision: number;
    createdAt: string;
    updatedAt: string;
    archivedAt: string | null;
    generationRunId: string | null;
    generationState: ImageGenerationState | null;
    imageCount: number;
    archivedImageCount: number;
}

export function isImageGroupResponseDto(value: unknown): value is ImageGroupResponseDto {
    return isRecord(value) && isId(value.id) && typeof value.name === "string" && typeof value.description === "string"
        && typeof value.origin === "string" && origins.includes(value.origin)
        && typeof value.splitHint === "string" && (imageSplitHints as readonly string[]).includes(value.splitHint)
        && isRevision(value.revision) && isDate(value.createdAt) && isDate(value.updatedAt) && isDateOrNull(value.archivedAt)
        && (value.generationRunId === null || isId(value.generationRunId))
        && (value.generationState === null || (typeof value.generationState === "string" && generationStates.includes(value.generationState)))
        && isCount(value.imageCount) && isCount(value.archivedImageCount);
}

export function isImageGroupList(value: unknown): value is ImageGroupResponseDto[] {
    return Array.isArray(value) && value.every(isImageGroupResponseDto);
}
