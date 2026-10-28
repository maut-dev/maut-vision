namespace MautVision.Desktop.Controllers.BaseModels;

internal static class BaseModelErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["base_models.not_found"] = "Эта модель отсутствует в каталоге приложения.",
        ["base_models.not_installed"] = "Сначала установите модель в каталоге моделей.",
        ["base_models.installation_active"] = "Дождитесь окончания установки модели или отмените её.",
        ["base_models.download_failed"] = "Не удалось скачать модель. Проверьте подключение к интернету и повторите установку.",
        ["base_models.source_changed"] = "Файл модели на сервере отличается от ожидаемого. Обновите приложение и повторите установку.",
        ["base_models.integrity_failed"] = "Файл модели повреждён. Повторите установку.",
        ["base_models.disk_full"] = "Недостаточно места на диске. Освободите место и повторите установку.",
        ["base_models.storage_failed"] = "Не удалось сохранить модель. Проверьте доступ к папке приложения.",
        ["base_models.install_failed"] = "Не удалось установить модель. Повторите установку.",
    };
}
