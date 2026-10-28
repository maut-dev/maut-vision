using System.Windows;

namespace MautVision.Desktop.Host;

/// <summary>
/// The size of the main window on its first show: the designed size, reduced to the work area of the screen (150 % on a 1080p
/// monitor leaves about 1280×688 DIP), and a minimum that never exceeds that area either.
/// </summary>
internal static class WindowBounds
{
    internal static readonly Size Designed = new(1200, 800);

    internal static readonly Size Minimum = new(760, 560);

    /// <summary>The window and minimum sizes that fit <paramref name="workArea"/> (DIP).</summary>
    internal static (Size Window, Size Minimum) Fit(Size workArea)
    {
        var minimum = new Size(Math.Min(Minimum.Width, workArea.Width), Math.Min(Minimum.Height, workArea.Height));
        var window = new Size(Math.Max(minimum.Width, Math.Min(Designed.Width, workArea.Width)), Math.Max(minimum.Height, Math.Min(Designed.Height, workArea.Height)));
        return (window, minimum);
    }

    /// <summary>Work area (DIP) of a 1920×1080 monitor at <paramref name="scale"/> with the taskbar of that scale (48 px at 100 %).</summary>
    internal static Size FullHdWorkArea(double scale) => new(1920 / scale, (1080 - 48 * scale) / scale);
}
