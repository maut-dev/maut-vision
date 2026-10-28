namespace MautVision.Core.Interfaces.Python;

/// <summary>
/// Состояние среды Python worker: Configured — host передал описание запуска; Available — его файлы на месте; Mode — packaged |
/// interpreter (null без настройки); Code — <c>runtime_not_configured</c> или <c>runtime_missing</c>, пока среда недоступна.
/// </summary>
public sealed record PythonRuntimeStatus(bool Configured, bool Available, string? Mode, string? Code);

/// <summary>Среда Python worker. Проверка лишь показывает состояние: запуск обучения проверяет среду сам.</summary>
public interface IPythonRuntime
{
    PythonRuntimeStatus GetStatus();

    /// <summary>ID процессов идущих worker вместе с их дочерними процессами (DataLoader); пусто, если worker не запущены.</summary>
    IReadOnlyList<int> GetWorkerProcessIds();
}
