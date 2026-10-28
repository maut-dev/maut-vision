using System.Runtime.InteropServices;
using System.Text;
using MautVision.Core.Interfaces.Hardware;

namespace MautVision.Core.Hardware;

/// <summary>An NVML call failed with a code other than «not supported» (the GPU is lost, the driver stopped answering).</summary>
internal sealed class NvmlException(string function, int code) : Exception($"{function} returned NVML error {code}.")
{
    internal int Code { get; } = code;
}

/// <summary>
/// NVIDIA Management Library loaded from an explicit path (never through PATH or the DLL search order), initialized once with
/// nvmlInit_v2 and shut down on <see cref="Dispose"/>. Devices, names and the driver version are read at load: they change only with
/// the driver. A value a device does not report (NOT_SUPPORTED, NO_PERMISSION) is null; any other error is an <see cref="NvmlException"/>.
/// </summary>
internal sealed class NvmlLibrary : IDisposable
{
    private const int Success = 0;
    private const int NotSupported = 3;
    private const int NoPermission = 4;
    private const int TemperatureGpu = 0;

    private readonly object gate = new();
    private readonly IntPtr module;
    private readonly Functions functions;
    private readonly (IntPtr Handle, string Name)[] devices;
    private bool disposed;

    private NvmlLibrary(IntPtr module, Functions functions, (IntPtr, string)[] devices, string? driverVersion)
    {
        this.module = module;
        this.functions = functions;
        this.devices = devices;
        DriverVersion = driverVersion;
    }

    internal string? DriverVersion { get; }

    internal int DeviceCount => devices.Length;

    /// <summary>The first candidate that exists, loads and initializes; null (with the reason in failure) when none does.</summary>
    internal static NvmlLibrary? TryLoad(IEnumerable<string> candidates, out string? failure)
    {
        failure = null;
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out var module))
            {
                failure = $"{path} could not be loaded.";
                continue;
            }
            try
            {
                var functions = new Functions(module);
                var init = functions.Init();
                if (init != Success)
                {
                    failure = $"nvmlInit_v2 returned {init}.";
                    NativeLibrary.Free(module);
                    continue;
                }
                try
                {
                    Check("nvmlDeviceGetCount_v2", functions.DeviceGetCount(out var count));
                    var devices = new (IntPtr, string)[count];
                    for (uint index = 0; index < count; index++)
                    {
                        Check("nvmlDeviceGetHandleByIndex_v2", functions.DeviceGetHandleByIndex(index, out var handle));
                        var name = new byte[96];
                        var named = functions.DeviceGetName(handle, name, (uint)name.Length);
                        devices[index] = (handle, named == Success ? Text(name) : $"NVIDIA GPU {index}");
                    }
                    var version = new byte[80];
                    var driver = functions.SystemGetDriverVersion(version, (uint)version.Length) == Success ? Text(version) : null;
                    return new NvmlLibrary(module, functions, devices, driver);
                }
                catch
                {
                    functions.Shutdown();
                    throw;
                }
            }
            catch (Exception error) when (error is EntryPointNotFoundException or NvmlException)
            {
                failure = error.Message;
                NativeLibrary.Free(module);
            }
        }
        return null;
    }

    /// <summary>The current values of every device; the index is the NVML index, the same as nvidia-smi's.</summary>
    internal IReadOnlyList<GpuSnapshot> Read()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = new GpuSnapshot[devices.Length];
            for (var index = 0; index < devices.Length; index++)
            {
                var (handle, name) = devices[index];
                long? total = null;
                long? used = null;
                if (Optional("nvmlDeviceGetMemoryInfo", functions.DeviceGetMemoryInfo(handle, out var memory)))
                {
                    total = (long)memory.Total;
                    used = (long)memory.Used;
                }
                double? load = Optional("nvmlDeviceGetUtilizationRates", functions.DeviceGetUtilizationRates(handle, out var rates)) ? rates.Gpu : null;
                double? temperature = Optional("nvmlDeviceGetTemperature", functions.DeviceGetTemperature(handle, TemperatureGpu, out var degrees)) ? degrees : null;
                result[index] = new GpuSnapshot(index, name, DriverVersion, total, used, load, temperature);
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            functions.Shutdown();
            NativeLibrary.Free(module);
        }
    }

    private static bool Optional(string function, int code) => code switch
    {
        Success => true,
        NotSupported or NoPermission => false,
        _ => throw new NvmlException(function, code),
    };

    private static void Check(string function, int code)
    {
        if (code != Success) throw new NvmlException(function, code);
    }

    private static string Text(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NoArguments();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountFunction(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HandleFunction(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceTextFunction(IntPtr device, [Out] byte[] text, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TextFunction([Out] byte[] text, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MemoryFunction(IntPtr device, out Memory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UtilizationFunction(IntPtr device, out Utilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TemperatureFunction(IntPtr device, int sensor, out uint temperature);

    /// <summary>The exports used; a missing one fails the load (EntryPointNotFoundException).</summary>
    private sealed class Functions(IntPtr module)
    {
        internal NoArguments Init { get; } = Export<NoArguments>(module, "nvmlInit_v2");
        internal NoArguments ShutdownCall { get; } = Export<NoArguments>(module, "nvmlShutdown");
        internal CountFunction DeviceGetCount { get; } = Export<CountFunction>(module, "nvmlDeviceGetCount_v2");
        internal HandleFunction DeviceGetHandleByIndex { get; } = Export<HandleFunction>(module, "nvmlDeviceGetHandleByIndex_v2");
        internal DeviceTextFunction DeviceGetName { get; } = Export<DeviceTextFunction>(module, "nvmlDeviceGetName");
        internal TextFunction SystemGetDriverVersion { get; } = Export<TextFunction>(module, "nvmlSystemGetDriverVersion");
        internal MemoryFunction DeviceGetMemoryInfo { get; } = Export<MemoryFunction>(module, "nvmlDeviceGetMemoryInfo");
        internal UtilizationFunction DeviceGetUtilizationRates { get; } = Export<UtilizationFunction>(module, "nvmlDeviceGetUtilizationRates");
        internal TemperatureFunction DeviceGetTemperature { get; } = Export<TemperatureFunction>(module, "nvmlDeviceGetTemperature");

        internal void Shutdown() => ShutdownCall();

        private static T Export<T>(IntPtr module, string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.TryGetExport(module, name, out var address)
                ? address
                : throw new EntryPointNotFoundException($"NVML has no export {name}."));
    }
}
