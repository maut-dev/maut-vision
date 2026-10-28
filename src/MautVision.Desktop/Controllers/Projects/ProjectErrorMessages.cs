namespace MautVision.Desktop.Controllers.Projects;

internal static class ProjectErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["projects.not_found"] = "Проект не найден. Обновите список проектов.",
        ["projects.name_invalid"] = "Введите название проекта длиной до 120 символов.",
        ["projects.description_invalid"] = "Описание проекта должно быть не длиннее 2000 символов.",
        ["projects.base_model_not_found"] = "Выберите базовую модель из каталога.",
        ["projects.base_model_not_installed"] = "Сначала установите выбранную модель в каталоге базовых моделей.",
        ["projects.input_size_invalid"] = "Ширина и высота входа должны быть положительными и кратными шагу модели.",
        ["projects.revision_conflict"] = "Проект уже изменился. Закройте настройки, проверьте данные и повторите изменение.",
        ["projects.archived"] = "Проект находится в архиве. Восстановите его, чтобы изменить настройки.",
        ["projects.storage_newer"] = "Проект сохранён более новой версией Maut Vision. Обновите приложение, чтобы открыть его.",
        ["projects.storage_migration_failed"] = "Не удалось обновить данные проекта до новой версии. Данные не изменены; подробности в журнале.",
        ["projects.storage_damaged"] = "База данных проекта повреждена, проект нельзя открыть. Подробности в журнале.",
        ["projects.open_superseded"] = "Открыт другой проект.",
        ["projects.deleting"] = "Проект удаляется.",
        ["projects.busy"] = "Идут фоновые задачи. Отмените их и повторите удаление проекта.",
        ["projects.deletion_conflict"] = "Проект изменился после открытия подтверждения. Обновите список проектов и повторите удаление.",
        ["projects.stop_timeout"] = "Задачи проекта не остановились. Повторите удаление позже.",
        ["projects.transfer_busy"] = "В проекте идут фоновые задачи (раздел «{section}»). Дождитесь их завершения или отмените и повторите экспорт.",
        ["projects.transfer_active"] = "Идёт экспорт проекта. Дождитесь его завершения и повторите.",
        ["projects.export_destination"] = "Не удалось записать архив в выбранное место. Выберите другое место сохранения и повторите экспорт.",
        ["projects.export_selection_expired"] = "Выбор места сохранения устарел. Выберите место заново.",
        ["projects.export_failed"] = "Не удалось экспортировать проект. Проверьте место сохранения и повторите; подробности в журнале.",
        ["projects.disk_full"] = "Недостаточно места на диске. Освободите место и повторите.",
        ["projects.out_of_memory"] = "Недостаточно памяти для этой операции. Закройте другие программы и повторите.",
        ["projects.import_selection_expired"] = "Выбор файла устарел. Выберите файл заново.",
        ["projects.import_active"] = "Уже выполняется импорт проекта. Дождитесь его завершения.",
        ["projects.import_not_found"] = "Импорт не найден. Обновите экран.",
        ["projects.import_failed"] = "Импорт не выполнен. Подробности в журнале.",
        ["projects.archive_changed"] = "Файл изменился после выбора. Выберите его заново.",
        ["projects.archive_damaged"] = "Архив повреждён. Экспортируйте проект заново и повторите импорт.",
        ["projects.archive_unknown"] = "Архив неизвестного формата: это не архив проекта Maut Vision.",
        ["projects.archive_legacy"] = "Архив создан прежней версией Maut Vision и не поддерживается. Экспортируйте проект заново в текущей версии.",
    };
}
