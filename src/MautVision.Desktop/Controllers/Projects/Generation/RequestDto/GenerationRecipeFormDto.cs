namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

/// <summary>Share of a project variant, 0–100 %.</summary>
public sealed record GenerationVariantWeightDto(Guid VariantId, double Percentage);

/// <summary>
/// Preset settings from the form: frame size (the project input or the size of the repeated run), background sets in selection order,
/// variant bindings with a share above 0, shares of all available project variants (sum 100), objects per frame (integer ≥ 0), share
/// of empty frames 0..1, scale above 0.
/// </summary>
public sealed record GenerationRecipeFormDto(
    string Name,
    string Description,
    int Width,
    int Height,
    Guid[] BackgroundSetIds,
    Guid[] ClassVariantIds,
    GenerationVariantWeightDto[] VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale);
