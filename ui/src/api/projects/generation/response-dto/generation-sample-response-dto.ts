import { isResourceUrl, type ResourceUrl } from "../../../resources/resource-url";
import { isRecord } from "../../../transport/envelopes";
import { isCount, isId } from "../../catalog/catalog-guards";
import { isImageBoxDto, type ImageBoxDto } from "../../images/response-dto/image-annotation-response-dto";
import { isGenerationRecipeFormDto } from "./generation-recipe-response-dto";
import type { GenerationRecipeFormDto } from "../request-dto/generation-recipe-form-dto";

/** Объект кадра: класс, привязка (цвет рамки — displayColor привязки из каталога) и рамка в долях кадра. */
export interface GenerationSampleObjectDto {
    classId: string;
    classVariantId: string;
    box: ImageBoxDto;
}

/**
 * Кадр «Пример кадра»: imageUrl — временный PNG (действует 10 минут для этой страницы; при ошибке загрузки запросить примеры
 * снова), width/height — размер кадра, backgroundId — фон, objects — рамки поверх изображения, missingObjects — сколько объектов
 * не поместилось.
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
 * «Настроить повтор»: recipe — настройки запуска для формы «Новый пресет» (имя с « · повтор»), seed и requestedCount — в
 * «Параметры запуска», notice — уведомление над формой, sourceRunId — запуск-источник.
 */
export interface GenerationRepeatDraftResponseDto {
    recipe: GenerationRecipeFormDto;
    seed: number;
    requestedCount: number;
    sourceRunId: string;
    notice: string;
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
        && isId(value.sourceRunId) && typeof value.notice === "string";
}
