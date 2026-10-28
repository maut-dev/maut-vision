namespace MautVision.Core.Interfaces.Hardware;

/// <summary>Состояние показателя: есть значение, собирается первый замер (нагрузка CPU) или недоступен.</summary>
public enum HardwareStatus
{
    Available,
    Collecting,
    Unavailable,
}

/// <summary>
/// Процессор: Model — из реестра (null, если не прочитана), PhysicalCores — ядра по топологии Windows, LogicalThreads — потоки.
/// UtilizationPercent — нагрузка системы между двумя замерами; null со статусом Collecting на первом замере и после паузы
/// больше 10 с. ReasonCode — <c>hardware.cpu_collecting</c> или <c>hardware.cpu_unavailable</c>.
/// </summary>
public sealed record CpuSnapshot(string? Model, int? PhysicalCores, int LogicalThreads, double? UtilizationPercent, HardwareStatus Status, string? ReasonCode);

/// <summary>Физическая память системы в байтах; UtilizationPercent — UsedBytes от TotalBytes.</summary>
public sealed record MemorySnapshot(long TotalBytes, long UsedBytes, double UtilizationPercent);

/// <summary>
/// Видеокарта NVIDIA: Index — тот же номер, что у nvidia-smi и устройства обучения; значение, которое драйвер не сообщает, — null.
/// </summary>
public sealed record GpuSnapshot(int Index, string Name, string? DriverVersion, long? MemoryTotalBytes, long? MemoryUsedBytes, double? UtilizationPercent,
    double? TemperatureC);

/// <summary>
/// Снимок нагрузки компьютера. Gpus пуст, если GpuStatus — Unavailable; GpuReasonCode — <c>hardware.gpu_not_found</c>,
/// <c>hardware.gpu_driver_missing</c>, <c>hardware.gpu_timeout</c> или <c>hardware.gpu_failed</c>. GpuSource — <c>nvml</c> или
/// <c>nvidia-smi</c>; GpuSampledAt — время чтения видеокарт (у nvidia-smi ответ кешируется на 5 с).
/// </summary>
public sealed record HardwareSnapshot(
    DateTimeOffset Timestamp,
    CpuSnapshot Cpu,
    MemorySnapshot? Ram,
    IReadOnlyList<GpuSnapshot> Gpus,
    HardwareStatus GpuStatus,
    string? GpuReasonCode,
    string? GpuSource,
    DateTimeOffset? GpuSampledAt);
