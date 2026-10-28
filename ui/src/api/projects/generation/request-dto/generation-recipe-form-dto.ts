/** Доля варианта проекта, 0–100 %. */
export interface GenerationVariantWeightDto {
    variantId: string;
    percentage: number;
}

/**
 * Настройки пресета из формы. width/height — размер кадра: вход проекта (или размер повторяемого запуска); backgroundSetIds —
 * наборы фонов в порядке выбора; classVariantIds — привязки, чей вариант проекта имеет долю больше 0 (recipeClassVariantIds);
 * variantWeights — доли всех доступных вариантов проекта (порядок задаёт выбор варианта, сумма 100); objectsPerImage — целое ≥ 0;
 * emptyProbability — доля пустых кадров 0..1 (в форме — проценты); scale — больше 0.
 */
export interface GenerationRecipeFormDto {
    name: string;
    description: string;
    width: number;
    height: number;
    backgroundSetIds: string[];
    classVariantIds: string[];
    variantWeights: GenerationVariantWeightDto[];
    objectsPerImage: number;
    emptyProbability: number;
    scale: number;
}
