using MautVision.Core.Interfaces.Projects.Catalog;

namespace MautVision.Desktop.Controllers.Projects.Generation;

/// <summary>Texts of generation codes; run errors name the failed phase.</summary>
internal static class GenerationErrorMessages
{
    /// <summary>The form notice of «Настроить повтор» (the previous text without the removed thread count).</summary>
    internal const string RepeatNotice = "Настройки сохранённого запуска перенесены в форму. Сохраните новый пресет и запустите его.";

    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["generation.variant_unavailable"] = "Выбранный вариант удалён, архивирован или недоступен. Проверьте варианты и классы в настройках пресета.",
        ["generation.variant_without_asset"] = "У выбранного варианта нет доступных изображений. Добавьте исходники или измените пресет.",
        ["generation.unsupported_asset"] = "Исходное изображение не подходит для генерации. Проверьте формат и прозрачный фон изображений класса.",
        ["generation.background_size_mismatch"] = "Размер выбранного набора фонов не совпадает с размером кадра. Выберите подходящий набор.",
        ["generation.canvas_mismatch"] = "Размер кадра пресета не совпадает с текущими настройками проекта. Создайте пресет для нужного размера.",
        ["generation.backgrounds_empty"] = "В выбранных наборах нет доступных фонов. Добавьте фоны или измените пресет.",
        ["generation.backgrounds_unavailable"] = "Выбранный набор фонов удалён, архивирован или недоступен. Проверьте фоны в пресете.",
        ["generation.invalid_recipe"] = "Настройки генерации некорректны. Проверьте размер кадра, доли вариантов и параметры объектов.",
        ["generation.unavailable"] = "Пресет или запуск больше недоступен. Обновите список и выберите действующий пресет.",
        ["generation.interrupted"] = "Генерация прервана закрытием приложения. Сохранённые результаты доступны, если группа результата сохранилась.",
        ["generation.cancelled"] = "Генерация отменена. Сохранённые результаты доступны, если группа результата сохранилась.",
        ["generation.preparation_failed"] = "Не удалось подготовить источники генерации. Проверьте исходные изображения и наборы фонов перед повтором.",
        ["generation.execution_failed"] = "Не удалось завершить генерацию. Проверьте исходники и сохранённые результаты перед повтором.",
        ["generation.disk_full"] = "Недостаточно места на диске. Освободите место и повторите операцию.",
        ["generation.out_of_memory"] = "Недостаточно памяти. Уменьшите объём обработки или число потоков и повторите запуск.",
        ["generation.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с генерацией.",
        ["generation.revision_conflict"] = "Пресет уже изменился. Обновите список и повторите действие.",
        ["generation.recipe_archived"] = "Пресет в архиве. Восстановите его, чтобы изменить или запустить.",
        ["generation.deletion_blocked"] = "Пресет нельзя удалить, пока идёт его запуск. Дождитесь завершения или отмените запуск.",
        ["generation.run_active"] = "Запуск ещё не завершён. Дождитесь завершения или отмените его перед удалением.",
        ["generation.deleted_operation"] = "Этот запуск удалён и не может быть запущен снова. Запустите пресет заново.",
        ["generation.count_invalid"] = "Количество изображений — целое число от 1.",
        ["generation.seed_invalid"] = "Seed — целое число от 0 до 4 294 967 295.",
    };

    /// <summary>A line of the blocker list in a deletion dialog of the catalog or the backgrounds: «Пресет «A», Пресет «B» и другие.»</summary>
    internal static string PresetReference(DeletionBlocker blocker)
        => "Используется в пресетах генерации: " + string.Join(", ", blocker.Examples.Select(name => $"Пресет «{name}»"))
            + (blocker.Count > blocker.Examples.Count ? " и другие." : ".");
}
