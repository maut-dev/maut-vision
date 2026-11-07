namespace MautVision.Desktop.Controllers.AppWindow.RequestDto;

/// <summary>window.getState: the state of the main window for the title bar.</summary>
public sealed record GetWindowStateRequestDto;

/// <summary>window.minimize: the window goes to the taskbar.</summary>
public sealed record MinimizeWindowRequestDto;

/// <summary>window.toggleMaximize: a maximized window is restored, any other is maximized.</summary>
public sealed record ToggleMaximizeWindowRequestDto;

/// <summary>window.close: the close button of the title bar; the window hides in the notification area, the application keeps running.</summary>
public sealed record CloseWindowRequestDto;
