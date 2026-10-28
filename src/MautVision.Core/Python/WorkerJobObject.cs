using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MautVision.Core.Python;

/// <summary>
/// Windows Job Object of one worker with «kill on close»: every process of the tree (DataLoader workers included) ends when the
/// handle closes — on <see cref="Dispose"/>, and also when the host dies, because the system closes the handles of a dead process.
/// <see cref="Terminate"/> ends the whole tree at once, not only the parent PID.
/// </summary>
internal sealed class WorkerJobObject : IDisposable
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int ErrorMoreData = 234;
    private const uint KillOnJobClose = 0x2000;
    private const uint DieOnUnhandledException = 0x400;

    private readonly SafeFileHandle handle;

    private WorkerJobObject(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    internal static WorkerJobObject Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose | DieOnUnhandledException },
        };
        var size = Marshal.SizeOf<ExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "SetInformationJobObject failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return new WorkerJobObject(handle);
    }

    /// <summary>Puts a started process into the job; its later children join it automatically.</summary>
    internal void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
    }

    /// <summary>Ends every process of the job; safe after the tree has exited and after disposal.</summary>
    internal void Terminate()
    {
        try
        {
            if (!handle.IsClosed) TerminateJobObject(handle, 1);
        }
        catch (ObjectDisposedException)
        {
            // Closing the handle ended the tree already.
        }
    }

    /// <summary>
    /// IDs of the processes of the tree (the worker and its DataLoader children); empty after the job ended or was disposed.
    /// </summary>
    internal IReadOnlyList<int> ProcessIds()
    {
        for (var capacity = 16; capacity <= 4096; capacity *= 4)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two counts, then ULONG_PTR process IDs from offset 8.
            var size = 8 + capacity * IntPtr.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool listed;
                try
                {
                    listed = QueryInformationJobObject(handle, JobObjectBasicProcessIdList, buffer, (uint)size, IntPtr.Zero);
                }
                catch (ObjectDisposedException)
                {
                    return [];
                }
                if (!listed)
                {
                    if (Marshal.GetLastWin32Error() == ErrorMoreData) continue;
                    return [];
                }
                var count = Marshal.ReadInt32(buffer, 4);
                var ids = new int[count];
                for (var index = 0; index < count; index++) ids[index] = (int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size);
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
