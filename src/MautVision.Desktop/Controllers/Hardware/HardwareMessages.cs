namespace MautVision.Desktop.Controllers.Hardware;

/// <summary>Why a value of «Ваш компьютер» is missing, by the reason code of the snapshot.</summary>
internal static class HardwareMessages
{
    internal static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hardware.cpu_collecting"] = "Собирается первый замер нагрузки.",
        ["hardware.cpu_unavailable"] = "Операционная система не сообщила данные CPU.",
        ["hardware.gpu_not_found"] = "NVIDIA GPU не обнаружена. Для других производителей телеметрия пока недоступна.",
        ["hardware.gpu_driver_missing"] = "nvidia-smi не найдена. Телеметрия GPU доступна при установленном драйвере NVIDIA; другие производители пока не поддерживаются.",
        ["hardware.gpu_timeout"] = "Драйвер GPU не ответил вовремя. Следующий замер будет выполнен автоматически.",
        ["hardware.gpu_failed"] = "Не удалось прочитать телеметрию NVIDIA. Проверьте состояние драйвера GPU.",
    };
}
