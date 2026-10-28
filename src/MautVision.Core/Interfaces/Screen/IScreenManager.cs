namespace MautVision.Core.Interfaces.Screen;

/// <summary>
/// Монитор в физических пикселях виртуального экрана. Index — номер, под которым его захватывает проверка с экрана (тот же порядок,
/// что у mss: 1..n по EnumDisplayMonitors); Name — имя устройства (\\.\DISPLAY1); IsPrimary — основной монитор.
/// </summary>
public sealed record MonitorInfo(int Index, int Left, int Top, int Width, int Height, bool IsPrimary, string Name);

/// <summary>«Снять экран»: PNG монитора в физических пикселях (рамка проверки ставится на нём), монитор и все мониторы на момент снимка.</summary>
public sealed record ScreenShot(MonitorInfo Monitor, byte[] Content, string ContentType, IReadOnlyList<MonitorInfo> Monitors);

/// <summary>
/// Мониторы и снимок экрана без Python. Координаты — физические пиксели при осведомлённости о DPI каждого монитора, как у захвата
/// mss в проверке с экрана; снимок делается тем же способом (GDI BitBlt), поэтому рамка на снимке совпадает с захватываемой.
/// Эксклюзивный полноэкранный режим игр не захватывается. Отказы: <c>screen.capture_unavailable</c> (нет рабочего стола),
/// <c>screen.monitor_not_found</c>, <c>screen.capture_failed</c>.
/// </summary>
public interface IScreenManager
{
    IReadOnlyList<MonitorInfo> ListMonitors();

    Task<ScreenShot> CaptureAsync(int monitor, CancellationToken cancellationToken = default);
}
