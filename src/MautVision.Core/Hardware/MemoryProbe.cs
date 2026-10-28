using System.Runtime.InteropServices;
using MautVision.Core.Interfaces.Hardware;
using Microsoft.Win32.SafeHandles;

namespace MautVision.Core.Hardware;

/// <summary>System memory (GlobalMemoryStatusEx) and the memory of given processes (GetProcessMemoryInfo), without processes or counters.</summary>
internal static class MemoryProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    // PROCESS_MEMORY_COUNTERS_EX2 (PrivateWorkingSetSize) is unknown to older Windows; then the working set is used.
    private static volatile bool privateWorkingSetUnsupported;

    internal static MemorySnapshot? ReadSystem()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0) return null;
        var total = (long)status.TotalPhysical;
        var used = Math.Clamp(total - (long)status.AvailablePhysical, 0, total);
        return new MemorySnapshot(total, used, 100.0 * used / total);
    }

    internal static long ReadProcesses(IEnumerable<int> processIds)
    {
        long bytes = 0;
        foreach (var id in processIds.Distinct())
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, id);
            if (process.IsInvalid) continue;
            if (!privateWorkingSetUnsupported)
            {
                var extended = new MemoryCountersEx2 { Size = (uint)Marshal.SizeOf<MemoryCountersEx2>() };
                if (GetProcessMemoryInfo(process, ref extended, extended.Size))
                {
                    bytes += (long)extended.PrivateWorkingSetSize;
                    continue;
                }
            }
            var counters = new MemoryCountersEx2 { Size = (uint)Marshal.OffsetOf<MemoryCountersEx2>(nameof(MemoryCountersEx2.PrivateUsage)) };
            if (!GetProcessMemoryInfo(process, ref counters, counters.Size)) continue;
            // The short structure is accepted where the long one is not: this Windows has no private working set.
            privateWorkingSetUnsupported = true;
            bytes += (long)counters.WorkingSetSize;
        }
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCountersEx2 counters, uint size);
}
