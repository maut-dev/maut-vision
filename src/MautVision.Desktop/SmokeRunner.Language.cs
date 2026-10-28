using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// The interface language without a restart: settings.setLanguage to English switches the page (settings.languageChanged), the tray and
    /// host-settings.json; the library, the base model catalog, the project settings and the navigation of a project speak English with no
    /// Cyrillic outside the names the user typed; a bridge error (a stale revision in the project settings) is shown in English; plural
    /// forms follow the language; switching back restores Russian. The run itself is Russian (--language ru of the smoke).
    /// </summary>
    private static async Task CheckLanguageAsync(CoreWebView2 web, string root, string projectId, SmokeHostProbe host, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        if (host.Tray.Texts != ("Открыть Maut Vision", "Выйти")) throw new InvalidOperationException("The smoke run does not start in Russian: " + host.Tray.Texts);
        await RunLifecycleUiStepAsync(web, LanguageEnglishScript, new { projectId }, checks, token);
        await WaitForTrayAsync(host, ("Open Maut Vision", "Exit"), token);
        RequireStoredLanguage(root, "en");
        checks.Add("English relabels the tray and is kept in host-settings.json");
        await RunLifecycleUiStepAsync(web, LanguageRussianScript, new { projectId }, checks, token);
        await WaitForTrayAsync(host, ("Открыть Maut Vision", "Выйти"), token);
        RequireStoredLanguage(root, "ru");
        checks.Add("Russian comes back to the tray and to host-settings.json");
    }

    private static async Task WaitForTrayAsync(SmokeHostProbe host, (string Open, string Quit) expected, CancellationToken token)
    {
        for (var attempt = 0; host.Tray.Texts != expected; attempt++)
        {
            if (attempt > 50) throw new InvalidOperationException($"The tray is labelled {host.Tray.Texts}, expected {expected}.");
            await Task.Delay(100, token);
        }
    }

    private static void RequireStoredLanguage(string root, string expected)
    {
        using var settings = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, Host.HostSettings.FileName)));
        var stored = settings.RootElement.TryGetProperty("language", out var value) ? value.GetString() : null;
        if (stored != expected) throw new InvalidOperationException($"host-settings.json keeps the language {stored}, expected {expected}.");
    }

    private const string LanguageHelpers = """
          const cyrillic = /[А-Яа-яЁё]/;
          const toEnglishLibrary = async () => {
            if (!$('[data-testid="project-list"]')) headerButton('All projects').click();
            return wait(() => $('[data-testid="project-list"]') && rows().length > 0, 'English library');
          };
          // Text a user reads in the element: text nodes and the labels of controls; names the user typed (projects) are not interface text.
          const shellText = async scope => {
            const names = (await request('projects.list', {})).data.items.flatMap(item => [item.name, item.description]).filter(Boolean)
              .sort((left, right) => right.length - left.length);
            const parts = [];
            const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) parts.push(node.textContent);
            for (const element of [scope, ...scope.querySelectorAll('*')]) {
              for (const name of ['aria-label', 'title', 'placeholder']) if (element.getAttribute(name)) parts.push(element.getAttribute(name));
            }
            let all = parts.join(' | ');
            for (const name of names) all = all.split(name).join('');
            return all;
          };
          const requireEnglish = async (scope, label) => {
            const all = await shellText(scope);
            const found = all.match(cyrillic);
            assert(!found, label + (found ? ': ' + all.slice(Math.max(0, found.index - 60), found.index + 60) : ''));
          };

        """;

    private const string LanguageEnglishScript = LanguageHelpers + """
          const {projectId} = input;
          await toLibrary();
          const system = await request('settings.setLanguage', {language: 'system'});
          assert(system.ok && system.data.setting === 'system' && ['ru', 'en'].includes(system.data.language), '«Как в системе» resolves to Russian or English');
          const english = await request('settings.setLanguage', {language: 'en'});
          assert(english.ok && english.data.setting === 'en' && english.data.language === 'en', 'settings.setLanguage stores English');
          await wait(() => document.documentElement.lang === 'en' && headerButton('All projects'), 'English interface');
          assert(text('h1') === 'Projects' && text('[data-testid="create-project"]') === 'Create a project'
            && text('[data-testid="import-project"]') === 'Import a project…' && part(row(projectId), 'export-project') === 'Export…'
            && $$('[data-testid="project-last"]').every(item => item.textContent === 'Last opened'),
            'The library switches to English without a reload');
          await requireEnglish($('#root'), 'The library has no Cyrillic outside project names');
          const invalid = await request('projects.open', {projectId: crypto.randomUUID()});
          assert(!invalid.ok && invalid.error.code === 'projects.not_found' && !('message' in invalid.error)
            && mautLocale.errorText(invalid.error.code, invalid.error.details) === 'The project was not found. Refresh the project list.',
            'A bridge error carries only its code; the English catalog names it');
          assert(mautLocale.errorText('bridge.timeout', {count: '1'}) === 'The application did not answer within 1 second. Repeat the request.'
            && mautLocale.errorText('bridge.timeout', {count: '10'}) === 'The application did not answer within 10 seconds. Repeat the request.',
            'English plural forms: one, other');

          headerButton('Base models').click();
          await wait(() => text('#catalog-heading') === 'Base models' && $('[data-testid="base-model-list"]'), 'English base model catalog');
          await requireEnglish($('main'), 'The base model catalog is in English');
          await toEnglishLibrary();

          const project = (await request('projects.list', {})).data.items.find(item => item.id === projectId);
          $$('button', row(projectId)).find(item => item.textContent === 'Settings').click();
          const editor = await wait(() => $$('[role="dialog"]').find(item => item.textContent.includes('Project settings')), 'English project settings');
          await requireEnglish(editor, 'The project settings dialog is in English');
          const updated = await request('projects.update', {projectId, revision: project.revision, name: project.name, description: project.description,
            baseModelId: project.baseModelId, inputSize: project.inputSize});
          assert(updated.ok, 'The project changes behind the open settings');
          $('[data-testid="project-save"]', editor).click();
          await wait(() => editor.textContent.includes('The project has already changed. Close the settings, check the data and repeat the change.'),
            'English revision conflict', () => editor.textContent);
          assert(true, 'A bridge error (projects.revision_conflict) is shown in English');
          $$('button', editor).find(item => item.textContent === 'Cancel').click();
          await wait(() => !$('[role="dialog"]'), 'Settings closed');

          $('[data-testid="open-project"]', row(projectId)).click();
          await wait(() => $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Project opened');
          await requireEnglish($('header'), 'The header of a project is in English');
          await requireEnglish($('aside nav'), 'The section navigation is in English');
          assert(labelled('Overview') && labelled('Training') && labelled('Checks')
            && document.body.textContent.includes('Overview · Project data, current jobs, models and computer load.'),
            'The sections and their descriptions are in English');
          await toEnglishLibrary();
          return {checks};
        """;

    private const string LanguageRussianScript = """
          await toLibrary();
          const russian = await request('settings.setLanguage', {language: 'ru'});
          assert(russian.ok && russian.data.setting === 'ru' && russian.data.language === 'ru', 'settings.setLanguage stores Russian');
          await wait(() => document.documentElement.lang === 'ru' && headerButton('Все проекты'), 'Russian interface');
          assert(text('h1') === 'Проекты' && text('[data-testid="create-project"]') === 'Создать проект'
            && mautLocale.errorText('projects.revision_conflict') === 'Проект уже изменился. Закройте настройки, проверьте данные и повторите изменение.',
            'Switching back restores the Russian interface and error texts');
          assert(mautLocale.errorText('bridge.timeout', {count: '1'}) === 'Приложение не ответило за 1 секунду. Повторите запрос.'
            && mautLocale.errorText('bridge.timeout', {count: '3'}) === 'Приложение не ответило за 3 секунды. Повторите запрос.'
            && mautLocale.errorText('bridge.timeout', {count: '10'}) === 'Приложение не ответило за 10 секунд. Повторите запрос.',
            'Russian plural forms: one, few, many');
          assert(mautLocale.errorText('checks.evaluation_input_mismatch', {datasetWidth: '64', datasetHeight: '48', versionWidth: '320', versionHeight: '256'})
            === 'Размер кадров датасета 64 × 48 не совпадает со входом версии 320 × 256.', 'Error texts take values of the error details');
          return {checks};
        """;
}
