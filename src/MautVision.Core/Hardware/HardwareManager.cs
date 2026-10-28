using MautVision.Core.Interfaces.Hardware;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Hardware;

/// <summary>GPU values of one reading with their status; shared by the snapshots of the nvidia-smi cache.</summary>
internal sealed record GpuReading(IReadOnlyList<GpuSnapshot> Gpus, HardwareStatus Status, string? ReasonCode, string Source, DateTimeOffset SampledAt);

/// <summary>
/// CPU and RAM through Windows functions; NVIDIA GPUs through NVML (nvml.dll from the driver's folders, loaded on the first request),
/// without it through nvidia-smi. Nothing is polled on a timer: every snapshot is taken by a request. nvidia-smi answers are cached for
/// <see cref="SmiCacheTime"/> and shared by concurrent requests; the GPU list for training is cached until refresh.
/// </summary>
internal sealed class HardwareManager(ILogger logger) : IHardwareManager, IDisposable
{
    internal const string GpuNotFound = "hardware.gpu_not_found";
    internal const string GpuDriverMissing = "hardware.gpu_driver_missing";
    internal const string GpuTimeout = "hardware.gpu_timeout";
    internal const string GpuFailed = "hardware.gpu_failed";
    internal static readonly TimeSpan SmiCacheTime = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim inventoryGate = new(1, 1);
    private readonly object nvmlGate = new();
    private readonly object smiGate = new();
    private GpuInventory? inventory;
    private NvmlLibrary? nvml;
    private bool nvmlTried;
    private bool disposed;
    private Task<GpuReading>? smiPending;
    private (GpuReading Reading, long At)? smiLast;

    /// <summary>Smoke seam: candidate nvidia-smi paths, the first existing one is used.</summary>
    internal IReadOnlyList<string> Candidates { get; set; } = NvidiaSmi.DefaultCandidates;

