using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// The window buttons of the title bar from the real page: «Свернуть», «Развернуть» and «Закрыть» exist at the right edge; maximize
    /// maximizes the host window and the page learns it (window.stateChanged: the button becomes «Восстановить»), a second click restores;
    /// minimize hides the page until the host restores the window; close hides the window in the tray as the system button did, without a
    /// second notice, and the tray shows it again.
    /// </summary>
    private static async Task CheckWindowControlsAsync(CoreWebView2 web, SmokeHostProbe host, Action<bool> setMinimized, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        if (host.State() != WindowState.Normal) throw new InvalidOperationException("The smoke window is not in its normal state before the window buttons.");
        await RunWindowScriptAsync(web, """
            const bar = await wait(() => $('[data-testid="title-bar"]'), 'Title bar');
            const minimize = $('[data-testid="window-minimize"]', bar);
            const maximize = $('[data-testid="window-maximize"]', bar);
            const close = $('[data-testid="window-close"]', bar);
            assert(minimize?.getAttribute('aria-label') === 'Свернуть' && maximize?.getAttribute('aria-label') === 'Развернуть'
              && close?.getAttribute('aria-label') === 'Закрыть' && maximize.dataset.maximized === 'false',
              'The title bar has the window buttons «Свернуть», «Развернуть» and «Закрыть»');
            const right = close.getBoundingClientRect();
            assert(Math.abs(right.right - document.documentElement.clientWidth) <= 1 && right.top <= 0.5
              && Math.abs(right.height - bar.getBoundingClientRect().height) <= 1 && Math.round(right.width) === 46,
              '«Закрыть» is 46 px wide, as tall as the title bar and flush with its top right corner');
            const region = element => { const style = getComputedStyle(element); return style.getPropertyValue('app-region') || style.getPropertyValue('-webkit-app-region'); };
            assert(region(bar) === 'drag' && region(close) === 'no-drag' && region($('[data-testid="title-bar-drag"]')) !== 'no-drag',
              'The title bar drags the window (app-region: drag) and its buttons stay clickable (no-drag)');
            maximize.click();
            """, checks, token);
        await WaitAsync(() => host.State() == WindowState.Maximized, "the window maximizes", token);
        await RunWindowScriptAsync(web, """
            const maximize = $('[data-testid="window-maximize"]');
            await wait(() => maximize.dataset.maximized === 'true' && maximize.getAttribute('aria-label') === 'Восстановить', 'Restore button',
              () => maximize.getAttribute('aria-label'));
            assert(true, '«Развернуть» maximizes the window and the page gets maximized: true (the button becomes «Восстановить»)');
            maximize.click();
            """, checks, token);
        await WaitAsync(() => host.State() == WindowState.Normal, "the window restores", token);
        await RunWindowScriptAsync(web, """
            const maximize = $('[data-testid="window-maximize"]');
            await wait(() => maximize.dataset.maximized === 'false' && maximize.getAttribute('aria-label') === 'Развернуть', 'Maximize button');
            assert(true, '«Восстановить» restores the window and the button becomes «Развернуть» again');
            $('[data-testid="window-minimize"]').click();
            """, checks, token);
        await WaitAsync(() => host.State() == WindowState.Minimized, "the window minimizes", token);
        await WaitVisibilityAsync(web, "hidden", token);
        setMinimized(false);
        await WaitAsync(() => host.State() == WindowState.Normal, "the host restores the window", token);
        await WaitVisibilityAsync(web, "visible", token);
        checks.Add("«Свернуть» minimizes the window (the page is hidden) and restoring it shows the page again");

        var notices = host.Tray.Notices.Count;
        await RunWindowScriptAsync(web, "$('[data-testid=\"window-close\"]').click();", checks, token);
        await WaitAsync(() => !host.WindowShown(), "the close button hides the window", token);
        await WaitVisibilityAsync(web, "hidden", token);
        if (host.Tray.Notices.Count != notices) throw new InvalidOperationException("The close button showed the tray notice again.");
        host.Tray.ClickOpen();
        await WaitAsync(host.WindowShown, "the tray shows the window after the close button", token);
        await WaitVisibilityAsync(web, "visible", token);
        checks.Add("«Закрыть» hides the window in the tray as the system close button did; «Открыть Maut Vision» shows it again");
    }

    private static async Task RunWindowScriptAsync(CoreWebView2 web, string body, JsonArray checks, CancellationToken token)
    {
        var result = await RunScriptAsync(web, "(async (input) => {" + BackgroundsUiHelpers + body + "\nreturn {checks};})(" + JsonSerializer.Serialize(new { }) + ")", token,
            attempts: 300);
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
    }
}
