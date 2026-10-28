namespace MautVision.Core.Interfaces.Hardware;

/// <summary>Metric state: has a value, the first sample is being collected (CPU load) or unavailable.</summary>
public enum HardwareStatus
{
    Available,
    Collecting,
    Unavailable,
}

/// <summary>
/// Processor: Model — from the registry (null if not read), PhysicalCores — cores by Windows topology, LogicalThreads — threads.
/// UtilizationPercent — system load between two samples; null with status Collecting on the first sample and after a pause
/// longer than 10 s. ReasonCode — <c>hardware.cpu_collecting</c> or <c>hardware.cpu_unavailable</c>.
/// </summary>
public sealed record CpuSnapshot(string? Model, int? PhysicalCores, int LogicalThreads, double? UtilizationPercent, HardwareStatus Status, string? ReasonCode);

/// <summary>Physical system memory in bytes; UtilizationPercent — UsedBytes of TotalBytes.</summary>
public sealed record MemorySnapshot(long TotalBytes, long UsedBytes, double UtilizationPercent);

/// <summary>
/// NVIDIA GPU: Index — the same number as in nvidia-smi and the training device; a value the driver does not report is null.
/// </summary>
public sealed record GpuSnapshot(int Index, string Name, string? DriverVersion, long? MemoryTotalBytes, long? MemoryUsedBytes, double? UtilizationPercent,
    double? TemperatureC);

/// <summary>
/// Computer load snapshot. Gpus is empty if GpuStatus is Unavailable; GpuReasonCode — <c>hardware.gpu_not_found</c>,
/// <c>hardware.gpu_driver_missing</c>, <c>hardware.gpu_timeout</c> or <c>hardware.gpu_failed</c>. GpuSource — <c>nvml</c> or
/// <c>nvidia-smi</c>; GpuSampledAt — time the GPUs were read (the nvidia-smi answer is cached for 5 s).
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
