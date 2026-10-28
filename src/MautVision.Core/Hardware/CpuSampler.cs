using System.Numerics;
using System.Runtime.InteropServices;
using MautVision.Core.Interfaces.Hardware;

namespace MautVision.Core.Hardware;

/// <summary>Accumulated system CPU times in 100 ns units: idle, kernel (idle included) and user.</summary>
internal readonly record struct CpuTimes(long Idle, long Kernel, long User);

/// <summary>
/// System CPU load between two samples from GetSystemTimes, without processes or performance counters. The first sample, and a sample
/// after a pause longer than <see cref="MaxGap"/>, only collects a baseline: the average over a hidden screen is not today's load.
/// The model (registry) and topology (GetLogicalProcessorInformationEx) are read once.
/// </summary>
internal sealed class CpuSampler
{
    internal const string Collecting = "hardware.cpu_collecting";
    internal const string Unavailable = "hardware.cpu_unavailable";
    internal static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);

    private readonly object gate = new();
    private readonly Lazy<CpuTopology> topology = new(CpuTopology.Read);
    private (CpuTimes Times, long At)? previous;

    /// <summary>Smoke seam: milliseconds of a monotonic clock.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Smoke seam: the accumulated system times; null when Windows does not report them.</summary>
    internal Func<CpuTimes?> Times { get; set; } = ReadSystemTimes;

    internal CpuSnapshot Sample()
    {
        var shape = topology.Value;
        var times = Times();
        var now = Clock();
        double? load = null;
        lock (gate)
        {
            if (times is { } current && previous is { } last && now >= last.At && now - last.At <= MaxGap.TotalMilliseconds)
            {
                var idle = current.Idle - last.Times.Idle;
                var total = current.Kernel - last.Times.Kernel + current.User - last.Times.User;
                // Counters that went back (or no time passed) give no load, only a new baseline.
                if (idle >= 0 && total > 0 && idle <= total) load = Math.Clamp(100.0 * (total - idle) / total, 0, 100);
            }
            previous = times is { } value ? (value, now) : null;
        }
        if (times is null) return new CpuSnapshot(shape.Model, shape.PhysicalCores, shape.LogicalThreads, null, HardwareStatus.Unavailable, Unavailable);
        return load is { } percent
            ? new CpuSnapshot(shape.Model, shape.PhysicalCores, shape.LogicalThreads, percent, HardwareStatus.Available, null)
            : new CpuSnapshot(shape.Model, shape.PhysicalCores, shape.LogicalThreads, null, HardwareStatus.Collecting, Collecting);
    }

    private static CpuTimes? ReadSystemTimes() => GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}

/// <summary>The processor model and its cores and threads; null when Windows does not report them.</summary>
internal sealed record CpuTopology(string? Model, int? PhysicalCores, int LogicalThreads)
{
    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    internal static CpuTopology Read()
    {
        var (cores, threads) = ReadCores();
        return new CpuTopology(ReadModel(), cores, threads ?? Environment.ProcessorCount);
    }

    private static string? ReadModel()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0 ? name.Trim() : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>One RelationProcessorCore entry per physical core; its group masks hold the threads of the core.</summary>
    private static (int? Cores, int? Threads) ReadCores()
    {
        uint length = 0;
        if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length) || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            return (null, null);
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return (null, null);
            var cores = 0;
            var threads = 0;
            for (var offset = 0; offset + 8 <= length;)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                if (relationship == RelationProcessorCore)
                {
                    cores++;
                    // PROCESSOR_RELATIONSHIP: GroupCount at 30, GROUP_AFFINITY[16 bytes] from 32 (offsets from the entry start).
                    var groups = (ushort)Marshal.ReadInt16(buffer, offset + 30);
                    for (var group = 0; group < groups; group++)
                        threads += BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer, offset + 32 + group * 16));
                }
                offset += size;
            }
            return cores > 0 ? (cores, threads > 0 ? threads : null) : (null, null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
