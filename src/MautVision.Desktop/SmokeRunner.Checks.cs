using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Checks;
using MautVision.Desktop.Transport;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Model checks API from the real page with the fake worker, on the versions the training check made: deletion review and the refusal
    /// for the working version with its Russian text; the ZIP export through «Сохранить как» and job.changed (the archive is opened in
    /// C#); an evaluation with the compatibility text formatted from details, its metrics through checks.changed; an image check whose
    /// preview loads from the resource channel; immediate refusal while the GPU is busy and stop; monitors and the screenshot; a video
    /// check with its sample frame; a screen check stopped by the reload of the page.
    /// </summary>
    private static async Task CheckChecksAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        // The training checks end their runs; a check needs the GPU slot free.
        for (var attempt = 0; (await core.Jobs.ListAsync(projectId, 200, cancellationToken: token)).Any(job => job.Status is JobStatus.Queued or JobStatus.Running); attempt++)
        {
            if (attempt > 600) throw new TimeoutException("Jobs of the training checks did not end.");
            await Task.Delay(100, token);
        }
        var directory = Path.Combine(root, "smoke-checks");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "проверка.jpg");
        await File.WriteAllBytesAsync(image, Photo(64, 48, 90), token);
        var hang = Path.Combine(directory, "fake-hang.png");
        await File.WriteAllBytesAsync(hang, Photo(32, 32, 91), token);
        var zip = Path.Combine(directory, "model-v1.zip");
        MainWindow.SmokeSaveAnswer = zip;
        MainWindow.SmokeFilesAnswer = [image];
        var arguments = JsonSerializer.Serialize(new { projectId });
        foreach (var check in (await RunScriptAsync(web, "(async ({projectId}) => {" + ChecksHelpers + ChecksModelsScript + "})(" + arguments + ")", token, attempts: 900))
            ["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        using (var archive = ZipFile.OpenRead(zip))
        {
            if (!archive.Entries.Select(entry => entry.FullName).SequenceEqual(["weights/best.pt", "weights/best.onnx", "manifest.json", "classes.json", "README.md"]))
                throw new InvalidOperationException("The exported archive has other entries: " + string.Join(", ", archive.Entries.Select(entry => entry.FullName)));
        }
        checks.Add("The archive saved through «Сохранить как» has weights, manifest, classes and README");

        MainWindow.SmokeFilesAnswer = [hang];
        MainWindow.SmokeVideoAnswer = Path.Combine(root, "smoke-video", "улица.mp4");
        foreach (var check in (await RunScriptAsync(web, "(async ({projectId}) => {" + ChecksHelpers + ChecksLiveScript + "})(" + arguments + ")", token, attempts: 900))
            ["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());

        // A screen check of the page stops when the page is replaced: nobody observes it any more.
        var started = await RunScriptAsync(web, "(async ({projectId}) => {" + ChecksHelpers + ChecksScreenScript + "})(" + arguments + ")", token, attempts: 300);
        foreach (var check in started["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        var checkId = Guid.Parse(started["checkId"]!.GetValue<string>());
        await web.ExecuteScriptAsync("window.__beforeReload = true; location.reload();");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token);
            if (await web.ExecuteScriptAsync("window.__beforeReload === undefined && document.readyState === 'complete'") == "true") break;
            if (attempt == 100) throw new TimeoutException("UI did not reload.");
        }
        var project = await core.Projects.GetAsync(projectId, token);
        for (var attempt = 0; ; attempt++)
        {
            var check = await project.Checks.GetCheckAsync(checkId, 0, token);
            if (check is { State: LiveCheckState.Cancelled, StopReason: LiveCheckStopReason.SessionEnded }) break;
            if (attempt > 300) throw new TimeoutException($"The screen check is {check.State} ({check.StopReason}) after the reload.");
            await Task.Delay(100, token);
        }
        checks.Add("A reload of the page stops its screen check (session_ended)");
    }

    private const string ChecksHelpers = """
        const checks = [];
        const events = [];
        const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
        const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
        window.chrome.webview.addEventListener('message', listen);
        let sequence = 0;
        const request = (method, payload) => new Promise((resolve, reject) => {
          const requestId = 'checks-smoke-' + Date.now() + '-' + (++sequence);
          const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 60000);
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
        const wait = async (query, label) => {
          for (let i = 0; i < 600; i++) { const value = await query(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
          throw new Error('Timed out: ' + label);
        };
        const finished = jobId => wait(() => events.find(event => event.event === 'job.changed' && event.data.id === jobId
          && ['succeeded', 'failed', 'cancelled'].includes(event.data.status)), 'job ' + jobId);
        const checkEvent = (checkId, states) => wait(() => events.filter(event => event.event === 'checks.changed' && event.data.check?.id === checkId)
          .find(event => states.includes(event.data.check.state)), 'check ' + checkId + ' ' + states);
        const load = src => new Promise((resolve, reject) => { const element = document.createElement('img'); element.onload = () => resolve(element);
          element.onerror = () => reject(new Error('Image did not load: ' + src)); element.src = src; });
        const rename = async (versionId, name) => {
          const current = await request('datasets.get', {projectId, versionId});
          const renamed = await request('datasets.update', {projectId, versionId, name, description: '', revision: current.data.revision});
          if (!renamed.ok) throw new Error('Rename failed: ' + JSON.stringify(renamed.error));
        };
        const versions = (await request('training.listModelVersions', {projectId})).data;
        const working = versions.find(version => version.isWorking);
        const datasetId = working.dataset.versionId;
        const parameters = {confidence: null, maxDetections: 2, device: 'cpu'};
        """;

    private const string ChecksModelsScript = """
        try {
          const review = await request('training.reviewModelVersionDeletion', {projectId, versionId: working.id});
          assert(review.ok && review.data.isWorking && review.data.blockers[0]?.code === 'training.version_working'
            && mautLocale.blockerText('training', review.data.blockers[0]).startsWith('Это рабочая версия'), 'The deletion review of the working version explains the blocker');
          const refused = await request('training.deleteModelVersion', {projectId, versionId: working.id});
          assert(!refused.ok && refused.error.code === 'training.version_in_use' && refused.error.details?.reason === 'working'
            && mautLocale.errorText(refused.error.code, refused.error.details).startsWith('Версию сейчас нельзя удалить'), 'Deleting the working version is refused with the reason');

          const selection = await request('training.selectModelExportDestination', {projectId, versionId: working.id});
          assert(selection.ok && selection.data.token && selection.data.fileName === 'model-v1.zip', '«Сохранить как» gives a destination token');
          const export1 = await request('training.startModelExport', {projectId, versionId: working.id, destinationToken: selection.data.token, operationId: 'checks-smoke-export'});
          assert(export1.ok && export1.data.kind === 'training.export', 'training.startModelExport returns the job at once');
          const exported = await finished(export1.data.id);
          assert(exported.data.status === 'succeeded' && exported.data.result?.fileName === 'model-v1.zip' && exported.data.result.bytes > 0
            && /^[0-9a-f]{64}$/.test(exported.data.result.sha256), 'The export ends in job.changed with the file, its size and SHA-256');

          await rename(datasetId, 'Проверка smoke');
          const empty = await request('checks.startEvaluation', {projectId, versionId: working.id, datasetVersionId: datasetId, split: 'test', device: 'cpu', batch: null,
            operationId: 'checks-smoke-empty'});
          assert(!empty.ok && empty.error.code === 'checks.evaluation_split_empty' && mautLocale.errorText(empty.error.code, empty.error.details) === 'В части Test этого датасета нет кадров.',
            'An empty part is refused with the text of the spec');
          const bad = await request('checks.startEvaluation', {projectId, versionId: working.id, datasetVersionId: datasetId, split: 'train', device: 'cpu', batch: null,
            operationId: 'checks-smoke-bad'});
          assert(!bad.ok && bad.error.code === 'checks.evaluation_parameters' && bad.error.details?.field === 'split', 'An unknown part names the field split');
          const started = await request('checks.startEvaluation', {projectId, versionId: working.id, datasetVersionId: datasetId, split: 'val', device: 'cpu', batch: 4,
            operationId: 'checks-smoke-evaluation'});
          assert(started.ok && started.data.job.kind === 'checks.evaluate' && ['queued', 'preparing', 'running'].includes(started.data.state)
            && started.data.dataset.name === 'Проверка smoke' && started.data.batch === 4, 'checks.startEvaluation returns the queued evaluation');
          const done = await finished(started.data.id);
          assert(done.data.status === 'succeeded', 'The evaluation job succeeds');
          await wait(() => events.some(event => event.event === 'checks.changed' && event.data.evaluations.some(change => change.id === started.data.id)), 'checks.changed evaluation');
          const evaluation = await request('checks.getEvaluation', {projectId, evaluationId: started.data.id});
          assert(evaluation.ok && evaluation.data.state === 'succeeded' && evaluation.data.metrics.mAP50 === 0.6 && evaluation.data.classMetrics.length === 1
            && evaluation.data.imageCount > 0 && evaluation.data.split === 'val', 'The evaluation has its metrics, per class and the image count');
          const listed = await request('checks.listEvaluations', {projectId, versionId: working.id, split: 'val'});
          const latest = await request('checks.listLatestEvaluations', {projectId, datasetVersionId: datasetId, split: 'val'});
          assert(listed.ok && listed.data.total === 1 && latest.ok && latest.data.some(item => item.id === started.data.id), 'Evaluations are listed and form the matrix');

          const picked = await request('checks.selectImage', {projectId});
          assert(picked.ok && picked.data.token && picked.data.name === 'проверка.jpg' && picked.data.byteSize > 0, 'checks.selectImage keeps the file behind a token');
          const imageStart = {projectId, versionId: working.id, selectionToken: picked.data.token, parameters, operationId: 'checks-smoke-image'};
          const image = await request('checks.startImage', imageStart);
          assert(image.ok && image.data.mode === 'image' && ['preparing', 'running', 'succeeded'].includes(image.data.state) && image.data.settings.maxDetections === 2,
            'checks.startImage returns the check at once');
          const again = await request('checks.startImage', imageStart);
          assert(again.ok && again.data.id === image.data.id, 'A repeated start returns the same check');
          const ended = await checkEvent(image.data.id, ['succeeded', 'failed']);
          assert(ended.data.check.state === 'succeeded' && ended.data.check.lastFrame.detections.length === 2 && ended.data.check.recentFrames.length === 0,
            'The image check ends in checks.changed with two detections');
          const snapshot = await request('checks.get', {projectId, checkId: image.data.id});
          assert(snapshot.ok && snapshot.data.recentFrames.length === 1 && snapshot.data.summary.detections === 2 && snapshot.data.preview?.url, 'checks.get has the journal and the preview');
          const preview = await load(snapshot.data.preview.url);
          assert(preview.naturalWidth > 0, 'The preview loads from the resource channel');
          const eventPreview = events.find(event => event.event === 'checks.changed' && event.data.check?.id === image.data.id && event.data.check.preview);
          assert(eventPreview && (await load(eventPreview.data.check.preview.url)).naturalWidth > 0, 'checks.changed carries the preview URL, not its bytes');
          return {checks};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;

    private const string ChecksLiveScript = """
        try {
          const picked = await request('checks.selectImage', {projectId});
          const hanging = await request('checks.startImage', {projectId, versionId: working.id, selectionToken: picked.data.token, parameters, operationId: 'checks-smoke-hang'});
          await checkEvent(hanging.data.id, ['running']);
          const busy = await request('checks.startImage', {projectId, versionId: working.id, selectionToken: picked.data.token, parameters, operationId: 'checks-smoke-busy'});
          assert(!busy.ok && busy.error.code === 'checks.gpu_busy_prediction' && mautLocale.errorText(busy.error.code, busy.error.details).startsWith('Уже идёт другая проверка'),
            'A second check is refused at once while one runs');
          const review = await request('training.reviewModelVersionDeletion', {projectId, versionId: working.id});
          assert(review.data.blockers.some(blocker => blocker.code === 'checks.live_active'), 'A running check blocks deleting its version');
          const stopping = await request('checks.stop', {projectId, checkId: hanging.data.id});
          assert(stopping.ok && ['cancelling', 'cancelled'].includes(stopping.data.state) && stopping.data.stopReason === 'user', 'checks.stop shows «Остановка…»');
          const stopped = await checkEvent(hanging.data.id, ['cancelled']);
          assert(stopped.data.check.stopReason === 'user', 'The stopped check is «Остановлено»');

          const monitors = await request('checks.listMonitors', {});
          assert(monitors.ok && monitors.data.length >= 1 && monitors.data[0].index === 1 && monitors.data.some(monitor => monitor.isPrimary), 'checks.listMonitors lists the monitors');
          const shot = await request('checks.captureScreen', {projectId, monitor: monitors.data[0].index});
          assert(shot.ok && shot.data.monitor.width === monitors.data[0].width, 'checks.captureScreen returns the monitor and a URL');
          const screen = await load(shot.data.url);
          assert(screen.naturalWidth === monitors.data[0].width && screen.naturalHeight === monitors.data[0].height, 'The screenshot loads in physical pixels');
          const missing = await request('checks.captureScreen', {projectId, monitor: 99});
          assert(!missing.ok && missing.error.code === 'screen.monitor_not_found' && mautLocale.errorText(missing.error.code, missing.error.details).startsWith('Монитор не найден'), 'An unknown monitor is refused');

          const video = await request('checks.selectVideo', {projectId});
          assert(video.ok && video.data.width === 640 && video.data.height === 360 && video.data.durationMs > 4000 && video.data.frame?.timestampMs > 2000,
            'checks.selectVideo probes the video and takes a frame from its middle');
          assert((await load(video.data.frame.url)).naturalWidth === video.data.frame.previewWidth, 'The sample frame loads');
          const other = await request('checks.previewVideoFrame', {projectId, selectionToken: video.data.token, timestampMs: 1000});
          assert(other.ok && other.data.timestampMs === 1000 && other.data.width === 640, '«Показать другой кадр» gives another frame');
          const region = {x: 0, y: 0, width: working.input.width, height: working.input.height};
          const outside = await request('checks.startVideo', {projectId, versionId: working.id, selectionToken: video.data.token, parameters, stepMs: 500,
            region: {...region, x: 700}, operationId: 'checks-smoke-outside'});
          assert(!outside.ok && outside.error.code === 'checks.region_invalid', 'A region outside the frame is refused');
          const run = await request('checks.startVideo', {projectId, versionId: working.id, selectionToken: video.data.token, parameters, stepMs: 500, region,
            operationId: 'checks-smoke-video'});
          const result = await checkEvent(run.data.id, ['succeeded', 'failed']);
          assert(result.data.check.state === 'succeeded' && result.data.check.summary.framesProcessed === 12 && result.data.check.journal.total === 6
            && result.data.check.settings.stepMs === 500, 'The video check ends with the summary of every frame');
          const journal = await request('checks.listJournal', {projectId, checkId: run.data.id, after: 3});
          assert(journal.ok && journal.data.items.map(item => item.ordinal).join() === '4,5,6' && journal.data.items[0].detections.length === 2,
            'checks.listJournal reads the frames after an ordinal');
          const list = await request('checks.list', {projectId});
          assert(list.ok && list.data[0].id === run.data.id && list.data.length >= 3, 'checks.list returns the checks newest first');
          return {checks};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;

    private const string ChecksScreenScript = """
        try {
          const started = await request('checks.startScreen', {projectId, versionId: working.id, monitor: 1, parameters, fps: 5, maxDurationSec: 120, region: null,
            operationId: 'checks-smoke-screen'});
          assert(started.ok && started.data.mode === 'screen' && started.data.settings.fps === 5 && started.data.source.monitor === 1, 'checks.startScreen starts a screen check');
          await checkEvent(started.data.id, ['running']);
          return {checks, checkId: started.data.id};
        } finally {
          window.chrome.webview.removeEventListener('message', listen);
        }
        """;
}
