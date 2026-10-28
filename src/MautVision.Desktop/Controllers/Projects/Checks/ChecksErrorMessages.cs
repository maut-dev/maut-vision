namespace MautVision.Desktop.Controllers.Projects.Checks;

/// <summary>
/// Texts of check and screen codes. A
/// worker failure (checks.worker_&lt;code&gt;) is shown with the worker's own Russian message in the check; the texts below are for places
/// that have only the code. Placeholders in braces take values of the error details.
/// </summary>
internal static class ChecksErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["checks.project_not_open"] = "Проект не открыт. Откройте проект, чтобы проверять модели.",
        ["checks.evaluation_not_found"] = "Проверка модели не найдена.",
        ["checks.check_not_found"] = "Проверка не найдена: живые проверки не сохраняются и пропадают после перезапуска приложения.",
        ["checks.runtime_not_configured"] = "Среда проверок не настроена: не задан путь к Python или работнику.",
        ["checks.runtime_missing"] = "Файлы среды проверок не найдены.",
        ["checks.evaluation_parameters"] = "Проверьте параметры проверки: положительный целый batch, устройство (cpu или номер видеокарты), часть датасета val или test.",
        ["checks.evaluation_classes_mismatch"] = "Классы датасета не совпадают с классами версии (состав или порядок): проверка на нём бессмысленна.",
        ["checks.evaluation_input_mismatch"] = "Размер кадров датасета {datasetWidth} × {datasetHeight} не совпадает со входом версии {versionWidth} × {versionHeight}.",
        ["checks.evaluation_split_empty"] = "В части {split} этого датасета нет кадров.",
        ["checks.evaluation_active"] = "Идёт проверка этой версии на датасете.",
        ["checks.live_active"] = "Идёт живая проверка этой версии.",
        ["checks.prediction_parameters"] = "Проверьте параметры проверки: порог уверенности от 0 до 1, устройство (cpu или номер видеокарты), положительный шаг видео, частота экрана от 0 до 60 кадров в секунду, предел времени от 1 секунды до 24 часов.",
        ["checks.prediction_source"] = "Файл для проверки не выбран или не подходит: нужна картинка PNG, JPEG, WebP, BMP или видео MP4, MKV, AVI, MOV, WebM, M4V.",
        ["checks.source_changed"] = "Файл изменился после выбора. Выберите его заново.",
        ["checks.selection_expired"] = "Выбор файла устарел. Выберите файл заново.",
        ["checks.region_invalid"] = "Рамка должна быть размером со вход модели и лежать внутри кадра.",
        ["checks.gpu_busy_training"] = "Видеокарта занята обучением. Дождитесь его окончания или отмените обучение, затем запустите проверку.",
        ["checks.gpu_busy_evaluation"] = "Видеокарта занята проверкой на датасете. Дождитесь её окончания или отмените её.",
        ["checks.gpu_busy_prediction"] = "Уже идёт другая проверка (изображение, видео или экран). Остановите её, чтобы начать новую.",
        ["checks.execution_failed"] = "Внутренняя ошибка при проверке модели.",
        ["checks.disk_full"] = "Недостаточно места на диске для проверки.",
        ["checks.out_of_memory"] = "Недостаточно памяти для подготовки проверки.",
        // Executor of the worker process.
        ["checks.worker_spawn_failed"] = "Не удалось запустить работника проверки.",
        ["checks.worker_io"] = "Связь с работником проверки прервалась.",
        ["checks.protocol_event"] = "Работник проверки прислал некорректное событие.",
        ["checks.protocol_sequence"] = "Нарушен порядок событий работника проверки.",
        ["checks.protocol_version"] = "Версия работника проверки не совпадает с приложением.",
        ["checks.result_operation_mismatch"] = "Результат работника относится к другой операции.",
        ["checks.result_invalid"] = "Работник проверки вернул некорректный результат.",
        ["checks.result_missing"] = "Работник завершился, но не записал результат.",
        ["checks.worker_usage"] = "Работник не получил папку задачи.",
        ["checks.worker_result_not_written"] = "Работник не смог записать result.json.",
        ["checks.worker_already_ran"] = "В папке задачи уже есть результат: повтор запрещён.",
        ["checks.worker_interrupted"] = "Работник прерван извне.",
        ["checks.worker_crashed"] = "Работник проверки аварийно завершился. Подробности — в журнале приложения.",
        // Codes of python/worker/protocol/errors.py; the check shows the worker's own message.
        ["checks.worker_request_invalid"] = "Работник отклонил параметры проверки.",
        ["checks.worker_protocol_unsupported"] = "Работник не поддерживает версию протокола приложения.",
        ["checks.worker_operation_not_implemented"] = "Операция не поддерживается этой версией работника.",
        ["checks.worker_input_path_invalid"] = "Путь входных данных выходит за пределы папки задачи.",
        ["checks.worker_dataset_invalid"] = "Датасет не прошёл проверку работника.",
        ["checks.worker_model_not_found"] = "Файл весов версии не найден в папке задачи.",
        ["checks.worker_model_load_failed"] = "Не удалось загрузить веса версии.",
        ["checks.worker_architecture_mismatch"] = "Веса не соответствуют архитектуре версии.",
        ["checks.worker_device_unavailable"] = "Выбранное устройство недоступно: проверьте номер видеокарты или выберите процессор.",
        ["checks.worker_out_of_memory"] = "Не хватило памяти устройства: уменьшите batch или выберите другое устройство.",
        ["checks.worker_evaluation_failed"] = "Проверка модели на датасете завершилась с ошибкой.",
        ["checks.worker_source_invalid"] = "Файл для проверки не читается.",
        ["checks.worker_region_invalid"] = "Рамка выходит за пределы кадра.",
        ["checks.worker_prediction_failed"] = "Проверка модели завершилась с ошибкой.",
        ["checks.worker_capture_unavailable"] = "Захват экрана недоступен: в среде работника нет пакета mss, нет доступного экрана или мониторы изменились после снимка.",
        ["checks.worker_internal_error"] = "Внутренняя ошибка работника проверки.",
        ["checks.worker_unknown"] = "Работник проверки сообщил об ошибке.",
        // Screen.
        ["screen.capture_unavailable"] = "Захват экрана недоступен: нет доступного экрана.",
        ["screen.capture_failed"] = "Не удалось снять экран. Подробности — в журнале приложения.",
        ["screen.monitor_not_found"] = "Монитор не найден: список мониторов изменился. Обновите список.",
    };

    /// <summary>The text of a check error: the worker's message for its own codes, otherwise the text of the code.</summary>
    internal static string Message(string code, string stored, Func<string, string> messageFor)
        => code.StartsWith("checks.worker_", StringComparison.Ordinal) && stored.Length > 0 && !IsHostCode(code) ? stored : messageFor(code);

    /// <summary>Codes the host gives by the exit code: their stored message has the raw stderr line, which stays in the job log.</summary>
    private static bool IsHostCode(string code) => code is "checks.worker_spawn_failed" or "checks.worker_io" or "checks.worker_usage"
        or "checks.worker_result_not_written" or "checks.worker_already_ran" or "checks.worker_interrupted" or "checks.worker_crashed";
}
