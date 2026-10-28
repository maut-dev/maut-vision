namespace MautVision.Desktop.Controllers.Projects.Generation.RequestDto;

/// <summary>Доля варианта проекта, 0–100 %.</summary>
public sealed record GenerationVariantWeightDto(Guid VariantId, double Percentage);

/// <summary>
/// Настройки пресета из формы: размер кадра (вход проекта или размер повторяемого запуска), наборы фонов в порядке выбора,
/// привязки вариантов с долей больше 0, доли всех доступных вариантов проекта (сумма 100), объектов на кадр (целое ≥ 0), доля
/// пустых кадров 0..1, масштаб больше 0.
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
