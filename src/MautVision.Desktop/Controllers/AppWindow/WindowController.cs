using MautVision.Desktop.Controllers.AppWindow.RequestDto;
using MautVision.Desktop.Controllers.AppWindow.ResponseDto;

namespace MautVision.Desktop.Controllers.AppWindow;

/// <summary>
/// The window buttons of the title bar drawn by the page (the window has no system caption): state, minimize, maximize or restore, and
/// close, which hides the window in the notification area. Changes made elsewhere (Win+Up, snapping, a double click on the drag area)
/// reach the page as window.stateChanged.
/// </summary>
internal sealed class WindowController(IHostWindow window)
{
    public async Task<WindowStateResponseDto> GetStateAsync(GetWindowStateRequestDto request, CancellationToken cancellationToken)
        => Map(await window.ApplyAsync(WindowCommand.None, cancellationToken));

    public async Task<WindowStateResponseDto> MinimizeAsync(MinimizeWindowRequestDto request, CancellationToken cancellationToken)
        => Map(await window.ApplyAsync(WindowCommand.Minimize, cancellationToken));

    public async Task<WindowStateResponseDto> ToggleMaximizeAsync(ToggleMaximizeWindowRequestDto request, CancellationToken cancellationToken)
        => Map(await window.ApplyAsync(WindowCommand.ToggleMaximize, cancellationToken));

    public async Task<WindowStateResponseDto> CloseAsync(CloseWindowRequestDto request, CancellationToken cancellationToken)
        => Map(await window.ApplyAsync(WindowCommand.Close, cancellationToken));

    internal static WindowStateResponseDto Map(HostWindowState state) => new(state.Maximized, state.Active);
}
