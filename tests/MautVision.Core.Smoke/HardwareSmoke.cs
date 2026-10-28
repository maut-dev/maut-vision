using System.Diagnostics;
using MautVision.Core.Hardware;
using MautVision.Core.Interfaces.Hardware;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The hardware snapshot: CPU load from collecting to available and back after a pause, RAM, the GPU sources (NVML, nvidia-smi through
/// a fake executable, none) with their reasons and the nvidia-smi cache, the memory of processes, and the cost of one sample.
/// </summary>
internal static class HardwareSmoke
{
    internal static async Task<int> RunAsync(string fixture)
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var folder = Path.Combine(fixture, "hardware");
        Directory.CreateDirectory(folder);
        var missingNvml = Path.Combine(folder, "no-nvml.dll");
        var missingSmi = Path.Combine(folder, "no-nvidia-smi.exe");

        // CPU: deterministic counters through the seams. Kernel time includes idle time.
        using (var manager = new HardwareManager(NullLogger.Instance) { NvmlCandidates = [missingNvml], Candidates = [missingSmi] })
        {
            var clock = 1_000L;
            var times = new CpuTimes(0, 0, 0);
            manager.Cpu.Clock = () => clock;
            manager.Cpu.Times = () => times;
            var first = await manager.SampleAsync();
            Check(first.Cpu is { Status: HardwareStatus.Collecting, UtilizationPercent: null, ReasonCode: CpuSampler.Collecting } && first.Cpu.LogicalThreads > 0,
                "The first CPU sample only collects a baseline.");
            clock += 2_000;
            times = new CpuTimes(Idle: 750, Kernel: 800, User: 200);
            var second = await manager.SampleAsync();
            Check(second.Cpu is { Status: HardwareStatus.Available, ReasonCode: null } && Math.Abs(second.Cpu.UtilizationPercent!.Value - 25) < 1e-9,
                $"The second sample is the load between the two: {second.Cpu.UtilizationPercent}.");
            clock += (long)CpuSampler.MaxGap.TotalMilliseconds + 1;
            times = new CpuTimes(1_000, 1_100, 400);
            Check((await manager.SampleAsync()).Cpu.Status == HardwareStatus.Collecting, "After a pause longer than 10 s the load is collected again.");
            clock += 2_000;
            times = new CpuTimes(1_000, 1_200, 500);
            Check((await manager.SampleAsync()).Cpu is { Status: HardwareStatus.Available, UtilizationPercent: 100 }, "The next sample after the pause is available.");
            clock += 2_000;
            times = new CpuTimes(900, 1_100, 400);
            Check((await manager.SampleAsync()).Cpu.Status == HardwareStatus.Collecting, "Counters that go back give a new baseline, not a load.");
            manager.Cpu.Times = () => null;
            Check((await manager.SampleAsync()).Cpu is { Status: HardwareStatus.Unavailable, ReasonCode: CpuSampler.Unavailable }, "Missing counters are unavailable.");

            // No NVML and no nvidia-smi: no GPU, the reason names the missing driver, no error.
            var none = await manager.SampleAsync();
            Check(none is { GpuStatus: HardwareStatus.Unavailable, GpuReasonCode: HardwareManager.GpuDriverMissing, GpuSource: "nvidia-smi", Gpus.Count: 0 }
                && none.GpuSampledAt is not null, "Without NVML and nvidia-smi GPUs are unavailable with the driver reason.");
            Check(none.Ram is { TotalBytes: > 0 } ram && ram.UsedBytes <= ram.TotalBytes && ram.UtilizationPercent is > 0 and <= 100, "RAM comes from Windows.");
        }

        // Real counters: two samples a moment apart give a load.
        using (var real = new HardwareManager(NullLogger.Instance) { NvmlCandidates = [missingNvml], Candidates = [missingSmi] })
        {
            await real.SampleAsync();
            await Task.Delay(150);
            var sample = await real.SampleAsync();
            Check(sample.Cpu is { Status: HardwareStatus.Available, UtilizationPercent: >= 0 and <= 100 } && sample.Cpu.LogicalThreads == Environment.ProcessorCount
                && sample.Cpu.PhysicalCores is > 0 && sample.Cpu.PhysicalCores <= sample.Cpu.LogicalThreads && !string.IsNullOrWhiteSpace(sample.Cpu.Model),
                $"Windows reports the CPU: {sample.Cpu.Model}, {sample.Cpu.PhysicalCores} cores, {sample.Cpu.LogicalThreads} threads.");
            var host = real.MeasureProcessMemory([Environment.ProcessId]);
            Check(host > 1024 * 1024 && host < Environment.WorkingSet * 2, $"The memory of this process is measured: {host} bytes.");
            Check(real.MeasureProcessMemory([int.MaxValue - 1, 0]) == 0 && real.MeasureProcessMemory([Environment.ProcessId, Environment.ProcessId]) < host * 2,
                "Missing processes are skipped and an ID is counted once.");
        }

        // nvidia-smi fallback through a fake executable: values, «[N/A]» as null, the 5 s cache and one run for concurrent requests.
        var fake = await FakeSmiAsync(folder);
        using (var smi = new HardwareManager(NullLogger.Instance) { NvmlCandidates = [missingNvml], Candidates = [fake.Executable] })
        {
            var clock = 10_000L;
            smi.Clock = () => clock;
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => smi.SampleAsync()));
            var gpu = concurrent[0];
            Check(gpu is { GpuStatus: HardwareStatus.Available, GpuSource: "nvidia-smi", GpuReasonCode: null, Gpus.Count: 2 }
                && gpu.Gpus[0] == new GpuSnapshot(0, "NVIDIA GeForce RTX 4060", "581.15", 8188L * 1024 * 1024, 817L * 1024 * 1024, 7, 48)
                && gpu.Gpus[1] is { Name: "Card, with comma", MemoryUsedBytes: null, UtilizationPercent: null, TemperatureC: null },
                "nvidia-smi values are read; [N/A] and [Not Supported] are null.");
            Check(File.ReadAllLines(fake.Runs).Length == 1 && concurrent.All(item => item.GpuSampledAt == gpu.GpuSampledAt),
                "Concurrent samples share one nvidia-smi run.");
            clock += 4_000;
            await smi.SampleAsync();
            Check(File.ReadAllLines(fake.Runs).Length == 1, "Within 5 s the nvidia-smi answer comes from the cache.");
            clock += 2_000;
            await smi.SampleAsync();
            Check(File.ReadAllLines(fake.Runs).Length == 2, "After 5 s nvidia-smi runs again.");
            var parsed = NvidiaSmi.ParseTelemetry("0, A, 1, 2, 1, 3, 4\n");
            Check(parsed.Single() is { Index: 0, Name: "A", DriverVersion: "1" }, "A telemetry line is parsed.");
            var damaged = false;
            try
            {
                NvidiaSmi.ParseTelemetry("0, A, 1, abc, 1, 3, 4\n");
            }
            catch (FormatException)
            {
                damaged = true;
            }
            Check(damaged, "Damaged nvidia-smi output is an error, not zeros.");
        }

        // NVML of this machine when it exists: the same GPUs as nvidia-smi, and the cost of one sample.
        using (var machine = new HardwareManager(NullLogger.Instance))
        {
            await machine.SampleAsync();
            var watch = Stopwatch.StartNew();
            const int Samples = 200;
            HardwareSnapshot last = null!;
            for (var index = 0; index < Samples; index++) last = await machine.SampleAsync();
            var perSample = watch.Elapsed.TotalMilliseconds / Samples;
            Check(last.GpuSource is "nvml" or "nvidia-smi" && (last.GpuStatus == HardwareStatus.Available) == (last.Gpus.Count > 0),
                $"This machine answers through {last.GpuSource}: {last.GpuStatus} {last.GpuReasonCode}.");
            if (last.GpuSource == "nvml")
            {
                Check(last.Gpus.All(item => item.MemoryTotalBytes > 0 && item.MemoryUsedBytes <= item.MemoryTotalBytes && item.UtilizationPercent is >= 0 and <= 100
                    && item.TemperatureC is > 0 and < 150 && item.DriverVersion is { Length: > 0 }), "NVML reports memory, load, temperature and the driver.");
                var inventory = await machine.ListGpusAsync(refresh: true);
                Check(inventory.DriverAvailable && inventory.Gpus.Select(item => (item.Index, item.Name)).SequenceEqual(last.Gpus.Select(item => (item.Index, item.Name))),
                    "The GPU list for training comes from the same NVML.");
            }
            using var cpuOnly = new HardwareManager(NullLogger.Instance) { NvmlCandidates = [missingNvml], Candidates = [missingSmi] };
            await cpuOnly.SampleAsync();
            var cpuWatch = Stopwatch.StartNew();
            for (var index = 0; index < Samples; index++) await cpuOnly.SampleAsync();
            var cpuPerSample = cpuWatch.Elapsed.TotalMilliseconds / Samples;
            Check(cpuPerSample < 1, $"CPU and RAM cost less than 1 ms per sample: {cpuPerSample:F3} ms.");
            Console.WriteLine($"Hardware sample: {perSample:F3} ms with {last.GpuSource} ({last.Gpus.Count} GPU), {cpuPerSample:F3} ms CPU and RAM only "
                + $"(mean of {Samples}).");
        }
        return checks;
    }

    /// <summary>A fake nvidia-smi: a .cmd file that Process.Start runs through cmd.exe; every run appends a line to Runs.</summary>
    private static async Task<(string Executable, string Runs)> FakeSmiAsync(string folder)
    {
        var runs = Path.Combine(folder, "smi-runs.txt");
        await File.WriteAllTextAsync(runs, "");
        var script = Path.Combine(folder, "nvidia-smi.cmd");
        await File.WriteAllTextAsync(script, $"""
            @echo off
            echo run>>"{runs}"
            echo 0, NVIDIA GeForce RTX 4060, 581.15, 8188, 817, 7, 48
            echo 1, Card, with comma, 581.15, 4096, [N/A], [Not Supported], [N/A]
            """.Replace("\n", "\r\n"));
        return (script, runs);
    }
}
