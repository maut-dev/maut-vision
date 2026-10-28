using System.Diagnostics;
using System.Runtime.InteropServices;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Screen;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MautVision.Core.Screen;

/// <summary>
/// Monitors and screenshots through Win32, without Python. Every call runs with the thread switched to per-monitor DPI awareness
/// (v2), so coordinates are physical pixels whatever the awareness of the host process; mss in the worker sets the process
/// per-monitor aware as well and enumerates with EnumDisplayMonitors, so indexes and bounds agree. The shot is GDI BitBlt with
/// CAPTUREBLT — the method of mss — encoded as PNG by SkiaSharp.
/// </summary>
internal sealed class ScreenManager(ILogger logger) : IScreenManager
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public IReadOnlyList<MonitorInfo> ListMonitors()
    {
        if (!OperatingSystem.IsWindows()) throw Unavailable("Screen capture needs Windows.");
        return WithPhysicalPixels(Enumerate);
    }

    public Task<ScreenShot> CaptureAsync(int monitor, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw Unavailable("Screen capture needs Windows.");
        cancellationToken.ThrowIfCancellationRequested();
        // The DPI context belongs to the thread: enumeration and capture run on one pool thread together.
        return Task.Run(() => WithPhysicalPixels(() =>
        {
            var watch = Stopwatch.StartNew();
            var monitors = Enumerate();
            var target = monitors.FirstOrDefault(item => item.Index == monitor)
                ?? throw new CoreException("screen.monitor_not_found", $"Monitor {monitor} does not exist; there are {monitors.Count}.")
                {
                    Details = new Dictionary<string, string> { ["monitor"] = monitor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["monitors"] = monitors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                };
            var png = Capture(target);
            logger.LogInformation("Screen of monitor {Monitor} ({Width}x{Height}) captured in {Elapsed} ms, {Bytes} bytes.", target.Index, target.Width,
                target.Height, watch.ElapsedMilliseconds, png.Length);
            return new ScreenShot(target, png, "image/png", monitors);
        }), cancellationToken);
    }

    private static T WithPhysicalPixels<T>(Func<T> action)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return action();
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    private static List<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();
        bool Callback(IntPtr handle, IntPtr dc, ref Rect bounds, IntPtr data)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            var named = GetMonitorInfoW(handle, ref info);
            // mss takes the rectangle of the enumeration; the info gives the primary flag and the device name.
            result.Add(new MonitorInfo(result.Count + 1, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                named && (info.Flags & 1) != 0, named ? info.Device : ""));
            return true;
        }
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero) || result.Count == 0)
            throw Unavailable("EnumDisplayMonitors found no monitor (a service session or no desktop).");
        return result;
    }

    private static byte[] Capture(MonitorInfo monitor)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw Unavailable("GetDC of the screen failed.");
        var memory = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = monitor.Width,
                // Negative height: top-down rows, as an image is stored.
                Height = -monitor.Height,
                Planes = 1,
                BitCount = 32,
            };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw Failed("CreateDIBSection failed.");
            var previous = SelectObject(memory, bitmap);
            try
            {
                // SRCCOPY | CAPTUREBLT: layered windows are included, as mss does.
                if (!BitBlt(memory, 0, 0, monitor.Width, monitor.Height, screen, monitor.Left, monitor.Top, 0x00CC0020 | 0x40000000))
                    throw Failed($"BitBlt failed with {Marshal.GetLastPInvokeError()}.");
                GdiFlush();
            }
            finally
            {
                SelectObject(memory, previous);
            }
            // The DIB section is read in place: BGRA rows without padding, the alpha byte ignored (opaque).
            using var pixmap = new SKPixmap(new SKImageInfo(monitor.Width, monitor.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), bits, monitor.Width * 4);
            // A low zlib level: a 4K screen is encoded in a fraction of a second; the file stays lossless.
            using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 3)) ?? throw Failed("PNG encoding failed.");
            return data.ToArray();
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static CoreException Unavailable(string message) => new("screen.capture_unavailable", message);

    private static CoreException Failed(string message) => new("screen.capture_failed", message);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
}
