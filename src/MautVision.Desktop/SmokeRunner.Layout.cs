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
    /// catalogue and every section of a project must not scroll or stick out horizontally, and a Radix list must open inside the viewport. In
    /// every tab of backgrounds, images, generation, datasets (and of a chosen dataset), training and checks and in the classes and models
    /// sections the scrolling regions are not nested, at most three and at least 96 px tall. The overview and analytics scroll with the page
    /// and have no scrolling region of their own. The dialog of AI assistants stays inside the window with each tab as its only scroll, the
    /// same height on every tab and no horizontal overflow with the longest call of the MCP run unfolded in its log.
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
            // The document never grows past the window: an absolutely positioned element (the hidden native input of a Radix Select or
            // Checkbox) escapes the clipping of a scrolling region that is not positioned and stretches the page.
            const viewportHeight = document.documentElement.clientHeight;
            if (document.scrollingElement.scrollHeight > viewportHeight + 1) found.push('the page is taller than the window: ' + document.scrollingElement.scrollHeight + ' > ' + viewportHeight);
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
          // One scroll per region: in every tab of a background set, an image group, generation, datasets (and the tabs of a chosen dataset),
          // training and checks and in the classes and models sections the visible scrolling regions are the list column and the work areas
          // of the tab, none inside another and none squeezed below a usable height. A horizontal table scroller hides its vertical overflow,
          // so it is not counted.
          const scrollers = scope => $$('*', scope).filter(element => {
            const box = element.getBoundingClientRect();
            const overflow = getComputedStyle(element).overflowY;
            return box.width >= 1 && (overflow === 'auto' || overflow === 'scroll');
          });
          const tall = element => Math.round(element.getBoundingClientRect().height);
          const regions = [];
          const checkRegions = async (id, name) => {
            const place = 'Section ' + id + (name ? ', tab ' + name : '');
            await verify(place);
            const found = scrollers($('[data-testid="' + id + '-section"]'));
            const nested = found.filter(element => found.some(other => other !== element && other.contains(element)));
            if (nested.length) throw new Error(place + ' at ' + width + '×' + height + ' scrolls inside a scrolling region: ' + nested.slice(0, 4).map(describe).join('; '));
            if (found.length > 3) throw new Error(place + ' at ' + width + '×' + height + ' has ' + found.length + ' scrolling regions: ' + found.map(describe).join('; '));
            if (found.some(element => tall(element) < 96)) {
              const parts = ['[role="tablist"]', '[role="tabpanel"]:not([hidden])'].map(selector => selector + ' ' + ($(selector) ? tall($(selector)) : '-'));
              throw new Error(place + ' at ' + width + '×' + height + ' squeezes a scrolling region below 96 px: '
                + found.map(element => describe(element) + ' ' + tall(element)).join('; ') + ' (' + parts.join(', ') + ')');
            }
            regions.push((name ?? id) + ' ' + found.map(tall).join('/') + ' px');
          };
          const openTab = async trigger => {
            trigger.dispatchEvent(new MouseEvent('mousedown', {bubbles: true, cancelable: true, button: 0}));
            await wait(() => trigger.getAttribute('aria-selected') === 'true', 'Tab ' + trigger.dataset.testid);
          };
          // list and item: the list column and the item to choose; required — without an item the section is skipped (its tabs belong to the item).
          // empty: the test id the section shows instead of its root when there is nothing to work with (the section is skipped then).
          // groups: tab prefixes; open — the tab to show first; item — the group exists only for a chosen item.
          const layoutTargets = [
            {id: 'backgrounds', list: 'background-sets', item: '[data-background-set-id]', required: true, groups: [{prefix: 'background-tab-'}]},
            {id: 'images', list: 'image-groups', item: '[data-image-group-id]', required: true, groups: [{prefix: 'image-tab-'}]},
            {id: 'generation', groups: [{prefix: 'tab-'}]},
            {id: 'datasets', list: 'dataset-list', item: '[data-dataset-version-id]', groups: [{prefix: 'datasets-tab-'},
              {prefix: 'dataset-detail-tab-', open: 'datasets-tab-datasets', item: true}]},
            {id: 'classes', list: 'class-list', item: '[data-class-id] button', groups: []},
            {id: 'training', groups: [{prefix: 'training-tab-'}]},
            {id: 'models', groups: []},
            {id: 'checks', empty: 'checks-empty', groups: [{prefix: 'checks-tab-'}]},
          ];
          for (const target of layoutTargets) {
            const {id, list} = target;
            if (!sections.includes(id)) continue;
            $('[data-section="' + id + '"]').click();
            await wait(() => $('[data-section="' + id + '"]')?.getAttribute('aria-current') === 'page'
              && ($('[data-testid="' + id + '-section"]') || target.empty && $('[data-testid="' + target.empty + '"]'))
              && (!list || $('[data-testid="' + list + '"]') && !$('[data-testid="' + list + '"] [role="status"]')), 'Section ' + id + ' ready');
            const root = $('[data-testid="' + id + '-section"]');
            if (!root) continue;
            const first = target.item ? $(target.item, $('[data-testid="' + list + '"]')) : null;
            if (target.required && !first) continue;
            if (first) first.click();
            if (!target.groups.length) {
              await checkRegions(id);
              continue;
            }
            for (const group of target.groups) {
              if (group.item && !first) continue;
              if (group.open) await openTab(await wait(() => $('[data-testid="' + group.open + '"]', root), 'Tab ' + group.open));
              const triggers = await wait(() => { const found = $$('[role="tab"][data-testid^="' + group.prefix + '"]', root); return found.length ? found : null; }, 'Tabs of ' + id);
              for (const trigger of triggers.filter(element => !element.disabled)) {
                await openTab(trigger);
                await checkRegions(id, trigger.dataset.testid);
              }
            }
          }
          assert(regions.length > 0, 'Every tab of backgrounds, images, generation, datasets (with the tabs of a chosen dataset), training and checks and the classes'
            + ' and models sections have at most 3 scrolling regions, none nested and none under 96 px, at ' + width + '×' + height + ' CSS px (' + label + '): '
            + regions.join(', '));
          // Dashboards scroll with the page: main is their only scroll, lists are paged and wide tables scroll only horizontally.
          const flowing = [];
          for (const id of ['overview', 'analytics'].filter(item => sections.includes(item))) {
            $('[data-section="' + id + '"]').click();
            await wait(() => $('[data-section="' + id + '"]')?.getAttribute('aria-current') === 'page' && $('[data-testid="' + id + '-section"], [data-testid="' + id + '-empty"]'),
              'Section ' + id + ' ready');
            await verify('Section ' + id);
            const root = $('[data-testid="' + id + '-section"]');
            if (!root) continue;
            const found = scrollers(root);
            if (found.length) throw new Error('Section ' + id + ' at ' + width + '×' + height + ' scrolls inside the page: ' + found.slice(0, 4).map(describe).join('; '));
            flowing.push(id);
          }
          assert(true, 'The sections ' + flowing.join(', ') + ' scroll only with the page at ' + width + '×' + height + ' CSS px (' + label + ')');
          $('[aria-label="Переключить проект"]').click();
          const list = await wait(() => $('[role="listbox"]'), 'Project list opened');
          await settle();
          const box = list.getBoundingClientRect();
          const inside = box.left >= -1 && box.top >= -1 && box.right <= innerWidth + 1 && box.bottom <= innerHeight + 1;
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[role="listbox"]'), 'Project list closed');
          if (!inside) throw new Error('The project list opens outside the viewport: ' + JSON.stringify(box));
          // The dialog of AI assistants: inside the window, never wider than itself, the same height on every tab, and the open tab is its
          // only scroll. The MCP run before this check logged calls with long arguments; the longest one is shown unfolded.
          $('[data-testid="mcp-settings-button"]').click();
          const mcpDialog = await wait(() => $('[data-testid="mcp-settings-dialog"]'), 'MCP dialog');
          await wait(() => $('[data-testid="mcp-status"]', mcpDialog) && $('[data-testid="mcp-call-log"] [data-sequence]', mcpDialog), 'MCP dialog ready');
          const logged = (await request('mcp.listCalls', {limit: 500})).data;
          const longest = logged.reduce((best, call) => call.arguments.length > best.arguments.length ? call : best);
          const mcpHeights = [];
          const mcpRegions = [];
          for (const trigger of $$('[role="tab"][data-testid^="mcp-tab-"]', mcpDialog)) {
            await openTab(trigger);
            const place = 'The MCP dialog, tab ' + trigger.dataset.testid + ', at ' + width + '×' + height + ' (' + label + ')';
            const panel = $('[role="tabpanel"]:not([hidden])', mcpDialog);
            if (trigger.dataset.testid === 'mcp-tab-log') {
              const row = () => $('[data-sequence="' + longest.sequence + '"]', panel);
              while (!row()) {
                const more = $('[data-testid="mcp-log-more"]', panel);
                if (!more) throw new Error(place + ' does not list the call ' + longest.sequence + ' with the longest arguments');
                const shown = $$('[data-sequence]', panel).length;
                more.click();
                await wait(() => $$('[data-sequence]', panel).length > shown, 'More calls');
              }
              $('[data-testid="mcp-call-arguments-toggle"]', row()).click();
              await wait(() => $('[data-testid="mcp-call-arguments-toggle"]', row()).getAttribute('aria-expanded') === 'true', 'Arguments unfolded');
              row().scrollIntoView({block: 'nearest'});
            }
            await verify(place);
            const dialogBox = mcpDialog.getBoundingClientRect();
            if (dialogBox.left < -1 || dialogBox.top < -1 || dialogBox.right > innerWidth + 1 || dialogBox.bottom > innerHeight + 1)
              throw new Error(place + ' is outside the viewport: ' + JSON.stringify(dialogBox));
            const found = scrollers(mcpDialog);
            if (found.length !== 1 || found[0] !== panel) throw new Error(place + ' must scroll only in the tab: ' + found.map(describe).join('; '));
            if (mcpDialog.scrollWidth > mcpDialog.clientWidth + 1 || panel.scrollWidth > panel.clientWidth + 1)
              throw new Error(place + ' overflows horizontally: ' + mcpDialog.scrollWidth + ' > ' + mcpDialog.clientWidth + ', ' + panel.scrollWidth + ' > ' + panel.clientWidth);
            if (tall(panel) < 96) throw new Error(place + ' squeezes the tab to ' + tall(panel) + ' px');
            mcpHeights.push(Math.round(dialogBox.height));
            mcpRegions.push(trigger.dataset.testid + ' ' + tall(panel) + ' px');
          }
          assert(mcpRegions.length === 3 && Math.max(...mcpHeights) - Math.min(...mcpHeights) <= 1, 'The MCP dialog has the tabs Server, Clients and Log of the same height ('
            + mcpHeights.join('/') + ' px)');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-testid="mcp-settings-dialog"]'), 'MCP dialog closed');
          assert(true, 'The MCP dialog stays inside the window at ' + width + '×' + height + ' CSS px (' + label + '): each tab is its only scroll ('
            + mcpRegions.join(', ') + ') and nothing overflows horizontally with the longest logged arguments (' + longest.arguments.length + ' characters) unfolded');
          assert(true,'The library, the catalogue and the sections ' + sections.join(', ') + ' do not overflow horizontally at ' + width + '×' + height
            + ' CSS px (' + label + '); the Radix list opens inside the viewport');
          return {checks};
        """;
}
