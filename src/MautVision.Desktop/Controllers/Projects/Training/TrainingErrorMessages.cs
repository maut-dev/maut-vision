namespace MautVision.Desktop.Controllers.Projects.Training;

/// <summary>
/// Texts of training codes. A worker failure (training.worker_&lt;code&gt;) is shown with the worker's own Russian message
/// in the run; the texts below are for places that have only the code.
/// </summary>
internal static class TrainingErrorMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["training.project_not_open"] = "Проект не открыт. Откройте проект, чтобы работать с обучением.",
        ["training.run_not_found"] = "Запуск обучения не найден.",
        ["training.version_not_found"] = "Версия модели не найдена.",
        ["training.parameter_invalid"] = "Проверьте параметр обучения: значение вне допустимого диапазона или параметр не поддерживается.",
        ["training.preset_name_invalid"] = "Имя пресета должно быть непустым (до 80 знаков) и не содержать управляющие знаки.",
        ["training.preset_not_found"] = "Пресет не найден. Обновите список пресетов.",
        ["training.runtime_not_configured"] = "Среда обучения не настроена: не задан путь к Python или работнику.",
        ["training.runtime_missing"] = "Файлы среды обучения не найдены.",
        ["training.base_model_not_installed"] = "Установите базовую модель проекта в каталоге моделей.",
        ["training.base_weights_missing"] = "Файл базовых весов отсутствует или повреждён. Установите модель в каталоге моделей.",
        ["training.input_size_invalid"] = "Размер входа должен быть кратен шагу модели: 32 пикселя, для YOLOv5 P6 — 64.",
        ["training.result_invalid"] = "Работник вернул некорректный результат. Подробности — в журнале обучения.",
        ["training.disk_full"] = "Недостаточно места на диске. Освободите место и повторите обучение.",
        ["training.out_of_memory"] = "Недостаточно памяти для подготовки обучения.",
        ["training.execution_failed"] = "Внутренняя ошибка при выполнении обучения.",
        ["training.active"] = "Датасет используется обучением.",
        // Model versions: deletion and the ZIP export.
        ["training.version_in_use"] = "Версию сейчас нельзя удалить: она рабочая, проверяется или выгружается.",
        ["training.version_working"] = "Это рабочая версия: сначала сделайте рабочей другую.",
        ["training.export_active"] = "Идёт выгрузка этой версии в ZIP.",
        ["training.artifact_corrupt"] = "Файл весов версии повреждён.",
        ["training.export_failed"] = "Не удалось записать ZIP. Проверьте место сохранения и повторите выгрузку.",
        ["training.export_selection_expired"] = "Выбор места сохранения устарел. Выберите место заново.",
        // Executor of the worker process.
        ["training.worker_spawn_failed"] = "Не удалось запустить работника обучения.",
        ["training.worker_io"] = "Связь с работником обучения прервалась.",
        ["training.protocol_event"] = "Работник обучения прислал некорректное событие.",
        ["training.protocol_sequence"] = "Нарушен порядок событий работника обучения.",
        ["training.protocol_version"] = "Версия работника обучения не совпадает с приложением.",
        ["training.result_operation_mismatch"] = "Результат работника относится к другой операции.",
        ["training.worker_usage"] = "Работник не получил папку задачи.",
        ["training.worker_result_not_written"] = "Работник не смог записать result.json.",
        ["training.worker_already_ran"] = "В папке задачи уже есть результат: повтор запрещён.",
        ["training.worker_interrupted"] = "Работник прерван извне.",
        ["training.worker_crashed"] = "Работник обучения аварийно завершился. Подробности — в журнале обучения.",
        ["training.result_missing"] = "Работник завершился, но не записал результат.",
        ["training.artifact_missing"] = "Файл результата обучения не найден.",
        ["training.artifact_integrity"] = "Файл результата обучения повреждён: SHA-256 или размер не совпадает.",
        // Codes of python/worker/protocol/errors.py; the run shows the worker's own message.
        ["training.worker_request_not_found"] = "В папке задачи нет request.json.",
        ["training.worker_request_unreadable"] = "request.json не читается.",
        ["training.worker_request_invalid"] = "Работник отклонил параметры запуска.",
        ["training.worker_protocol_unsupported"] = "Работник не поддерживает версию протокола приложения.",
        ["training.worker_operation_not_implemented"] = "Операция не поддерживается этой версией работника.",
        ["training.worker_input_path_invalid"] = "Путь входных данных выходит за пределы папки задачи.",
        ["training.worker_dataset_invalid"] = "Датасет не прошёл проверку работника.",
        ["training.worker_model_not_found"] = "Файл базовых весов не найден в папке задачи.",
        ["training.worker_model_load_failed"] = "Не удалось загрузить базовые веса.",
        ["training.worker_architecture_mismatch"] = "Веса не соответствуют архитектуре проекта.",
        ["training.worker_device_unavailable"] = "Выбранное устройство недоступно: проверьте номер видеокарты или выберите процессор.",
        ["training.worker_out_of_memory"] = "Не хватило памяти устройства: уменьшите batch или размер входа.",
        ["training.worker_training_failed"] = "Обучение завершилось с ошибкой.",
        ["training.worker_no_weights_produced"] = "Обучение не сохранило веса: ни одной завершённой эпохи.",
        ["training.worker_artifact_invalid"] = "Работник не смог подготовить файлы результата.",
        ["training.worker_internal_error"] = "Внутренняя ошибка работника обучения.",
        ["training.worker_unknown"] = "Работник обучения сообщил об ошибке.",
        ["training.worker_evaluation_failed"] = "Проверка модели завершилась с ошибкой.",
        ["training.worker_source_invalid"] = "Файл для проверки не читается.",
        ["training.worker_region_invalid"] = "Рамка выходит за пределы кадра.",
        ["training.worker_prediction_failed"] = "Проверка модели завершилась с ошибкой.",
        ["training.worker_capture_unavailable"] = "Захват экрана недоступен.",
    };

    /// <summary>The text of a run error: the worker's message for its own codes, otherwise the text of the code.</summary>
    internal static string RunMessage(string code, string stored, Func<string, string> messageFor)
        => code.StartsWith("training.worker_", StringComparison.Ordinal) && stored.Length > 0 && !IsHostCode(code) ? stored : messageFor(code);

    /// <summary>Codes the host gives by the exit code: their stored message has the raw stderr line, which stays in the job log.</summary>
    private static bool IsHostCode(string code) => code is "training.worker_spawn_failed" or "training.worker_io" or "training.worker_usage"
        or "training.worker_result_not_written" or "training.worker_already_ran" or "training.worker_interrupted" or "training.worker_crashed";
}
