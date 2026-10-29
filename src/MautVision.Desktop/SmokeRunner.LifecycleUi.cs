using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Projects;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>Files of the project whose import is cancelled after a reload: enough for several seconds of extraction.</summary>
    private const int BulkTransferFiles = 15_000;

    /// <summary>
    /// Project lifecycle from the library of the real page, after the bridge checks: the last opened project first with «Последний открытый»;
    /// «Экспортировать…» with a closed «Сохранить как» (nothing starts) and for real (banner with progress and result, «Скрыть»);
    /// «Импортировать проект…» of that archive as a copy « (импорт)» that opens by itself from the library; refusals of an archive of the
    /// early format, a damaged and an unknown archive with their texts; «Удалить…» of the copy: busy and stale revision in the
    /// dialog, the running training listed with «Отменить задачи и удалить»; an import followed again after a reload of the page and
    /// cancelled; two opens in a row (the later wins); the open project deleted elsewhere returns to the library; a deletion with a busy
    /// file shows the cleanup notice.
    /// </summary>
    private static async Task CheckLifecycleUiAsync(CoreWebView2 web, string root, string projectId, JsonArray checks, CancellationToken token)
    {
        var folder = Path.Combine(root, "smoke-transfer-ui");
        Directory.CreateDirectory(folder);
        var mainArchive = Path.Combine(folder, "ui-main.zip");
        var bulkArchive = Path.Combine(folder, "ui-bulk.zip");
        var legacy = Path.Combine(root, "smoke-transfer", "legacy.zip");

        await RunLifecycleUiStepAsync(web, LifecycleUiLibraryScript, new { projectId }, checks, token);
        MainWindow.SmokeSaveAnswer = mainArchive;
        await RunLifecycleUiStepAsync(web, LifecycleUiExportScript, new { projectId, fileName = "ui-main.zip" }, checks, token);
        MainWindow.SmokeArchiveAnswer = mainArchive;
        var imported = await RunLifecycleUiStepAsync(web, LifecycleUiImportScript, new { projectId }, checks, token);
        var importedId = imported["importedId"]!.GetValue<string>();

        // Refused archives: of an early format, a cut one (no end record) and a file that is not ZIP at all.
        var damaged = Path.Combine(folder, "damaged.zip");
        var bytes = await File.ReadAllBytesAsync(mainArchive, token);
        await File.WriteAllBytesAsync(damaged, bytes[..(bytes.Length / 2)], token);
        var unknown = Path.Combine(folder, "unknown.zip");
        await File.WriteAllTextAsync(unknown, "Это не архив проекта.", token);
        foreach (var (archive, message, label) in new[]
        {
            (legacy, "Архив создан прежней версией Maut Vision и не поддерживается. Экспортируйте проект заново в текущей версии.", "of an early format"),
            (damaged, "Архив повреждён. Экспортируйте проект заново и повторите импорт.", "cut in the middle"),
            (unknown, "Архив неизвестного формата: это не архив проекта Maut Vision.", "that is not ZIP"),
        })
        {
            MainWindow.SmokeArchiveAnswer = archive;
            await RunLifecycleUiStepAsync(web, LifecycleUiRefusedScript, new { message, label }, checks, token);
        }

        await RunLifecycleUiStepAsync(web, LifecycleUiDeletionScript, new { importedId, importedName = imported["importedName"]!.GetValue<string>() }, checks, token);

        // A project with many small files: its import runs long enough to survive a reload of the page and be cancelled.
        var bulk = await RunLifecycleUiStepAsync(web, LifecycleUiCreateScript, new { name = "Перенос UI" }, checks, token);
        var bulkId = bulk["id"]!.GetValue<string>();
        var bulkFiles = Path.Combine(root, "projects", bulkId, "files", "bulk");
        Directory.CreateDirectory(bulkFiles);
        var payload = new byte[512];
        for (var index = 0; index < BulkTransferFiles; index++)
        {
            payload[0] = (byte)index;
            await File.WriteAllBytesAsync(Path.Combine(bulkFiles, index.ToString("D5") + ".bin"), payload, token);
        }
        MainWindow.SmokeSaveAnswer = bulkArchive;
        await RunLifecycleUiStepAsync(web, LifecycleUiBulkExportScript, new { bulkId, files = BulkTransferFiles }, checks, token);
        MainWindow.SmokeArchiveAnswer = bulkArchive;
        await RunLifecycleUiStepAsync(web, LifecycleUiBulkImportScript, new { }, checks, token);
        await web.ExecuteScriptAsync("window.__beforeReload = true; location.reload();");
        for (var attempt = 0; ; attempt++)
        {
            if (await web.ExecuteScriptAsync("window.__beforeReload === undefined && document.readyState === 'complete'") == "true") break;
            if (attempt == 100) throw new TimeoutException("UI did not reload.");
            await Task.Delay(100, token);
        }
        await RunLifecycleUiStepAsync(web, LifecycleUiReconnectScript, new { bulkName = "Перенос UI" }, checks, token);
        await RunLifecycleUiStepAsync(web, LifecycleUiOpenScript, new { projectId, bulkId }, checks, token);

        // A file of the project held open: the folder cannot be renamed, the deletion stands and the rest waits for the next start.
        var held = await RunLifecycleUiStepAsync(web, LifecycleUiCreateScript, new { name = "Удаление UI" }, checks, token);
        var heldId = held["id"]!.GetValue<string>();
        var heldFolder = Path.Combine(root, "projects", heldId, "files");
        Directory.CreateDirectory(heldFolder);
        await using (var handle = new FileStream(Path.Combine(heldFolder, "held.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            await handle.WriteAsync(payload, token);
            await handle.FlushAsync(token);
            await RunLifecycleUiStepAsync(web, LifecycleUiCleanupScript, new { heldId, heldName = "Удаление UI" }, checks, token);
        }
    }

    private static async Task<JsonNode> RunLifecycleUiStepAsync(CoreWebView2 web, string body, object arguments, JsonArray checks, CancellationToken token)
    {
        var script = "(async (input) => {" + BackgroundsUiHelpers + LifecycleUiHelpers + body + "})(" + JsonSerializer.Serialize(arguments) + ")";
        JsonNode result;
        try
        {
            result = await RunScriptAsync(web, script, token, attempts: 900);
        }
        catch (TimeoutException error)
        {
            var passed = await web.ExecuteScriptAsync("JSON.stringify(window.__lifecycleUiChecks ?? [])");
            throw new TimeoutException(error.Message + " Passed: " + JsonSerializer.Deserialize<string>(passed), error);
        }
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        return result;
    }

    private const string LifecycleUiHelpers = """
          window.__lifecycleUiChecks = checks;
          const headerButton = label => $$('header button').find(item => item.textContent.includes(label));
          const row = id => $('[data-project-id="' + id + '"]');
          const rows = () => $$('[data-testid="project-list"] [data-project-id]');
          const toLibrary = async () => {
            if (!$('[data-testid="project-list"]')) headerButton('Все проекты').click();
            return wait(() => $('[data-testid="project-list"]') && rows().length > 0, 'Library');
          };
          const banners = kind => $$('[data-testid="transfer-banner"][data-kind="' + kind + '"]');
          const banner = kind => banners(kind)[0];
          const part = (element, id) => $('[data-testid="' + id + '"]', element)?.textContent ?? '';
          const finishedBanner = kind => banners(kind).find(item => item.dataset.status !== 'running');
          const projectByName = async name => (await request('projects.list', {})).data.items.find(item => item.name === name);
          const hint = 'Большой проект может переноситься несколько минут; работать с другими проектами можно.';
          const filesText = /^Файлов: [\d\s ]+ из [\d\s ]+\.$/;
          // Тексты баннеров между опросами: ход может пройти быстрее шага ожидания.
          const recordTexts = () => {
            const seen = new Set();
            const observer = new MutationObserver(() => $$('[data-testid="transfer-banner"]').forEach(item =>
              seen.add(item.dataset.kind + '|' + item.dataset.status + '|' + part(item, 'transfer-detail') + '|' + part(item, 'transfer-hint'))));
            observer.observe(document.body, {subtree: true, childList: true, characterData: true, attributes: true});
            return {seen, stop: () => observer.disconnect()};
          };

        """;

    private const string LifecycleUiLibraryScript = """
          const {projectId} = input;
          await toLibrary();
          // The bridge checks before ran an export and imports; the page followed them by events like any other.
          const earlier = $$('[data-testid="transfer-banner"]');
          assert(earlier.every(item => item.dataset.status !== 'running'), 'Transfers started through the bridge are followed by the library: ' + earlier.map(item => item.dataset.kind + ' ' + item.dataset.status).join(', '));
          earlier.forEach(item => $('[data-testid="transfer-dismiss"]', item).click());
          await wait(() => !$('[data-testid="transfer-banner"]'), 'Earlier banners hidden');
          const library = (await request('projects.list', {})).data;
          assert(library.lastProjectId === projectId && rows()[0].dataset.projectId === projectId
            && part(rows()[0], 'project-last') === 'Последний открытый' && $$('[data-testid="project-last"]').length === 1,
            'The last opened project is listed first with «Последний открытый»');
          const exportButton = $('[data-testid="export-project"]', row(projectId));
          const importButton = $('[data-testid="import-project"]');
          assert(exportButton.textContent === 'Экспортировать…' && exportButton.title === 'Сохранить проект со всеми материалами, датасетами и весами моделей в один файл'
            && importButton.textContent === 'Импортировать проект…' && importButton.title === 'Добавить проект из архива, сохранённого на другом компьютере'
            && labelled('Удалить проект ' + library.items.find(item => item.id === projectId).name)?.textContent === 'Удалить…',
            '«Экспортировать…», «Импортировать проект…» and «Удалить…» are shown with their tooltips and the accessible name of deletion');
          exportButton.click();
          await sleep(600);
          assert(!banner('export') && !$('[data-testid="transfer-error"]'), 'A closed «Сохранить как» starts nothing');
          return {checks};
        """;

    private const string LifecycleUiExportScript = """
          const {projectId, fileName} = input;
          const name = (await request('projects.list', {})).data.items.find(item => item.id === projectId).name;
          $('[data-testid="export-project"]', row(projectId)).click();
          const done = await wait(() => finishedBanner('export'), 'Export banner finished');
          assert(done.dataset.status === 'succeeded' && part(done, 'transfer-title') === 'Проект «' + name + '» экспортирован'
            && part(done, 'transfer-detail') === 'Архив сохранён. Перенесите файл на другой компьютер и выберите «Импортировать проект…».'
            && part(done, 'transfer-bytes').startsWith(fileName + ' · ') && !$('[data-testid="export-project"]', row(projectId)).disabled,
            'The export banner ends with «Проект «X» экспортирован», the file and its size');
          $('[data-testid="transfer-dismiss"]', done).click();
          await wait(() => !banner('export'), 'Export banner hidden');
          assert(true, '«Скрыть» removes the export banner');
          return {checks};
        """;

    private const string LifecycleUiImportScript = """
          const {projectId} = input;
          const source = (await request('projects.list', {})).data.items.find(item => item.id === projectId);
          const copyName = source.name + ' (импорт)';
          $('[data-testid="import-project"]').click();
          await wait(() => $('[data-testid="active-project-name"]')?.textContent === copyName
            && $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Imported project opened', () => text('[data-testid="active-project-name"]'));
          const copy = await projectByName(copyName);
          assert(copy && copy.id !== projectId, 'The imported copy « (импорт)» opens by itself when the library is on screen');
          await toLibrary();
          const done = await wait(() => finishedBanner('import'), 'Import banner');
          assert(done.dataset.status === 'succeeded' && part(done, 'transfer-title') === 'Проект «' + copyName + '» импортирован'
            && part(done, 'transfer-detail') === 'Проект добавлен в библиотеку.', 'The import banner says «Проект «X» импортирован» and «Проект добавлен в библиотеку.»');
          assert(rows()[0].dataset.projectId === copy.id && $('[data-testid="project-last"]', rows()[0]) && $$('[data-testid="project-last"]').length === 1,
            'The opened copy becomes the last project and moves first');
          $('[data-testid="transfer-dismiss"]', done).click();
          await wait(() => !banner('import'), 'Import banner hidden');
          return {checks, importedId: copy.id, importedName: copyName};
        """;

    private const string LifecycleUiRefusedScript = """
          const {message, label} = input;
          await toLibrary();
          const before = (await request('projects.list', {})).data.items.length;
          $('[data-testid="import-project"]').click();
          const done = await wait(() => finishedBanner('import'), 'Refused import banner');
          assert(done.dataset.status === 'failed' && part(done, 'transfer-title') === 'Импорт не выполнен' && part(done, 'transfer-detail') === message
            && (await request('projects.list', {})).data.items.length === before,
            'An archive ' + label + ' is refused with «Импорт не выполнен» and «' + message + '»');
          $('[data-testid="transfer-dismiss"]', done).click();
          await wait(() => !banner('import'), 'Refused banner hidden');
          return {checks};
        """;

    private const string LifecycleUiDeletionScript = """
          const {importedId, importedName} = input;
          await toLibrary();
          const dialog = () => $('[data-testid="project-deletion-dialog"]');
          const confirm = () => $('[data-testid="project-deletion-confirm"]');
          const reviewed = () => dialog() && !$('[role="status"]', dialog()) && enabled(confirm());
          labelled('Удалить проект ' + importedName).click();
          await wait(reviewed, 'Deletion reviewed');
          assert($('h2', dialog()).textContent === 'Удалить проект с компьютера?'
            && part(dialog(), 'project-deletion-text') === '“' + importedName + '” и все его материалы, датасеты, история обучения и обученные модели будут удалены без возможности восстановления. Общие базовые YOLO и файлы других проектов сохранятся. Для сохранения копии сначала экспортируйте проект.'
            && confirm().textContent === 'Удалить проект' && !$('[data-testid="project-deletion-activities"]', dialog()),
            'The deletion dialog of an idle project has its title, text and «Удалить проект»');

          // Work starts after the review: the deletion without cancelling is refused in the dialog.
          const datasets = (await request('datasets.list', {projectId: importedId})).data;
          const dataset = datasets.find(item => item.name === 'fake:hang') ?? datasets.find(item => item.name === 'Проверка smoke') ?? datasets[0];
          if (dataset.name !== 'fake:hang') {
            const current = await request('datasets.get', {projectId: importedId, versionId: dataset.id});
            const renamed = await request('datasets.update', {projectId: importedId, versionId: dataset.id, name: 'fake:hang', description: '', revision: current.data.revision});
            if (!renamed.ok) throw new Error('Rename failed: ' + JSON.stringify(renamed.error));
          }
          const run = await request('training.startRun', {projectId: importedId, datasetVersionId: dataset.id, parameters: {epochs: 2, batch: 4, device: 'cpu'},
            presetName: null, operationId: 'lifecycle-ui-run'});
          assert(run.ok, 'A hanging training run starts in the copy');
          confirm().click();
          await wait(() => part(dialog(), 'project-deletion-error'), 'Busy refusal');
          assert(part(dialog(), 'project-deletion-error') === 'Идут фоновые задачи. Отмените их и повторите удаление проекта.' && confirm().disabled,
            'Work started after the review refuses «Удалить проект» in the dialog');
          $('[data-testid="project-deletion-retry"]', dialog()).click();
          await wait(() => reviewed() && confirm().textContent === 'Отменить задачи и удалить', 'Review with jobs');
          assert($$('[data-testid="project-deletion-activity"]', dialog()).map(item => item.textContent).includes('Обучение · fake:hang')
            && !part(dialog(), 'project-deletion-error'), '«Проверить снова» lists the running training and offers «Отменить задачи и удалить»');

          // The project changes after the review: the stale revision is refused with its text.
          const project = (await request('projects.list', {})).data.items.find(item => item.id === importedId);
          const updated = await request('projects.update', {projectId: importedId, revision: project.revision, name: project.name, description: 'Изменён после подтверждения',
            baseModelId: project.baseModelId, inputSize: project.inputSize});
          assert(updated.ok, 'The copy changes after the review');
          confirm().click();
          await wait(() => part(dialog(), 'project-deletion-error'), 'Conflict refusal');
          assert(part(dialog(), 'project-deletion-error') === 'Проект изменился после открытия подтверждения. Обновите список проектов и повторите удаление.',
            'A stale revision is refused in the dialog with its text');
          $('[data-testid="project-deletion-retry"]', dialog()).click();
          await wait(() => reviewed() && confirm().textContent === 'Отменить задачи и удалить', 'Review again');
          const runStates = [];
          const listen = event => { if (event.data?.kind === 'event' && event.data.event === 'job.changed' && event.data.data.id === run.data.id) runStates.push(event.data.data.status); };
          window.chrome.webview.addEventListener('message', listen);
          confirm().click();
          await wait(() => !dialog() && !row(importedId), 'Deleted with jobs');
          window.chrome.webview.removeEventListener('message', listen);
          const library = (await request('projects.list', {})).data;
          assert(!library.items.some(item => item.id === importedId) && library.lastProjectId === null && !$('[data-testid="project-last"]')
            && runStates.includes('cancelled') && !$('[data-testid="project-deletion-notice"]'),
            '«Отменить задачи и удалить» cancels the training and removes the project; the last project mark goes with it');
          return {checks};
        """;

    private const string LifecycleUiCreateScript = """
          const created = await request('projects.create', {name: input.name, description: '', baseModelId: 'yolo26n', inputSize: {width: 320, height: 256}});
          if (!created.ok) throw new Error('Create failed: ' + JSON.stringify(created.error));
          await toLibrary();
          await wait(() => row(created.data.id), 'Created project listed');
          return {checks, id: created.data.id};
        """;

    private const string LifecycleUiBulkExportScript = """
          const {bulkId, files} = input;
          await toLibrary();
          const record = recordTexts();
          $('[data-testid="export-project"]', row(bulkId)).click();
          const done = await wait(() => finishedBanner('export'), 'Bulk export finished');
          record.stop();
          const running = [...record.seen].filter(item => item.startsWith('export|running|'));
          assert(done.dataset.status === 'succeeded' && running.length > 0
            && running.every(item => { const [, , detail, shown] = item.split('|'); return (filesText.test(detail) || detail === 'Подготовка данных.') && shown === hint; }),
            'The running export shows «Файлов: N из M.» (or «Подготовка данных.») with the hint: ' + running.join(' / '));
          assert(part(done, 'transfer-title') === 'Проект «Перенос UI» экспортирован', 'The export of ' + files + ' files succeeds');
          $('[data-testid="transfer-dismiss"]', done).click();
          await wait(() => !banner('export'), 'Bulk export banner hidden');
          return {checks};
        """;

    private const string LifecycleUiBulkImportScript = """
          $('[data-testid="import-project"]').click();
          const running = await wait(() => banner('import')?.dataset.status === 'running' && filesText.test(part(banner('import'), 'transfer-detail')) && banner('import'),
            'Import running with files', () => part(banner('import'), 'transfer-detail'));
          assert(part(running, 'transfer-title') === 'Импорт проекта…' && part(running, 'transfer-hint') === hint
            && /из/.test(part(running, 'transfer-bytes')) && $('[data-testid="import-project"]').disabled,
            'The running import shows «Импорт проекта…», «Файлов: N из M.», the bytes and the hint; a second import cannot start');
          return {checks};
        """;

    private const string LifecycleUiReconnectScript = """
          const {bulkName} = input;
          await toLibrary();
          const running = await wait(() => banner('import')?.dataset.status === 'running' && banner('import'), 'Import banner after the reload');
          assert(filesText.test(part(running, 'transfer-detail')) || part(running, 'transfer-detail') === 'Проверка архива.',
            'After a reload of the page the running import is shown again from projects.listImports');
          $('[data-testid="transfer-cancel"]', running).click();
          const done = await wait(() => finishedBanner('import'), 'Import cancelled');
          assert(done.dataset.status === 'cancelled' && part(done, 'transfer-title') === 'Импорт отменён'
            && part(done, 'transfer-detail') === 'Операция отменена. Изменений нет.' && !(await projectByName(bulkName + ' (импорт)'))
            && !$('[data-testid="active-project-name"]'),
            '«Отменить» stops the import: «Импорт отменён», «Операция отменена. Изменений нет.» and no project added');
          $('[data-testid="transfer-dismiss"]', done).click();
          await wait(() => !banner('import'), 'Cancelled banner hidden');
          return {checks};
        """;

    private const string LifecycleUiOpenScript = """
          const {projectId, bulkId} = input;
          await toLibrary();
          const name = (await request('projects.list', {})).data.items.find(item => item.id === projectId).name;
          $('[data-testid="open-project"]', row(bulkId)).click();
          $('[data-testid="open-project"]', row(projectId)).click();
          await wait(() => $('[data-testid="active-project-name"]')?.textContent === name, 'The later open wins');
          await sleep(800);
          assert($('[data-testid="active-project-name"]')?.textContent === name && !$('.fixed [role="alert"]'),
            'Of two opens in a row the later one wins; the earlier answer is dropped without an error');
          const library = (await request('projects.list', {})).data;
          assert(library.lastProjectId === projectId, 'Core remembers the later open as the last project');

          await toLibrary();
          $('[data-testid="open-project"]', row(bulkId)).click();
          await wait(() => $('[data-testid="active-project-name"]')?.textContent === 'Перенос UI', 'Bulk project opened');
          const review = await request('projects.reviewDeletion', {projectId: bulkId});
          const deleted = await request('projects.delete', {projectId: bulkId, revision: review.data.revision, cancelJobs: false});
          assert(deleted.ok, 'The open project is deleted elsewhere');
          await wait(() => $('[data-testid="project-list"]') && !row(bulkId) && !$('[data-testid="active-project-name"]'), 'Back in the library');
          assert(true, 'Deleting the open project returns the page to the library');
          return {checks};
        """;

    private const string LifecycleUiCleanupScript = """
          const {heldId, heldName} = input;
          await toLibrary();
          labelled('Удалить проект ' + heldName).click();
          const dialog = () => $('[data-testid="project-deletion-dialog"]');
          const confirm = await wait(() => dialog() && enabled($('[data-testid="project-deletion-confirm"]')), 'Deletion reviewed');
          assert(confirm.textContent === 'Удалить проект', 'An idle project is deleted with «Удалить проект»');
          confirm.click();
          const notice = await wait(() => !dialog() && $('[data-testid="project-deletion-notice"]'), 'Cleanup notice');
          assert($('p', notice).textContent === 'Проект удалён. Часть файлов пока занята; приложение повторит очистку при следующем запуске.' && !row(heldId)
            && !(await request('projects.list', {})).data.items.some(item => item.id === heldId),
            'A busy file leaves the deletion done with «Проект удалён. Часть файлов пока занята; …»');
          $('button', notice).click();
          await wait(() => !$('[data-testid="project-deletion-notice"]'), 'Notice hidden');
          return {checks};
        """;
}
