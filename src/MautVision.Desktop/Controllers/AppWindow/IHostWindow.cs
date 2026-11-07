namespace MautVision.Desktop.Controllers.AppWindow;

/// <summary>A command of the window buttons in the title bar of the page; <see cref="None"/> only reads the state.</summary>
internal enum WindowCommand
{
    None,
    Minimize,
    ToggleMaximize,
    Close,
}

/// <summary>The state of the main window the title bar shows: maximized (the restore icon) and active (bright buttons).</summary>
internal sealed record HostWindowState(bool Maximized, bool Active);

/// <summary>The main window as the title bar of the page sees it.</summary>
internal interface IHostWindow
{
    /// <summary>
    /// Applies the command on the UI thread and returns the state after it. Close does what the system close button did: the window
    /// hides in the notification area.
    /// </summary>
    Task<HostWindowState> ApplyAsync(WindowCommand command, CancellationToken cancellationToken);
}
