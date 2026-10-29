# Desktop

Desktop manages windows and WebView2, creates Core and connects the API. The user first sees a loading window; the main UI opens after
Core has initialized successfully.

```csharp
loadingWindow.Show();
var core = CoreFactory.GetInstance(options);
await core.InitializeAsync(cancellationToken);
mainWindow.Show();
loadingWindow.Close();
```

The example shows the startup order. Initialization does not block the loading window thread. On failure the user gets a clear message;
the main UI never opens with an unready Core.

Desktop is split by responsibility:

- Controllers call Core contracts and return DTOs.
- Transport receives WebView2 messages, parses JSON, chooses the route and builds the response or error.
- Events subscribes to Core and forwards notifications through Transport.

A controller does not implement business algorithms. A combined response for a screen, such as the project overview (`overview.get`), is
assembled by the controller from several managers; there is no separate scenario layer for that.

A minimized window hides WebView2: the page gets `document.visibilityState = hidden` and stops its polling, and WebView2 throttles it.
After the window is restored the page is visible again. Mapping entities to DTOs is extracted when it complicates the controller code.
WebView2 calls run on the UI thread; subscriptions and resources are released by their owner.

## Window, tray and single instance

The application runs until the user chooses "Exit". Closing the window hides it to the notification area: WebView2 is hidden as for a
minimized window, and jobs continue. The first hide shows a notification once that the application keeps running in the notification
area; the fact is kept in `host-settings.json` of the work directory.

Texts of the host (loading window, tray menu and notice, exit question, native file dialogs, startup failures) come from
`Strings.resx` (English) and `Strings.ru.resx`. Their culture follows the interface language kept in `host-settings.json`; a change
relabels the tray at once ([localization](../UI/localization.md#language)).

```text
window close button        → window hidden, page hidden
tray: click, "Open"        → window and page visible
tray: "Exit"               → window shown → question if background work runs → unsaved changes guard → cancel transfers → exit
Windows session end, Restart Manager, closing the hidden window → cancel transfers → exit
```

Session end and the installer request ask nothing and do not hide the window to the tray
([shutdown requested by Windows](appdata-and-packaging.md#shutdown-requested-by-windows)).

The first window size is 1200×800 DIP, reduced to the screen work area; the minimum size is not larger than that either. The manifest
enables PerMonitorV2: the window and WebView2 render at the scale of their monitor.

The question before exit is a native message box, not a page dialog: exit does not depend on whether the page responds. The host asks it
if jobs of open projects are running (`IJobsManager.CountActive`) or an import is in progress. After consent, running exports and imports
are cancelled and awaited for up to 5 s to remove temporary files; leftovers are removed by the cleanup after the next start.

The single instance is a named Mutex in the session namespace (`Local\`) whose name contains a hash of the full path of the work
directory; therefore checks with separate work directories do not interfere. A second start with the same directory signals the event of
the first one (which shows its window) and exits before Core and the log are created.

The tray icon is a WinForms `NotifyIcon` on the WPF UI thread. A smoke run does not create the icon: a recording tray lets the checks
cover hiding, showing, the notification and the exit question without a real tray.
