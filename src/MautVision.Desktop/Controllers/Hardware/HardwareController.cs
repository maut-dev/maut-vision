using MautVision.Core.Interfaces.Hardware;
using MautVision.Core.Interfaces.Python;
using MautVision.Desktop.Controllers.Hardware.RequestDto;
using MautVision.Desktop.Controllers.Hardware.ResponseDto;

namespace MautVision.Desktop.Controllers.Hardware;

/// <summary>
/// «Ваш компьютер»: the Core snapshot plus the memory of the application, which only the host can sum up — itself, its WebView2 processes
/// (webViewProcessIds, kept by the window) and the Python workers of Core.
/// </summary>
internal sealed class HardwareController(IHardwareManager hardware, IPythonRuntime python, Func<IReadOnlyList<int>> webViewProcessIds)
{
    /// <summary>Smoke seam: replaces or fails the snapshot, so the page shows a missing GPU or an error on any machine.</summary>
    internal static Func<HardwareSampleResponseDto, HardwareSampleResponseDto>? SmokeOverride;

    public async Task<HardwareSampleResponseDto> SampleAsync(SampleHardwareRequestDto request, CancellationToken cancellationToken)
    {
        var response = await MeasureAsync(cancellationToken);
        return SmokeOverride is { } smoke ? smoke(response) : response;
    }

    private async Task<HardwareSampleResponseDto> MeasureAsync(CancellationToken cancellationToken)
    {
        var snapshot = await hardware.SampleAsync(cancellationToken);
        var host = hardware.MeasureProcessMemory([Environment.ProcessId]);
        var webIds = webViewProcessIds().Where(id => id != Environment.ProcessId).ToArray();
        var web = hardware.MeasureProcessMemory(webIds);
        var workerIds = python.GetWorkerProcessIds();
        var workers = hardware.MeasureProcessMemory(workerIds);
        var cpu = snapshot.Cpu;
        return new HardwareSampleResponseDto(
            snapshot.Timestamp,
            new CpuSampleDto(cpu.Model, cpu.PhysicalCores, cpu.LogicalThreads, cpu.UtilizationPercent, Map(cpu.Status), cpu.ReasonCode),
            snapshot.Ram is { } ram ? new MemorySampleDto(ram.TotalBytes, ram.UsedBytes, ram.UtilizationPercent) : null,
            new ApplicationMemoryDto(host + web + workers, host, web, webIds.Length, workers, workerIds.Count),
            snapshot.Gpus.Select(gpu => new GpuSampleDto(gpu.Index, gpu.Name, gpu.DriverVersion, gpu.MemoryTotalBytes, gpu.MemoryUsedBytes, gpu.UtilizationPercent,
                gpu.TemperatureC)).ToArray(),
            Map(snapshot.GpuStatus),
            snapshot.GpuReasonCode,
            snapshot.GpuSource,
            snapshot.GpuSampledAt);
    }

    private static string Map(HardwareStatus status) => status switch
    {
        HardwareStatus.Available => "available",
        HardwareStatus.Collecting => "collecting",
        HardwareStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown hardware status."),
    };
}
