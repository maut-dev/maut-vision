import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isImageBoxDto, type ImageBoxDto } from "../../images/response-dto/image-annotation-response-dto";
import { isGenerationRecipeFormDto } from "./generation-recipe-response-dto";
import type { GenerationRecipeFormDto } from "../request-dto/generation-recipe-form-dto";

/** An object of a frame: the class, the binding (box color — displayColor of the binding from the catalog) and the box in frame fractions. */
export interface GenerationSampleObjectDto {
    classId: string;
    classVariantId: string;
    box: ImageBoxDto;
}

/**
 * A frame of "Sample frame": imageUrl — a temporary PNG (valid for 10 minutes for this page; on a loading error request the samples
 * again), width/height — the frame size, backgroundId — the background, objects — boxes over the image, missingObjects — how many objects
 * did not fit.
 */
export interface GenerationSampleResponseDto {
    sampleIndex: number;
    imageUrl: ResourceUrl;
    width: number;
    height: number;
    backgroundId: string;
    objects: GenerationSampleObjectDto[];
    missingObjects: number;
}

export interface GenerationSamplesResponseDto {
    samples: GenerationSampleResponseDto[];
}

/**
 * "Set up a repeat": recipe — the run settings for the form "New preset" (the name with " · repeat"), seed and requestedCount — to
 * "Run parameters", sourceRunId — the source run.
 */
export interface GenerationRepeatDraftResponseDto {
    recipe: GenerationRecipeFormDto;
    seed: number;
    requestedCount: number;
    sourceRunId: string;
}

function isSampleObject(value: unknown): value is GenerationSampleObjectDto {
    return isRecord(value) && isId(value.classId) && isId(value.classVariantId) && isImageBoxDto(value.box);
}

function isSample(value: unknown): value is GenerationSampleResponseDto {
    return isRecord(value) && isCount(value.sampleIndex) && isResourceUrl(value.imageUrl) && isCount(value.width) && isCount(value.height)
        && isId(value.backgroundId) && Array.isArray(value.objects) && value.objects.every(isSampleObject) && isCount(value.missingObjects);
}

export function isGenerationSamplesResponseDto(value: unknown): value is GenerationSamplesResponseDto {
    return isRecord(value) && Array.isArray(value.samples) && value.samples.every(isSample);
}

export function isGenerationRepeatDraftResponseDto(value: unknown): value is GenerationRepeatDraftResponseDto {
    return isRecord(value) && isGenerationRecipeFormDto(value.recipe) && isCount(value.seed) && isCount(value.requestedCount)
        && isId(value.sourceRunId);
}
