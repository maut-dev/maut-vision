using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Generation;

/// <summary>
/// Вход запуска: копия пресета (с ревизией), seed (uint32), количество кадров и название группы результата. Готовит
/// <see cref="IGenerationManager.PrepareRunAsync"/>; ID запуска — ID задачи.
/// </summary>
public sealed record GenerationRunInput(GenerationRecipe Recipe, uint Seed, int RequestedCount, string OutputGroupName);

/// <summary>
/// Запуск генерации (CPU, возобновляемый). Подготовка: проверки размера кадра (<c>generation.canvas_mismatch</c>), размера наборов
/// фонов (<c>generation.background_size_mismatch</c>), доступности вариантов и привязок (<c>generation.variant_unavailable</c>),
/// изображений (<c>generation.variant_without_asset</c>, <c>generation.unsupported_asset</c>), фонов
/// (<c>generation.backgrounds_empty</c>); снимок нужных файлов; группа результата в состоянии producing. Затем кадры рендерятся
/// окнами параллельно и публикуются строго по порядку пачками с checkpoint (producedCount). После перезапуска приложения запуск
/// продолжается с checkpoint теми же кадрами. Отмена и ошибка оставляют опубликованное, группа становится partial; успешный
/// запуск — completed. Итог — <see cref="GenerationRunResult"/>.
/// </summary>
[JobKind("generation.run")]
public interface IGenerationRunJob : IJobDefinition<GenerationRunInput>
{
}
