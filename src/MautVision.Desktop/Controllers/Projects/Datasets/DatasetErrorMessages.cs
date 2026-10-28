using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Datasets;

namespace MautVision.Desktop.Controllers.Projects.Datasets;

/// <summary>
/// Texts of dataset codes, plan warnings, integrity issues and deletion blockers. User-facing texts are kept here in one place.
/// </summary>
internal static class DatasetErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["datasets.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с датасетами.",
        ["datasets.unavailable"] = "Набор данных не найден.",
        ["datasets.revision_conflict"] = "Состояние набора данных уже изменилось. Обновите экран и повторите.",
        ["datasets.archived"] = "Датасет в архиве. Восстановите его, чтобы изменить, проверить, экспортировать или обучать на нём.",
        ["datasets.corrupt"] = "Файлы датасета не прошли проверку. Откройте детали проверки или соберите новую версию из исходников.",
        ["datasets.name_invalid"] = "Введите непустое название сборки (до 120 символов).",
        ["datasets.description_invalid"] = "Описание датасета — не больше 4000 символов.",
        ["datasets.selection_invalid"] = "Выберите хотя бы один класс и одну группу кадров.",
        ["datasets.variants_invalid"] = "Вариант не может быть одновременно целевым и негативным. Обновите выбор вариантов.",
        ["datasets.roles_invalid"] = "Выберите роли объектов без варианта и объектов невыбранных классов.",
        ["datasets.split_invalid"] = "Проверьте доли: валидация больше нуля, валидация и тест вместе меньше 100%.",
        ["datasets.seed_invalid"] = "Seed — целое число от 0 до 4 294 967 295.",
        ["datasets.request_invalid"] = "Некорректный запрос. Обновите экран и повторите.",
        ["datasets.cursor_invalid"] = "Некорректная позиция списка.",
        ["datasets.group_unavailable"] = "Выбранная группа изображений удалена или в архиве. Обновите выбор групп.",
        ["datasets.class_unavailable"] = "Выбранный класс удалён или в архиве. Обновите выбор классов.",
        ["datasets.unselected_class"] = "В выбранных группах есть объекты классов, не включённых в выборку.",
        ["datasets.unassigned_variant"] = "В выбранных группах есть объекты без варианта, а для них выбран отказ.",
        ["datasets.variant_role_missing"] = "В кадрах есть объекты вариантов, которым не назначена роль (например, архивных).",
        ["datasets.plan_stale"] = "Группы, разметка или классы изменились после проверки. Проверьте план перед сборкой снова.",
        ["datasets.input_size_mismatch"] = "Размер входа датасета не совпадает с настройками проекта. Соберите датасет заново.",
        ["datasets.empty"] = "В плане нет кадров для сборки. Выберите группы с проверенной разметкой.",
        ["datasets.split_collision"] = "Одинаковые кадры попали в разные части датасета. Уберите повторы или измените назначение групп.",
        ["datasets.source_missing"] = "Исходное изображение удалено или недоступно. Проверьте план и соберите датасет снова.",
        ["datasets.source_changed"] = "Исходное изображение изменилось после импорта. Проверьте файлы проекта и соберите датасет снова.",
        ["datasets.build_failed"] = "Не удалось собрать датасет. Подробности — в журнале.",
        ["datasets.export_failed"] = "Не удалось записать ZIP. Выберите доступное место сохранения и повторите экспорт.",
        ["datasets.export_destination"] = "Не удалось записать ZIP. Выберите доступное место сохранения и повторите экспорт.",
        ["datasets.export_selection_expired"] = "Выбор места сохранения устарел. Выберите место заново.",
        ["datasets.disk_full"] = "Недостаточно места на диске. Освободите место и повторите операцию.",
        ["datasets.out_of_memory"] = "Недостаточно памяти для этой операции. Уменьшите объём обработки и повторите запуск.",
        ["datasets.operation_active"] = "Сначала отмените операцию и дождитесь завершения обработки.",
        ["datasets.deletion_blocked"] = "Удалению мешают текущие операции или обучение. Проверьте зависимости в окне удаления.",
        ["datasets.deletion_revision_conflict"] = "Датасет или история изменились после проверки. Проверьте состав удаления снова.",
        ["datasets.classes_invalid"] = "В датасете нет корректного списка классов.",
        ["datasets.materialize_target"] = "Папка задачи обучения уже занята. Повторите запуск.",
        ["datasets.file_missing"] = "Файл отсутствует.",
        ["datasets.file_changed"] = "Содержимое отличается от сохранённой версии.",
        ["datasets.file_unreadable"] = "Файл недоступен или повреждён.",
    };

    /// <summary>The text of a plan warning with its count.</summary>
    internal static string Warning(DatasetPlanWarning warning) => warning.Code switch
    {
        "datasets.insufficient_samples" => "Недостаточно независимых источников для разбиения. Добавьте изображения другого происхождения.",
        "datasets.mixed_source_splits" => $"Один источник попадает в разные части датасета (кадров: {warning.Count}). Каждый кадр следует назначению своей группы.",
        "datasets.objects_as_negatives" => $"Объекты архивных или удалённых классов и вариантов станут негативами: {warning.Count}.",
        "datasets.split_collision" => $"Одинаковые кадры в разных частях датасета: {warning.Count}. Уберите повторы или измените назначение групп.",
        _ => $"Предупреждение плана: {warning.Count}.",
    };

    /// <summary>A line of the blocker list in the deletion dialog.</summary>
    internal static string For(DeletionBlocker blocker) => blocker.Code switch
    {
        "datasets.verify_active" => "Идёт проверка целостности этого датасета.",
        "datasets.export_active" => "Идёт экспорт этого датасета.",
        "datasets.reader_active" => "Датасет передаётся обучению.",
        "training.active" => $"Датасет используется обучением: запусков в очереди или в работе — {blocker.Count}.",
        "checks.evaluation_active" => $"Датасет используется проверкой модели: проверок в очереди или в работе — {blocker.Count}.",
        _ => $"Датасет используется: {blocker.Count}." + (blocker.Examples.Count > 0 ? " " + string.Join(", ", blocker.Examples) + "." : ""),
    };
}
