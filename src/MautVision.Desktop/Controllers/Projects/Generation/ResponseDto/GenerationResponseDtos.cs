using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Generation.RequestDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Generation.ResponseDto;

/// <summary>
/// Пресет генерации: настройки формы, Revision — для изменения, архива и запуска; ArchivedAt — «В архиве». Удалённые и архивные
/// наборы, привязки и варианты UI отмечает по своим спискам.
/// </summary>
public sealed record GenerationRecipeResponseDto(
    Guid Id,
    string Name,
    string Description,
    int Width,
    int Height,
    Guid[] BackgroundSetIds,
    Guid[] ClassVariantIds,
    GenerationVariantWeightDto[] VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

/// <summary>
/// Запуск: Job — снимок задачи generation.run (состояние, прогресс processed/total — «P / R изображений», ошибка с текстом, итог);
/// Recipe — копия пресета («Настройки этого запуска»); ProducedCount — опубликовано; MissingObjectFrames — кадров с недостающими
/// объектами; OutputGroupId — группа результата («Открыть изображения»; null, пока запуск не подготовлен); OutputGroupName — её имя.
/// </summary>
public sealed record GenerationRunResponseDto(
    JobResponseDto Job,
    GenerationRecipeResponseDto Recipe,
    uint Seed,
    int RequestedCount,
    int ProducedCount,
    int MissingObjectFrames,
    Guid? OutputGroupId,
    string OutputGroupName);

/// <summary>Страница запусков, новые первыми; Total — «Запуски (N)».</summary>
public sealed record GenerationRunPageResponseDto(GenerationRunResponseDto[] Items, long Total);

public sealed record GenerationRecipeDeletionResponseDto(Guid RecipeId);

/// <summary>Запуск удалён; изображения группы результата сохранены.</summary>
public sealed record GenerationRunDeletionResponseDto(Guid RunId);

/// <summary>«Настроить повтор»: настройки для формы нового пресета (имя « · повтор»), seed и количество запуска, уведомление для формы.</summary>
public sealed record GenerationRepeatDraftResponseDto(GenerationRecipeFormDto Recipe, uint Seed, int RequestedCount, Guid SourceRunId);

/// <summary>Объект кадра: класс, привязка (её цвет рамки берётся из каталога) и рамка в долях кадра.</summary>
public sealed record GenerationSampleObjectDto(Guid ClassId, Guid ClassVariantId, ImageBoxResponseDto Box);

/// <summary>
/// Кадр «Пример кадра»: ImageUrl — временный ресурс PNG (действует 10 минут для этой страницы), Width/Height — размер кадра,
/// BackgroundId — фон, Objects — рамки, MissingObjects — сколько объектов не поместилось.
/// </summary>
public sealed record GenerationSampleResponseDto(int SampleIndex, string ImageUrl, int Width, int Height, Guid BackgroundId, GenerationSampleObjectDto[] Objects,
    int MissingObjects);

public sealed record GenerationSamplesResponseDto(GenerationSampleResponseDto[] Samples);

/// <summary>Изменённый или удалённый (Revision = null) пресет.</summary>
public sealed record GenerationEntityChangeDto(Guid Id, long? Revision);

/// <summary>
/// Событие generation.changed: Recipes — пресеты с новыми ревизиями или удалённые; Runs — запуски, у которых изменились данные
/// помимо состояния задачи (подготовлен — появилась группа результата; удалён): перечитать их getRun. Состояние и прогресс — job.changed.
/// </summary>
public sealed record GenerationChangedEventDto(string ProjectId, GenerationEntityChangeDto[] Recipes, Guid[] Runs);
