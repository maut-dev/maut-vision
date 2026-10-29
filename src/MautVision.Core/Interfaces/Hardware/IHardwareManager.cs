namespace MautVision.Core.Interfaces.Hardware;

/// <summary>NVIDIA GPU: Index — the number for device, memory in MiB.</summary>
public sealed record GpuInfo(int Index, string Name, long MemoryTotalMb, string DriverVersion);

/// <summary>DriverAvailable — the NVIDIA driver answered (NVML or nvidia-smi); without a driver the list is empty (the device is entered manually).</summary>
public sealed record GpuInventory(bool DriverAvailable, IReadOnlyList<GpuInfo> Gpus, DateTimeOffset CheckedAt);

/// <summary>
/// Computer hardware: GPUs for choosing the training device ("Processor" / "GPU N" / "Auto") and a load snapshot for
/// the project overview. It polls nothing on a timer: each snapshot is taken on request.
/// </summary>
public interface IHardwareManager
{
    /// <summary>
    /// GPUs via NVML, without it via nvidia-smi with a time limit; the answer is cached, refresh polls again. Polling errors
    /// are not thrown: the result is an empty list with DriverAvailable = false.
    /// </summary>
    Task<GpuInventory> ListGpusAsync(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot of CPU, RAM and GPUs. CPU and RAM are read by Windows functions without processes; GPUs by NVML, without it nvidia-smi
    /// (the answer is cached for 5 s, concurrent requests wait for one launch). Read errors are not thrown but become a status.
    /// </summary>
    Task<HardwareSnapshot> SampleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Process memory by ID in bytes: the private working set where Windows reports it, otherwise the working set. Exited and
    /// inaccessible processes are skipped.
    /// </summary>
    long MeasureProcessMemory(IEnumerable<int> processIds);
}
