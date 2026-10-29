using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// The sections «Генерация», «Датасеты», «Обучение» and «Модели» in English (called while the page speaks English): no Cyrillic in
    /// the section outside data the user typed (names and descriptions read from the host), an English plural form (a recipe start
    /// button for one and two images, or the image count of a dataset) and an English validation message (the image count of a run).
    /// </summary>
    private static async Task CheckProductionSectionsInEnglishAsync(CoreWebView2 web, string projectId, JsonArray checks, CancellationToken token)
    {
        await RunLifecycleUiStepAsync(web, LanguageHelpers + ProductionSectionsEnglishScript, new { projectId }, checks, token);
    }

    private const string ProductionSectionsEnglishScript = """
          const {projectId} = input;
          // Strings of user data: every name, description and file name the host returns for the project.
          const userData = new Set();
          const collect = value => {
            if (Array.isArray(value)) { value.forEach(collect); return; }
            if (!value || typeof value !== 'object') return;
            for (const [key, item] of Object.entries(value)) {
              if (typeof item === 'string' && /name|description|label/i.test(key) && cyrillic.test(item)) userData.add(item);
              else collect(item);
            }
          };
          for (const [method, payload] of [
            ['projects.list', {}], ['classes.getWorkspace', {projectId}], ['images.listGroups', {projectId}], ['backgrounds.listSets', {projectId}],
            ['generation.listRecipes', {projectId}], ['generation.listRuns', {projectId}], ['datasets.list', {projectId}],
            ['datasets.listOperations', {projectId, kind: 'build'}], ['datasets.listOperations', {projectId, kind: 'verify'}],
            ['datasets.listOperations', {projectId, kind: 'export'}], ['training.listPresets', {}], ['training.listRuns', {projectId}],
            ['training.listModelVersions', {projectId}],
          ]) {
            const reply = await request(method, payload);
            if (reply.ok) collect(reply.data);
          }
          const names = [...userData].sort((left, right) => right.length - left.length);
          const requireSectionEnglish = async (scope, label) => {
            let all = await shellText(scope);
            for (const name of names) all = all.split(name).join('');
            const found = all.match(cyrillic);
            assert(!found, label + (found ? ': ' + all.slice(Math.max(0, found.index - 60), found.index + 60) : ''));
          };

          $('[data-testid="open-project"]', row(projectId)).click();
          await wait(() => $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Project opened');

          $('[data-section="generation"]').click();
          await wait(() => $('[data-testid="generation-section"]') && $('[data-testid="launch-count"]'), 'English generation');
          await wait(() => text('[data-testid="tab-recipes"]').startsWith('Presets ('), 'English generation tabs');
          await requireSectionEnglish($('main'), 'The generation section has no Cyrillic outside user data');
          const count = $('[data-testid="launch-count"]');
          const before = count.value;
          setValue(count, '0');
          await wait(() => text('[data-testid="launch-hint"]') === 'Enter a whole count from 1.', 'English validation', () => text('[data-testid="launch-hint"]'));
          assert(true, 'A validation message of generation is in English');
          let plural = false;
          if ($('[data-testid="recipe-start"]')) {
            setValue(count, '1');
            await wait(() => text('[data-testid="recipe-start"]') === 'Generate 1 image', 'English plural one', () => text('[data-testid="recipe-start"]'));
            setValue(count, '2');
            await wait(() => text('[data-testid="recipe-start"]') === 'Generate 2 images', 'English plural other', () => text('[data-testid="recipe-start"]'));
            plural = true;
          }
          setValue(count, before);

          $('[data-section="datasets"]').click();
          await wait(() => $('[data-testid="datasets-section"]') && text('[data-testid="datasets-tab-build"]') === 'Build', 'English datasets');
          await wait(() => !$('[data-testid="dataset-list"] [role="status"]'), 'Datasets loaded');
          await requireSectionEnglish($('main'), 'The datasets section has no Cyrillic outside user data');
          const sizes = $$('[data-dataset-version-id] p:nth-child(2)').map(item => item.textContent);
          assert(sizes.length > 0 && sizes.every(item => /^(1 image|[\d,]+ images) · /.test(item)), 'Dataset image counts take English plural forms: ' + sizes.join(' | '));
          $('[data-testid="datasets-tab-build"]').click();
          await wait(() => $('[data-testid="dataset-build-form"]') && !$('[data-testid="dataset-build-form"]').closest('[hidden]'), 'English dataset build form');
          await requireSectionEnglish($('main'), 'The dataset build form has no Cyrillic outside user data');
          $('[data-testid="datasets-tab-datasets"]').click();

          $('[data-section="training"]').click();
          await wait(() => $('[data-testid="training-section"]') && text('[data-testid="training-tab-settings"]') === 'Settings', 'English training');
          await wait(() => $('[data-testid="training-history"]'), 'Training history loaded');
          await requireSectionEnglish($('main'), 'The training section has no Cyrillic outside user data');
          $('[data-testid="training-tab-history"]').click();
          await requireSectionEnglish($('main'), 'The training history has no Cyrillic outside user data');
          $('[data-testid="training-tab-settings"]').click();

          $('[data-section="models"]').click();
          await wait(() => $('[data-testid="models-section"]') && ($('[data-testid="models-list"]') || $('[data-testid="models-empty"]')), 'English models');
          await requireSectionEnglish($('main'), 'The models section has no Cyrillic outside user data');
          if ($('[data-testid="models-summary"]')) {
            assert(/^\d+ versions? · working — /.test(text('[data-testid="models-summary"]')), 'The models summary takes an English plural form');
            plural = true;
          }
          assert(plural, 'An English plural form was checked in the production sections');

          await toEnglishLibrary();
          return {checks};
        """;
}