    /// <summary>Smoke seam: candidate nvml.dll paths (System32 of current drivers, NVSMI of older ones), the first that loads is used.</summary>
    internal IReadOnlyList<string> NvmlCandidates { get; set; } =
    [
        Path.Combine(Environment.SystemDirectory, "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
    ];

    internal CpuSampler Cpu { get; } = new();

    /// <summary>Smoke seam: milliseconds of the clock of the nvidia-smi cache.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    public async Task<GpuInventory> ListGpusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await inventoryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (inventory is not null && !refresh) return inventory;
            inventory = await ProbeInventoryAsync(cancellationToken).ConfigureAwait(false);
            return inventory;
        }
        finally
        {
            inventoryGate.Release();
        }
    }

    public async Task<HardwareSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var cpu = Cpu.Sample();
        var ram = MemoryProbe.ReadSystem();
        var gpu = ReadNvml() ?? await ReadSmiAsync(cancellationToken).ConfigureAwait(false);
        return new HardwareSnapshot(timestamp, cpu, ram, gpu.Gpus, gpu.Status, gpu.ReasonCode, gpu.Source, gpu.SampledAt);
    }

    public long MeasureProcessMemory(IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        return MemoryProbe.ReadProcesses(processIds);
    }

    public void Dispose()
    {
        lock (nvmlGate)
        {
            disposed = true;
            nvml?.Dispose();
            nvml = null;
        }
    }

    /// <summary>NVML loaded once per process; null when it is absent or does not initialize (nvidia-smi is used then).</summary>
    private NvmlLibrary? Nvml()
    {
        lock (nvmlGate)
        {
            if (nvmlTried || disposed) return nvml;
            nvmlTried = true;
            nvml = NvmlLibrary.TryLoad(NvmlCandidates, out var failure);
            if (nvml is not null)
                logger.LogInformation("NVML found {Count} GPU(s), driver {Driver}.", nvml.DeviceCount, nvml.DriverVersion);
            else
                logger.LogInformation("NVML is not available ({Reason}); nvidia-smi is used for GPU telemetry.", failure ?? "nvml.dll not found");
            return nvml;
        }
    }

    /// <summary>The NVML reading; null without NVML.</summary>
    private GpuReading? ReadNvml()
    {
        var library = Nvml();
        if (library is null) return null;
        var now = DateTimeOffset.UtcNow;
        try
        {
            var gpus = library.Read();
            return gpus.Count == 0
                ? new GpuReading([], HardwareStatus.Unavailable, GpuNotFound, "nvml", now)
                : new GpuReading(gpus, HardwareStatus.Available, null, "nvml", now);
        }
        catch (Exception error) when (error is NvmlException or ObjectDisposedException)
        {
            logger.LogWarning(error, "NVML telemetry could not be read.");
            return new GpuReading([], HardwareStatus.Unavailable, GpuFailed, "nvml", now);
        }
    }

    /// <summary>The cached nvidia-smi reading while it is fresh; otherwise one run shared by every request that comes meanwhile.</summary>
    private Task<GpuReading> ReadSmiAsync(CancellationToken cancellationToken)
    {
        Task<GpuReading> pending;
        lock (smiGate)
        {
            if (smiLast is { } last && Clock() - last.At < SmiCacheTime.TotalMilliseconds) return Task.FromResult(last.Reading);
            pending = smiPending ??= StartSmiProbe();
        }
        return pending.WaitAsync(cancellationToken);
    }

    /// <summary>Not bound to one caller's token: other requests wait for the same run. Called under smiGate.</summary>
    private Task<GpuReading> StartSmiProbe()
    {
        var probe = Task.Run(ProbeTelemetryAsync);
        _ = probe.ContinueWith(done =>
        {
            lock (smiGate)
            {
                if (done.IsCompletedSuccessfully) smiLast = (done.Result, Clock());
                if (ReferenceEquals(smiPending, done)) smiPending = null;
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return probe;
    }

    private async Task<GpuReading> ProbeTelemetryAsync()
    {
        var (outcome, text, detail) = await NvidiaSmi.QueryAsync(Candidates, NvidiaSmi.TelemetryQuery, CancellationToken.None).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        switch (outcome)
        {
            case NvidiaSmiOutcome.Missing:
                return new GpuReading([], HardwareStatus.Unavailable, GpuDriverMissing, "nvidia-smi", now);
            case NvidiaSmiOutcome.TimedOut:
                logger.LogWarning("nvidia-smi did not answer within {Seconds} s.", NvidiaSmi.Timeout.TotalSeconds);
                return new GpuReading([], HardwareStatus.Unavailable, GpuTimeout, "nvidia-smi", now);
            case NvidiaSmiOutcome.Failed:
                logger.LogWarning("nvidia-smi telemetry failed: {Detail}.", detail);
                return new GpuReading([], HardwareStatus.Unavailable, GpuFailed, "nvidia-smi", now);
        }
        try
        {
            var gpus = NvidiaSmi.ParseTelemetry(text);
            return gpus.Count == 0
                ? new GpuReading([], HardwareStatus.Unavailable, GpuNotFound, "nvidia-smi", now)
                : new GpuReading(gpus, HardwareStatus.Available, null, "nvidia-smi", now);
        }
        catch (FormatException error)
        {
            logger.LogWarning(error, "nvidia-smi telemetry could not be parsed.");
            return new GpuReading([], HardwareStatus.Unavailable, GpuFailed, "nvidia-smi", now);
        }
    }

    private async Task<GpuInventory> ProbeInventoryAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (Nvml() is { } library)
        {
            try
            {
                var gpus = library.Read().Select(gpu => new GpuInfo(gpu.Index, gpu.Name, (gpu.MemoryTotalBytes ?? 0) / (1024 * 1024), gpu.DriverVersion ?? "")).ToArray();
                return new GpuInventory(true, gpus, now);
            }
            catch (Exception error) when (error is NvmlException or ObjectDisposedException)
            {
                logger.LogWarning(error, "NVML could not list GPUs.");
                return new GpuInventory(false, [], now);
            }
        }
        var (outcome, text, detail) = await NvidiaSmi.QueryAsync(Candidates, NvidiaSmi.InventoryQuery, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case NvidiaSmiOutcome.Missing:
                return new GpuInventory(false, [], now);
            case NvidiaSmiOutcome.TimedOut:
                logger.LogWarning("nvidia-smi did not answer within {Seconds} s.", NvidiaSmi.Timeout.TotalSeconds);
                return new GpuInventory(false, [], now);
            case NvidiaSmiOutcome.Failed:
                logger.LogInformation("nvidia-smi failed ({Detail}); no GPU is offered.", detail);
                return new GpuInventory(false, [], now);
        }
        var parsed = NvidiaSmi.ParseInventory(text);
        logger.LogInformation("nvidia-smi found {Count} GPU(s): {Names}.", parsed.Count, string.Join(", ", parsed.Select(gpu => gpu.Name)));
        return new GpuInventory(true, parsed, now);
    }
}
