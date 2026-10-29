using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// The variants, classes, backgrounds and images sections in English: each section has no Cyrillic outside data the user gave
    /// (project, class, variant, group and set names with descriptions, file names); the summary of a background set takes the
    /// English plural form, and a blur radius out of range shows the English validation message. Runs while the interface is English,
    /// between the English and the Russian steps of <see cref="CheckLanguageAsync"/>.
    /// </summary>
    private static Task CheckImagesCatalogBackgroundsEnglishAsync(CoreWebView2 web, string projectId, JsonArray checks, CancellationToken token) =>
        RunLifecycleUiStepAsync(web, LanguageHelpers + ImagesCatalogBackgroundsEnglishScript, new { projectId }, checks, token);

    private const string ImagesCatalogBackgroundsEnglishScript = """
          const {projectId} = input;
          const fileNames = /[^\s|·/:()«»“”"]+\.(png|jpe?g|webp|bmp|mp4|mkv|avi|mov|webm|m4v)/gi;
          // Text a user reads in a section, without the data the user gave: names and descriptions, chosen files.
          const sectionText = async scope => {
            const names = (await request('projects.list', {})).data.items.flatMap(item => [item.name, item.description]);
            const workspace = (await request('classes.getWorkspace', {projectId})).data;
            names.push(...[...workspace.classes, ...workspace.variants].flatMap(item => [item.name, item.description]));
            names.push(...(await request('images.listGroups', {projectId})).data.flatMap(item => [item.name, item.description]));
            names.push(...(await request('backgrounds.listSets', {projectId})).data.flatMap(item => [item.name, item.description]));
            const parts = [];
            const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) parts.push(node.textContent);
            for (const element of [scope, ...scope.querySelectorAll('*')]) {
              for (const name of ['aria-label', 'title', 'placeholder', 'alt']) if (element.getAttribute(name)) parts.push(element.getAttribute(name));
            }
            let all = parts.join(' | ');
            for (const name of names.filter(Boolean).sort((left, right) => right.length - left.length)) all = all.split(name).join('');
            return all.replace(fileNames, '');
          };
          const requireEnglishSection = async label => {
            const all = await sectionText($('main'));
            const found = all.match(cyrillic);
            assert(!found, label + (found ? ': ' + all.slice(Math.max(0, found.index - 60), found.index + 60) : ''));
          };
          const openSection = async (id, ready) => {
            $('[data-section="' + id + '"]').click();
            return wait(() => $('[data-section="' + id + '"]')?.getAttribute('aria-current') === 'page' && ready(), 'English section ' + id);
          };

          await toEnglishLibrary();
          $('[data-testid="open-project"]', row(projectId)).click();
          await wait(() => $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Project opened in English');

          await openSection('variants', () => $('[data-testid="variant-list"]'));
          assert(text('[data-testid="catalog-add"]') === 'Add variant' && $('[data-testid="catalog-search"]').placeholder === 'Search variants',
            'The variants section speaks English');
          await requireEnglishSection('The variants section has no Cyrillic outside user data');

          await openSection('classes', () => $('[data-testid="class-list"]') && $('[data-testid="class-content"]')
            && !$('[data-testid="class-content"] p[role="status"]'));
          assert(text('[data-testid="catalog-add"]') === 'Add class', 'The classes section speaks English');
          const assetCounts = $$('[data-testid="asset-count"]').map(item => item.textContent);
          assert(assetCounts.every(value => /^(1 image|[\d,]+ images)$/.test(value) && value !== '1 images'),
            'Image counts of class variants take the English plural: ' + assetCounts.join(', '));
          await requireEnglishSection('The classes section has no Cyrillic outside user data');

          const sets = (await request('backgrounds.listSets', {projectId})).data;
          const set = sets.find(item => !item.archivedAt && item.itemCount > 1) ?? sets.find(item => !item.archivedAt);
          assert(set, 'The project has an active background set');
          await openSection('backgrounds', () => $('[data-testid="backgrounds-section"]') && $('[data-background-set-id="' + set.id + '"]'));
          $('[data-background-set-id="' + set.id + '"]').click();
          await wait(() => text('[data-testid="background-set-title"]') === set.name
            && ($('[data-testid="background-gallery"]') || $('[data-testid="backgrounds-empty"]')), 'English background set opened');
          const summary = (set.itemCount === 1 ? '1 background' : set.itemCount.toLocaleString('en-US') + ' backgrounds')
            + ' · ' + set.targetSize.width + ' × ' + set.targetSize.height;
          assert(text('[data-background-set-id="' + set.id + '"] [data-testid="background-set-summary"]') === summary,
            'A background set summary takes the English plural: ' + summary);
          if ($('[data-testid="background-add-toggle"]').getAttribute('aria-expanded') === 'false') $('[data-testid="background-add-toggle"]').click();
          if ($('[data-testid="background-runs-toggle"]')?.getAttribute('aria-expanded') === 'false') $('[data-testid="background-runs-toggle"]').click();
          (await wait(() => $('[data-testid="blur-enabled"]'), 'Blur settings')).click();
          setValue(await wait(() => $('[data-testid="blur-max"]'), 'Blur radius fields'), '2000');
          await wait(() => text('[data-testid="blur-error"]') === 'The radius is from 0 to 1000 px.', 'English blur validation',
            () => text('[data-testid="blur-error"]'));
          assert(true, 'A blur radius out of range shows the English validation message');
          await requireEnglishSection('The backgrounds section has no Cyrillic outside user data');
          $('[data-testid="blur-enabled"]').click();

          const groups = (await request('images.listGroups', {projectId})).data;
          const group = groups.find(item => item.origin === 'manual' && !item.archivedAt && item.imageCount > item.archivedImageCount)
            ?? groups.find(item => item.imageCount > 0) ?? groups[0];
          assert(group, 'The project has an image group');
          await openSection('images', () => $('[data-testid="images-section"]') && $('[data-image-group-id="' + group.id + '"]'));
          $('[data-image-group-id="' + group.id + '"]').click();
          await wait(() => text('[data-testid="image-group-title"]').startsWith(group.name)
            && ($('[data-testid="image-tile"]') || $('[data-testid="images-empty"]')), 'English image group opened');
          assert(text('[data-testid="image-group-count"]').startsWith('Frames: ') && $('[data-testid="filter-archive"]').textContent === 'Active',
            'The images section speaks English');
          await requireEnglishSection('The images section has no Cyrillic outside user data');

          await toEnglishLibrary();
          return {checks};
        """;
}
