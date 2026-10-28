namespace MautVision.Desktop.Controllers.Jobs;

internal static class JobErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["jobs.not_found"] = "Задача не найдена. Обновите список задач.",
        ["jobs.project_not_open"] = "Проект не открыт. Откройте проект, чтобы увидеть его задачи.",
        ["jobs.failed"] = "Задача завершилась с ошибкой. Подробности в журнале.",
        ["jobs.interrupted"] = "Задача прервана: приложение было закрыто во время её выполнения. Запустите её снова.",
        ["jobs.kind_unknown"] = "Эта версия приложения не умеет выполнять задачу. Данные задачи сохранены.",
        ["jobs.input_invalid"] = "Параметры задачи несовместимы с этой версией приложения. Данные задачи сохранены.",
        ["jobs.checkpoint_invalid"] = "Сохранённый прогресс задачи несовместим с этой версией приложения. Данные задачи сохранены.",
    };
}
