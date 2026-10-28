namespace MautVision.Core.Interfaces.Hardware;

/// <summary>Видеокарта NVIDIA: Index — номер для device, память в МиБ.</summary>
public sealed record GpuInfo(int Index, string Name, long MemoryTotalMb, string DriverVersion);

/// <summary>DriverAvailable — драйвер NVIDIA ответил (NVML или nvidia-smi); без драйвера список пуст (выбор устройства вводится вручную).</summary>
public sealed record GpuInventory(bool DriverAvailable, IReadOnlyList<GpuInfo> Gpus, DateTimeOffset CheckedAt);

/// <summary>
/// Железо компьютера: видеокарты для выбора устройства обучения («Процессор» / «Видеокарта N» / «Автовыбор») и снимок нагрузки для
/// обзора проекта. Сам ничего не опрашивает по таймеру: каждый снимок делается по запросу.
/// </summary>
public interface IHardwareManager
{
    /// <summary>
    /// Видеокарты по NVML, без него — по nvidia-smi с ограничением времени; ответ кешируется, refresh — опросить заново. Ошибки
    /// опроса не выбрасываются: результат — пустой список с DriverAvailable = false.
    /// </summary>
    Task<GpuInventory> ListGpusAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Снимок CPU, RAM и видеокарт. CPU и RAM читаются функциями Windows без процессов; видеокарты — NVML, без него nvidia-smi
    /// (ответ кешируется на 5 с, одновременные запросы ждут один запуск). Ошибки чтения не выбрасываются, а становятся статусом.
    /// </summary>
    Task<HardwareSnapshot> SampleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Память процессов по ID в байтах: приватный рабочий набор, где Windows его сообщает, иначе рабочий набор. Завершившиеся и
    /// недоступные процессы пропускаются.
    /// </summary>
    long MeasureProcessMemory(IEnumerable<int> processIds);
}
