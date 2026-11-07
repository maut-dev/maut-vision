namespace MautVision.Desktop.Controllers.AppWindow.ResponseDto;

/// <summary>
/// The state of the main window: Maximized — the title bar offers «Восстановить»; Active — the window has the focus (inactive buttons are
/// dimmed). Also the event window.stateChanged.
/// </summary>
public sealed record WindowStateResponseDto(bool Maximized, bool Active);
