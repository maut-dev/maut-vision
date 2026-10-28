using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Host;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

/// <summary>
/// Host seams of the window for the smoke run: the recording tray, closing the window, whether it is shown, the work count and answer of
/// «Выйти» (count null — the count of Core) with the questions asked, and how many times a second launch activated the window.
/// </summary>
internal sealed record SmokeHostProbe(SmokeTray Tray, Action CloseWindow, Func<bool> WindowShown, Action<int?, bool> SetQuitAnswer,
    Func<IReadOnlyList<string>> QuitQuestions, Func<int> Activations);

internal static partial class SmokeRunner
{
    /// <summary>
    /// Project lifecycle from the real page and the host, after the other checks: the library with the last project, export of the smoke project
    /// through «Сохранить как» (projects.export job with its label and result), import of that archive as a copy (new id, « (импорт)»,
    /// projectImport.changed and project.changed), refusal of an archive of an early format with its text, deletion with a stale
    /// revision and then for real (project.removed), the order of opens, then the host: closing hides the window in the tray with a
    /// one-time notice, the tray shows it again, «Выйти» with background work asks and can be declined, a second launch with the same work
    /// directory activates this window and ends.
    /// </summary>
    private static async Task CheckLifecycleAsync(CoreWebView2 web, string root, string projectId, SmokeHostProbe host, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var day = new DateTime(2026, 10, 23);
        if (ProjectsController.DefaultFileName("Детектор: линия/2*?", day) != "Детектор_ линия_2__ 2026-10-23.zip"
            || ProjectsController.DefaultFileName(new string('я', 150), day).Length != ProjectsController.MaxFileNameLength
            || !ProjectsController.DefaultFileName(new string('я', 150), day).EndsWith(" 2026-10-23.zip", StringComparison.Ordinal))
            throw new InvalidOperationException("The default archive name does not follow «<имя> <YYYY-MM-DD>.zip» with «_» and 100 characters.");
        checks.Add("The default export name is «<имя> <YYYY-MM-DD>.zip» with invalid characters replaced by «_», at most 100 characters");

        var folder = Path.Combine(root, "smoke-transfer");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "Проект smoke.zip");
        var legacy = Path.Combine(folder, "legacy.zip");
        using (var zip = ZipFile.Open(legacy, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                await writer.WriteAsync("""{"formatVersion":3,"projectId":"old","tables":[{"name":"images","rows":0}],"blobs":[]}""");
            zip.CreateEntry("tables/images.jsonl");
        }
        MainWindow.SmokeSaveAnswer = archive;
        var exported = await RunLifecycleScriptAsync(web, LifecycleExportScript, new { projectId }, checks, token);
        MainWindow.SmokeArchiveAnswer = archive;
        var imported = await RunLifecycleScriptAsync(web, LifecycleImportScript, new { projectId, archiveBytes = exported["archiveBytes"]!.GetValue<long>(),
            projectName = exported["projectName"]!.GetValue<string>() }, checks, token);
        MainWindow.SmokeArchiveAnswer = legacy;
        await RunLifecycleScriptAsync(web, LifecycleLegacyScript, new { }, checks, token);
        await RunLifecycleScriptAsync(web, LifecycleDeletionScript, new { projectId, importedId = imported["projectId"]!.GetValue<string>(),
            importedName = imported["projectName"]!.GetValue<string>() }, checks, token);
        if (!File.Exists(archive) || Directory.GetFiles(folder, ".*.tmp").Length != 0)
            throw new InvalidOperationException("The export did not leave exactly the archive in the chosen folder.");
        checks.Add("The export left the archive and no temporary file next to it");
        await CheckLifecycleUiAsync(web, root, projectId, checks, token);

        // Host: closing hides the window in the tray, the notice comes once and is remembered in the work directory.
        host.CloseWindow();
        await WaitAsync(() => !host.WindowShown(), "the window hides on close", token);
        await WaitVisibilityAsync(web, "hidden", token);
        if (host.Tray.Notices is not ["Maut Vision продолжает работать в области уведомлений"]) throw new InvalidOperationException("The tray notice was not shown once: " + string.Join("|", host.Tray.Notices));
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, HostSettings.FileName), token)))
        {
            if (!settings.RootElement.GetProperty("trayNoticeShown").GetBoolean()) throw new InvalidOperationException("The tray notice is not remembered.");
        }
        checks.Add("Closing the window hides it in the tray with the page hidden; the notice «Maut Vision продолжает работать в области уведомлений» is shown and remembered");
        host.Tray.ClickOpen();
        await WaitAsync(host.WindowShown, "the tray shows the window", token);
        await WaitVisibilityAsync(web, "visible", token);
        host.CloseWindow();
        await WaitAsync(() => !host.WindowShown(), "the window hides again", token);
        if (host.Tray.Notices.Count != 1) throw new InvalidOperationException("The tray notice was shown again.");
        host.Tray.ClickOpen();
        await WaitAsync(host.WindowShown, "the tray shows the window again", token);
        await WaitVisibilityAsync(web, "visible", token);
        checks.Add("«Открыть Maut Vision» shows the window and its page; the notice is not repeated");

        host.SetQuitAnswer(2, false);
        host.Tray.ClickQuit();
        await Task.Delay(300, token);
        if (host.QuitQuestions() is not [var question] || question != MainWindow.QuitQuestion(2) || !host.WindowShown())
            throw new InvalidOperationException("«Выйти» with background work did not ask or did not stay: " + string.Join("|", host.QuitQuestions()));
        host.SetQuitAnswer(null, false);
        checks.Add("«Выйти» with background work asks «Идут фоновые задачи: N. Выйти? …»; «Нет» keeps the application running");

        // A second launch with the same work directory shows this window and ends at once; smoke runs use their own work directories.
        var before = host.Activations();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--work-directory");
        start.ArgumentList.Add(root);
        using (var second = Process.Start(start)!)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            await second.WaitForExitAsync(limit.Token);
            if (second.ExitCode != 0) throw new InvalidOperationException($"The second launch exited with {second.ExitCode}.");
        }
        await WaitAsync(() => host.Activations() > before, "the second launch activates the first", token);
        if (SingleInstance.Name(root) == SingleInstance.Name(root + "-other") || SingleInstance.Name(root) != SingleInstance.Name(root.ToUpperInvariant() + "\\"))
            throw new InvalidOperationException("The instance name does not follow the work directory.");
        checks.Add("A second launch with the same work directory activates the running window and exits; the mutex name follows the work directory");
    }

    private static async Task WaitAsync(Func<bool> condition, string label, CancellationToken token)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(50, token);
        }
        throw new TimeoutException("Timed out: " + label);
    }

    /// <summary>Runs one step of the lifecycle page script with the shared helpers; its checks join the report, the rest is returned.</summary>
    private static async Task<JsonNode> RunLifecycleScriptAsync(CoreWebView2 web, string body, object arguments, JsonArray checks, CancellationToken token)
    {
        var result = await RunScriptAsync(web, "(async (input) => {" + LifecyclePrelude + body + "})(" + JsonSerializer.Serialize(arguments) + ")", token,
            attempts: 600);
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        return result;
    }

    private const string LifecyclePrelude = """
        const checks = [];
        const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
        let sequence = 0;
        const request = (method, payload) => new Promise((resolve, reject) => {
          const requestId = 'lifecycle-smoke-' + Date.now() + '-' + (++sequence);
          const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 30000);
          const reply = event => {
            const data = event.data;
            if (data?.kind !== 'response' || data.requestId !== requestId) return;
            clearTimeout(timeout);
            window.chrome.webview.removeEventListener('message', reply);
            resolve(data);
          };
          window.chrome.webview.addEventListener('message', reply);
          window.chrome.webview.postMessage({requestId, method, payload});
        });
        const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
        const until = async (query, label) => {
          for (let attempt = 0; attempt < 550; attempt++) {
            const value = await query();
            if (value) return value;
            await sleep(100);
          }
          throw new Error('Timed out: ' + label);
        };
        const events = [];
        const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
        window.chrome.webview.addEventListener('message', listen);
        """;

    private const string LifecycleExportScript = """
        try {
          const {projectId} = input;
          const reopened = await request('projects.open', {projectId});
          assert(reopened.ok, 'The smoke project is opened again before the library is read');
          const library = await request('projects.list', {});
          assert(library.ok && Array.isArray(library.data.items) && library.data.lastProjectId === projectId
            && library.data.items.some(item => item.id === projectId), 'projects.list returns the items and the last opened project');
          const project = library.data.items.find(item => item.id === projectId);
          const destination = await request('projects.selectExportDestination', {projectId});
          assert(destination.ok && typeof destination.data.selectionToken === 'string' && destination.data.fileName === 'Проект smoke.zip',
            'projects.selectExportDestination returns a token and the file name, never the path');
          const operationId = 'project-export-smoke';
          const started = await request('projects.startExport', {projectId, operationId, selectionToken: destination.data.selectionToken});
          assert(started.ok && started.data.kind === 'projects.export' && started.data.label === project.name,
            'projects.startExport queues projects.export labelled with the project name');
          const repeated = await request('projects.startExport', {projectId, operationId, selectionToken: destination.data.selectionToken});
          assert(repeated.ok && repeated.data.id === started.data.id, 'A repeated start of the same operation returns the same export');
          const exported = await until(async () => {
            const job = await request('jobs.get', {projectId, jobId: started.data.id});
            return job.ok && ['succeeded', 'failed', 'cancelled'].includes(job.data.status) ? job.data : null;
          }, 'export finished');
          assert(exported.status === 'succeeded' && exported.result.files === exported.result.totalFiles && exported.result.files > 2
            && exported.result.archiveBytes > 0 && exported.processed === exported.total,
            'The export succeeds with files and bytes counted against totals known upfront');
          assert(events.some(event => event.event === 'job.changed' && event.data.id === started.data.id && event.data.status === 'running'),
            'The export reports its state through job.changed');
          const expired = await request('projects.startExport', {projectId, operationId: 'project-export-other', selectionToken: destination.data.selectionToken});
          assert(!expired.ok && expired.error.code === 'projects.export_selection_expired', 'A destination of another operation is refused');
          return {checks, archiveBytes: exported.result.archiveBytes, projectName: project.name};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;

    private const string LifecycleImportScript = """
        try {
          const {projectId, archiveBytes, projectName} = input;
          const chosen = await request('projects.selectImportArchive', {});
          assert(chosen.ok && chosen.data.fileName === 'Проект smoke.zip' && chosen.data.byteSize === archiveBytes,
            'projects.selectImportArchive returns a token, the name and the size of the archive');
          const importStart = await request('projects.startImport', {operationId: 'project-import-smoke', selectionToken: chosen.data.selectionToken});
          assert(importStart.ok && importStart.data.status === 'running' && importStart.data.fileName === 'Проект smoke.zip',
            'projects.startImport answers at once with a running snapshot');
          const imported = await until(() => events.find(event => event.event === 'projectImport.changed' && event.data.id === importStart.data.id
            && event.data.status !== 'running')?.data, 'import finished');
          assert(imported.status === 'succeeded' && imported.projectId !== projectId && imported.projectName === projectName + ' (импорт)'
            && imported.processedFiles === imported.totalFiles && imported.error === null,
            'The archive of an existing project is imported as a copy with a new id and « (импорт)»');
          const ordered = events.filter(event => event.event === 'projectImport.changed' && event.data.id === importStart.data.id).map(event => event.data.sequence);
          assert(ordered.every((value, index) => index === 0 || value > ordered[index - 1]), 'Import snapshots come in increasing sequence');
          await until(() => events.some(event => event.event === 'project.changed' && event.data.id === imported.projectId), 'project.changed of the copy');
          checks.push('The imported project arrives as project.changed');
          const got = await request('projects.getImport', {importId: importStart.data.id});
          const all = await request('projects.listImports', {});
          assert(got.ok && got.data.status === 'succeeded' && all.ok && all.data[0].id === importStart.data.id, 'projects.getImport and projects.listImports return the import');
          const withCopy = await request('projects.list', {});
          assert(withCopy.data.items.some(item => item.id === imported.projectId) && withCopy.data.lastProjectId === projectId,
            'The copy is in the library; the last project stays');
          return {checks, projectId: imported.projectId, projectName: imported.projectName};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;

    private const string LifecycleLegacyScript = """
        try {
          const old = await request('projects.selectImportArchive', {});
          const refused = await request('projects.startImport', {operationId: 'project-import-legacy', selectionToken: old.data.selectionToken});
          const legacy = await until(async () => {
            const state = await request('projects.getImport', {importId: refused.data.id});
            return state.data.status !== 'running' ? state.data : null;
          }, 'legacy import finished');
          assert(legacy.status === 'failed' && legacy.error.code === 'projects.archive_legacy'
            && mautLocale.errorText(legacy.error.code, legacy.error.details) === 'Архив создан прежней версией Maut Vision и не поддерживается. Экспортируйте проект заново в текущей версии.',
            'An archive of an early format is refused with its text');
          return {checks};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;

    private const string LifecycleDeletionScript = """
        try {
          const {projectId, importedId, importedName} = input;
          const review = await request('projects.reviewDeletion', {projectId: importedId});
          assert(review.ok && review.data.activities.length === 0 && review.data.transferActive === false && review.data.name === importedName,
            'projects.reviewDeletion lists no work of the copy');
          const stale = await request('projects.delete', {projectId: importedId, revision: review.data.revision + 1, cancelJobs: false});
          assert(!stale.ok && stale.error.code === 'projects.deletion_conflict'
            && mautLocale.errorText(stale.error.code, stale.error.details) === 'Проект изменился после открытия подтверждения. Обновите список проектов и повторите удаление.',
            'A stale revision refuses the deletion with its text');
          const deleted = await request('projects.delete', {projectId: importedId, revision: review.data.revision, cancelJobs: false});
          assert(deleted.ok && deleted.data.projectId === importedId && typeof deleted.data.cleanupPending === 'boolean', 'projects.delete deletes the copy');
          await until(() => events.some(event => event.event === 'project.removed' && event.data.projectId === importedId), 'project.removed');
          const after = await request('projects.list', {});
          const missing = await request('projects.open', {projectId: importedId});
          assert(!after.data.items.some(item => item.id === importedId) && !missing.ok && missing.error.code === 'projects.not_found',
            'project.removed arrives and the deleted project is gone');
          // The page numbers its opens from the clock (they keep growing across reloads), so the probe starts there too.
          const base = Date.now();
          const newer = await request('projects.open', {projectId, sequence: base + 1});
          const older = await request('projects.open', {projectId, sequence: base});
          const latest = await request('projects.open', {projectId, sequence: base + 2});
          assert(newer.ok && !older.ok && older.error.code === 'projects.open_superseded' && latest.ok,
            'An open older than the latest one is answered as superseded');
          return {checks};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;
}
