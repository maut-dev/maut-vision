using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Images;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Datasets API from the real page: a plan (a specification error with its field and Russian text, counts and warnings), the
    /// build with progress and result in job.changed and datasets.changed, the version and a samples page whose image and
    /// thumbnail load from the resource channel, an integrity check, an export through the «Сохранить как» seam (the ZIP is
    /// checked here), the operation history and deletion. Classes and annotated images are prepared through Core.
    /// </summary>
    private static async Task CheckDatasetsAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var directory = Path.Combine(root, "smoke-datasets");
        Directory.CreateDirectory(directory);
        var variant = await project.Variants.CreateAsync("Датасет", "", token);
        var klass = await project.Classes.CreateAsync("Объект датасета", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        var group = await project.Images.CreateGroupAsync("Кадры датасета", "", ImageSplitHint.Unassigned, token);
        var paths = Enumerable.Range(0, 12).Select(index =>
        {
            var path = Path.Combine(directory, $"кадр-{index:00}.jpg");
            File.WriteAllBytes(path, Photo(96, 72, index));
            return path;
        }).ToArray();
        var import = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(group.Id,
            paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray()), projectId, "datasets-smoke-import", cancellationToken: token);
        for (var attempt = 0; ; attempt++)
        {
            var current = await core.Jobs.GetAsync(projectId, import.Id, token);
            if (current.Status == JobStatus.Succeeded) break;
            if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 600)
                throw new InvalidOperationException($"Dataset fixture import is {current.Status}.");
            await Task.Delay(50, token);
        }
        foreach (var image in (await project.Images.ListImagesAsync(group.Id, new ImageFilter(), 100, cancellationToken: token)).Items)
        {
            await project.Images.SaveAnnotationAsync(image.Id, new AnnotationInput([new AnnotationObjectInput(null, klass.Id, binding.Id,
                new ImageBox(0.25, 0.2, 0.4, 0.5))], null, ImageReviewState.Reviewed), image.AnnotationRevision, token);
        }
        var destination = Path.Combine(directory, "Датасет smoke.zip");
        MainWindow.SmokeSaveAnswer = destination;
        var arguments = JsonSerializer.Serialize(new { projectId, groupId = group.Id, classId = klass.Id, bindingId = binding.Id });
        var result = await RunScriptAsync(web, "(" + DatasetsScript + ")(" + arguments + ")", token, attempts: 600);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());

        using (var zip = ZipFile.OpenRead(destination))
        {
            var names = zip.Entries.Select(entry => entry.FullName).ToArray();
            if (!names.Contains("data.yaml") || !names.Contains("manifest.json") || !names.Contains("dataset-metadata.json")
                || names.Count(name => name.StartsWith("images/", StringComparison.Ordinal)) != 12 || names.Count(name => name.StartsWith("labels/", StringComparison.Ordinal)) != 12)
            {
                throw new InvalidOperationException("The exported ZIP does not have the dataset layout: " + string.Join(", ", names));
            }
        }
        checks.Add("The ZIP chosen in «Сохранить как» has data.yaml, manifest.json, metadata, 12 images and 12 labels");
    }

    /// <summary>A small JPEG with a gradient and a box, different for each index.</summary>
    private static byte[] Photo(int width, int height, int index)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor((byte)(index * 19), (byte)(80 + index * 7), (byte)(200 - index * 9)));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 4f + index, height / 5f, width / 3f, height / 2f, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private const string DatasetsScript = """
        async ({projectId, groupId, classId, bindingId}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'datasets-smoke-' + (++sequence);
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
          const wait = async (query, label) => {
            for (let i = 0; i < 300; i++) { const value = query(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
            throw new Error('Timed out: ' + label);
          };
          const finished = jobId => wait(() => events.find(event => event.event === 'job.changed' && event.data.id === jobId
            && ['succeeded', 'failed', 'cancelled'].includes(event.data.status)), 'job ' + jobId);
          const load = url => new Promise((resolve, reject) => {
            const element = document.createElement('img');
            element.onload = () => resolve(element);
            element.onerror = () => reject(new Error('Image did not load: ' + url));
            element.src = url;
          });
          const specification = {name: '  Датасет smoke  ', description: '', groupIds: [groupId], classIds: [classId], targetVariantIds: [bindingId],
            negativeVariantIds: [], unassignedVariantRole: 'target', unselectedClassRole: 'negative', includeReviewedEmpty: false,
            allowPartialGeneration: false, seed: 11, valRatio: 0.25, testRatio: 0};
          try {
            const invalid = await request('datasets.plan', {projectId, specification: {...specification, valRatio: 0}, operationId: 'datasets-smoke-invalid'});
            assert(!invalid.ok && invalid.error.code === 'datasets.split_invalid' && invalid.error.details?.field === 'ratios'
              && mautLocale.errorText(invalid.error.code, invalid.error.details).startsWith('Проверьте доли'), 'datasets.plan names a wrong field with a Russian text');
            const badSeed = await request('datasets.plan', {projectId, specification: {...specification, seed: -1}, operationId: 'datasets-smoke-seed'});
            assert(!badSeed.ok && badSeed.error.code === 'datasets.seed_invalid', 'A seed outside uint32 returns datasets.seed_invalid');
            const planned = await request('datasets.plan', {projectId, specification, operationId: 'datasets-smoke-plan'});
            const plan = planned.data;
            assert(planned.ok && plan.counts.total === 12 && plan.counts.test === 0 && plan.skipped.total === 0 && plan.classes[0].index === 0
              && plan.classes[0].classId === classId && plan.specification.name === 'Датасет smoke' && plan.buildable && typeof plan.hash === 'string',
              'datasets.plan returns counts, classes and the hash');
            assert(plan.warnings.every(warning => mautLocale.planWarningText(warning).length > 0), 'Plan warnings have texts by code');
            const again = await request('datasets.getPlan', {projectId, planId: plan.id});
            assert(again.ok && again.data.hash === plan.hash, 'datasets.getPlan returns the plan');

            const start = {projectId, planId: plan.id, planHash: plan.hash, operationId: 'datasets-smoke-build'};
            const started = await request('datasets.startBuild', start);
            assert(started.ok && started.data.kind === 'build' && started.data.job.kind === 'datasets.build' && started.data.specification.seed === 11
              && started.data.versionName === 'Датасет smoke', 'datasets.startBuild returns the build at once');
            const repeated = await request('datasets.startBuild', start);
            assert(repeated.ok && repeated.data.job.id === started.data.job.id, 'A repeated start of the same operation returns the same build');
            const versionId = started.data.versionId;
            const built = await finished(started.data.job.id);
            assert(built.data.status === 'succeeded' && built.data.result.produced === 12 && built.data.result.linked === 12 && built.data.result.counts.total === 12
              && built.data.processed === 12 && built.data.total === 12, 'The build result arrives in job.changed');
            await wait(() => events.some(event => event.event === 'datasets.changed' && event.data.versions.some(change => change.id === versionId)), 'datasets event');
            assert(true, 'datasets.changed names the built version');

            const listed = await request('datasets.list', {projectId});
            const version = listed.data?.find(item => item.id === versionId);
            assert(listed.ok && version && version.integrityState === 'verified' && version.inputSizeMatches && version.counts.total === 12
              && version.classMap[0].name === 'Объект датасета' && version.composition.boxes === 12, 'datasets.list returns the verified version');
            const page = await request('datasets.listSamples', {projectId, versionId, split: 'train', limit: 3});
            assert(page.ok && page.data.items.length === 3 && page.data.nextCursor && page.data.items[0].boxes[0].index === 0
              && page.data.items[0].boxes[0].classVariantId === bindingId && page.data.items[0].format === 'jpeg', 'datasets.listSamples returns a page with boxes');
            const thumbnail = await load(page.data.items[0].thumbnailUrl);
            const original = await load(page.data.items[0].imageUrl);
            assert(thumbnail.naturalWidth > 0 && original.naturalWidth === 96 && original.naturalHeight === 72, 'A sample image and thumbnail load from the resource channel');
            const sample = await request('datasets.getSample', {projectId, versionId, sampleId: page.data.items[0].id});
            assert(sample.ok && sample.data.id === page.data.items[0].id && sample.data.split === 'train', 'datasets.getSample returns the sample');
            const badSplit = await request('datasets.listSamples', {projectId, versionId, split: 'validation'});
            assert(!badSplit.ok && badSplit.error.code === 'datasets.request_invalid', 'An unknown split returns datasets.request_invalid');

            const verify = await request('datasets.startVerify', {projectId, versionId, operationId: 'datasets-smoke-verify'});
            assert(verify.ok && verify.data.kind === 'verify' && verify.data.versionId === versionId, 'datasets.startVerify returns the check at once');
            const verified = await finished(verify.data.job.id);
            assert(verified.data.status === 'succeeded' && verified.data.result.state === 'verified' && verified.data.result.checkedFiles === 26
              && verified.data.result.issueCount === 0, 'The integrity result arrives in job.changed');

            const selection = await request('datasets.selectExportDestination', {projectId, versionId});
            assert(selection.ok && selection.data.token && selection.data.fileName === 'Датасет smoke.zip', '«Сохранить как» returns a token and the file name');
            const exported = await request('datasets.startExport', {projectId, versionId, destinationToken: selection.data.token, operationId: 'datasets-smoke-export'});
            assert(exported.ok && exported.data.kind === 'export' && exported.data.fileName === 'Датасет smoke.zip', 'datasets.startExport returns the export at once');
            const reused = await request('datasets.startExport', {projectId, versionId, destinationToken: selection.data.token, operationId: 'datasets-smoke-export-2'});
            assert(!reused.ok && reused.error.code === 'datasets.export_selection_expired', 'A destination token serves one operation');
            const done = await finished(exported.data.job.id);
            assert(done.data.status === 'succeeded' && done.data.result.exportedImages === 12 && done.data.result.bytes > 0, 'The export result arrives in job.changed');

            const exports = await request('datasets.listOperations', {projectId, kind: 'export'});
            assert(exports.ok && exports.data.total === 1 && exports.data.items[0].versionAvailable, 'datasets.listOperations lists the export');
            const removedOperation = await request('datasets.deleteOperation', {projectId, operationId: verify.data.job.id});
            assert(removedOperation.ok, 'datasets.deleteOperation removes a finished check from the history');
            const renamed = await request('datasets.update', {projectId, versionId, name: 'Переименован', description: '', revision: version.revision + 1});
            assert(renamed.ok && renamed.data.name === 'Переименован', 'datasets.update renames the version');
            const archived = await request('datasets.setArchived', {projectId, versionId, archived: true, revision: renamed.data.revision});
            const blockedVerify = await request('datasets.startVerify', {projectId, versionId, operationId: 'datasets-smoke-archived'});
            assert(archived.ok && archived.data.archivedAt && !blockedVerify.ok && blockedVerify.error.code === 'datasets.archived'
              && mautLocale.errorText(blockedVerify.error.code, blockedVerify.error.details).startsWith('Датасет в архиве'), 'An archived version cannot be checked');
            const review = await request('datasets.reviewDeletion', {projectId, versionId});
            assert(review.ok && review.data.samples === 12 && review.data.blockers.length === 0, 'datasets.reviewDeletion counts the samples');
            const deleted = await request('datasets.delete', {projectId, versionId, revision: review.data.revision});
            assert(deleted.ok && deleted.data.samples === 12, 'datasets.delete deletes the version');
            const gone = await request('datasets.get', {projectId, versionId});
            const passport = await request('datasets.getPassport', {projectId, versionId});
            assert(!gone.ok && gone.error.code === 'datasets.unavailable' && passport.ok && passport.data.deletedAt && passport.data.classMap.length === 1,
              'A deleted version keeps its passport');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;
}
