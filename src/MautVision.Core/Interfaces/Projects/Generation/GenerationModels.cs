using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Images;

namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>Доля варианта проекта в кадрах: 0–100 %, сумма долей пресета — 100 % (± 1e-6).</summary>
public sealed record GenerationVariantWeight(Guid VariantId, double Percentage);

/// <summary>
/// Настройки пресета из формы. Width/Height — размер кадра (обычно вход проекта; кратны 32). BackgroundSetIds — наборы фонов
/// в порядке выбора; ClassVariantIds — привязки (варианты классов), чей вариант проекта имеет долю больше 0; VariantWeights —
/// доли вариантов проекта (порядок задаёт выбор варианта). ObjectsPerImage — целое ≥ 0, EmptyProbability — 0..1,
/// Scale — больше 0, наибольшая доля стороны кадра (больше 1 действует как 1).
/// </summary>
public sealed record GenerationRecipeInput(
    string Name,
    string Description,
    int Width,
    int Height,
    IReadOnlyList<Guid> BackgroundSetIds,
    IReadOnlyList<Guid> ClassVariantIds,
    IReadOnlyList<GenerationVariantWeight> VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale);

/// <summary>
/// Пресет генерации. Revision растёт с каждым сохранённым изменением (правка, архив); запуск хранит копию пресета с его ревизией.
/// Архивный пресет только для чтения, кроме восстановления и удаления.
/// </summary>
public sealed record GenerationRecipe(
    Guid Id,
    string Name,
    string Description,
    int Width,
    int Height,
    IReadOnlyList<Guid> BackgroundSetIds,
    IReadOnlyList<Guid> ClassVariantIds,
    IReadOnlyList<GenerationVariantWeight> VariantWeights,
    int ObjectsPerImage,
    double EmptyProbability,
    double Scale,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    /// <summary>Настройки пресета в виде формы.</summary>
    public GenerationRecipeInput ToInput() => new(Name, Description, Width, Height, BackgroundSetIds, ClassVariantIds, VariantWeights,
        ObjectsPerImage, EmptyProbability, Scale);
}

/// <summary>
/// Запуск генерации — задача <see cref="IGenerationRunJob"/>. Job — её снимок (состояние, прогресс: Processed — опубликовано,
/// Total — запрошено; ошибка; итог <see cref="GenerationRunResult"/>). Recipe — копия пресета на момент запуска.
/// ProducedCount — непрерывный префикс опубликованных кадров; OutputGroupId — группа результата (null, пока запуск не подготовлен).
/// </summary>
public sealed record GenerationRun(
    JobInfo Job,
    GenerationRecipe Recipe,
    uint Seed,
    int RequestedCount,
    int ProducedCount,
    int MissingObjectFrames,
    Guid? OutputGroupId,
    string OutputGroupName);

/// <summary>Страница запусков, новые первыми; Total — все запуски проекта.</summary>
public sealed record GenerationRunPage(IReadOnlyList<GenerationRun> Items, long Total);

/// <summary>
/// Итог запуска (в <see cref="JobInfo.Result"/>, также после отмены и сбоя): RequestedCount, ProducedCount — опубликовано,
/// MissingObjectFrames — кадров, в которых не нашлось места части объектов («Кадров с недостающими объектами: N»),
/// OutputGroupId — группа результата, Seed.
/// </summary>
public sealed record GenerationRunResult(int RequestedCount, int ProducedCount, int MissingObjectFrames, Guid? OutputGroupId, uint Seed);

/// <summary>«Настроить повтор»: настройки запуска для формы (к имени добавлено « · повтор»), его seed и количество.</summary>
public sealed record GenerationRepeatDraft(GenerationRecipeInput Recipe, uint Seed, int RequestedCount, Guid SourceRunId);

/// <summary>Объект кадра: класс, привязка (вариант класса) и рамка в долях кадра.</summary>
public sealed record GenerationSampleObject(Guid ClassId, Guid ClassVariantId, ImageBox Box);

/// <summary>
/// «Пример кадра»: PNG тех же пикселей, что даст запуск с этим seed для SampleIndex, фон BackgroundId, объекты с рамками и
/// MissingObjects — сколько объектов не поместилось.
/// </summary>
public sealed record GenerationSample(int SampleIndex, Guid BackgroundId, IReadOnlyList<GenerationSampleObject> Objects, int MissingObjects,
    byte[] Png, int Width, int Height);

/// <summary>Изменённый или удалённый (Revision = null) пресет.</summary>
public sealed record GenerationEntityChange(Guid Id, long? Revision);

/// <summary>
/// Что изменилось в генерации проекта: Recipes — пресеты с новыми ревизиями или удалённые; Runs — запуски, у которых изменились
/// данные помимо состояния задачи (подготовлен — появилась группа результата; удалён). Состояние и прогресс — событие JobChanged.
/// </summary>
public sealed class GenerationChangedEventArgs(string projectId, IReadOnlyList<GenerationEntityChange> recipes, IReadOnlyList<Guid> runs) : EventArgs
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<GenerationEntityChange> Recipes { get; } = recipes;
    public IReadOnlyList<Guid> Runs { get; } = runs;
}
