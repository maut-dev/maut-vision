using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Overview, Checks (every mode) and Analytics in English on the project the other checks filled: no Cyrillic in the sections outside
    /// the names the user typed and the files the checks read; headings, the version caption, the evaluation form and the analytics table
    /// read in English; plural forms (threads of the CPU, frames of a dataset split) follow the language; a validation message of the check
    /// fields is English. Switching back restores Russian on the page and in the tray.
    /// </summary>
    private static async Task CheckInsightsLanguageAsync(CoreWebView2 web, string projectId, SmokeHostProbe host, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var arguments = JsonSerializer.Serialize(new { projectId });
        try
        {
            foreach (var body in new[] { InsightsEnglishOverviewScript, InsightsEnglishChecksScript, InsightsEnglishAnalyticsScript })
                await RunInsightsLanguageStepAsync(web, body, arguments, checks, token);
        }
        catch
        {
            // The failure is the result; Russian comes back so the report and the next run start from the usual language.
            try { await RunInsightsLanguageStepAsync(web, InsightsRussianScript, arguments, new JsonArray(), token); } catch (Exception) { }
            throw;
        }
        await RunInsightsLanguageStepAsync(web, InsightsRussianScript + InsightsRussianAnalyticsScript, arguments, checks, token);
        await WaitForTrayAsync(host, ("Открыть Maut Vision", "Выйти"), token);
        checks.Add("The sections switch back to Russian with the tray");
    }

    private static async Task RunInsightsLanguageStepAsync(CoreWebView2 web, string body, string arguments, JsonArray checks, CancellationToken token)
    {
        var script = "(async ({projectId}) => {" + BackgroundsUiHelpers + TrainingUiHelpers + ChecksUiHelpers + OverviewUiHelpers + InsightsLanguageHelpers
            + body + "})(" + arguments + ")";
        JsonNode result;
        try
        {
            result = await RunScriptAsync(web, script, token, attempts: 900);
        }
        catch (TimeoutException error)
        {
            var passed = await web.ExecuteScriptAsync("JSON.stringify(window.__trainingUiChecks ?? [])");
            throw new TimeoutException(error.Message + " Passed: " + JsonSerializer.Deserialize<string>(passed), error);
        }
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
    }

    private const string InsightsLanguageHelpers = """
          const cyrillic = /[А-Яа-яЁё]/;
          const englishPlural = (count, one, other) => count + ' ' + (new Intl.PluralRules('en').select(count) === 'one' ? one : other);
          // Names the user typed and files the checks read: projects, datasets, classes of the versions, sources of live checks, job labels
          // stored as text. They are data and stay as typed.
          const userNames = async () => {
            const names = (await request('projects.list', {})).data.items.flatMap(item => [item.name, item.description]);
            names.push(...(await request('datasets.list', {projectId})).data.map(item => item.name));
            names.push(...(await request('training.listModelVersions', {projectId})).data.flatMap(item => (item.classes ?? []).map(entry => entry.name)));
            names.push(...(await request('checks.list', {projectId})).data.flatMap(item => [item.source.name, ...item.summary.classes.map(entry => entry.name)]));
            names.push(...(await request('checks.listEvaluations', {projectId, limit: 200})).data.items
              .flatMap(item => [item.dataset.name, ...item.classMetrics.map(entry => entry.name)]));
            names.push(...(await request('jobs.list', {projectId, limit: 200})).data.map(item => item.label && !item.label.code ? item.label.text : null));
            return names.filter(Boolean).sort((left, right) => right.length - left.length);
          };
          // Text a user reads in the scope (text nodes and the labels of controls) without user names. skip: selectors of elements whose
          // text is not read as such.
          const sectionText = async (scope, skip) => {
            const names = await userNames();
            const outside = node => !skip || !(node.nodeType === Node.TEXT_NODE ? node.parentElement : node).closest(skip);
            const parts = [];
            const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) if (outside(node)) parts.push(node.textContent);
            for (const element of [scope, ...scope.querySelectorAll('*')]) {
              if (!outside(element)) continue;
              for (const name of ['aria-label', 'title', 'placeholder', 'alt']) if (element.getAttribute(name)) parts.push(element.getAttribute(name));
            }
            let all = parts.join(' | ');
            for (const name of names) all = all.split(name).join('');
            return all;
          };
          const sectionEnglish = async (scope, label, skip) => {
            const all = await sectionText(scope, skip);
            const found = all.match(cyrillic);
            assert(!found, label + (found ? ': ' + all.slice(Math.max(0, found.index - 60), found.index + 60) : ''));
          };
          // Hidden native selects mirror the options of the visible triggers.
          const borrowed = 'select';
          const toEnglish = async () => {
            if (document.documentElement.lang === 'en') return;
            const english = await request('settings.setLanguage', {language: 'en'});
            assert(english.ok && english.data.language === 'en', 'settings.setLanguage stores English for the sections');
            await wait(() => document.documentElement.lang === 'en' && labelled('Overview'), 'English interface');
          };

        """;

    private const string InsightsEnglishOverviewScript = """
          const opened = (await request('projects.list', {})).data.lastProjectId;
          if (opened !== projectId || !$('[data-section="overview"]')) await openProject(projectId);
          await toEnglish();
          section('overview').click();
          await wait(() => $('[data-testid="overview-section"]') && $('[data-testid="overview-ready-datasets"]'), 'English overview');
          await wait(() => $('[data-testid="overview-cpu"]')?.dataset.status === 'available', 'English hardware sample');
          assert(text('#overview-data-heading') === 'Project data' && text('#overview-jobs-heading') === 'Running now'
            && text('#overview-models-heading') === 'Latest models' && text('#overview-hardware-heading') === 'Your computer'
            && text('[data-testid="overview-edit"]') === 'Project settings',
            'The overview headings are in English');
          const sample = (await request('hardware.sample', {})).data;
          assert(text('[data-testid="overview-cpu-threads"]').endsWith(englishPlural(sample.cpu.logicalThreads, 'thread', 'threads'))
            && /^\d+(\.\d{1,2})? GB used of \d+(\.\d{1,2})? GB$/.test(text('[data-testid="overview-ram-detail"]'))
            && text('[data-testid="overview-load-chart"] figcaption').startsWith('Load over 2 minutes'),
            'English plural forms and units of the computer: ' + text('[data-testid="overview-cpu-threads"]') + ' · ' + text('[data-testid="overview-ram-detail"]'));
          await sectionEnglish($('[data-testid="overview-section"]'), 'The overview has no Cyrillic outside user names');
          return {checks};
        """;

    private const string InsightsEnglishChecksScript = """
          await toEnglish();
          await openChecks();
          const versions = await modelVersions();
          const working = versions.find(item => item.isWorking) ?? versions[0];
          await wait(() => text('[data-testid="checks-version"]').startsWith('Version ' + working.number + ' · working'), 'English version option',
            () => text('[data-testid="checks-version"]'));
          assert($$('[role="tablist"][aria-label="Check mode"] [role="tab"]').map(item => item.textContent).join() === 'Image,Dataset,Video,Screen'
            && text('[data-testid="checks-caption"]').startsWith('Weights best.pt of version ' + working.number + ' · input '),
            'The checks shell, its modes and the version caption are in English');
          const modes = {image: 'check-image', dataset: 'model-evaluations', video: 'check-video', screen: 'check-screen'};
          for (const [id, testId] of Object.entries(modes)) {
            modeTab(id).click();
            await wait(() => panel(id) && !panel(id).hidden && $('[data-testid="' + testId + '"]', panel(id)), 'English mode ' + id);
            if (id === 'screen') await wait(() => !text('[data-testid="check-screen-monitor"]').startsWith('Looking'), 'Monitors listed');
            if (id === 'dataset') await wait(() => text('[data-testid="evaluation-hint"]'), 'Evaluation hint');
            await sectionEnglish(panel(id), 'The ' + id + ' mode has no Cyrillic outside user names', borrowed);
          }
          modeTab('dataset').click();
          const smoke = (await request('datasets.list', {projectId})).data.find(item => item.name === 'Проверка smoke');
          await pick($('[data-testid="evaluation-dataset"]'), 'Проверка smoke · ');
          const split = smoke.counts.test > 0 ? 'Test' : 'Validation';
          const expected = englishPlural(smoke.counts[split === 'Test' ? 'test' : 'val'], 'frame', 'frames') + ' in the ' + split + ' split. The input size comes from the version.';
          await wait(() => text('[data-testid="evaluation-hint"]') === expected, 'English frames of the split', () => text('[data-testid="evaluation-hint"]'));
          assert(true, 'The evaluation form counts frames of the split with English plural forms: ' + expected);
          modeTab('image').click();
          const confidence = await wait(() => $('[data-testid="check-image-confidence"]'), 'Confidence field');
          setValue(confidence, '2');
          await wait(() => $('[data-testid="check-image"] p[role="alert"]')?.textContent === 'The confidence threshold is a number from 0 to 1.'
            && $('[data-testid="check-image-pick"]').disabled, 'English validation', () => $('[data-testid="check-image"] p[role="alert"]')?.textContent);
          setValue(confidence, '0.25');
          await wait(() => !$('[data-testid="check-image"] p[role="alert"]'), 'Valid confidence');
          assert(true, 'A validation message of the check fields is in English');
          return {checks};
        """;

    private const string InsightsEnglishAnalyticsScript = """
          await toEnglish();
          await openSection('analytics', 'analytics-section');
          const table = await wait(() => $('[data-testid="analytics-table"]'), 'English analytics table');
          assert(text('[data-testid="analytics-copy"]') === 'Copy the table' && text('[data-testid="analytics-table"] tr[data-row="bestEpoch"] td') === 'Best epoch'
            && $('[data-testid="analytics-table"] tr[data-row="mAP50"] td').title.startsWith('mAP50 — mean average precision')
            && $$('[data-testid="analytics-column"]').every(item => /^Version \d+$/.test(item.textContent)),
            'The analytics table, its help and the version columns are in English');
          await wait(() => $('[data-testid="analytics-curves"] figcaption, [data-testid="analytics-curves-empty"]'), 'English curves');
          await sectionEnglish($('[data-testid="analytics-section"]'), 'Analytics has no Cyrillic outside user names', borrowed);
          return {checks};
        """;

    private const string InsightsRussianScript = """
          const russian = await request('settings.setLanguage', {language: 'ru'});
          assert(russian.ok && russian.data.language === 'ru', 'settings.setLanguage stores Russian after the sections');
          await wait(() => document.documentElement.lang === 'ru' && labelled('Аналитика'), 'Russian interface');
        """;

    private const string InsightsRussianAnalyticsScript = """
          await wait(() => text('[data-testid="analytics-copy"]') === 'Копировать таблицу', 'Russian analytics', () => text('[data-testid="analytics-copy"]'));
          assert(true, 'Analytics reads in Russian again');
          return {checks};
        """;
}
