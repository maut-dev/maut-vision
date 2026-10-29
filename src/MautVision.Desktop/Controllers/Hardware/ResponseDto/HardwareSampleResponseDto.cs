namespace MautVision.Desktop.Controllers.Hardware.ResponseDto;

/// <summary>Status: available, collecting (the first CPU sample), unavailable. Reason: the hardware.* code of why there is no value (the page shows its text).</summary>
public sealed record CpuSampleDto(string? Model, int? PhysicalCores, int LogicalThreads, double? UtilizationPercent, string Status, string? Reason);

public sealed record MemorySampleDto(long TotalBytes, long UsedBytes, double UtilizationPercent);

/// <summary>
/// Memory of the application: the host process, the WebView2 processes and the running Python workers with their children, in bytes
/// (private working set where Windows reports it).
/// </summary>
public sealed record ApplicationMemoryDto(long TotalBytes, long HostBytes, long WebViewBytes, int WebViewProcesses, long WorkerBytes, int WorkerProcesses);

public sealed record GpuSampleDto(int Index, string Name, string? DriverVersion, long? MemoryTotalBytes, long? MemoryUsedBytes, double? UtilizationPercent,
    double? TemperatureC);

/// <summary>
/// hardware.sample: one snapshot by request (the page asks every ~2 s while "Your computer" is visible; the host polls nothing).
/// Ram is null when Windows does not report it. GpuStatus: available or unavailable with the GpuReason code; GpuSource: nvml or nvidia-smi.
/// </summary>
public sealed record HardwareSampleResponseDto(
    DateTimeOffset Timestamp,
    CpuSampleDto Cpu,
    MemorySampleDto? Ram,
    ApplicationMemoryDto Application,
    GpuSampleDto[] Gpus,
    string GpuStatus,
    string? GpuReason,
    string? GpuSource,
    DateTimeOffset? GpuSampledAt);
