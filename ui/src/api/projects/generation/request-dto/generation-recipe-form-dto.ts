/** A share of a project variant, 0–100 %. */
export interface GenerationVariantWeightDto {
    variantId: string;
    percentage: number;
}

/**
 * Preset settings from the form. width/height — the frame size: the project input (or the size of the repeated run); backgroundSetIds —
 * background sets in the order of choice; classVariantIds — bindings whose project variant has a share above 0 (recipeClassVariantIds);
 * variantWeights — shares of all available project variants (the order follows the choice, the sum is 100); objectsPerImage — an integer ≥ 0;
 * emptyProbability — the share of empty frames 0..1 (percent in the form); scale — above 0.
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
