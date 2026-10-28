using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using MautVision.Desktop.Host;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

/// <summary>Layout seams of the window for the smoke run: an explicit size of the WebView2 control (null — the window's), the DPI scale of
/// the window, and the window frame around the control in DIP.</summary>
internal sealed record SmokeLayoutProbe(Action<Size?> SetBrowserSize, Func<double> DpiScale, Func<Size> Frame);

internal static partial class SmokeRunner
{
    /// <summary>
    /// The page at the sizes users get: the window of a 1080p monitor at the scale of this run (the window reduced to the work area) and the
    /// minimum window. WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--force-device-scale-factor=1.25 or 1.5 (smoke.ps1 -DeviceScaleFactor) renders
    /// the page at that scale on any monitor; the control gets the size in DIP that gives the CSS viewport of such a window. The library, the
    /// catalogue and every section of a project must not scroll or stick out horizontally, and a Radix list must open inside the viewport.
    /// </summary>
    private static async Task CheckLayoutAsync(CoreWebView2 web, string projectId, SmokeLayoutProbe layout, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var forced = ForcedDeviceScale();
        var dpi = layout.DpiScale();
        var scale = forced ?? dpi;
        foreach (var userScale in new[] { 1.5, 1.25 })
        {
            var (window, minimum) = WindowBounds.Fit(WindowBounds.FullHdWorkArea(userScale));
            if (window.Width > 1920 / userScale || window.Height > (1080 - 48 * userScale) / userScale || minimum.Width > window.Width)
                throw new InvalidOperationException($"The window at {userScale:P0} on 1080p does not fit the work area: {window}.");
        }
        checks.Add("At 125 % and 150 % on a 1080p monitor the main window and its minimum fit the work area");

        var frame = layout.Frame();
        var targets = new List<(string Label, Size Viewport)>
        {
            ($"the window of a 1080p monitor at {scale:P0}", Viewport(WindowBounds.Fit(WindowBounds.FullHdWorkArea(scale)).Window, frame)),
            ("the minimum window", Viewport(WindowBounds.Minimum, frame)),
        };
        try
        {
            foreach (var (label, viewport) in targets)
            {
                layout.SetBrowserSize(new Size(viewport.Width * scale / dpi, viewport.Height * scale / dpi));
                var arguments = JsonSerializer.Serialize(new { projectId, width = viewport.Width, height = viewport.Height, scale, label });
                var result = await RunScriptAsync(web, "(async (input) => {" + BackgroundsUiHelpers + LayoutScript + "})(" + arguments + ")", token, attempts: 900);
                foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
            }
        }
        finally
        {
            layout.SetBrowserSize(null);
        }
    }

    /// <summary>The CSS viewport of a window of <paramref name="window"/> DIP at its own scale: the window without its frame.</summary>
    private static Size Viewport(Size window, Size frame) => new(Math.Floor(window.Width - frame.Width), Math.Floor(window.Height - frame.Height));

    /// <summary>The scale forced on WebView2 by --force-device-scale-factor in WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, if any.</summary>
    internal static double? ForcedDeviceScale()
    {
        var arguments = Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") ?? "";
        var match = Regex.Match(arguments, @"--force-device-scale-factor=([0-9]+(?:\.[0-9]+)?)");
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private const string LayoutScript = """
          const {projectId, width, height, scale, label} = input;
          await wait(() => Math.abs(innerWidth - width) <= 1 && Math.abs(innerHeight - height) <= 1, 'Viewport ' + width + '×' + height,
            () => innerWidth + '×' + innerHeight);
          assert(Math.abs(devicePixelRatio - scale) < 0.01, 'The page renders at ' + Math.round(scale * 100) + ' % (' + label + ', ' + width + '×' + height + ' CSS px)');
          const scrolls = element => {
            for (let parent = element.parentElement; parent && parent !== document.body; parent = parent.parentElement) {
              const overflow = getComputedStyle(parent).overflowX;
              if (overflow === 'auto' || overflow === 'scroll') return true;
            }
            return false;
          };
          const describe = element => element.tagName.toLowerCase() + (element.dataset.testid ? '[' + element.dataset.testid + ']' : '')
            + ' «' + (element.getAttribute('aria-label') ?? element.textContent ?? '').trim().slice(0, 40) + '»';
          const problems = () => {
            const found = [];
            const viewport = document.documentElement.clientWidth;
            if (document.scrollingElement.scrollWidth > viewport + 1) found.push('the page scrolls horizontally: ' + document.scrollingElement.scrollWidth + ' > ' + viewport);
            const main = $('main');
            if (main && main.scrollWidth > main.clientWidth + 1) found.push('main scrolls horizontally: ' + main.scrollWidth + ' > ' + main.clientWidth);
            for (const element of $$('header *, aside *, main *, [role="listbox"], [role="menu"], [role="dialog"]')) {
              const box = element.getBoundingClientRect();
              if (box.width < 1 || box.height < 1 || getComputedStyle(element).visibility === 'hidden') continue;
              if ((box.right > viewport + 1 || box.left < -1) && !scrolls(element)) found.push(describe(element) + ' ' + Math.round(box.left) + '…' + Math.round(box.right));
            }
            return found;
          };
          const settle = async () => {
            await sleep(300);
            await wait(() => !$$('[role="status"]').some(item => item.textContent.includes('Загрузка раздела')), 'Section loaded');
            await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
          };
          const verify = async place => {
            await settle();
            const found = problems();
            if (found.length) throw new Error(place + ' at ' + width + '×' + height + ' (' + label + ') overflows: ' + found.slice(0, 8).join('; '));
          };
          const headerButton = text => $$('header button').find(item => item.textContent.includes(text));
          headerButton('Все проекты').click();
          await wait(() => $('[data-testid="open-project"]'), 'Library');
          await verify('The library');
          headerButton('Базовые модели').click();
          await settle();
          await verify('The base model catalogue');
          headerButton('Все проекты').click();
          const listed = (await request('projects.list', {})).data.items.filter(item => !item.archivedAt);
          const target = listed.find(item => item.id === projectId) ?? listed[0];
          assert(target, 'A project is left for the layout check');
          (await wait(() => $('[data-project-id="' + target.id + '"] [data-testid="open-project"]'), 'Project row')).click();
          await wait(() => $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Project opened');
          const sections = $$('[data-section]').filter(item => !item.disabled).map(item => item.dataset.section);
          for (const id of sections) {
            $('[data-section="' + id + '"]').click();
            await wait(() => $('[data-section="' + id + '"]')?.getAttribute('aria-current') === 'page', 'Section ' + id);
            await verify('Section ' + id);
          }
          $('[aria-label="Переключить проект"]').click();
          const list = await wait(() => $('[role="listbox"]'), 'Project list opened');
          await settle();
          const box = list.getBoundingClientRect();
          const inside = box.left >= -1 && box.top >= -1 && box.right <= innerWidth + 1 && box.bottom <= innerHeight + 1;
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[role="listbox"]'), 'Project list closed');
          if (!inside) throw new Error('The project list opens outside the viewport: ' + JSON.stringify(box));
          assert(true, 'The library, the catalogue and the sections ' + sections.join(', ') + ' do not overflow horizontally at ' + width + '×' + height
            + ' CSS px (' + label + '); the Radix list opens inside the viewport');
          return {checks};
        """;
}
