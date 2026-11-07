using System.Runtime.InteropServices;
using System.Windows;

namespace MautVision.Desktop.Host;

/// <summary>
/// The frameless windows of the host (WindowChrome without a caption): Windows 11 rounded corners, the resize band around WebView2 and the
/// compensation of a maximized window that overhangs its monitor.
/// </summary>
/// <remarks>
/// WebView2 is a child window that covers the area it is given and answers the hit tests over it itself, so the top-level window can
/// resize only where WebView2 is not: a band of <see cref="ResizeBand"/> DIP around it in the canvas colour. A maximized window keeps its
/// sizing frame beyond the monitor edges (Windows positions it so), so the content is inset by that overhang instead.
/// </remarks>
internal static class WindowFrame
{
    /// <summary>Width of the resize band around WebView2 of a normal window (DIP).</summary>
    internal const double ResizeBand = 6;

    /// <summary>Length of a corner of the band that resizes diagonally (DIP), as wide as the corners of a standard Windows frame.</summary>
    internal const double CornerGrip = 16;

    internal const int WmNcHitTest = 0x0084;

    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Asks DWM for rounded corners (Windows 11); earlier versions ignore the attribute.</summary>
    internal static void RoundCorners(IntPtr window)
    {
        var preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(window, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>
    /// The part of a maximized window beyond the work area of its monitor (DIP), or zero when the window is not maximized or does not
    /// overhang.
    /// </summary>
    internal static Thickness MaximizedOverhang(IntPtr window)
    {
        if (!GetWindowRect(window, out var bounds)) return default;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref info)) return default;
        var scale = Scale(window);
        return new Thickness(
            Math.Max(0, info.Work.Left - bounds.Left) / scale,
            Math.Max(0, info.Work.Top - bounds.Top) / scale,
            Math.Max(0, bounds.Right - info.Work.Right) / scale,
            Math.Max(0, bounds.Bottom - info.Work.Bottom) / scale);
    }

    /// <summary>
    /// The resize edge or corner of the band under a WM_NCHITTEST point (screen coordinates in <paramref name="lParam"/>), or null when
    /// the point is inside the band (the window answers HTCLIENT there and WebView2 the points over itself).
    /// </summary>
    internal static int? ResizeHitTest(IntPtr window, IntPtr lParam)
    {
        if (!GetWindowRect(window, out var bounds)) return null;
        var value = lParam.ToInt64();
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        var scale = Scale(window);
        var band = ResizeBand * scale;
        var grip = CornerGrip * scale;
        var left = x < bounds.Left + band;
        var right = x >= bounds.Right - band;
        var top = y < bounds.Top + band;
        var bottom = y >= bounds.Bottom - band;
        if (!left && !right && !top && !bottom) return null;
        // Near a corner the band resizes diagonally along both of its sides, as a standard frame does.
        var nearLeft = x < bounds.Left + grip;
        var nearRight = x >= bounds.Right - grip;
        var nearTop = y < bounds.Top + grip;
        var nearBottom = y >= bounds.Bottom - grip;
        if ((top && nearLeft) || (left && nearTop)) return HtTopLeft;
        if ((top && nearRight) || (right && nearTop)) return HtTopRight;
        if ((bottom && nearLeft) || (left && nearBottom)) return HtBottomLeft;
        if ((bottom && nearRight) || (right && nearBottom)) return HtBottomRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return left ? HtLeft : HtRight;
    }

    private static double Scale(IntPtr window)
    {
        var dpi = GetDpiForWindow(window);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
