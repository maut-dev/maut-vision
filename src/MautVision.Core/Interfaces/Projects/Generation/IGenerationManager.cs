namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>
/// Генерация проекта: пресеты (правила наложения объектов на фоны) и запуски. Работает только у открытого проекта
/// (<c>generation.project_not_open</c>). Изменения пресетов сверяют ревизию (<c>generation.revision_conflict</c>) и сообщаются
/// событием <see cref="IProjectsManager.GenerationChanged"/>; состояние запусков — событием JobChanged задачи
/// <see cref="IGenerationRunJob"/>. Отсутствующие пресет или запуск — <c>generation.unavailable</c>.
/// </summary>
public interface IGenerationManager
{
    /// <summary>Все пресеты, включая архивные, по времени создания.</summary>
    Task<IReadOnlyList<GenerationRecipe>> ListRecipesAsync(CancellationToken cancellationToken = default);

    Task<GenerationRecipe> GetRecipeAsync(Guid recipeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Новый пресет. Неверные настройки — <c>generation.invalid_recipe</c> с полем (Details["field"]: name, description, canvas,
    /// backgroundSetIds, classVariantIds, variantWeights, objectsPerImage, emptyProbability, scale). Наборы фонов, привязки и
    /// варианты должны существовать (могут быть в архиве: это проверит запуск).
    /// </summary>
    Task<GenerationRecipe> CreateRecipeAsync(GenerationRecipeInput input, CancellationToken cancellationToken = default);

    /// <summary>Изменяет пресет с ревизией, которую видел пользователь; архивный — <c>generation.recipe_archived</c>.</summary>
    Task<GenerationRecipe> UpdateRecipeAsync(Guid recipeId, GenerationRecipeInput input, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>«В архив» / «Восстановить»; повтор с тем же значением ничего не меняет.</summary>
    Task<GenerationRecipe> SetRecipeArchivedAsync(Guid recipeId, bool archived, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет пресет (и архивный). Незавершённый запуск этого пресета блокирует удаление (<c>generation.deletion_blocked</c>);
    /// запуски хранят копию пресета и остаются.
    /// </summary>
    Task DeleteRecipeAsync(Guid recipeId, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Запуски проекта, новые первыми (limit 1–200, before — ID последнего запуска предыдущей страницы).</summary>
    Task<GenerationRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default);

    Task<GenerationRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверяет запуск и готовит вход задачи <see cref="IGenerationRunJob"/>; задачу ставит ICore.Jobs.EnqueueAsync с тем же
    /// operationId. Проверки: пресет есть и не в архиве (<c>generation.unavailable</c>), ревизия (<c>generation.revision_conflict</c>),
    /// количество ≥ 1 (<c>generation.count_invalid</c>), размер кадра равен входу проекта (<c>generation.canvas_mismatch</c>),
    /// источники (как у запуска, без декодирования), operationId удалённого запуска (<c>generation.deleted_operation</c>).
    /// Если запуск этой операции уже есть, возвращает его вход (повтор запроса).
    /// </summary>
    Task<GenerationRunInput> PrepareRunAsync(Guid recipeId, long expectedRevision, uint seed, int count, string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет завершённый запуск (<c>generation.run_active</c> для незавершённого); изображения группы результата сохраняются.
    /// Тот же operationId больше не запускает генерацию.
    /// </summary>
    Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>«Настроить повтор»: настройки запуска для формы нового пресета, seed и количество.</summary>
    Task<GenerationRepeatDraft> GetRepeatDraftAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// «Пример кадра»: до 4 кадров (sampleIndex 0..count−1) настроек формы с этим seed — те же пиксели и рамки, что даст запуск,
    /// без сохранения. Проверки — как у пресета и запуска (включая <c>generation.unsupported_asset</c>).
    /// </summary>
    Task<IReadOnlyList<GenerationSample>> PreviewSamplesAsync(GenerationRecipeInput input, uint seed, int count = 4,
        CancellationToken cancellationToken = default);
}
