using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Training API from the real page with the fake worker (smoke.ps1 passes --worker-executable and a catalogue that matches the
    /// seeded weights): runtime and GPUs, presets with a parameter error in Russian, a run through job.changed and training.changed
    /// to a model version whose plot loads from the resource channel, history and log pages, the working version, a worker failure
    /// with its own message, the dataset blocker and cancellation. The annotated dataset is prepared through Core.
    /// </summary>
    private static async Task CheckTrainingAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var directory = Path.Combine(root, "smoke-training");
        Directory.CreateDirectory(directory);
        var variant = await project.Variants.CreateAsync("Обучение", "", token);
        var klass = await project.Classes.CreateAsync("Объект обучения", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        var group = await project.Images.CreateGroupAsync("Кадры обучения", "", ImageSplitHint.Unassigned, token);
        var paths = Enumerable.Range(0, 8).Select(index =>
        {
            var path = Path.Combine(directory, $"обучение-{index}.jpg");
            File.WriteAllBytes(path, Photo(64, 64, 40 + index));
            return path;
        }).ToArray();
        var import = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray()), projectId, "training-smoke-import", cancellationToken: token);
        await WaitJobAsync(core, projectId, import.Id, token);
        foreach (var image in (await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 100, cancellationToken: token)).Items)
        {
            await project.Images.SaveAnnotationAsync(image.Id, new AnnotationInput([new AnnotationObjectInput(null, klass.Id, binding.Id,
                new ImageBox(0.3, 0.3, 0.4, 0.4))], null, ImageReviewState.Reviewed), image.AnnotationRevision, token);
        }
        var plan = await project.Datasets.PlanAsync(new DatasetSpecification("Обучение smoke", "", [group.Id], [klass.Id], [binding.Id], [],
            DatasetUnassignedVariantRole.Target, DatasetUnselectedClassRole.Negative, false, false, 3, 0.25, 0), "training-smoke-plan", token);
        var build = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(
            await project.Datasets.PrepareBuildAsync(plan.Id, plan.Hash, "training-smoke-build", token), projectId, "training-smoke-build", cancellationToken: token);
        await WaitJobAsync(core, projectId, build.Id, token);
        var arguments = JsonSerializer.Serialize(new { projectId, datasetId = build.Id });
        var result = await RunScriptAsync(web, "(" + TrainingScript + ")(" + arguments + ")", token, attempts: 900);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
    }

    private static async Task WaitJobAsync(ICore core, string projectId, Guid jobId, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await core.Jobs.GetAsync(projectId, jobId, token);
            if (current.Status == JobStatus.Succeeded) return;
            if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 1200)
                throw new InvalidOperationException($"Training fixture job {current.Kind} is {current.Status}.");
            await Task.Delay(50, token);
        }
    }

    private const string TrainingScript = """
        async ({projectId, datasetId}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'training-smoke-' + (++sequence);
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
          const rename = async name => {
            const current = await request('datasets.get', {projectId, versionId: datasetId});
            const renamed = await request('datasets.update', {projectId, versionId: datasetId, name, description: '', revision: current.data.revision});
            if (!renamed.ok) throw new Error('Rename failed: ' + JSON.stringify(renamed.error));
          };
          try {
            const runtime = await request('training.getRuntime', {});
            assert(runtime.ok && runtime.data.configured && runtime.data.available && runtime.data.mode === 'packaged' && runtime.data.code === null,
              'training.getRuntime reports the configured worker');
            const gpus = await request('training.listGpus', {});
            assert(gpus.ok && typeof gpus.data.driverAvailable === 'boolean' && Array.isArray(gpus.data.gpus), 'training.listGpus answers with the GPU list');

            const badPreset = await request('training.savePreset', {name: 'Плохой', parameters: {mosaic: 2}});
            assert(!badPreset.ok && badPreset.error.code === 'training.parameter_invalid' && badPreset.error.details?.field === 'mosaic'
              && mautLocale.errorText(badPreset.error.code, badPreset.error.details).startsWith('Проверьте параметр'), 'An invalid preset parameter names its field with a Russian text');
            const saved = await request('training.savePreset', {name: 'Smoke', parameters: {epochs: 2, batch: 4}});
            const listed = await request('training.listPresets', {});
            assert(saved.ok && listed.ok && listed.data.some(preset => preset.id === saved.data.id && preset.parameters.epochs === 2)
              && events.some(event => event.event === 'trainingPresets.changed'), 'Presets are saved, listed and announced');
            const removed = await request('training.deletePreset', {presetId: saved.data.id});
            assert(removed.ok && removed.data.presetId === saved.data.id, 'training.deletePreset removes the preset');

            const invalid = await request('training.startRun', {projectId, datasetVersionId: datasetId, parameters: {epochs: 0}, presetName: null, operationId: 'training-smoke-invalid'});
            assert(!invalid.ok && invalid.error.code === 'training.parameter_invalid' && invalid.error.details?.field === 'epochs', 'A run with zero epochs is rejected');
            await rename('Успех smoke');
            const start = {projectId, datasetVersionId: datasetId, parameters: {epochs: 2, batch: 4, device: 'cpu'}, presetName: 'Smoke', operationId: 'training-smoke-run'};
            const started = await request('training.startRun', start);
            assert(started.ok && started.data.job.kind === 'training.run' && ['queued', 'preparing'].includes(started.data.state)
              && started.data.parameters.optimizer === 'AdamW' && started.data.presetName === 'Smoke' && started.data.dataset.versionId === datasetId,
              'training.startRun returns the queued run with complete parameters');
            const repeated = await request('training.startRun', start);
            assert(repeated.ok && repeated.data.id === started.data.id, 'A repeated start returns the same run');
            const done = await finished(started.data.id);
            assert(done.data.status === 'succeeded' && done.data.result?.versionId && done.data.progress === 100, 'The run finishes in job.changed with the version');
            assert(events.some(event => event.event === 'training.changed' && event.data.runs.some(change => change.id === started.data.id))
              && events.some(event => event.event === 'training.changed' && event.data.versions.some(change => change.id === done.data.result.versionId)),
              'training.changed names the run and the version');
            const run = await request('training.getRun', {projectId, runId: started.data.id});
            assert(run.ok && run.data.state === 'succeeded' && run.data.resultVersionId === done.data.result.versionId && run.data.epochCount === 2 && run.data.progress.epoch === 2 && run.data.progress.epochs === 2
              && run.data.effectiveParams?.optimizer === 'AdamW' && run.data.environment?.torch === '2.14.0+fake', 'training.getRun returns the finished run');
            const epochs = await request('training.listEpochs', {projectId, runId: started.data.id, limit: 1});
            const more = await request('training.listEpochs', {projectId, runId: started.data.id, after: epochs.data.nextAfter});
            assert(epochs.ok && epochs.data.items[0].epoch === 1 && epochs.data.total === 2 && more.data.items[0].epoch === 2 && more.data.nextAfter === null
              && typeof more.data.items[0].metrics.mAP50 === 'number', 'training.listEpochs pages the history');
            const tail = await request('training.tailLog', {projectId, runId: started.data.id, limit: 2});
            const log = await request('training.listLog', {projectId, runId: started.data.id, limit: 1000});
            assert(tail.ok && tail.data.items.length === 2 && tail.data.items[1].kind === 'system' && log.ok && log.data.items.length === log.data.total
              && log.data.items.some(item => item.kind === 'log'), 'training.tailLog and training.listLog return the log');
            const versions = await request('training.listModelVersions', {projectId});
            const version = await request('training.getModelVersion', {projectId, versionId: done.data.result.versionId});
            assert(versions.ok && versions.data.length === 1 && version.ok && version.data.isWorking && version.data.number === 1
              && version.data.classes[0].classId && version.data.files.length === 4 && version.data.notes.length === 1, 'The version has its passport and files');
            const plot = version.data.files.find(file => file.kind === 'plot');
            const image = await new Promise((resolve, reject) => { const element = document.createElement('img'); element.onload = () => resolve(element);
              element.onerror = () => reject(new Error('Plot did not load')); element.src = plot.url; });
            assert(image.naturalWidth === 1 && version.data.files.find(file => file.kind === 'weights_pt').url === null, 'A plot of the version loads from the resource channel');
            const working = await request('training.setWorkingModelVersion', {projectId, versionId: version.data.id});
            assert(working.ok && working.data.isWorking, 'training.setWorkingModelVersion keeps the working version');

            await rename('fake:fail=device_unavailable');
            const failing = await request('training.startRun', {...start, operationId: 'training-smoke-fail'});
            const failed = await finished(failing.data.id);
            const failedRun = await request('training.getRun', {projectId, runId: failing.data.id});
            assert(failed.data.status === 'failed' && failed.data.error.code === 'training.worker_device_unavailable'
              && failedRun.data.state === 'failed' && failedRun.data.error.workerMessage === 'Сообщение worker: device_unavailable.',
              'A worker failure keeps the worker code and its own message');

            await rename('fake:hang');
            const hanging = await request('training.startRun', {...start, operationId: 'training-smoke-hang'});
            await wait(async () => (await request('training.getRun', {projectId, runId: hanging.data.id})).data?.state === 'training', 'training state');
            const review = await request('datasets.reviewDeletion', {projectId, versionId: datasetId});
            assert(review.ok && review.data.blockers.some(blocker => blocker.code === 'training.active' && mautLocale.blockerText('datasets', blocker).startsWith('Датасет используется обучением')),
              'A running training blocks deleting its dataset');
            const cancelling = await request('training.cancelRun', {projectId, runId: hanging.data.id});
            assert(cancelling.ok && ['cancelling', 'cancelled'].includes(cancelling.data.state), 'training.cancelRun shows «Отмена…»');
            const cancelled = await finished(hanging.data.id);
            assert(cancelled.data.status === 'cancelled', 'The cancelled run ends as cancelled');
            const runs = await request('training.listRuns', {projectId, limit: 10});
            assert(runs.ok && runs.data.total === 3 && runs.data.items[0].id === hanging.data.id && runs.data.items[0].state === 'cancelled',
              'training.listRuns lists the runs newest first');
            const missing = await request('training.getRun', {projectId, runId: '00000000-0000-4000-8000-000000000000'});
            assert(!missing.ok && missing.error.code === 'training.run_not_found' && mautLocale.errorText(missing.error.code, missing.error.details) === 'Запуск обучения не найден.', 'An unknown run is training.run_not_found');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;
}
