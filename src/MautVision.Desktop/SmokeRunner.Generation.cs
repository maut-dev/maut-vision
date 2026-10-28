using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Generation API from the real page: a recipe (a validation error with its field and Russian text, generation.changed),
    /// samples served as transient resources, a run of a few frames with its progress and result in job.changed, the output
    /// group with thumbnails and reviewed annotations in the images API, the run list, the repeat draft, deletion of the run (its
    /// operation is not started again) and of the recipe. The catalog and the backgrounds are prepared through Core.
    /// </summary>
    private static async Task CheckGenerationAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var size = project.InputSize;
        var directory = Path.Combine(root, "smoke-generation");
        Directory.CreateDirectory(directory);
        string Write(string name, byte[] content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }
        var variant = await project.Variants.CreateAsync("Генерация", "", token);
        var klass = await project.Classes.CreateAsync("Объект генерации", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        var assets = await core.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id,
            [Write("спрайт-1.png", Sprite(120, 80, SKColors.OrangeRed)), Write("спрайт-2.png", Sprite(60, 140, SKColors.MediumSeaGreen))]),
            projectId, "generation-smoke-assets", cancellationToken: token);
        var set = await project.Backgrounds.CreateSetAsync("Фоны генерации", "", token);
        var backgrounds = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(set.Id,
            [new BackgroundFileSource(Write("фон-1.png", Frame(size.Width, size.Height, SKColors.SteelBlue))),
                new BackgroundFileSource(Write("фон-2.png", Frame(size.Width, size.Height, SKColors.DarkKhaki)))]), projectId, "generation-smoke-backgrounds", cancellationToken: token);
        foreach (var job in new[] { assets, backgrounds })
        {
            for (var attempt = 0; ; attempt++)
            {
                var current = await core.Jobs.GetAsync(projectId, job.Id, token);
                if (current.Status == JobStatus.Succeeded) break;
                if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 600)
                    throw new InvalidOperationException($"Generation fixture job {current.Kind} is {current.Status}.");
                await Task.Delay(50, token);
            }
        }
        var arguments = JsonSerializer.Serialize(new { projectId, width = size.Width, height = size.Height, setId = set.Id, variantId = variant.Id, bindingId = binding.Id });
        var result = await RunScriptAsync(web, "(" + GenerationScript + ")(" + arguments + ")", token, attempts: 600);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private static byte[] Sprite(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawOval(new SKRect(4, 4, width - 4, height - 4), paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Frame(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(width / 5f, height / 5f, width / 4f, height / 6f, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private const string GenerationScript = """
        async ({projectId, width, height, setId, variantId, bindingId}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'generation-smoke-' + (++sequence);
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
          const load = url => new Promise((resolve, reject) => {
            const element = document.createElement('img');
            element.onload = () => resolve(element);
            element.onerror = () => reject(new Error('Image did not load: ' + url));
            element.src = url;
          });
          const form = {name: '  Пресет smoke  ', description: '', width, height, backgroundSetIds: [setId], classVariantIds: [bindingId],
            variantWeights: [{variantId, percentage: 100}], objectsPerImage: 2, emptyProbability: 0, scale: 0.5};
          try {
            const invalid = await request('generation.createRecipe', {projectId, recipe: {...form, scale: 0}});
            assert(!invalid.ok && invalid.error.code === 'generation.invalid_recipe' && invalid.error.details?.field === 'scale'
              && invalid.error.message.startsWith('Настройки генерации'), 'generation.invalid_recipe names the field and has a Russian text');
            const created = await request('generation.createRecipe', {projectId, recipe: form});
            assert(created.ok && created.data.name === 'Пресет smoke' && created.data.revision === 1 && created.data.classVariantIds[0] === bindingId
              && created.data.variantWeights[0].percentage === 100, 'generation.createRecipe stores the recipe');
            const recipe = created.data;
            await wait(() => events.some(event => event.event === 'generation.changed' && event.data.recipes.some(change => change.id === recipe.id && change.revision === 1)),
              'recipe event');
            assert(true, 'generation.changed names the created recipe');
            const listed = await request('generation.listRecipes', {projectId});
            assert(listed.ok && listed.data.some(item => item.id === recipe.id), 'generation.listRecipes returns the recipe');

            const preview = await request('generation.previewSamples', {projectId, recipe: form, seed: 5, count: 2});
            const samples = preview.data?.samples ?? [];
            assert(preview.ok && samples.length === 2 && samples[0].width === width && samples[0].objects.length === 2
              && samples[0].objects[0].classVariantId === bindingId && samples[0].objects[0].box.width > 0, 'generation.previewSamples returns frames with boxes');
            const sample = await load(samples[0].imageUrl);
            assert(sample.naturalWidth === width && sample.naturalHeight === height, 'A sample loads from the resource channel at the canvas size');
            const badSeed = await request('generation.previewSamples', {projectId, recipe: form, seed: -1});
            assert(!badSeed.ok && badSeed.error.code === 'generation.seed_invalid', 'A seed outside uint32 returns generation.seed_invalid');

            const start = {projectId, recipeId: recipe.id, recipeRevision: recipe.revision, seed: 5, count: 3, operationId: 'generation-smoke-run'};
            const stale = await request('generation.start', {...start, recipeRevision: 7, operationId: 'generation-smoke-stale'});
            assert(!stale.ok && stale.error.code === 'generation.revision_conflict', 'A stale recipe revision returns generation.revision_conflict');
            const started = await request('generation.start', start);
            assert(started.ok && started.data.job.kind === 'generation.run' && started.data.requestedCount === 3 && started.data.seed === 5
              && started.data.recipe.id === recipe.id && started.data.outputGroupName.startsWith('Пресет smoke · '), 'generation.start returns the run at once');
            const again = await request('generation.start', start);
            assert(again.ok && again.data.job.id === started.data.job.id, 'A repeated start of the same operation returns the same run');
            const runId = started.data.job.id;
            const done = await wait(() => events.find(event => event.event === 'job.changed' && event.data.id === runId
              && ['succeeded', 'failed', 'cancelled'].includes(event.data.status)), 'run ' + runId);
            assert(done.data.status === 'succeeded' && done.data.result.producedCount === 3 && done.data.result.requestedCount === 3
              && done.data.result.outputGroupId && done.data.processed === 3 && done.data.total === 3, 'The run result arrives in job.changed');
            assert(events.some(event => event.event === 'job.changed' && event.data.id === runId && event.data.status === 'running'), 'Progress of the run arrives in job.changed');
            assert(events.some(event => event.event === 'generation.changed' && event.data.runs.includes(runId)), 'generation.changed names the prepared run');
            const groupId = done.data.result.outputGroupId;
            await wait(() => events.some(event => event.event === 'images.changed' && (event.data.imagesChangedIn.includes(groupId)
              || event.data.groups.some(change => change.id === groupId))), 'images event');
            assert(true, 'images.changed names the output group');
            const group = await request('images.getGroup', {projectId, groupId});
            assert(group.ok && group.data.origin === 'generated' && group.data.generationState === 'completed' && group.data.generationRunId === runId
              && group.data.imageCount === 3, 'The output group is generated and completed');
            const page = await request('images.list', {projectId, groupId});
            assert(page.ok && page.data.items.length === 3 && page.data.items[0].reviewState === 'reviewed' && page.data.items[0].objectCount === 2
              && page.data.items[0].lineage.kind === 'generated', 'Generated images are reviewed and keep their lineage');
            const thumbnail = await load(page.data.items[0].thumbnailUrl);
            assert(Math.max(thumbnail.naturalWidth, thumbnail.naturalHeight) <= 256 && thumbnail.naturalWidth > 0, 'A generated image thumbnail loads');
            const annotation = await request('images.getAnnotation', {projectId, imageId: page.data.items[0].id});
            assert(annotation.ok && annotation.data.objects.length === 2 && annotation.data.objects[0].classVariantId === bindingId
              && JSON.stringify(annotation.data.objects.map(item => item.box)) === JSON.stringify(samples[0].objects.map(item => item.box)),
              'The first frame has the boxes of the first sample');
            const runs = await request('generation.listRuns', {projectId});
            assert(runs.ok && runs.data.total >= 1 && runs.data.items[0].job.id === runId && runs.data.items[0].producedCount === 3
              && runs.data.items[0].outputGroupId === groupId, 'generation.listRuns returns the run with its group');
            const draft = await request('generation.getRepeatDraft', {projectId, runId});
            assert(draft.ok && draft.data.recipe.name === 'Пресет smoke · повтор' && draft.data.seed === 5 && draft.data.requestedCount === 3
              && draft.data.notice.startsWith('Настройки сохранённого запуска'), 'generation.getRepeatDraft fills the form for a repeat');
            const removed = await request('generation.deleteRun', {projectId, runId});
            assert(removed.ok && removed.data.runId === runId, 'generation.deleteRun deletes a finished run');
            const kept = await request('images.getGroup', {projectId, groupId});
            assert(kept.ok && kept.data.imageCount === 3, 'The images of a deleted run stay');
            const replay = await request('generation.start', start);
            assert(!replay.ok && replay.error.code === 'generation.deleted_operation' && replay.error.message.includes('удалён'),
              'The operation of a deleted run does not start again');
            const deleted = await request('generation.deleteRecipe', {projectId, recipeId: recipe.id, revision: recipe.revision});
            assert(deleted.ok, 'generation.deleteRecipe deletes the recipe');
            const gone = await request('generation.getRecipe', {projectId, recipeId: recipe.id});
            assert(!gone.ok && gone.error.code === 'generation.unavailable' && gone.error.message.startsWith('Пресет или запуск'),
              'A deleted recipe returns generation.unavailable');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;
}
