using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using Microsoft.Web.WebView2.Core;
using SkiaSharp;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Backgrounds API from the real page: a set, files chosen through the smoke answer of the files dialog with their fit,
    /// a scaled source preview and samples served as transient resources, a run with crops and its result in job.changed,
    /// backgrounds.changed, the gallery page with thumbnails, remove by item revision, the run history, a video run through
    /// the smoke answer of the video dialog with its errors, and deletion of the set. Runs after the video check, which
    /// leaves its clip in smoke-video.
    /// </summary>
    private static async Task CheckBackgroundsAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var size = (await core.Projects.GetAsync(projectId, token)).InputSize;
        var directory = Path.Combine(root, "smoke-backgrounds");
        Directory.CreateDirectory(directory);
        string Write(string name, int width, int height, SKColor color, SKEncodedImageFormat format)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(color);
                using var paint = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(width / 4f, height / 4f, width / 3f, height / 5f, paint);
            }
            using var data = bitmap.Encode(format, 90);
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }
        MainWindow.SmokeFilesAnswer =
        [
            Write("фон-2.png", size.Width, size.Height, SKColors.SteelBlue, SKEncodedImageFormat.Png),
            Write("большой-10.jpg", size.Width * 2 + 40, size.Height * 2 + 20, SKColors.Olive, SKEncodedImageFormat.Jpeg),
            Write("малый-1.png", size.Width / 2, size.Height / 2, SKColors.Teal, SKEncodedImageFormat.Png),
        ];
        MainWindow.SmokeVideoAnswer = Path.Combine(root, "smoke-video", "улица.mp4");
        var arguments = JsonSerializer.Serialize(new { projectId, width = size.Width, height = size.Height });
        var result = await RunScriptAsync(web, "(" + BackgroundsScript + ")(" + arguments + ")", token, attempts: 600);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private const string BackgroundsScript = """
        async ({projectId, width, height}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'backgrounds-smoke-' + (++sequence);
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
          const finished = id => wait(() => events.find(event => event.event === 'job.changed' && event.data.id === id
            && ['succeeded', 'failed', 'cancelled'].includes(event.data.status)), 'job ' + id);
          try {
            const badName = await request('backgrounds.createSet', {projectId, name: ' ', description: ''});
            assert(!badName.ok && badName.error.code === 'backgrounds.name_invalid' && badName.error.message.startsWith('Введите'),
              'backgrounds.name_invalid has a Russian text');
            const set = await request('backgrounds.createSet', {projectId, name: '  Улица  ', description: ''});
            assert(set.ok && set.data.name === 'Улица' && set.data.targetSize.width === width && set.data.targetSize.height === height
              && set.data.revision === 1 && set.data.itemCount === 0 && set.data.matchesProjectInput === true, 'backgrounds.createSet takes the project input size');
            await wait(() => events.some(event => event.event === 'backgrounds.changed' && event.data.sets.some(change => change.id === set.data.id)), 'set event');
            assert(true, 'backgrounds.changed names the created set');
            const listed = await request('backgrounds.listSets', {projectId});
            assert(listed.ok && listed.data.some(item => item.id === set.data.id), 'backgrounds.listSets returns the set');

            const files = await request('backgrounds.selectFiles', {projectId, setId: set.data.id});
            const names = files.data?.files?.map(file => file.name);
            assert(files.ok && files.data.selectionToken && files.data.exact === 1 && files.data.larger === 1 && files.data.smaller === 1 && files.data.unreadable === 0
              && names.join('|') === 'большой-10.jpg|малый-1.png|фон-2.png' && files.data.files[0].fit === 'larger' && files.data.files[0].width === width * 2 + 40,
              'backgrounds.selectFiles reads the headers and counts fitting and skipped files at once');
            const closed = await request('backgrounds.selectFiles', {projectId, setId: set.data.id});
            assert(closed.ok && closed.data.selectionToken === null && closed.data.files.length === 0, 'A closed files dialog answers without a token');
            const token = files.data.selectionToken;
            const source = await request('backgrounds.previewSource', {projectId, selectionToken: token, sourceIndex: 0});
            assert(source.ok && source.data.width === width * 2 + 40 && source.data.sourceIndex === 0 && source.data.name === 'большой-10.jpg',
              'backgrounds.previewSource gives the original size of the file');
            const sourceImage = await load(source.data.previewUrl);
            assert(sourceImage.naturalWidth === source.data.previewWidth && sourceImage.naturalHeight === source.data.previewHeight,
              'The source preview loads from the resource channel');
            const operationId = 'backgrounds-smoke-crop';
            const crop = {projectId, setId: set.data.id, selectionToken: token, operationId, sourceIndices: [0, 1, 2],
              crops: [{sourceIndex: 0, left: 20, top: 10}], blur: {minRadius: 1, maxRadius: 2, seed: 7}};
            const preview = await request('backgrounds.previewImport', crop);
            const samples = preview.data?.samples ?? [];
            assert(preview.ok && samples.length === 3 && preview.data.blur.seed === 7 && samples[0].crop.left === 20 && samples[0].imageUrl
              && samples[1].reason === 'backgrounds.wrong_size' && samples[1].message === `Размер ${width / 2}×${height / 2}, нужен ${width}×${height}`
              && samples[1].imageUrl === null && samples[2].imageUrl, 'backgrounds.previewImport returns samples with reasons in Russian');
            const sampleImage = await load(samples[0].imageUrl);
            assert(sampleImage.naturalWidth === width && sampleImage.naturalHeight === height, 'A sample is a PNG of exactly the set size');
            const badBlur = await request('backgrounds.import', {...crop, operationId: 'backgrounds-smoke-blur', blur: {minRadius: 3, maxRadius: 2}});
            assert(!badBlur.ok && badBlur.error.code === 'backgrounds.blur_invalid', 'A reversed blur range returns backgrounds.blur_invalid');
            const job = await request('backgrounds.import', crop);
            assert(job.ok && job.data.kind === 'backgrounds.import' && job.data.status === 'queued', 'backgrounds.import returns the queued job');
            const again = await request('backgrounds.import', crop);
            assert(again.ok && again.data.id === job.data.id, 'A repeated import of the same operation returns the same job');
            const other = await request('backgrounds.import', {...crop, operationId: 'backgrounds-smoke-other'});
            assert(!other.ok && other.error.code === 'backgrounds.selection_expired', 'A selection cannot be reused by another operation');
            const done = await finished(job.data.id);
            const summary = done.data.result;
            assert(done.data.status === 'succeeded' && summary.total === 3 && summary.processed === 3 && summary.added === 2 && summary.rejected === 1
              && summary.blur.seed === 7 && summary.targetSize.width === width && summary.issues[0].reason === 'backgrounds.wrong_size'
              && summary.issues[0].message.startsWith('Размер '), 'The run result with skip reasons arrives in job.changed');
            await wait(() => events.some(event => event.event === 'backgrounds.changed' && event.data.itemsChangedIn.includes(set.data.id)), 'itemsChangedIn');
            assert(true, 'backgrounds.changed reports the set whose backgrounds changed');
            const page = await request('backgrounds.list', {projectId, setId: set.data.id});
            const first = page.data?.items?.[0];
            assert(page.ok && page.data.items.length === 2 && page.data.nextCursor === null && first.origin.kind === 'image-crop'
              && first.origin.crop.left === 20 && first.origin.blurRadius > 0 && first.width === width && page.data.items[1].origin.kind === 'uploaded',
              'backgrounds.list returns the backgrounds with their origin');
            const thumbnail = await load(first.thumbnailUrl);
            assert(Math.max(thumbnail.naturalWidth, thumbnail.naturalHeight) === 256, 'The background thumbnail loads and fits 256 px');
            const original = await fetch(first.originalUrl, {cache: 'no-store'});
            assert(original.status === 200 && original.headers.get('content-type') === 'image/png' && (await original.arrayBuffer()).byteLength === first.byteSize,
              'The original URL serves the stored PNG');
            const removed = await request('backgrounds.setRemoved', {projectId, items: [{id: first.id, revision: first.revision}], removed: true});
            assert(removed.ok && removed.data[0].revision === 2 && removed.data[0].removedAt, 'backgrounds.setRemoved removes by the item revision');
            const stale = await request('backgrounds.setRemoved', {projectId, items: [{id: first.id, revision: 1}], removed: false});
            assert(!stale.ok && stale.error.code === 'backgrounds.revision_conflict', 'A stale item revision returns backgrounds.revision_conflict');
            const hidden = await request('backgrounds.list', {projectId, setId: set.data.id, removed: 'removed'});
            assert(hidden.ok && hidden.data.items.length === 1 && hidden.data.items[0].id === first.id, 'The removed filter lists removed backgrounds');
            const runs = await request('backgrounds.listRuns', {projectId, setId: set.data.id});
            assert(runs.ok && runs.data.length === 1 && runs.data[0].id === job.data.id, 'backgrounds.listRuns returns the history of the set');

            const video = await request('backgrounds.selectVideo', {});
            assert(video.ok && video.data.selectionToken && video.data.width === 640 && video.data.height === 360 && video.data.durationMs === 5000,
              'backgrounds.selectVideo reads the chosen video');
            const frame = await request('backgrounds.previewVideoFrame', {projectId, selectionToken: video.data.selectionToken, timestampMs: 2500});
            assert(frame.ok && frame.data.timestampMs === 2500 && frame.data.width === 640 && (await load(frame.data.previewUrl)).naturalWidth === frame.data.previewWidth,
              'backgrounds.previewVideoFrame gives a frame for the region');
            const videoRun = {projectId, setId: set.data.id, selectionToken: video.data.selectionToken, operationId: 'backgrounds-smoke-video',
              region: {left: 100, top: 50}, startMs: 0, intervalMs: 1000, count: 3, skipSimilar: true};
            const tooMany = await request('backgrounds.import', {...videoRun, count: 6});
            assert(!tooMany.ok && tooMany.error.code === 'backgrounds.count_invalid' && tooMany.error.message.startsWith('Количество'),
              'More backgrounds than frames returns backgrounds.count_invalid');
            const outside = await request('backgrounds.previewImport', {...videoRun, region: {left: 640, top: 0}});
            assert(!outside.ok && outside.error.code === 'backgrounds.region_invalid', 'A region outside the video returns backgrounds.region_invalid');
            const videoSamples = await request('backgrounds.previewImport', videoRun);
            assert(videoSamples.ok && videoSamples.data.samples.length === 3 && videoSamples.data.samples[0].timestampMs === 0 && videoSamples.data.blur === null,
              'Video samples are read by seeking');
            const videoJob = await request('backgrounds.import', videoRun);
            const videoDone = await finished(videoJob.data.id);
            assert(videoDone.data.status === 'succeeded' && videoDone.data.result.added + videoDone.data.result.similar >= 3 && videoDone.data.result.requestedCount === 3,
              'A video run adds frames of one region until the requested count');

            const review = await request('backgrounds.reviewSetDeletion', {projectId, setId: set.data.id});
            assert(review.ok && review.data.wholeSet && review.data.items === 2 + videoDone.data.result.added && review.data.blockers.length === 0 && review.data.setName === 'Улица',
              'backgrounds.reviewSetDeletion returns the composition');
            const deleted = await request('backgrounds.deleteSet', {projectId, setId: set.data.id, reviewRevision: review.data.revision});
            assert(deleted.ok && deleted.data.setDeleted, 'backgrounds.deleteSet deletes the set');
            const gone = await request('backgrounds.getSet', {projectId, setId: set.data.id});
            assert(!gone.ok && gone.error.code === 'backgrounds.set_not_found' && gone.error.message.includes('удалён'),
              'A deleted set returns backgrounds.set_not_found');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;
}
