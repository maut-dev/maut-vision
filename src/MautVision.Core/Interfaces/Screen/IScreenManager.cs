namespace MautVision.Core.Interfaces.Screen;

/// <summary>
/// Monitor in physical pixels of the virtual screen. Index — the number under which the screen check captures it (the same order
/// as mss: 1..n by EnumDisplayMonitors); Name — device name (\\.\DISPLAY1); IsPrimary — the primary monitor.
/// </summary>
public sealed record MonitorInfo(int Index, int Left, int Top, int Width, int Height, bool IsPrimary, string Name);

/// <summary>"Capture screen": PNG of the monitor in physical pixels (the check crop is placed on it), the monitor and all monitors at the time of the capture.</summary>
public sealed record ScreenShot(MonitorInfo Monitor, byte[] Content, string ContentType, IReadOnlyList<MonitorInfo> Monitors);

/// <summary>
/// Monitors and a screenshot without Python. Coordinates are physical pixels with per-monitor DPI awareness, like the mss
/// capture in the screen check; the screenshot is taken the same way (GDI BitBlt), so the crop on it matches the captured one.
/// Exclusive full-screen mode of games is not captured. Refusals: <c>screen.capture_unavailable</c> (no desktop),
/// <c>screen.monitor_not_found</c>, <c>screen.capture_failed</c>.
/// </summary>
public interface IScreenManager
{
    IReadOnlyList<MonitorInfo> ListMonitors();

    Task<ScreenShot> CaptureAsync(int monitor, CancellationToken cancellationToken = default);
}
