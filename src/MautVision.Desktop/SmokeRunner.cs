using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.System;
using MautVision.Desktop.Resources;
using MautVision.Desktop.Transport;
using SkiaSharp;

namespace MautVision.Desktop;

/// <summary>The window's close guard and the number of system.close requests the smoke run received instead of closing.</summary>
internal sealed record SmokeCloseProbe(CloseGuard Guard, Func<int> CloseRequests);

internal static partial class SmokeRunner
{
    public static async Task<bool> RunAsync(CoreWebView2 web, string report, string root, ICore core, ResourceHandles handles,
        FileSelections selections, SmokeCloseProbe close, Action<bool> setMinimized, SmokeHostProbe host, SmokeLayoutProbe layout, CancellationToken token)
    {
        // Every job snapshot of the run: later checks delete some jobs (generation runs, dataset history), their labels stay known here.
        var jobsSeen = new System.Collections.Concurrent.ConcurrentDictionary<Guid, MautVision.Core.Interfaces.Jobs.JobInfo>();
        EventHandler<MautVision.Core.Interfaces.Jobs.JobChangedEventArgs> remember = (_, args) => jobsSeen[args.Job.Id] = args.Job;
        core.Jobs.JobChanged += remember;
        try
        {
            await web.ExecuteScriptAsync(Script);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var state = await web.ExecuteScriptAsync("JSON.stringify(window.__nativeSmoke ?? {done:false})");
                var serialized = JsonSerializer.Deserialize<string>(state);
                if (serialized is not null)
                {
                    using var result = JsonDocument.Parse(serialized);
                    if (result.RootElement.GetProperty("done").GetBoolean())
                    {
                        var passed = result.RootElement.GetProperty("success").GetBoolean();
                        if (passed)
                        {
                            var id = result.RootElement.GetProperty("projectId").GetString()!;
                            if (!Guid.TryParseExact(id, "D", out _) || !File.Exists(Path.Combine(root, "projects", id, "project.json")))
                            {
                                throw new InvalidOperationException("UI project was not persisted as project.json.");
                            }
                            await web.ExecuteScriptAsync("""
                                window.__hostEvent = null;
                                window.chrome.webview.addEventListener('message', event => {
                                    if (event.data?.kind === 'event' && event.data.event === 'project.opened') {
                                        window.__hostEvent = event.data;
                                    }
                                });
                                """);
                            await core.Projects.OpenAsync(id, token);
                            var delivered = false;
                            for (var attempt = 0; attempt < 50; attempt++)
                            {
                                var received = await web.ExecuteScriptAsync($"Boolean(window.__hostEvent?.data?.id === {JsonSerializer.Serialize(id)} && !('protocolVersion' in window.__hostEvent))");
                                if (received == "true") { delivered = true; break; }
                                await Task.Delay(100, token);
                            }
                            if (!delivered) throw new InvalidOperationException("Core event was not delivered without a frontend request.");
                            var final = JsonNode.Parse(serialized)!;
                            final["checks"]!.AsArray().Add("Core event delivered independently of a frontend request");
                            await RequireLogAsync(root, token);
                            final["checks"]!.AsArray().Add("Application log in the work directory has the startup record and UI errors");
                            await CheckResourcesAsync(web, root, id, handles, final, token);
                            await CheckCatalogAsync(web, root, id, selections, final, token);
                            await CheckCatalogUiAsync(web, root, id, selections, final, token);
                            await CheckImagesAsync(web, root, id, selections, final, token);
                            await CheckImagesUiAsync(web, root, id, selections, final, token);
                            await CheckCloseGuardAsync(web, close, final, token);
                            await CheckVideoAsync(web, root, id, final, token);
                            await CheckBackgroundsAsync(web, root, id, core, final, token);
                            await CheckBackgroundsUiAsync(web, root, id, final, token);
                            await CheckGenerationAsync(web, root, id, core, final, token);
                            await CheckGenerationUiAsync(web, root, id, core, final, token);
                            await CheckDatasetsAsync(web, root, id, core, final, token);
                            await CheckDatasetsUiAsync(web, root, id, core, final, token);
                            await CheckTrainingAsync(web, root, id, core, final, token);
                            await CheckTrainingUiAsync(web, id, final, token);
                            await CheckChecksAsync(web, root, id, core, final, token);
                            await CheckChecksUiAsync(web, root, id, final, token);
                            await CheckOverviewAsync(web, id, core, jobsSeen.Values.ToArray(), setMinimized, final, token);
                            await CheckOverviewUiAsync(web, id, final, token);
                            await CheckInsightsLanguageAsync(web, id, host, final, token);
                            await CheckLifecycleAsync(web, root, id, host, final, token);
                            await CheckLanguageAsync(web, root, id, host, final, token);
                            await CheckLayoutAsync(web, id, layout, final, token);
                            serialized = final.ToJsonString();
                            await using var image = File.Create(Path.ChangeExtension(report, ".png"));
                            await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                        }
                        await File.WriteAllTextAsync(report, serialized, token);
                        return passed;
                    }
                }
                await Task.Delay(100, token);
            }
            throw new TimeoutException("WebView2 smoke did not complete within 120 seconds.");
        }
        catch (Exception error)
        {
            WriteFailure(report, error);
            return false;
        }
        finally
        {
            core.Jobs.JobChanged -= remember;
        }
    }

    /// <summary>
    /// Resource channel from the real page: an issued original and thumbnail load in &lt;img&gt;, an unknown token is 404,
    /// Range returns the requested bytes, and tokens stop working after a reload. The fixture kind exists only in smoke mode.
    /// </summary>
    private static async Task CheckResourcesAsync(CoreWebView2 web, string root, string projectId, ResourceHandles handles, JsonNode report, CancellationToken token)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(root, "projects", projectId, "files", "smoke-fixture");
        Directory.CreateDirectory(directory);
        byte[] jpeg;
        using (var bitmap = new SKBitmap(640, 480))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.SteelBlue);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(320, 240, 150, paint);
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
            jpeg = data.ToArray();
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, id.ToString("D") + ".jpg"), jpeg, token);
        var reference = new ResourceReference("smoke-fixture", id);
        var original = handles.Issue(projectId, reference, ResourceVariant.Original);
        var thumbnail = handles.Issue(projectId, reference, ResourceVariant.Thumbnail);
        if (handles.Issue(projectId, reference, ResourceVariant.Original) != original)
        {
            throw new InvalidOperationException("Repeated issue of a fresh handle must return the same URL.");
        }
        var unknown = WebViewPolicy.ResourceOrigin + ResourceHandles.PathPrefix + "AAAAAAAAAAAAAAAAAAAAAA";
        var expected = Convert.ToBase64String(jpeg.AsSpan(10, 100));
        var second = Guid.NewGuid();
        await File.WriteAllBytesAsync(Path.Combine(directory, second.ToString("D") + ".jpg"), jpeg, token);
        var guarded = handles.Issue(projectId, new ResourceReference("smoke-fixture", second), ResourceVariant.Original);
        var arguments = JsonSerializer.Serialize(new { original, thumbnail, guarded, unknown, expected, length = jpeg.Length });
        var result = await RunScriptAsync(web, "(" + ResourceScript + ")(" + arguments + ")", token);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
        report["resourceLatency"] = result["latency"]!.DeepClone();

        // A reload starts a new page session: the old URL must not be served even though the file is unchanged.
        await web.ExecuteScriptAsync("window.__beforeReload = true; location.reload();");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(100, token);
            if (await web.ExecuteScriptAsync("window.__beforeReload === undefined && document.readyState === 'complete'") == "true") break;
            if (attempt == 100) throw new TimeoutException("UI did not reload.");
        }
        var stale = await RunScriptAsync(web, "(async url => { const reply = await fetch(url, {cache: 'no-store'}); return {status: reply.status}; })(" + JsonSerializer.Serialize(original) + ")", token);
        if (stale["status"]!.GetValue<int>() != 404)
        {
            throw new InvalidOperationException("A resource URL still worked after the page reload: " + stale["status"]);
        }
        checks.Add("Resource URL stops working after a page reload");
        var renewed = handles.Issue(projectId, reference, ResourceVariant.Original);
        var fresh = await RunScriptAsync(web, "(async url => { const reply = await fetch(url); return {status: reply.status}; })(" + JsonSerializer.Serialize(renewed) + ")", token);
        if (renewed == original || fresh["status"]!.GetValue<int>() != 200)
        {
            throw new InvalidOperationException("A URL issued after the reload must be new and work.");
        }
        checks.Add("A URL issued for the new page works");
    }

    /// <summary>
    /// Catalog API from the real page: variants, classes, bindings, the workspace, an import job started with a selection
    /// token (registered here instead of the native dialog), events, the asset page with a working thumbnail URL and error codes.
    /// </summary>
    private static async Task CheckCatalogAsync(CoreWebView2 web, string root, string projectId, FileSelections selections, JsonNode report,
        CancellationToken token)
    {
        var sources = Path.Combine(root, "smoke-sources");
        Directory.CreateDirectory(sources);
        var fixture = Path.Combine(sources, "объект.png");
        using (var bitmap = new SKBitmap(new SKImageInfo(640, 480, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { Color = SKColors.Orange };
                canvas.DrawCircle(320, 240, 150, paint);
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(fixture, data.ToArray(), token);
        }
        var selection = selections.Register([fixture]);
        var arguments = JsonSerializer.Serialize(new { projectId, selection });
        var result = await RunScriptAsync(web, "(" + CatalogScript + ")(" + arguments + ")", token, attempts: 300);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private const string CatalogScript = """
        async ({projectId, selection}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'catalog-smoke-' + (++sequence);
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 5000);
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
            for (let i = 0; i < 100; i++) { const value = query(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
            throw new Error('Timed out: ' + label);
          };
          try {
            const variant = await request('variants.create', {projectId, name: '  День  ', description: 'Светлый фон'});
            assert(variant.ok && variant.data.name === 'День' && variant.data.revision === 1 && variant.data.archivedAt === null, 'variants.create crosses the bridge');
            const duplicate = await request('variants.create', {projectId, name: 'ДЕНЬ', description: ''});
            assert(!duplicate.ok && duplicate.error.code === 'variants.name_duplicate' && mautLocale.errorText(duplicate.error.code, duplicate.error.details).includes('архиве'), 'Case-insensitive duplicate name returns variants.name_duplicate');
            const created = await request('classes.create', {projectId, name: 'Кошка', description: ''});
            assert(created.ok && created.data.sortOrder === 0, 'classes.create crosses the bridge');
            const binding = await request('classes.addVariant', {projectId, classId: created.data.id, variantId: variant.data.id});
            assert(binding.ok && binding.data.available && binding.data.color === null && /^#[0-9a-f]{6}$/.test(binding.data.displayColor), 'classes.addVariant returns an available binding with a default colour');
            const workspace = await request('classes.getWorkspace', {projectId});
            assert(workspace.ok && workspace.data.variants.length === 1 && workspace.data.classes.length === 1 && workspace.data.classVariants[0].id === binding.data.id
              && workspace.data.classOrderRevision >= 1 && workspace.data.catalogRevision > 1, 'classes.getWorkspace returns the catalog snapshot');
            await wait(() => events.some(event => event.event === 'catalog.changed' && event.data.projectId === projectId
              && event.data.classVariants.some(change => change.id === binding.data.id)), 'catalog.changed event');
            assert(true, 'catalog.changed names the new binding');
            const stale = await request('classes.reorder', {projectId, classIds: [created.data.id], orderRevision: workspace.data.classOrderRevision + 5});
            assert(!stale.ok && stale.error.code === 'classes.order_revision_conflict' && mautLocale.errorText(stale.error.code, stale.error.details).startsWith('Порядок классов изменился'), 'A stale order revision returns classes.order_revision_conflict');
            const color = await request('classes.setVariantColor', {projectId, classVariantId: binding.data.id, revision: binding.data.revision, color: 'red'});
            assert(!color.ok && color.error.code === 'classes.color_invalid', 'An invalid colour returns classes.color_invalid');
            const conflict = await request('classes.update', {projectId, classId: created.data.id, revision: 9, name: 'Кот', description: ''});
            assert(!conflict.ok && conflict.error.code === 'classes.revision_conflict', 'A stale class revision returns classes.revision_conflict');
            const expired = await request('classes.importAssets', {projectId, classVariantId: binding.data.id, selectionToken: 'unknown', operationId: 'smoke-import-bad'});
            assert(!expired.ok && expired.error.code === 'classes.selection_expired', 'An unknown selection token returns classes.selection_expired');
            const job = await request('classes.importAssets', {projectId, classVariantId: binding.data.id, selectionToken: selection, operationId: 'smoke-import'});
            assert(job.ok && job.data.kind === 'classes.import_assets' && job.data.status === 'queued' && job.data.result === null, 'classes.importAssets returns the queued job at once');
            const again = await request('classes.importAssets', {projectId, classVariantId: binding.data.id, selectionToken: selection, operationId: 'smoke-import'});
            assert(again.ok && again.data.id === job.data.id, 'A repeated import of the same operation returns the same job');
            const other = await request('classes.importAssets', {projectId, classVariantId: binding.data.id, selectionToken: selection, operationId: 'smoke-import-other'});
            assert(!other.ok && other.error.code === 'classes.selection_expired', 'A selection cannot be reused by another operation');
            const finished = await wait(() => events.find(event => event.event === 'job.changed' && event.data.id === job.data.id && event.data.status === 'succeeded'), 'import job.changed');
            const summary = finished.data.result;
            assert(summary && summary.total === 1 && summary.added === 1 && summary.duplicates === 0 && summary.rejected === 0 && Array.isArray(summary.rejectedFiles),
              'The import result arrives in job.changed');
            await wait(() => events.some(event => event.event === 'catalog.changed' && event.data.assetsChangedIn.includes(binding.data.id)), 'assets catalog.changed');
            assert(true, 'catalog.changed reports the binding whose images changed');
            const page = await request('classes.listAssets', {projectId, classVariantId: binding.data.id});
            const asset = page.data?.items?.[0];
            assert(page.ok && page.data.items.length === 1 && page.data.nextCursor === null && asset.format === 'png' && asset.width === 640 && asset.height === 480 && asset.hasAlpha,
              'classes.listAssets returns the imported image with size and alpha');
            const image = await new Promise((resolve, reject) => {
              const element = document.createElement('img');
              element.onload = () => resolve(element);
              element.onerror = () => reject(new Error('Thumbnail did not load'));
              element.src = asset.thumbnailUrl;
            });
            assert(image.naturalWidth === 256 && image.naturalHeight === 192, 'The asset thumbnail URL loads in <img> and fits 256 px');
            const original = await fetch(asset.originalUrl, {cache: 'no-store'});
            assert(original.status === 200 && original.headers.get('content-type') === 'image/png' && (await original.arrayBuffer()).byteLength === asset.byteSize,
              'The original URL serves the stored bytes');
            const refreshed = await request('classes.getWorkspace', {projectId});
            const counted = refreshed.data.classVariants[0];
            assert(counted.assetCount === 1 && counted.opaqueAssetCount === 0 && counted.maxAssetWidth === 640, 'Binding counters follow the import');
            const review = await request('classes.reviewVariantDeletion', {projectId, classVariantId: binding.data.id});
            assert(review.ok && review.data.targetKind === 'classVariant' && review.data.targetName === 'День' && review.data.classVariants === 1 && review.data.assets === 1
              && review.data.blockers.length === 0, 'classes.reviewVariantDeletion returns the composition');
            const mismatch = await request('classes.deleteVariant', {projectId, classVariantId: binding.data.id, reviewRevision: 'outdated'});
            assert(!mismatch.ok && mismatch.error.code === 'classes.deletion_revision_conflict', 'A wrong review revision returns classes.deletion_revision_conflict');
            const removed = await request('classes.deleteVariant', {projectId, classVariantId: binding.data.id, reviewRevision: review.data.revision});
            assert(removed.ok && removed.data.assets === 1 && removed.data.cleanupPending === false, 'classes.deleteVariant deletes the binding and its image');
            const gone = await request('classes.listAssets', {projectId, classVariantId: binding.data.id});
            assert(!gone.ok && gone.error.code === 'classes.not_found', 'A deleted binding returns classes.not_found');
            const invalid = await request('classes.getWorkspace', {projectId: 'not-a-guid'});
            assert(!invalid.ok && invalid.error.code === 'bridge.validation', 'An invalid project ID is rejected');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;

    /// <summary>
    /// Images API from the real page: groups, an import from a file selection and from a folder selection (registered
    /// here instead of the native dialogs), job.changed with the result, images.changed, the gallery page with a thumbnail
    /// URL that loads, annotations, error codes with Russian texts and deletion of a group.
    /// </summary>
    private static async Task CheckImagesAsync(CoreWebView2 web, string root, string projectId, FileSelections selections, JsonNode report,
        CancellationToken token)
    {
        var sources = Path.Combine(root, "smoke-images");
        var folder = Path.Combine(sources, "кадры");
        Directory.CreateDirectory(Path.Combine(folder, "вложенная"));
        byte[] Encode(int width, int height, SKColor color, SKEncodedImageFormat format)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(color);
                using var paint = new SKPaint { Color = SKColors.White };
                canvas.DrawRect(10, 10, width / 3f, height / 3f, paint);
            }
            using var data = bitmap.Encode(format, 90);
            return data.ToArray();
        }
        var photo = Path.Combine(sources, "кадр-1.jpg");
        await File.WriteAllBytesAsync(photo, Encode(640, 480, SKColors.SteelBlue, SKEncodedImageFormat.Jpeg), token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "frame2.png"), Encode(64, 48, SKColors.Orange, SKEncodedImageFormat.Png), token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "frame10.png"), Encode(64, 48, SKColors.Olive, SKEncodedImageFormat.Png), token);
        await File.WriteAllBytesAsync(Path.Combine(folder, "вложенная", "deep.png"), Encode(64, 48, SKColors.Teal, SKEncodedImageFormat.Png), token);
        var files = selections.Register([photo]);
        var directory = selections.Register([folder], FileSelectionKind.Folder);
        var arguments = JsonSerializer.Serialize(new { projectId, files, directory });
        var result = await RunScriptAsync(web, "(" + ImagesScript + ")(" + arguments + ")", token, attempts: 300);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private const string ImagesScript = """
        async ({projectId, files, directory}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'images-smoke-' + (++sequence);
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 5000);
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
            for (let i = 0; i < 150; i++) { const value = query(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
            throw new Error('Timed out: ' + label);
          };
          try {
            const group = await request('images.createGroup', {projectId, name: '  Улица  ', description: '', splitHint: 'train'});
            assert(group.ok && group.data.name === 'Улица' && group.data.origin === 'manual' && group.data.splitHint === 'train' && group.data.revision === 1
              && group.data.imageCount === 0 && group.data.generationState === null, 'images.createGroup crosses the bridge');
            const badSplit = await request('images.createGroup', {projectId, name: 'Кадры', description: '', splitHint: 'later'});
            assert(!badSplit.ok && badSplit.error.code === 'bridge.validation', 'An unknown split hint is rejected');
            const badName = await request('images.createGroup', {projectId, name: ' ', description: '', splitHint: 'test'});
            assert(!badName.ok && badName.error.code === 'images.name_invalid' && mautLocale.errorText(badName.error.code, badName.error.details).startsWith('Введите'), 'images.name_invalid has a Russian text');
            const stale = await request('images.updateGroup', {projectId, groupId: group.data.id, name: 'Двор', description: '', splitHint: 'test', revision: 7});
            assert(!stale.ok && stale.error.code === 'images.revision_conflict', 'A stale group revision returns images.revision_conflict');
            await wait(() => events.some(event => event.event === 'images.changed' && event.data.groups.some(change => change.id === group.data.id && change.revision === 1)),
              'images.changed for the new group');
            assert(true, 'images.changed names the created group');
            const expired = await request('images.import', {projectId, groupId: group.data.id, selectionToken: 'unknown', operationId: 'images-smoke-bad'});
            assert(!expired.ok && expired.error.code === 'images.selection_expired', 'An unknown selection returns images.selection_expired');
            const job = await request('images.import', {projectId, groupId: group.data.id, selectionToken: files, operationId: 'images-smoke-files'});
            assert(job.ok && job.data.kind === 'images.import' && job.data.status === 'queued', 'images.import returns the queued job');
            const folderJob = await request('images.import', {projectId, groupId: group.data.id, selectionToken: directory, operationId: 'images-smoke-folder', recursive: false});
            assert(folderJob.ok, 'images.import accepts a folder selection');
            const done = await wait(() => events.find(event => event.event === 'job.changed' && event.data.id === folderJob.data.id && event.data.status === 'succeeded'), 'folder import');
            await wait(() => events.find(event => event.event === 'job.changed' && event.data.id === job.data.id && event.data.status === 'succeeded'), 'file import');
            const summary = done.data.result;
            assert(summary && summary.total === 2 && summary.added === 2 && summary.duplicates === 0 && summary.rejected === 0 && Array.isArray(summary.rejectedFiles),
              'The folder import result arrives in job.changed (no recursion)');
            await wait(() => events.some(event => event.event === 'images.changed' && event.data.imagesChangedIn.includes(group.data.id)), 'imagesChangedIn');
            assert(true, 'images.changed reports the group whose images changed');
            const page = await request('images.list', {projectId, groupId: group.data.id});
            const names = page.data?.items?.map(item => item.displayName);
            assert(page.ok && page.data.nextCursor === null && names.length === 3 && names.includes('кадр-1.jpg')
              && names.indexOf('frame2.png') < names.indexOf('frame10.png'), 'images.list returns the imported images in natural order');
            const first = page.data.items.find(item => item.displayName === 'кадр-1.jpg');
            assert(first.width === 640 && first.height === 480 && first.format === 'jpeg' && first.annotationRevision === 1 && first.reviewState === 'unreviewed'
              && first.lineage.kind === 'file' && first.lineage.sourceFamilyId.startsWith('file:'), 'An image DTO carries size, revisions and lineage');
            const image = await new Promise((resolve, reject) => {
              const element = document.createElement('img');
              element.onload = () => resolve(element);
              element.onerror = () => reject(new Error('Thumbnail did not load'));
              element.src = first.thumbnailUrl;
            });
            assert(image.naturalWidth === 256 && image.naturalHeight === 192, 'The image thumbnail URL loads in <img> and fits 256 px');
            const original = await fetch(first.originalUrl, {cache: 'no-store'});
            assert(original.status === 200 && original.headers.get('content-type') === 'image/jpeg' && (await original.arrayBuffer()).byteLength === first.byteSize,
              'The original URL serves the stored bytes');
            const created = await request('classes.create', {projectId, name: 'Автомобиль', description: ''});
            const saved = await request('images.saveAnnotation', {projectId, imageId: first.id, annotationRevision: first.annotationRevision,
              objects: [{id: null, classId: created.data.id, classVariantId: null, box: {x: 0.1, y: 0.1, width: 0.5, height: 0.5}}],
              roi: {x: 0, y: 0, width: 1, height: 1}, reviewState: 'reviewed'});
            assert(saved.ok && saved.data.annotation.revision === 2 && saved.data.annotation.objects.length === 1 && saved.data.annotation.objects[0].id
              && saved.data.image.objectCount === 1 && saved.data.image.reviewState === 'reviewed', 'images.saveAnnotation saves a revision with a new object ID');
            const conflict = await request('images.saveAnnotation', {projectId, imageId: first.id, annotationRevision: 1, objects: [], roi: null, reviewState: 'reviewed'});
            assert(!conflict.ok && conflict.error.code === 'images.revision_conflict', 'A stale annotation revision returns images.revision_conflict');
            const outside = await request('images.saveAnnotation', {projectId, imageId: first.id, annotationRevision: 2,
              objects: [{id: null, classId: created.data.id, classVariantId: null, box: {x: 0.8, y: 0, width: 0.5, height: 0.5}}], roi: null, reviewState: 'reviewed'});
            assert(!outside.ok && outside.error.code === 'images.box_invalid' && mautLocale.errorText(outside.error.code, outside.error.details).includes('Рамка'), 'A box outside the frame returns images.box_invalid');
            const annotations = await request('images.listAnnotations', {projectId, imageIds: page.data.items.map(item => item.id)});
            assert(annotations.ok && annotations.data.length === 3 && annotations.data.find(item => item.imageId === first.id).objects[0].box.width === 0.5,
              'images.listAnnotations returns the annotations of a page');
            const blocked = await request('classes.reviewDeletion', {projectId, classId: created.data.id});
            assert(blocked.ok && blocked.data.blockers.some(item => item.code === 'images.annotations' && mautLocale.blockerText('catalog', item).includes('Улица / кадр-1.jpg')),
              'An annotated class is blocked with the image names');
            const imports = await request('images.listImports', {projectId, groupId: group.data.id});
            assert(imports.ok && imports.data.length === 2 && imports.data.every(item => item.kind === 'images.import'), 'images.listImports returns the history of the group');
            const review = await request('images.reviewGroupDeletion', {projectId, groupId: group.data.id});
            assert(review.ok && review.data.wholeGroup && review.data.images === 3 && review.data.blockers.length === 0 && review.data.groupName === 'Улица',
              'images.reviewGroupDeletion returns the composition');
            const removed = await request('images.deleteGroup', {projectId, groupId: group.data.id, reviewRevision: review.data.revision});
            assert(removed.ok && removed.data.groupDeleted && removed.data.images === 3, 'images.deleteGroup deletes the group');
            const gone = await request('images.list', {projectId, groupId: group.data.id});
            assert(!gone.ok && gone.error.code === 'images.group_not_found', 'A deleted group returns images.group_not_found');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;

    /// <summary>
    /// Frames from video through the bridge: a 5 s H.264 clip made with the shipped ffmpeg, images.selectVideo through the
    /// smoke answer of the video dialog (then a closed dialog), range errors with Russian texts that leave the token usable,
    /// an import of 5 frames with names and lineage, and their thumbnails.
    /// </summary>
    private static async Task CheckVideoAsync(CoreWebView2 web, string root, string projectId, JsonNode report, CancellationToken token)
    {
        var directory = Path.Combine(root, "smoke-video");
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "улица.mp4");
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=5",
            "-c:v", "libopenh264", "-g", "25", video })
            start.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Smoke video was not created: " + await errors);
        }
        MainWindow.SmokeVideoAnswer = video;
        var result = await RunScriptAsync(web, "(" + VideoScript + ")(" + JsonSerializer.Serialize(new { projectId }) + ")", token, attempts: 300);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
        // The same through the «Кадры из видео» panel; the page ends on it for the preview capture.
        MainWindow.SmokeVideoAnswer = video;
        result = await RunScriptAsync(web, "(" + VideoUiScript + ")()", token, attempts: 600);
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private const string VideoUiScript = """
        async () => {
          const checks = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const wait = async (query, label) => {
            for (let i = 0; i < 150; i++) { const value = query(); if (value) return value; await sleep(100); }
            throw new Error('Timed out: ' + label);
          };
          const $ = (selector, scope = document) => scope.querySelector(selector);
          const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
          const enabled = element => element && !element.disabled ? element : null;
          const setValue = (input, value) => {
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
            input.dispatchEvent(new Event('input', {bubbles: true}));
          };
          const tiles = () => $$('[data-testid="image-tile"]');
          const alertText = () => $('[data-testid="video-alert"]').textContent;

          if ($('[data-testid="annotation-editor"]')) {
            $('[data-testid="editor-back"]').click();
            await wait(() => !$('[data-testid="annotation-editor"]'), 'Editor closed');
          }
          setValue($('[data-testid="image-group-new-name"]'), 'Улица из видео');
          (await wait(() => enabled($('[data-testid="image-group-create"]')), 'Create enabled')).click();
          await wait(() => $('[data-testid="image-group-title"]')?.textContent === 'Улица из видео' && $('[data-testid="images-empty"]'), 'Video group selected');
          (await wait(() => enabled($('[data-testid="video-open"]')), 'Video button')).click();
          const panel = await wait(() => $('[data-testid="video-frame-import"]'), 'Video panel');
          assert(panel.textContent.includes('Кадры из видео') && $('[data-testid="video-info"]').textContent === 'Выберите видео, затем задайте какие кадры извлечь.'
            && $('[data-testid="video-extract"]').disabled && $('[data-testid="video-start"]').disabled, '«Кадры из видео» opens a panel waiting for a video');
          $('[data-testid="video-choose"]').click();
          await wait(() => $('[data-testid="video-info"]').textContent.startsWith('улица.mp4'), 'Video chosen');
          assert($('[data-testid="video-info"]').textContent === 'улица.mp4 · 640 × 360 · 5.0 с' && $('[data-testid="video-start"]').value === '0'
            && $('[data-testid="video-interval"]').value === '1' && $('[data-testid="video-count"]').value === '5'
            && $('[data-testid="video-choose"]').textContent === 'Другое видео', 'The chosen video shows its size and duration with default fields');
          setValue($('[data-testid="video-count"]'), '6');
          await wait(() => alertText() === 'Последний кадр выходит за длительность видео. Уменьшите количество или интервал.', 'Range alert');
          assert($('[data-testid="video-extract"]').disabled && $('[data-testid="video-alert"]').getAttribute('role') === 'alert',
            'A count past the end shows the alert and disables «Извлечь кадры»');
          setValue($('[data-testid="video-count"]'), '5');
          (await wait(() => alertText() === '' && enabled($('[data-testid="video-extract"]')), 'Range valid')).click();
          await wait(() => $$('[data-testid="import-summary"]').some(item => item.textContent.includes('Добавлено 5 / 5 · пропущено 0 · ошибок 0')), 'Video import result');
          await wait(() => tiles().length === 5 && tiles().every(tile => tile.querySelector('[data-status="loaded"] img')?.naturalWidth > 0), 'Frame thumbnails');
          assert(tiles()[0].textContent.includes('улица.mp4-frame-0.png') && tiles()[4].textContent.includes('улица.mp4-frame-4000.png')
            && tiles()[0].textContent.includes('640 × 360'), 'Five frames appear in the gallery with thumbnails');
          assert($('[data-testid="video-info"]').textContent.startsWith('Задача запущена') && $('[data-testid="video-extract"]').disabled,
            'After the start the panel asks for the video again');
          await sleep(300);
          return {checks};
        }
        """;

    /// <summary>
    /// Window close guard from the host side: the page answers app.closeRequested at once when nothing is unsaved; with an
    /// unsaved annotation it refuses and shows its dialog, «Продолжить разметку» keeps the draft, «Уйти без сохранения»
    /// asks the host to close; a page that does not answer (its thread busy) lets the window close after the timeout.
    /// </summary>
    private static async Task CheckCloseGuardAsync(CoreWebView2 web, SmokeCloseProbe close, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        async Task<bool> Script(string expression) => await web.ExecuteScriptAsync("Boolean(" + expression + ")") == "true";
        async Task Until(string expression, string label)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (await Script(expression)) return;
                await Task.Delay(100, token);
            }
            throw new TimeoutException("Timed out: " + label);
        }
        const string Guard = "document.querySelector('[data-testid=\"unsaved-guard\"]')";
        const string Status = "document.querySelector('[data-testid=\"editor-status\"]')?.textContent";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Check(await close.Guard.AskAsync(TimeSpan.FromSeconds(5)) && watch.Elapsed < TimeSpan.FromSeconds(1) && !await Script(Guard),
            "Without unsaved changes the page allows closing the window at once");
        await web.ExecuteScriptAsync("document.querySelector('[data-testid=\"editor-reviewed\"]').click()");
        await Until(Status + " === 'Есть несохранённые изменения'", "Dirty editor");
        Check(!await close.Guard.AskAsync(TimeSpan.FromSeconds(5)), "An unsaved annotation refuses the window close");
        await Until(Guard, "Guard dialog");
        await web.ExecuteScriptAsync("[...document.querySelectorAll('[data-testid=\"unsaved-guard\"] button')].find(item => item.textContent.trim() === 'Продолжить разметку').click()");
        await Until("!" + Guard + " && " + Status + " === 'Есть несохранённые изменения'", "Guard kept the draft");
        Check(close.CloseRequests() == 0, "«Продолжить разметку» keeps the window and the draft");
        Check(!await close.Guard.AskAsync(TimeSpan.FromSeconds(5)), "A second close attempt asks again");
        await Until(Guard, "Guard dialog again");
        await web.ExecuteScriptAsync("document.querySelector('[data-testid=\"unsaved-leave\"]').click()");
        for (var attempt = 0; attempt < 50 && close.CloseRequests() == 0; attempt++) await Task.Delay(100, token);
        await Until("!" + Guard + " && " + Status + " !== 'Есть несохранённые изменения'", "Draft dropped");
        Check(close.CloseRequests() == 1, "«Уйти без сохранения» drops the draft and asks the host to close (system.close)");
        // A page whose thread is busy cannot answer; the window closes after the timeout instead of waiting.
        var busy = web.ExecuteScriptAsync("(() => { const end = Date.now() + 2500; while (Date.now() < end) {} return 1; })()");
        await Task.Delay(200, token);
        watch.Restart();
        var allowed = await close.Guard.AskAsync(CloseGuard.AnswerTimeout);
        Check(allowed && watch.Elapsed >= TimeSpan.FromMilliseconds(900) && watch.Elapsed < TimeSpan.FromSeconds(2),
            $"An unanswered close request allows the close after {CloseGuard.AnswerTimeout.TotalSeconds:0} s");
        await busy;
    }

    private const string VideoScript = """
        async ({projectId}) => {
          const checks = [];
          const events = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const listen = event => { if (event.data?.kind === 'event') events.push(event.data); };
          window.chrome.webview.addEventListener('message', listen);
          let sequence = 0;
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'video-smoke-' + (++sequence);
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 10000);
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
            for (let i = 0; i < 250; i++) { const value = query(); if (value) return value; await new Promise(resolve => setTimeout(resolve, 100)); }
            throw new Error('Timed out: ' + label);
          };
          try {
            const selected = await request('images.selectVideo', {});
            const video = selected.data;
            assert(selected.ok && typeof video.selectionToken === 'string' && video.displayName === 'улица.mp4' && video.width === 640 && video.height === 360
              && video.durationMs === 5000 && video.byteSize > 0, 'images.selectVideo returns the probed video without its path');
            const closed = await request('images.selectVideo', {});
            assert(closed.ok && closed.data.selectionToken === null && closed.data.durationMs === null, 'A closed video dialog returns a null token');
            const group = await request('images.createGroup', {projectId, name: 'Кадры видео', description: '', splitHint: 'unassigned'});
            const groupId = group.data.id;
            const missing = await request('images.import', {projectId, groupId, selectionToken: video.selectionToken, operationId: 'video-smoke-missing'});
            assert(!missing.ok && missing.error.code === 'video.frames_invalid' && mautLocale.errorText(missing.error.code, missing.error.details).startsWith('Укажите'), 'A video import without a range returns video.frames_invalid');
            const beyond = await request('images.import', {projectId, groupId, selectionToken: video.selectionToken, operationId: 'video-smoke-beyond',
              startMs: 0, intervalMs: 1000, count: 6});
            assert(!beyond.ok && beyond.error.code === 'video.range_invalid'
              && mautLocale.errorText(beyond.error.code, beyond.error.details) === 'Последний кадр выходит за длительность видео. Уменьшите количество или интервал.',
              'A range past the end returns video.range_invalid with the Russian text');
            const job = await request('images.import', {projectId, groupId, selectionToken: video.selectionToken, operationId: 'video-smoke-import',
              startMs: 0, intervalMs: 1000, count: 5});
            assert(job.ok && job.data.kind === 'images.import', 'A rejected range leaves the selection usable for the corrected import');
            const done = await wait(() => events.find(event => event.event === 'job.changed' && event.data.id === job.data.id
              && ['succeeded', 'failed', 'cancelled'].includes(event.data.status)), 'video import');
            assert(done.data.status === 'succeeded' && done.data.result.total === 5 && done.data.result.added === 5, 'The video import adds 5 frames');
            const page = await request('images.list', {projectId, groupId});
            const items = page.data.items;
            assert(items.length === 5 && items.every((item, index) => item.displayName === `улица.mp4-frame-${index * 1000}.png` && item.format === 'png'
              && item.width === 640 && item.height === 360 && item.lineage.kind === 'video' && item.lineage.videoName === 'улица.mp4'
              && item.lineage.timestampMs === index * 1000 && item.lineage.frameIndex === index && /^video:[0-9a-f]{64}$/.test(item.lineage.sourceFamilyId)),
              'Frames carry names, native size and video lineage');
            const sizes = await Promise.all(items.map(item => new Promise((resolve, reject) => {
              const element = document.createElement('img');
              element.onload = () => resolve([element.naturalWidth, element.naturalHeight]);
              element.onerror = () => reject(new Error('Frame thumbnail did not load'));
              element.src = item.thumbnailUrl;
            })));
            assert(sizes.every(([width, height]) => width === 256 && height === 144), 'Frame thumbnails load in <img>');
            const reused = await request('images.import', {projectId, groupId, selectionToken: video.selectionToken, operationId: 'video-smoke-other',
              startMs: 0, intervalMs: 1000, count: 1});
            assert(!reused.ok && reused.error.code === 'images.selection_expired' && mautLocale.errorText(reused.error.code, reused.error.details).includes('видео'),
              'A used video selection does not start another operation');
            return {checks};
          } finally {
            window.chrome.webview.removeEventListener('message', listen);
          }
        }
        """;

    /// <summary>
    /// «Варианты» и «Классы» through the real UI: dialogs, list index and order, archive, colour, deletion review, the
    /// thumbnail grid and per-image deletion. The native file dialog is never shown in smoke mode, so the import starts
    /// with a selection token registered here for the binding the UI created; the grid then follows catalog.changed.
    /// The page ends on the Classes section with a selected class and its images for the preview capture.
    /// </summary>
    private static async Task CheckCatalogUiAsync(CoreWebView2 web, string root, string projectId, FileSelections selections, JsonNode report,
        CancellationToken token)
    {
        var sources = Path.Combine(root, "smoke-ui-sources");
        Directory.CreateDirectory(sources);
        var transparent = Path.Combine(sources, "кот.png");
        var small = Path.Combine(sources, "малый.png");
        var opaque = Path.Combine(sources, "фото.jpg");
        await WriteFixtureAsync(transparent, 640, 480, SKEncodedImageFormat.Png, token);
        await WriteFixtureAsync(small, 120, 120, SKEncodedImageFormat.Png, token);
        await WriteFixtureAsync(opaque, 300, 200, SKEncodedImageFormat.Jpeg, token);
        var selection = selections.Register([transparent, small, opaque]);
        var arguments = JsonSerializer.Serialize(new { projectId, selection });
        var result = await RunScriptAsync(web, "(" + CatalogUiScript + ")(" + arguments + ")", token, attempts: 400);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private static async Task WriteFixtureAsync(string path, int width, int height, SKEncodedImageFormat format, CancellationToken token)
    {
        var alpha = format == SKEncodedImageFormat.Png ? SKAlphaType.Premul : SKAlphaType.Opaque;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, alpha));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(format == SKEncodedImageFormat.Png ? SKColors.Transparent : SKColors.SteelBlue);
            using var paint = new SKPaint { Color = format == SKEncodedImageFormat.Png ? SKColors.Orange : SKColors.Gold, IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) * 0.4f, paint);
        }
        using var data = bitmap.Encode(format, 90);
        await File.WriteAllBytesAsync(path, data.ToArray(), token);
    }

    private const string CatalogUiScript = """
        async ({projectId, selection}) => {
          const checks = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const wait = async (query, label, describe) => {
            for (let i = 0; i < 100; i++) { const value = query(); if (value) return value; await sleep(100); }
            throw new Error('Timed out: ' + label + (describe ? ' (' + describe() + ')' : ''));
          };
          const $ = (selector, scope = document) => scope.querySelector(selector);
          const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
          const labelled = label => $('[aria-label="' + label + '"]');
          const enabled = element => element && !element.disabled ? element : null;
          const setValue = (input, value) => {
            const prototype = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(prototype, 'value').set.call(input, value);
            input.dispatchEvent(new Event('input', {bubbles: true}));
          };
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'catalog-ui-smoke-' + crypto.randomUUID();
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 5000);
            const reply = event => {
              if (event.data?.kind !== 'response' || event.data.requestId !== requestId) return;
              clearTimeout(timeout);
              window.chrome.webview.removeEventListener('message', reply);
              resolve(event.data);
            };
            window.chrome.webview.addEventListener('message', reply);
            window.chrome.webview.postMessage({requestId, method, payload});
          });
          const row = (kind, name) => $$('[data-' + kind + '-id]').find(item => item.querySelector('button span')?.textContent.endsWith(name));
          const index = name => row('class', name)?.querySelector('[data-class-index]')?.textContent;
          const createEntry = async (name, description) => {
            enabled($('[data-testid="catalog-add"]')).click();
            const dialog = await wait(() => $('[data-testid="catalog-entry-dialog"]'), 'Entry dialog opened');
            setValue($('[data-testid="catalog-name"]', dialog), name);
            setValue($('#catalog-description', dialog), description);
            (await wait(() => enabled($('[data-testid="catalog-save"]', dialog)), 'Save enabled')).click();
            return dialog;
          };

          (await wait(() => enabled($('[data-testid="open-project"]')), 'Library lists the project')).click();
          await wait(() => $('[data-testid="active-project-name"]'), 'Project opened');
          (await wait(() => enabled($('[data-section="variants"]')), 'Variants section enabled')).click();
          await wait(() => row('variant', 'День'), 'Variants list shows the catalog');
          assert(document.body.textContent.includes('Общие варианты отображения объектов для всего проекта.'), 'Variants section opens with the existing variant');

          await createEntry('Ночь', 'Тёмный фон');
          await wait(() => !$('[data-testid="catalog-entry-dialog"]') && row('variant', 'Ночь'), 'Created variant listed');
          assert(row('variant', 'Ночь').textContent.includes('Тёмный фон'), 'Variant created through the dialog');
          const duplicate = await createEntry('НОЧЬ', '');
          const error = await wait(() => $('[data-testid="catalog-dialog-error"]', duplicate), 'Duplicate error shown');
          assert(error.textContent.includes('Такое название уже существует'), 'Duplicate name error is shown inside the dialog');
          [...duplicate.querySelectorAll('button')].find(item => item.textContent === 'Отмена').click();
          await wait(() => !$('[data-testid="catalog-entry-dialog"]'), 'Dialog closed');

          $('[data-section="classes"]').click();
          await wait(() => row('class', 'Кошка'), 'Classes list shows the catalog');
          await createEntry('Собака', '');
          await wait(() => !$('[data-testid="catalog-entry-dialog"]') && row('class', 'Собака'), 'Created class listed');
          assert(index('Кошка') === '0' && index('Собака') === '1', 'Class list shows indices among active classes from 0');
          row('class', 'Собака').querySelector('button').click();
          const content = await wait(() => $('[data-testid="class-content"] [data-selected-class-id]') && $('[data-testid="class-content"] h2')?.textContent.startsWith('Собака') && $('[data-testid="class-content"]'), 'Class selected');
          assert(content.textContent.includes('Подключите общий вариант проекта'), 'Selected class without variants shows the hint');

          enabled($('[data-testid="attach-variant-open"]')).click();
          const attach = await wait(() => $('[data-testid="attach-variant-dialog"]'), 'Attach dialog opened');
          $('[data-testid="attach-variant"]', attach).click();
          (await wait(() => $$('[role="option"]').find(item => item.textContent.includes('Ночь')), 'Variant options shown')).click();
          // The listbox closes asynchronously; submitting while it is still open could be taken as a click outside it.
          await wait(() => !$('[role="listbox"]') && $('[data-testid="attach-variant"]', attach).textContent.includes('Ночь'), 'Variant chosen');
          (await wait(() => enabled($('[data-testid="attach-save"]', attach)), 'Attach enabled')).click();
          // Queried afresh: the class content may re-render when catalog.changed arrives alongside the reply.
          const section = await wait(() => !$('[data-testid="attach-variant-dialog"]') && $('[data-testid="class-content"] [data-class-variant-id]'),
            'Binding section shown', () => 'dialog ' + ($('[data-testid="attach-variant-dialog"]') ? 'open: ' + $('[data-testid="attach-variant-dialog"]').textContent : 'closed')
              + '; class content: ' + $('[data-testid="class-content"]')?.textContent.slice(0, 200));
          assert(section.querySelector('h3').textContent === 'Ночь' && $('[data-testid="asset-count"]', section).textContent === '0 изображений'
            && section.textContent.includes('Добавьте PNG с прозрачным фоном'), 'Variant attached through the dialog with an empty grid');

          labelled('Выше: Собака').click();
          await wait(() => index('Собака') === '0' && index('Кошка') === '1', 'Order changed');
          assert(labelled('Выше: Собака').disabled && !labelled('Ниже: Собака').disabled, 'Arrows reorder classes and the first cannot move up');
          setValue($('[data-testid="catalog-search"]'), 'соб');
          await wait(() => !row('class', 'Кошка'), 'Search filters');
          assert(labelled('Ниже: Собака').disabled, 'Arrows are disabled while a search filter is active');
          setValue($('[data-testid="catalog-search"]'), '');
          await wait(() => row('class', 'Кошка'), 'Search cleared');

          labelled('В архив: Кошка').click();
          const confirm = await wait(() => $('[data-testid="archive-confirm"]'), 'Archive confirmation');
          assert(confirm.textContent.includes('«Кошка» будет скрыт из рабочих списков'), 'Archive asks for confirmation');
          $('[data-testid="archive-confirm-action"]').click();
          await wait(() => !$('[data-testid="archive-confirm"]') && !row('class', 'Кошка'), 'Archived class hidden');
          $('[data-testid="catalog-show-archive"]').click();
          const archived = await wait(() => row('class', 'Кошка'), 'Archive shown');
          assert(archived.dataset.archived === 'true' && archived.textContent.includes('В архиве') && index('Кошка') === undefined, 'Archived class is shown with the archive mark and without an index');
          labelled('Восстановить: Кошка').click();
          await wait(() => row('class', 'Кошка') && !row('class', 'Кошка').dataset.archived && index('Кошка') === '1', 'Class restored');
          assert(true, 'Archived class is restored');
          $('[data-testid="catalog-show-archive"]').click();

          const color = $('input[type="color"]', section);
          setValue(color, '#00ff00');
          await wait(() => labelled('Сбросить цвет: Ночь'), 'Colour saved after the debounce');
          labelled('Сбросить цвет: Ночь').click();
          await wait(() => !labelled('Сбросить цвет: Ночь'), 'Colour reset');
          assert(true, 'Colour is saved after 400 ms and reset to the default');

          const classVariantId = section.dataset.classVariantId;
          const job = await request('classes.importAssets', {projectId, classVariantId, selectionToken: selection, operationId: crypto.randomUUID()});
          assert(job.ok, 'Fixture import started for the binding created in the UI');
          const loaded = await wait(() => {
            const figures = $$('[data-class-asset-id]', section);
            return figures.length === 3 && figures.every(item => item.querySelector('[data-status="loaded"] img')?.naturalWidth > 0) && figures;
          }, 'Thumbnails rendered');
          assert(loaded.some(item => item.textContent.includes('640 × 480 px')), 'Grid renders resource thumbnails after catalog.changed');
          assert($('[data-testid="asset-count"]', section).textContent === '3 изображения', 'Badge counts images with the Russian plural');
          const opaque = loaded.find(item => item.textContent.includes('300 × 200 px'));
          assert(opaque.textContent.includes('Без прозрачности') && !loaded.find(item => item.textContent.includes('120 × 120 px')).textContent.includes('Без прозрачности'),
            'Images without alpha are marked');
          assert($('[data-testid="scale-warning"]', section)?.textContent.includes('320 × 256 px'), 'Scale warning uses the project input size');

          labelled('Удалить класс: Собака').focus();
          labelled('Удалить класс: Собака').click();
          const review = await wait(() => $('[data-testid="deletion-counts"]'), 'Deletion review');
          const count = name => $('[data-count="' + name + '"]', review).textContent;
          assert(count('classes') === '1' && count('classVariants') === '1' && count('assets') === '3', 'Deletion dialog shows the composition counts');
          [...$('[data-testid="deletion-dialog"]').querySelectorAll('button')].find(item => item.textContent === 'Отмена').click();
          await wait(() => !$('[data-testid="deletion-dialog"]'), 'Deletion dialog closed');
          await wait(() => document.activeElement === labelled('Удалить класс: Собака'), 'Focus returned');
          assert(true, 'Focus returns to the control that opened the dialog');

          labelled('Удалить изображение: Собака / Ночь · 120 × 120 px').click();
          await wait(() => $('[data-testid="deletion-counts"] [data-count="assets"]')?.textContent === '1', 'Image deletion review');
          (await wait(() => enabled($('[data-testid="deletion-confirm"]')), 'Delete enabled')).click();
          await wait(() => !$('[data-testid="deletion-dialog"]') && $$('[data-class-asset-id]', section).length === 2
            && $('[data-testid="asset-count"]', section).textContent === '2 изображения', 'Image deleted');
          assert(true, 'Image deleted through review and confirmation');
          await wait(() => $$('[data-class-asset-id] [data-status="loaded"]', section).length === 2, 'Final thumbnails');
          await sleep(300);
          return {checks};
        }
        """;

    /// <summary>
    /// «Изображения» through the real UI: a group created in the form, an import from a selection registered here (the
    /// native dialog is never shown in smoke mode, the button only proves a cancelled choice is harmless), the gallery,
    /// group editing, the annotation editor driven by pointer events (draw, class change, move, resize, undo/redo, Ctrl+S),
    /// the unsaved changes guard, the shared viewer (x2, fit, fullscreen, Esc, focus return), Shift range selection with
    /// deletion and group archive. The page ends in the editor showing a box for the preview capture.
    /// </summary>
    private static async Task CheckImagesUiAsync(CoreWebView2 web, string root, string projectId, FileSelections selections, JsonNode report,
        CancellationToken token)
    {
        var sources = Path.Combine(root, "smoke-images-ui");
        Directory.CreateDirectory(sources);
        SKColor[] colors = [SKColors.SteelBlue, SKColors.DarkOliveGreen, SKColors.SlateGray, SKColors.Sienna];
        var files = new List<string>();
        for (var index = 0; index < colors.Length; index++)
        {
            using var bitmap = new SKBitmap(640, 480);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(colors[index]);
                using var paint = new SKPaint { Color = SKColors.Gold, IsAntialias = true };
                canvas.DrawRect(160 + index * 40, 120, 200, 160, paint);
            }
            using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
            var path = Path.Combine(sources, $"двор-{index + 1}.jpg");
            await File.WriteAllBytesAsync(path, data.ToArray(), token);
            files.Add(path);
        }
        var selection = selections.Register(files);
        var arguments = JsonSerializer.Serialize(new { projectId, selection });
        var result = await RunScriptAsync(web, "(" + ImagesUiScript + ")(" + arguments + ")", token, attempts: 600);
        var checks = report["checks"]!.AsArray();
        foreach (var check in result["checks"]!.AsArray())
        {
            checks.Add(check!.GetValue<string>());
        }
    }

    private const string ImagesUiScript = """
        async ({projectId, selection}) => {
          const checks = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const wait = async (query, label) => {
            for (let i = 0; i < 100; i++) { const value = query(); if (value) return value; await sleep(100); }
            throw new Error('Timed out: ' + label);
          };
          const $ = (selector, scope = document) => scope.querySelector(selector);
          const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
          const labelled = label => $('[aria-label="' + label + '"]');
          const enabled = element => element && !element.disabled ? element : null;
          const button = (text, scope = document) => $$('button', scope).find(item => item.textContent.trim() === text);
          const setValue = (input, value) => {
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
            input.dispatchEvent(new Event('input', {bubbles: true}));
          };
          const choose = (select, value) => {
            Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(select, value);
            select.dispatchEvent(new Event('change', {bubbles: true}));
          };
          const key = (target, key, options = {}) => target.dispatchEvent(new KeyboardEvent('keydown', {key, bubbles: true, cancelable: true, ...options}));
          const pick = async (trigger, text) => {
            trigger.click();
            (await wait(() => $$('[role="option"]').find(item => item.textContent.includes(text)), 'Option ' + text)).click();
            await wait(() => !$('[role="listbox"]'), 'Select closed');
          };
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'images-ui-smoke-' + crypto.randomUUID();
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 5000);
            const reply = event => {
              if (event.data?.kind !== 'response' || event.data.requestId !== requestId) return;
              clearTimeout(timeout);
              window.chrome.webview.removeEventListener('message', reply);
              resolve(event.data);
            };
            window.chrome.webview.addEventListener('message', reply);
            window.chrome.webview.postMessage({requestId, method, payload});
          });
          const frame = () => $('[data-annotation-canvas]');
          const at = (x, y) => { const rect = frame().getBoundingClientRect(); return {clientX: rect.left + x * rect.width, clientY: rect.top + y * rect.height}; };
          const pointer = (target, type, point) => target.dispatchEvent(new PointerEvent(type, {bubbles: true, cancelable: true, composed: true, pointerId: 7, isPrimary: true,
            pointerType: 'mouse', button: type === 'pointermove' ? -1 : 0, buttons: type === 'pointerup' ? 0 : 1, ...point}));
          const drag = async (target, from, to) => {
            pointer(target, 'pointerdown', at(...from));
            await sleep(30);
            pointer(target, 'pointermove', at((from[0] + to[0]) / 2, (from[1] + to[1]) / 2));
            await sleep(30);
            pointer(target, 'pointermove', at(...to));
            await sleep(30);
            pointer(target, 'pointerup', at(...to));
            await sleep(80);
          };
          const objectBox = () => $('[data-box-id]:not([data-box-id="roi"])', frame());
          const near = (value, expected) => Math.abs(parseFloat(value) - expected) < 0.6;
          const status = () => $('[data-testid="editor-status"]')?.textContent;
          const tiles = () => $$('[data-testid="image-tile"]');

          (await wait(() => enabled($('[data-section="images"]')), 'Images section enabled')).click();
          await wait(() => $('[data-testid="images-section"]') && !$('[data-testid="image-groups"] [role="status"]'), 'Images section opened');
          assert(document.body.textContent.includes('Кадры для просмотра и разметки')
            && $('[data-testid="image-groups"]').textContent.includes('Создайте набор кадров: чистые фоны или изображения с разметкой.')
            && $('[data-testid="image-group-content"]').textContent.includes('Создайте группу для импорта кадров и разметки.'),
            'Images section opens with the empty groups panel');

          setValue($('[data-testid="image-group-new-name"]'), 'Двор');
          (await wait(() => enabled($('[data-testid="image-group-create"]')), 'Create enabled')).click();
          const groupButton = await wait(() => $$('[data-image-group-id]').find(item => item.textContent.startsWith('Двор') && item.getAttribute('aria-pressed') === 'true'), 'Group created and selected');
          const groupId = groupButton.dataset.imageGroupId;
          assert(groupButton.textContent.includes('Исходные изображения · Без назначения') && $('[data-testid="image-group-title"]').textContent === 'Двор'
            && $('[data-testid="images-empty"]').textContent.includes('Добавьте готовые кадры или кадры из видео'), 'Group created through the form shows the empty gallery');

          button('Импорт файлов').click();
          await wait(() => enabled(button('Импорт файлов')), 'Cancelled selection');
          assert(!$('[data-testid="image-imports"] [role="alert"]') && !tiles().length, 'A cancelled file choice starts no import');
          assert($('[data-testid="import-skip-duplicates"]').getAttribute('aria-checked') === 'true' && $('[data-testid="import-recursive"]').getAttribute('aria-checked') === 'false',
            'Import options default to skipping duplicates without subfolders');

          const job = await request('images.import', {projectId, groupId, selectionToken: selection, operationId: crypto.randomUUID(), skipDuplicates: true});
          assert(job.ok, 'Fixture import started for the group created in the UI');
          await wait(() => tiles().length === 4 && tiles().every(tile => tile.querySelector('[data-status="loaded"] img')?.naturalWidth > 0), 'Thumbnails rendered');
          assert($('[data-testid="image-group-count"]').textContent === 'Кадров: 4' && tiles()[0].textContent.includes('640 × 480 · без рамок'),
            'Gallery shows thumbnails with counts after images.changed');
          await wait(() => $('[data-testid="import-summary"]')?.textContent.includes('Добавлено 4 / 4 · пропущено 0 · ошибок 0'), 'Import history');
          assert($$('[data-testid="image-imports"] [data-job-id]').length === 1, 'Import history of the group shows the result from job.changed');

          labelled('Изменить группу: Двор').click();
          const groupDialog = await wait(() => $('[data-testid="image-group-dialog"]'), 'Group dialog');
          setValue($('[data-testid="image-group-name"]', groupDialog), 'Двор и улица');
          await pick($('[data-testid="image-group-split"]', groupDialog), 'Обучение');
          $('[data-testid="image-group-save"]', groupDialog).click();
          await wait(() => !$('[data-testid="image-group-dialog"]') && $('[data-testid="image-group-title"]').textContent === 'Двор и улица', 'Group renamed');
          assert($('[data-image-group-id="' + groupId + '"]').textContent.includes('Исходные изображения · Обучение'), 'Group name and purpose change through the dialog');

          const workspace = await request('classes.getWorkspace', {projectId});
          const carId = workspace.data.classes.find(item => item.name === 'Автомобиль').id;
          const firstId = tiles()[0].dataset.imageId;
          $('[data-testid="image-open"]', tiles()[0]).click();
          await wait(() => $('[data-testid="annotation-editor"]') && frame()?.querySelector('[data-status="loaded"] img')?.naturalWidth > 0 && status() === 'Не проверено', 'Editor opened');
          assert($('[data-testid="annotation-editor"]').textContent.includes('Разметка · двор-1.jpg'), 'Tile opens the annotation editor');

          await drag(frame(), [0.2, 0.2], [0.5, 0.6]);
          const drawn = await wait(() => objectBox(), 'Box drawn');
          assert(near(drawn.style.left, 20) && near(drawn.style.top, 20) && near(drawn.style.width, 30) && near(drawn.style.height, 40)
            && status() === 'Есть несохранённые изменения' && drawn.dataset.selected === 'true', 'Pointer drag draws a selected box at the pointer position');
          await pick($('[data-testid="editor-class"]'), 'Автомобиль');
          await wait(() => objectBox().textContent === '1. Автомобиль', 'Class changed');
          assert($('[data-testid="editor-variant"]').textContent.includes('Без варианта'), 'Class of the selected box changes through the select');

          await drag(objectBox(), [0.35, 0.4], [0.45, 0.5]);
          assert(near(objectBox().style.left, 30) && near(objectBox().style.top, 30) && near(objectBox().style.width, 30), 'Dragging a box moves it');
          await drag($('[data-handle="se"]', frame()), [0.6, 0.7], [0.8, 0.9]);
          assert(near(objectBox().style.width, 50) && near(objectBox().style.height, 60) && near(objectBox().style.left, 30), 'The corner handle resizes the box');
          $('[data-testid="editor-undo"]').click();
          await wait(() => near(objectBox().style.width, 30), 'Undo');
          key(document.body, 'y', {ctrlKey: true});
          await wait(() => near(objectBox().style.width, 50), 'Redo');
          assert(true, 'Undo and Ctrl+Y redo restore box states');

          choose($('[data-testid="zoom-select"]', $('[data-testid="annotation-editor"]')), '2');
          await wait(() => Math.abs(frame().getBoundingClientRect().width - 1280) < 1, 'Editor zoom');
          await drag(frame(), [0.05, 0.05], [0.15, 0.12]);
          await wait(() => $$('[data-box-id]', frame()).length === 2, 'Second box at x2');
          const second = $$('[data-box-id]', frame())[1];
          assert(near(second.style.left, 5) && near(second.style.width, 10), 'Pointer coordinates stay correct at x2 zoom');
          key(document.body, 'Delete');
          await wait(() => $$('[data-box-id]', frame()).length === 1, 'Box deleted');
          assert(true, 'Delete removes the selected box');
          key(document.body, '0');

          key(document.body, 's', {ctrlKey: true});
          await wait(() => status() === 'Не проверено' && $('[data-testid="image-details"]').textContent.includes('Ревизия разметки 2'), 'Saved');
          const savedOnce = await request('images.getAnnotation', {projectId, imageId: firstId});
          assert(savedOnce.ok && savedOnce.data.revision === 2 && savedOnce.data.objects.length === 1 && savedOnce.data.objects[0].classId === carId
            && Math.abs(savedOnce.data.objects[0].box.width - 0.5) < 0.01, 'Ctrl+S saves a new annotation revision');

          $('[data-testid="editor-reviewed"]').click();
          await wait(() => status() === 'Есть несохранённые изменения', 'Dirty again');
          $('[data-section="overview"]').click();
          const guardDialog = await wait(() => $('[data-testid="unsaved-guard"]'), 'Guard on section change');
          assert(guardDialog.textContent.includes('Уйти без сохранения?') && guardDialog.textContent.includes('Изменения разметки этого изображения ещё не сохранены'),
            'Section change with unsaved annotation asks first');
          button('Продолжить разметку', guardDialog).click();
          await wait(() => !$('[data-testid="unsaved-guard"]'), 'Guard closed');
          assert($('[data-testid="annotation-editor"]') && status() === 'Есть несохранённые изменения', '«Продолжить разметку» keeps the draft');
          button('Назад к изображениям').click();
          await wait(() => $('[data-testid="unsaved-guard"]'), 'Guard on back');
          $('[data-testid="unsaved-save"]').click();
          await wait(() => !$('[data-testid="unsaved-guard"]') && !$('[data-testid="annotation-editor"]') && tiles().length === 4, 'Saved and continued');
          const savedTwice = await request('images.getAnnotation', {projectId, imageId: firstId});
          assert(savedTwice.data.revision === 3 && savedTwice.data.reviewState === 'reviewed', '«Сохранить и продолжить» saves, then leaves the editor');
          const firstTile = await wait(() => $('[data-image-id="' + firstId + '"]'), 'First tile');
          await wait(() => firstTile.querySelector('[data-testid="image-meta"]').textContent === '640 × 480 · рамок: 1 · проверено' && firstTile.querySelector('[data-testid="tile-overlay"] rect'), 'Tile updated');
          assert(true, 'Tile shows the saved boxes and review state');

          const viewButton = labelled('Просмотреть двор-1.jpg');
          viewButton.focus();
          viewButton.click();
          const viewer = await wait(() => $('[data-image-viewer]') && $('[data-viewer-image]')?.naturalWidth === 640 && $('[data-image-viewer]'), 'Viewer opened');
          const zoomSelect = $('[data-testid="zoom-select"]', viewer);
          assert(zoomSelect.value === 'fit' && zoomSelect.options[0].text.startsWith('Вписать (') && $('[data-viewer-overlay]', viewer)
            && $('[data-testid="viewer-position"]', viewer).textContent === '1 / 4', 'Viewer opens fitted with boxes and position');
          choose(zoomSelect, '2');
          await wait(() => Math.abs($('[data-viewer-image]').getBoundingClientRect().width - 1280) < 1, 'x2');
          const viewport = $('[data-viewer-viewport]', viewer);
          assert(viewport.scrollWidth > viewport.clientWidth, 'x2 shows two CSS pixels per frame pixel inside the scrolled viewport');
          key(viewport, '-');
          await wait(() => zoomSelect.value === '1', 'Zoom out');
          key(viewport, '0');
          await wait(() => zoomSelect.value === 'fit', 'Fit');
          assert(true, 'Keys zoom out and reset to fit');
          key(viewport, 'ArrowRight');
          await wait(() => $('[data-testid="viewer-position"]', viewer)?.textContent === '2 / 4' && $('[data-image-viewer]').textContent.includes('двор-2.jpg'), 'Next image');
          assert(true, 'Arrow key opens the next image');
          button('На весь экран', viewer).click();
          await wait(() => $('[data-image-viewer]').dataset.viewerFullscreen === 'true', 'Fullscreen');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => $('[data-image-viewer]')?.dataset.viewerFullscreen === 'false', 'Fullscreen left');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-image-viewer]') && document.activeElement === labelled('Просмотреть двор-1.jpg'), 'Viewer closed');
          assert(true, 'First Esc leaves fullscreen, second closes and returns focus');

          const checkbox = index => $('button[role="checkbox"]', tiles()[index]);
          checkbox(2).click();
          checkbox(3).dispatchEvent(new MouseEvent('click', {bubbles: true, cancelable: true, shiftKey: true}));
          const deleteSelected = await wait(() => $('[data-testid="delete-selected"]'), 'Selection actions');
          assert(deleteSelected.textContent.includes('Удалить выбранные (2)') && tiles().filter(tile => tile.dataset.selected).length === 2, 'Shift+click selects a range');
          deleteSelected.click();
          await wait(() => $('[data-testid="images-deletion-count"]')?.textContent === 'Изображений к удалению: 2', 'Deletion review');
          assert($('[data-testid="images-deletion-dialog"]').textContent.includes('Удалить изображения навсегда?'), 'Deletion dialog reviews the selection');
          (await wait(() => enabled($('[data-testid="images-deletion-confirm"]')), 'Delete enabled')).click();
          await wait(() => !$('[data-testid="images-deletion-dialog"]') && tiles().length === 2 && $('[data-testid="image-group-count"]').textContent === 'Кадров: 2', 'Images deleted');
          assert(true, 'Selected images are deleted through the dialog');

          labelled('В архив: Двор и улица').click();
          await wait(() => $('[data-image-group-id="' + groupId + '"]').textContent.includes('Двор и улица · Архив') && button('Импорт файлов').disabled, 'Group archived');
          assert(true, 'Archived group is marked and closed for import');
          labelled('Восстановить: Двор и улица').click();
          await wait(() => !$('[data-image-group-id="' + groupId + '"]').textContent.includes('Архив') && !button('Импорт файлов').disabled, 'Group restored');
          assert(true, 'Archived group is restored');

          $('[data-testid="image-open"]', $('[data-image-id="' + firstId + '"]')).click();
          await wait(() => frame()?.querySelector('[data-status="loaded"] img')?.naturalWidth > 0 && objectBox(), 'Final editor');
          pointer(objectBox(), 'pointerdown', at(0.55, 0.6));
          pointer(objectBox(), 'pointerup', at(0.55, 0.6));
          await wait(() => objectBox().dataset.selected === 'true' && status() === 'Проверено', 'Final box selected');
          frame().scrollIntoView({block: 'center'});
          await sleep(300);
          return {checks};
        }
        """;

    /// <summary>
    /// «Фоны» through the real UI, in three page scripts because each native dialog answer is consumed once: a set created in
    /// the form and «Готовые кадры» with the pre-import size summary, thumbnails and skip reasons in the run history; «Нарезать
    /// из изображений» walking the files with arrow-key moves, «Назад», a skipped file, the review with blur, seed and samples;
    /// «Нарезать из видео» with its example frame, count message and samples; then «Убрать»/«Восстановить», the viewer, set
    /// archive and rename, deletion of the selection and the size mismatch after the project input changes. The page ends on
    /// the selected set with its thumbnails for the preview capture.
    /// </summary>
    private static async Task CheckBackgroundsUiAsync(CoreWebView2 web, string root, string projectId, JsonNode report, CancellationToken token)
    {
        var directory = Path.Combine(root, "smoke-backgrounds-ui");
        Directory.CreateDirectory(directory);
        string Write(string name, int width, int height, SKColor color)
        {
            using var bitmap = new SKBitmap(width, height);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(color);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                canvas.DrawCircle(width * 0.4f, height * 0.45f, Math.Min(width, height) / 5f, paint);
                using var stripe = new SKPaint { Color = SKColors.Gold };
                canvas.DrawRect(0, height * 0.8f, width, height * 0.06f, stripe);
            }
            using var data = bitmap.Encode(name.EndsWith(".jpg", StringComparison.Ordinal) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90);
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }
        var checks = report["checks"]!.AsArray();
        async Task Phase(string body, int attempts)
        {
            var script = "(async ({projectId}) => {" + BackgroundsUiHelpers + body + "})(" + JsonSerializer.Serialize(new { projectId }) + ")";
            var result = await RunScriptAsync(web, script, token, attempts);
            foreach (var check in result["checks"]!.AsArray())
            {
                checks.Add(check!.GetValue<string>());
            }
        }
        // The project input is 320 × 256 at this point.
        MainWindow.SmokeFilesAnswer =
        [
            Write("готовый-1.png", 320, 256, SKColors.SteelBlue),
            Write("готовый-2.png", 320, 256, SKColors.Sienna),
            Write("крупный.jpg", 680, 532, SKColors.DarkOliveGreen),
            Write("малый.png", 160, 128, SKColors.Teal),
        ];
        await Phase(BackgroundsUiReadyScript, 600);
        MainWindow.SmokeFilesAnswer =
        [
            Write("мелкий.png", 100, 80, SKColors.Purple),
            Write("скрин-1.png", 700, 500, SKColors.SlateGray),
            Write("скрин-2.png", 700, 500, SKColors.IndianRed),
            Write("точный.png", 320, 256, SKColors.CadetBlue),
        ];
        await Phase(BackgroundsUiCropScript, 600);
        MainWindow.SmokeVideoAnswer = Path.Combine(root, "smoke-video", "улица.mp4");
        await Phase(BackgroundsUiRestScript, 900);
    }

    private const string BackgroundsUiHelpers = """
          const checks = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const wait = async (query, label, describe) => {
            for (let i = 0; i < 200; i++) { const value = query(); if (value) return value; await sleep(100); }
            throw new Error('Timed out: ' + label + (describe ? ' (' + describe() + ')' : ''));
          };
          const $ = (selector, scope = document) => scope.querySelector(selector);
          const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
          const labelled = (label, scope = document) => $('[aria-label="' + label + '"]', scope);
          const enabled = element => element && !element.disabled ? element : null;
          const text = selector => $(selector)?.textContent ?? '';
          const setValue = (input, value) => {
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, value);
            input.dispatchEvent(new Event('input', {bubbles: true}));
          };
          const key = (target, key, options = {}) => target.dispatchEvent(new KeyboardEvent('keydown', {key, bubbles: true, cancelable: true, ...options}));
          const request = (method, payload) => new Promise((resolve, reject) => {
            const requestId = 'backgrounds-ui-smoke-' + crypto.randomUUID();
            const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', reply); reject(new Error('No reply: ' + method)); }, 10000);
            const reply = event => {
              if (event.data?.kind !== 'response' || event.data.requestId !== requestId) return;
              clearTimeout(timeout);
              window.chrome.webview.removeEventListener('message', reply);
              resolve(event.data);
            };
            window.chrome.webview.addEventListener('message', reply);
            window.chrome.webview.postMessage({requestId, method, payload});
          });
          const tiles = () => $$('[data-testid="background-tile"]');
          const thumbnailsLoaded = count => tiles().length === count
            && tiles().every(tile => tile.dataset.removed || tile.querySelector('[data-status="loaded"] img')?.naturalWidth > 0);
          const runs = () => $$('[data-testid="background-runs"] [data-job-id]');
          const finished = row => row && ['succeeded', 'failed', 'cancelled'].includes(row.dataset.status) ? row : null;
          const setButton = name => $$('[data-background-set-id]').find(item => item.querySelector('span')?.textContent === name);
          const mode = id => $('[data-import-mode="' + id + '"]');

        """;

    private const string BackgroundsUiReadyScript = """
          (await wait(() => enabled($('[data-section="backgrounds"]')), 'Backgrounds section enabled')).click();
          await wait(() => $('[data-testid="backgrounds-section"]') && !$('[data-testid="background-sets"] [role="status"]'), 'Backgrounds section opened');
          assert(document.body.textContent.includes('Готовые фоны или кадры из видео размером со вход модели.')
            && $('[data-testid="background-sets"]').textContent.includes('Создайте набор для подготовки фонов генерации.')
            && $('[data-testid="background-set-content"]').textContent.includes('Создайте первый набор фонов. Размер кадров должен совпадать с входом модели.'),
            'Backgrounds section opens lazily with the empty sets panel');

          setValue($('[data-testid="background-set-new-name"]'), 'Улица UI');
          (await wait(() => enabled($('[data-testid="background-set-create"]')), 'Create enabled')).click();
          const created = await wait(() => setButton('Улица UI')?.getAttribute('aria-pressed') === 'true' && setButton('Улица UI'), 'Set created and selected');
          assert(created.textContent.includes('0 фонов · 320 × 256') && !$('[data-testid="set-size-mismatch"]', created)
            && $('[data-testid="backgrounds-empty"]').textContent.includes('Фонов пока нет.'), 'A set created in the form takes the project input size');
          assert($('[data-testid="background-add-toggle"]').getAttribute('aria-expanded') === 'true'
            && $('[role="radiogroup"][aria-label="Способ добавления фонов"]') && mode('ready').getAttribute('aria-checked') === 'true'
            && $('[data-testid="background-add"]').textContent.includes('Каждый фон — картинка ровно 320 × 256 px'),
            '«Добавить фоны» is open for an empty set with «Готовые кадры» chosen');
          key(mode('ready'), 'ArrowRight');
          await wait(() => mode('images').getAttribute('aria-checked') === 'true' && document.activeElement === mode('images'), 'Arrow moves the mode');
          key(mode('images'), 'ArrowLeft');
          await wait(() => mode('ready').getAttribute('aria-checked') === 'true', 'Arrow back');
          assert(true, 'Arrow keys move between the add modes');

          $('[data-testid="ready-choose"]').click();
          const summary = await wait(() => $('[data-testid="ready-summary"]'), 'Pre-import summary');
          assert($('[data-testid="ready-exact"]').textContent === '2' && summary.textContent.includes('Выбрано файлов: 4.')
            && $('[data-testid="ready-skipped"]').textContent.includes('Будут пропущены: 2 (больше — 1, меньше — 1, не читаются — 0).')
            && $('[data-testid="ready-import"]').textContent === 'Импортировать 2 фона', 'The pre-import summary counts fitting and skipped files from the headers');
          $('[data-testid="ready-import"]').click();
          await wait(() => thumbnailsLoaded(2), 'Ready thumbnails', () => tiles().length + ' tiles');
          assert(tiles()[0].textContent.includes('готовый-1.png') && $('[data-testid="background-meta"]', tiles()[0]).textContent === '320 × 256'
            && setButton('Улица UI').textContent.includes('2 фона · 320 × 256'), 'Imported backgrounds appear with thumbnails from the resource channel');
          const readyRun = await wait(() => finished(runs()[0]), 'Ready run finished');
          assert(readyRun.dataset.status === 'succeeded' && $('[data-testid="run-summary"]', readyRun).textContent.includes('добавлено 2, повторов 0, отклонено 2')
            && $('[data-testid="background-runs-toggle"]').getAttribute('aria-expanded') === 'true', 'The run history of the set shows the result');
          const issues = $('[data-testid="run-issues"]', readyRun);
          issues.open = true;
          assert(issues.textContent.includes('Причины пропуска (2)') && issues.textContent.includes('крупный.jpg · Размер 680×532, нужен 320×256')
            && issues.textContent.includes('малый.png · Размер 160×128, нужен 320×256'), 'Skip reasons name the files with their size');
          return {checks};
        """;

    private const string BackgroundsUiCropScript = """
          const frame = () => $('[data-testid="crop-dialog"] [data-testid="crop-frame"]');
          const at = () => frame() && frame().dataset.left + ',' + frame().dataset.top;
          const dialog = () => $('[data-testid="crop-dialog"]');
          mode('images').click();
          (await wait(() => enabled($('[data-testid="images-choose"]')), 'Images mode')).click();
          await wait(() => dialog() && $('[data-testid="crop-image"]', dialog())?.naturalWidth > 0, 'First frame to crop');
          assert(dialog().textContent.includes('Нарезка фонов из изображений') && dialog().textContent.includes('Файл 2 из 4.')
            && text('[data-testid="crop-file"]') === 'скрин-1.png' && at() === '190,122' && frame().getAttribute('role') === 'slider'
            && frame().getAttribute('aria-label') === 'Положение рамки на кадре', 'A smaller file is skipped by its header and the frame starts centred');
          await wait(() => document.activeElement === frame(), 'Frame focused');
          key(frame(), 'ArrowRight');
          await wait(() => at() === '191,122', 'Moved 1 px');
          key(frame(), 'ArrowDown', {shiftKey: true});
          await wait(() => at() === '191,132', 'Moved 10 px');
          assert(text('[data-testid="crop-position"]') === 'Рамка 320 × 256 px · x 191, y 132 · кадр 700 × 500', 'Arrows move the frame by 1 px and by 10 px with Shift');
          $('[data-testid="crop-take"]').click();
          await wait(() => text('[data-testid="crop-file"]') === 'скрин-2.png' && $('[data-testid="crop-image"]', dialog())?.naturalWidth > 0, 'Second file');
          assert(at() === '191,132', 'A file of the same size starts at the last taken position');
          key(frame(), 'ArrowLeft', {shiftKey: true});
          await wait(() => at() === '181,132', 'Moved left');
          [...dialog().querySelectorAll('button')].find(item => item.textContent === 'Назад').click();
          await wait(() => text('[data-testid="crop-file"]') === 'скрин-1.png' && at() === '191,132', 'Back to the first');
          assert(true, '«Назад» returns to the previous file with its decision');
          $('[data-testid="crop-take"]').click();
          await wait(() => text('[data-testid="crop-file"]') === 'скрин-2.png' && frame(), 'Second file again');
          $('[data-testid="crop-skip"]').click();
          const review = await wait(() => $('[data-testid="crop-review"]'), 'Review stage');
          assert(text('[data-testid="crop-count-crop"]') === 'Нарезать по рамке: 1' && text('[data-testid="crop-count-whole"]') === 'Взять целиком (ровно 320 × 256): 1'
            && text('[data-testid="crop-count-skip"]') === 'Пропущено вами: 1' && $('[data-testid="crop-small"]').textContent.includes('Меньше входа модели, пропущено: 1')
            && $('[data-testid="crop-small"]').textContent.includes('мелкий.png · размер 100×80, нужен не меньше 320×256'), 'The review counts cropped, whole, skipped and small files');
          $('[data-testid="blur-enabled"]', review).click();
          const seed = await wait(() => $('[data-testid="blur-seed"]', review), 'Blur fields');
          assert($('[data-testid="blur-min"]', review).value === '0' && $('[data-testid="blur-max"]', review).value === '4'
            && $('[role="slider"][aria-label="Радиус до"]', review)?.getAttribute('aria-valuemax') === '32' && seed.placeholder === 'Из операции',
            'Blur defaults to 0–4 px with a 0–32 px slider and the seed «Из операции»');
          setValue($('[data-testid="blur-max"]', review), '1200');
          await wait(() => $('[data-testid="blur-error"]', review)?.textContent === 'Радиус — от 0 до 1000 px.' && $('[data-testid="crop-import"]').disabled, 'Blur error');
          assert(true, 'A radius over 1000 px shows the error and blocks the import');
          setValue($('[data-testid="blur-max"]', review), '3');
          setValue($('[data-testid="blur-min"]', review), '1');
          setValue(seed, '42');
          const samples = await wait(() => {
            const figures = $$('[data-testid="import-sample"]', review);
            return figures.length === 2 && figures.every(item => item.querySelector('img')?.naturalWidth === 320)
              && $('[data-testid="import-samples"]', review)?.textContent.includes('seed 42') && $('[data-testid="import-samples"]', review);
          }, 'Samples with blur');
          assert(samples.textContent.includes('Примеры будущих фонов 320 × 256 · размытие 1–3 px, seed 42') && samples.textContent.includes('скрин-1.png · ')
            && samples.textContent.includes('точный.png'), 'Samples show the future backgrounds with the blur and the seed');
          const importButton = await wait(() => enabled($('[data-testid="crop-import"]')), 'Import enabled');
          assert(importButton.textContent === 'Импортировать 2 фона', 'The import button names the count');
          importButton.click();
          await wait(() => !dialog() && thumbnailsLoaded(4), 'Cropped thumbnails', () => tiles().length + ' tiles');
          const cropped = tiles().find(tile => tile.textContent.includes('скрин-1.png'));
          assert(/^320 × 256 · вырезан из x 191, y 132 · размытие [\d,]+ px$/.test($('[data-testid="background-meta"]', cropped).textContent),
            'The cropped background shows its region and blur radius');
          const cropRun = await wait(() => finished(runs()[0]), 'Crop run');
          assert(cropRun.dataset.status === 'succeeded' && runs().length === 2 && $('[data-testid="run-summary"]', cropRun).textContent.includes('размытие 1–3 px, seed 42'),
            'The crop run keeps the seed in the history');
          return {checks};
        """;

    private const string BackgroundsUiRestScript = """
          const video = () => $('[data-testid="bg-video-import"]');
          const message = () => text('[data-testid="bg-video-message"]');
          mode('video').click();
          (await wait(() => enabled($('[data-testid="bg-video-choose"]')), 'Video mode')).click();
          await wait(() => $('[data-testid="crop-image"]', video())?.naturalWidth > 0, 'Example frame');
          assert(text('[data-testid="bg-video-info"]') === 'улица.mp4 · 640 × 360 · 5.0 с' && $('[data-testid="crop-frame"]', video()).dataset.left === '160'
            && $('[data-testid="bg-video-count"]').value === '20' && $('[data-testid="bg-video-interval"]').value === '1'
            && $('[data-testid="bg-video-skip-similar"]').getAttribute('aria-checked') === 'true', 'The video shows an example frame with a centred frame and defaults');
          assert(message() === 'С шагом 1 с из видео получится не больше 5 фонов. Уменьшите шаг или количество.' && $('[data-testid="bg-video-run"]').disabled,
            'Too many backgrounds for the video show the old message and block the run');
          setValue($('[data-testid="bg-video-count"]'), '3');
          await wait(() => message() === 'С таким шагом доступно до 5 кадров.', 'Count fixed');
          assert($('[data-testid="bg-video-run"]').textContent === 'Нарезать 3 фона', 'The available frames line and the run button follow the fields');
          const firstFrame = $('[data-testid="crop-image"]', video()).src;
          $('[data-testid="bg-video-another"]').click();
          await wait(() => enabled($('[data-testid="bg-video-another"]')) && enabled($('[data-testid="bg-video-samples"]'))
            && $('[data-testid="crop-image"]', video())?.src !== firstFrame && $('[data-testid="crop-image"]', video())?.naturalWidth > 0, 'Another frame');
          assert($('[data-testid="crop-frame"]', video()).dataset.left === '160', '«Показать другой кадр» keeps the frame');
          $('[data-testid="bg-video-samples"]').click();
          const samples = await wait(() => {
            const figures = $$('[data-testid="import-sample"]', video());
            return figures.length === 3 && figures.every(item => item.querySelector('img')?.naturalWidth === 320) && $('[data-testid="import-samples"]', video());
          }, 'Video samples', () => 'figures ' + $$('[data-testid="import-sample"]', video()).length + ', images ' + $$('[data-testid="import-sample"] img', video()).map(item => item.naturalWidth).join('/')
            + ', button ' + $('[data-testid="bg-video-samples"]').disabled);
          assert(samples.textContent.includes('seed 42') && samples.textContent.includes('улица.mp4 · 0 с'), 'Video samples use the shared blur with its seed');
          $('[data-testid="bg-video-run"]').click();
          await wait(() => runs().length === 3 && finished(runs()[0]), 'Video run');
          const videoRun = runs()[0];
          const added = Number(/добавлено (\d+) из 3/.exec($('[data-testid="run-summary"]', videoRun)?.textContent ?? '')?.[1]);
          assert(videoRun.dataset.status === 'succeeded' && added >= 1 && text('[data-testid="bg-video-info"]').startsWith('Задача запущена'),
            'The video run adds frames until the requested count and asks for the video again');
          await wait(() => thumbnailsLoaded(4 + added), 'Video thumbnails');

          const first = tiles()[0];
          const firstName = $('label span', first).textContent;
          labelled('Убрать фон: ' + firstName).click();
          await wait(() => tiles().length === 3 + added && text('[data-testid="background-set-count"]').includes('убрано: 1'), 'Removed');
          assert(!tiles().some(tile => tile.dataset.backgroundId === first.dataset.backgroundId), '«Убрать» hides the background from the active list');
          $('[data-testid="show-removed"]').click();
          const hidden = await wait(() => tiles().find(tile => tile.dataset.removed), 'Removed shown');
          assert(hidden.textContent.includes(firstName + ' · Убран') && hidden.textContent.includes('Восстановите фон для просмотра'), '«Показать убранные фоны» lists the removed one');
          labelled('Восстановить фон: ' + firstName).click();
          await wait(() => !tiles().some(tile => tile.dataset.removed) && !text('[data-testid="background-set-count"]').includes('убрано'), 'Restored');
          assert(true, '«Восстановить» returns the background');
          $('[data-testid="show-removed"]').click();
          await wait(() => $('[data-testid="show-removed"]').getAttribute('aria-checked') === 'false' && thumbnailsLoaded(4 + added), 'Active list again');
          await sleep(300);
          const open = await wait(() => thumbnailsLoaded(4 + added) && labelled('Открыть фон: скрин-1.png'), 'Cropped tile', () => tiles().map(tile => tile.textContent).join(' | '));
          open.focus();
          open.click();
          const viewer = await wait(() => $('[data-image-viewer]') && $('[data-viewer-image]')?.naturalWidth === 320 && $('[data-image-viewer]'), 'Viewer');
          const details = $('[data-testid="background-details"]', viewer).textContent;
          assert(details.includes('Источник: Фрагмент изображения · ') && details.includes('КБ') && details.includes('Область: x 191, y 132 · 320 × 256 px')
            && details.includes('Размытие: '), 'The shared viewer shows the source, region and blur');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-image-viewer]') && document.activeElement === labelled('Открыть фон: скрин-1.png'), 'Viewer closed');
          assert(true, 'Closing the viewer returns focus to the tile');

          labelled('В архив: Улица UI').click();
          await wait(() => setButton('Улица UI')?.textContent.includes('· В архиве') && $('[data-testid="background-add-toggle"]').getAttribute('aria-expanded') === 'false', 'Archived');
          assert(labelled('Убрать фон: ' + firstName).disabled && $('[data-testid="bg-video-choose"]').disabled, 'An archived set is read-only');
          labelled('Восстановить: Улица UI').click();
          await wait(() => !setButton('Улица UI')?.textContent.includes('В архиве') && !labelled('Убрать фон: ' + firstName).disabled, 'Restored set');
          assert(true, 'The set is restored from the archive');

          labelled('Изменить набор фонов: Улица UI').click();
          const setDialog = await wait(() => $('[data-testid="background-set-dialog"]'), 'Set dialog');
          setValue($('[data-testid="background-set-name"]', setDialog), 'Улица и двор');
          const description = $('[data-testid="background-set-description"]', setDialog);
          Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(description, 'Кадры улицы');
          description.dispatchEvent(new Event('input', {bubbles: true}));
          $('[data-testid="background-set-save"]', setDialog).click();
          await wait(() => !$('[data-testid="background-set-dialog"]') && text('[data-testid="background-set-title"]') === 'Улица и двор', 'Renamed');
          assert($('[data-testid="background-set-content"]').textContent.includes('Кадры улицы') && setButton('Улица и двор'), 'The set name and description change through the dialog');

          $('[data-testid="background-select-page"]').click();
          await wait(() => tiles().every(tile => tile.dataset.selected), 'Page selected');
          assert(text('[data-testid="background-select-page"]') === 'Снять выделение', '«Выбрать все на странице» selects the loaded backgrounds');
          $('[data-testid="background-select-page"]').click();
          await wait(() => !tiles().some(tile => tile.dataset.selected), 'Selection cleared');
          const checkbox = index => $('button[role="checkbox"]', tiles()[index]);
          checkbox(0).click();
          checkbox(1).dispatchEvent(new MouseEvent('click', {bubbles: true, cancelable: true, shiftKey: true}));
          const deleteSelected = await wait(() => $('[data-testid="background-delete-selected"]'), 'Selection actions');
          assert(deleteSelected.textContent.includes('Удалить выбранные (2)'), 'Shift+click selects a range');
          deleteSelected.click();
          await wait(() => text('[data-testid="backgrounds-deletion-count"]') === 'Будет удалено фонов: 2.', 'Deletion review');
          assert($('[data-testid="backgrounds-deletion-dialog"]').textContent.includes('Удалить фоны навсегда?')
            && $('[data-testid="backgrounds-deletion-dialog"]').textContent.includes('Это действие нельзя отменить.'), 'The deletion dialog reviews the selection');
          (await wait(() => enabled($('[data-testid="backgrounds-deletion-confirm"]')), 'Delete enabled')).click();
          await wait(() => !$('[data-testid="backgrounds-deletion-dialog"]') && thumbnailsLoaded(2 + added), 'Deleted');
          assert(setButton('Улица и двор').textContent.includes(`${2 + added} фон`), 'Selected backgrounds are deleted through the dialog');

          const projects = await request('projects.list', {});
          const current = projects.data.items.find(item => item.id === projectId);
          const updated = await request('projects.update', {projectId, name: current.name, description: current.description, baseModelId: current.baseModelId,
            inputSize: {width: 320, height: 320}, revision: current.revision});
          assert(updated.ok, 'The project input changes to 320 × 320');
          const mismatch = await wait(() => $('[data-testid="set-size-mismatch"]', setButton('Улица и двор')), 'Size mismatch mark');
          assert(mismatch.title === 'Размер набора 320 × 256 отличается от входа проекта'
            && text('[data-testid="set-size-mismatch-note"]').includes('Размер набора 320 × 256 отличается от входа проекта 320 × 320'),
            'A set made for another input size is marked after project.changed');
          $('[data-testid="background-add-toggle"]').click();
          $('[data-testid="background-runs-toggle"]').click();
          await wait(() => $('[data-testid="background-add-toggle"]').getAttribute('aria-expanded') === 'false'
            && $('[data-testid="background-runs-toggle"]').getAttribute('aria-expanded') === 'false' && thumbnailsLoaded(2 + added), 'Final gallery');
          await sleep(300);
          return {checks};
        """;

    /// <summary>
    /// «Генерация» through the real UI: a recipe made in the form (an incompatible background set shown but disabled, the
    /// weights total for ≠ 100, «Поровну», validation hints), «Пример кадра» with boxes and the shared viewer, editing (new
    /// revision), archive and restore, a run with progress from job.changed and a result with missing objects, «Открыть
    /// изображения» on the output group, the class deletion blocked by the recipe, «Настроить повтор» and deletion of the run.
    /// The catalog and backgrounds are seeded through Core. The page ends on the repeat form with samples for the capture.
    /// </summary>
    private static async Task CheckGenerationUiAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var size = project.InputSize;
        var directory = Path.Combine(root, "smoke-generation-ui");
        Directory.CreateDirectory(directory);
        string Write(string name, byte[] content)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            return path;
        }
        var variant = await project.Variants.CreateAsync("Сумерки UI", "", token);
        var empty = await project.Variants.CreateAsync("Пустой UI", "", token);
        var klass = await project.Classes.CreateAsync("Знак UI", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        // 300 × 200: two such objects never fit a 320 × 320 frame at scale 1, so the run reports frames with missing objects.
        var assets = await core.Jobs.EnqueueAsync<IImportClassAssetsJob, ImportClassAssetsInput>(new ImportClassAssetsInput(binding.Id,
            [Write("знак.png", Sprite(300, 200, SKColors.Crimson))]), projectId, "generation-ui-smoke-assets", cancellationToken: token);
        var set = await project.Backgrounds.CreateSetAsync("Фоны UI", "", token);
        var backgrounds = await core.Jobs.EnqueueAsync<IImportBackgroundsJob, ImportBackgroundsInput>(new ImportBackgroundsInput(set.Id,
            [new BackgroundFileSource(Write("фон-ui-1.png", Frame(size.Width, size.Height, SKColors.SlateGray))),
                new BackgroundFileSource(Write("фон-ui-2.png", Frame(size.Width, size.Height, SKColors.DarkSeaGreen)))]), projectId, "generation-ui-smoke-backgrounds", cancellationToken: token);
        foreach (var job in new[] { assets, backgrounds })
        {
            for (var attempt = 0; ; attempt++)
            {
                var current = await core.Jobs.GetAsync(projectId, job.Id, token);
                if (current.Status == JobStatus.Succeeded) break;
                if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 600)
                    throw new InvalidOperationException($"Generation UI fixture job {current.Kind} is {current.Status}.");
                await Task.Delay(50, token);
            }
        }
        var checks = report["checks"]!.AsArray();
        var arguments = JsonSerializer.Serialize(new { projectId, variantId = variant.Id, emptyVariantId = empty.Id, width = size.Width, height = size.Height });
        foreach (var body in new[] { GenerationUiFormScript, GenerationUiRecipeScript, GenerationUiRunScript, GenerationUiRepeatScript })
        {
            var script = "(async ({projectId, variantId, emptyVariantId, width, height}) => {" + BackgroundsUiHelpers + GenerationUiHelpers + body + "})(" + arguments + ")";
            // A script that fails to parse would leave the previous phase's result in place.
            await web.ExecuteScriptAsync("window.__smokeScript = undefined;");
            var result = await RunScriptAsync(web, script, token, attempts: 900);
            foreach (var check in result["checks"]!.AsArray())
            {
                checks.Add(check!.GetValue<string>());
            }
        }
    }

    private const string GenerationUiHelpers = """
          const form = () => $('[data-testid="recipe-form"]');
          const setRow = name => $$('[data-generation-set-id]').find(row => row.querySelector('[data-testid="set-name"]')?.textContent === name);
          const weightInputs = () => $$('[data-generation-variant-id][data-eligible] [data-testid="weight-input"]');
          const weightOf = id => $('[data-generation-variant-id="' + id + '"] [data-testid="weight-input"]');
          const card = name => $$('[data-generation-recipe-id]').find(item => $('[data-testid="recipe-name-title"]', item)?.firstChild?.textContent === name);
          const runCard = () => $('[data-generation-run-id]');
          const samples = () => $$('[data-testid="generation-sample"]');
          const samplesLoaded = () => samples().length === 4 && samples().every(item => item.querySelector('[data-status="loaded"] img')?.naturalWidth > 0) && samples();

        """;

    private const string GenerationUiFormScript = """
          (await wait(() => enabled($('[data-section="generation"]')), 'Generation section enabled')).click();
          await wait(() => form() && setRow('Фоны UI') && weightOf(variantId) && $('[data-testid="recipes-empty"]') && $('[data-testid="runs-empty"]'), 'Generation opened');
          assert(document.body.textContent.includes('Наложение объектов на фоны с заданными долями вариантов.') && text('[data-testid="tab-recipes"]') === 'Пресеты (0)'
            && text('[data-testid="tab-runs"]') === 'Запуски (0)' && text('[data-testid="recipe-form-title"]') === 'Новый пресет'
            && text('[data-testid="recipe-canvas"]') === width + ' × ' + height
            && text('[data-testid="recipes-empty"]').startsWith('Сохраните пресет для следующих генераций.'), 'Generation opens lazily with the new recipe form and empty lists');

          const other = setRow('Улица и двор');
          assert(other && !other.dataset.fits && $('button[role="checkbox"]', other).disabled
            && $('[data-testid="set-incompatible"]', other).textContent.startsWith('Размер набора 320 × 256 не совпадает с кадром ' + width + ' × ' + height),
            'A background set of another size is shown but disabled with an explanation');
          assert(setRow('Фоны UI').textContent.includes('2 фона · ' + width + ' × ' + height), 'A fitting set shows «N фонов · W × H»');
          $('button[role="checkbox"]', setRow('Фоны UI')).click();
          setValue($('[data-testid="recipe-name"]'), 'Улица UI пресет');
          await wait(() => $('button[role="checkbox"]', setRow('Фоны UI')).getAttribute('aria-checked') === 'true', 'Set chosen');
          const mine = $('[data-generation-variant-id="' + variantId + '"]');
          const none = $('[data-generation-variant-id="' + emptyVariantId + '"]');
          assert($('[data-testid="variant-classes"]', mine).textContent === '1 класс' && !none.dataset.eligible && weightOf(emptyVariantId).disabled
            && $('[data-testid="variant-classes"]', none).textContent === 'Добавьте изображения к классам', 'Variants show «N классов» or «Добавьте изображения к классам»');
          assert(text('[data-testid="weights-total"]') === 'Всего: 0% из 100%' && $('[data-testid="weights-total"]').dataset.invalid && $('[data-testid="recipe-save"]').disabled,
            'A new form starts at 0% and cannot be saved');

          $('[data-testid="weights-even"]').click();
          await wait(() => text('[data-testid="weights-total"]') === 'Всего: 100% из 100%', 'Even weights');
          const even = weightInputs().map(input => Number(input.value));
          assert(even.length >= 2 && even.reduce((sum, value) => sum + value, 0) === 100 && Math.max(...even) - Math.min(...even) <= 1
            && !$('[data-testid="weights-total"]').dataset.invalid, '«Поровну» splits 100% between the variants with images');
          for (const input of weightInputs()) setValue(input, input === weightOf(variantId) ? '90' : '0');
          await wait(() => text('[data-testid="weights-total"]') === 'Всего: 90% из 100%', 'Weights 90');
          assert($('[data-testid="weights-total"]').dataset.invalid && $('[data-testid="weights-total"]').className.includes('text-destructive')
            && $('[data-testid="recipe-save"]').disabled && $('[data-testid="recipe-samples"]').disabled, 'A total other than 100% is shown as an error and blocks saving');
          setValue(weightOf(variantId), '100');
          setValue($('[data-testid="recipe-objects"]'), '2');
          setValue($('[data-testid="recipe-empty"]'), '0');
          setValue($('[data-testid="recipe-scale"]'), '0');
          await wait(() => text('[data-testid="objects-hint"]') === 'Масштаб объекта — число больше 0.', 'Scale hint');
          assert($('[data-testid="recipe-save"]').disabled, 'A scale of 0 shows the old hint and blocks saving');
          setValue($('[data-testid="recipe-scale"]'), '0.4');
          await wait(() => enabled($('[data-testid="recipe-save"]')) && text('[data-testid="objects-hint"]').startsWith('0 объектов — только фоны'), 'Form valid');
          assert(text('[data-testid="weights-total"]') === 'Всего: 100% из 100%', 'The form is valid with 100%');

          $('[data-testid="recipe-samples"]').click();
          const shown = await wait(samplesLoaded, 'Samples', () => samples().length + ' samples');
          assert(shown.every(item => item.dataset.objects === '2' && $$('[data-testid="sample-overlay"] rect', item).length === 2)
            && $('[data-testid="generation-samples"] h3').textContent.includes('seed ' + $('[data-testid="launch-seed"]').value)
            && text('[data-testid="generation-samples-note"]').startsWith('Так будут выглядеть первые 4 кадра'), '«Пример кадра» shows 4 frames with their boxes before saving');
          const open = $('[data-testid="generation-sample-open"]', shown[0]);
          open.focus();
          open.click();
          const viewer = await wait(() => $('[data-image-viewer]') && $('[data-viewer-image]')?.naturalWidth === width && $('[data-image-viewer]'), 'Sample viewer');
          assert(viewer.textContent.includes('Знак UI · Сумерки UI') && $('[data-testid="generation-sample-details"]', viewer).textContent.includes('объектов: 2'),
            'A sample opens in the shared viewer with its boxes');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-image-viewer]') && document.activeElement === open, 'Viewer closed');
          assert(true, 'Closing the sample viewer returns focus to the sample');
          setValue($('[data-testid="recipe-objects"]'), '1');
          await wait(() => text('[data-testid="generation-samples-note"]').startsWith('Настройки или seed изменились'), 'Stale samples');
          setValue($('[data-testid="recipe-objects"]'), '2');
          await wait(() => text('[data-testid="generation-samples-note"]').startsWith('Так будут выглядеть'), 'Samples current again');
          assert(true, 'Samples are marked stale when the form changes');
          return {checks};
        """;

    private const string GenerationUiRecipeScript = """
          $('[data-testid="recipe-save"]').click();
          const saved = await wait(() => card('Улица UI пресет'), 'Recipe saved');
          assert(saved.dataset.revision === '1' && text('[data-testid="tab-recipes"]') === 'Пресеты (1)' && $('[data-testid="recipe-name"]').value === ''
            && text('[data-testid="generation-notice"]').includes('Пресет «Улица UI пресет» сохранён.'), 'The recipe is saved from the form');
          assert($('[data-testid="recipe-variants"]', saved).textContent.includes('Сумерки UI — 100% · Знак UI') && $('[data-testid="recipe-sets"]', saved).textContent === 'Фоны UI · 2 фона'
            && $('[data-testid="recipe-details"]', saved).textContent.includes(width + ' × ' + height + ' px'), 'The recipe card shows its details');

          labelled('Изменить пресет: Улица UI пресет').focus();
          labelled('Изменить пресет: Улица UI пресет').click();
          await wait(() => text('[data-testid="recipe-form-title"]') === 'Изменить пресет' && document.activeElement === $('[data-testid="recipe-name"]'), 'Edit form');
          assert($('[data-testid="recipe-name"]').value === 'Улица UI пресет' && $('[data-testid="recipe-scale"]').value === '0.4' && weightOf(variantId).value === '100'
            && $('button[role="checkbox"]', setRow('Фоны UI')).getAttribute('aria-checked') === 'true', '«Изменить» fills the form with the recipe and focuses the name');
          setValue($('[data-testid="recipe-name"]'), 'Улица UI');
          setValue($('[data-testid="recipe-scale"]'), '1');
          (await wait(() => enabled($('[data-testid="recipe-save"]')), 'Save edit')).click();
          await wait(() => card('Улица UI')?.dataset.revision === '2' && text('[data-testid="recipe-form-title"]') === 'Новый пресет', 'Recipe edited');
          await wait(() => document.activeElement === labelled('Изменить пресет: Улица UI'), 'Focus back to the card');
          const stored = await request('generation.listRecipes', {projectId});
          const recipe = stored.data.find(item => item.name === 'Улица UI');
          assert(recipe && recipe.revision === 2 && recipe.scale === 1 && recipe.objectsPerImage === 2 && recipe.emptyProbability === 0 && text('[data-testid="tab-recipes"]') === 'Пресеты (1)',
            'Editing saves a new revision of the same recipe and returns focus to the card');

          labelled('В архив: Улица UI').click();
          await wait(() => !card('Улица UI') && $('[data-testid="recipes-empty"]'), 'Archived hidden');
          $('[data-testid="show-archived-recipes"]').click();
          const archived = await wait(() => card('Улица UI')?.dataset.archived && card('Улица UI'), 'Archived shown');
          assert($('[data-testid="recipe-archived"]', archived).textContent === 'Архив' && $('[data-testid="recipe-start"]', archived).disabled
            && $('[data-testid="recipe-edit"]', archived).disabled, 'An archived recipe is shown with «Показать архивные пресеты» and cannot run or change');
          labelled('Восстановить: Улица UI').click();
          await wait(() => card('Улица UI') && !card('Улица UI').dataset.archived && enabled($('[data-testid="recipe-start"]', card('Улица UI'))), 'Restored');
          $('[data-testid="show-archived-recipes"]').click();
          await wait(() => $('[data-testid="show-archived-recipes"]').getAttribute('aria-checked') === 'false' && card('Улица UI'), 'Archive filter off');
          assert(true, 'The recipe is restored from the archive');

          setValue($('[data-testid="launch-count"]'), '0');
          await wait(() => text('[data-testid="launch-hint"]') === 'Укажите целое количество от 1.' && $('[data-testid="recipe-start"]').disabled, 'Count hint');
          assert(true, 'An invalid count shows the old hint and blocks the run');
          setValue($('[data-testid="launch-count"]'), '8');
          await wait(() => text('[data-testid="recipe-start"]') === 'Сгенерировать 8 изображений', 'Count 8');
          assert(true, 'The run button names the count');
          return {checks};
        """;

    private const string GenerationUiRunScript = """
          const seen = new Set();
          const watch = () => { const item = runCard(); if (item) seen.add(item.dataset.status + ':' + $('[data-testid="run-progress"]', item).getAttribute('aria-valuenow')); };
          const observer = new MutationObserver(watch);
          observer.observe(document.body, {subtree: true, childList: true, attributes: true, attributeFilter: ['data-status', 'aria-valuenow']});
          const seed = $('[data-testid="launch-seed"]').value;
          const revision = card('Улица UI').dataset.revision;
          try {
            $('[data-testid="recipe-start"]', card('Улица UI')).click();
            await wait(() => text('[data-testid="generation-notice"]').includes('Запуск «Улица UI» поставлен в очередь: 8 изображений.'), 'Run queued');
            $('[data-testid="notice-show-runs"]').click();
            const done = await wait(() => runCard()?.dataset.status === 'succeeded' && runCard(), 'Run finished', () => [...seen].join(', '));
            watch();
            assert([...seen].some(value => value.startsWith('queued:') || value.startsWith('running:')) && seen.has('succeeded:100')
              && $('[data-testid="tab-runs"]').getAttribute('aria-pressed') === 'true', 'The run card follows the run live from job.changed: ' + [...seen].join(', '));
            assert(text('[data-testid="run-summary"]') === '8 / 8 изображений · 100%. Кадров с недостающими объектами: 8' && text('[data-testid="run-status"]').startsWith('Готово · ')
              && text('[data-testid="tab-runs"]') === 'Запуски (1)' && !$('[data-testid="run-cancel"]', done) && $('[data-testid="run-repeat"]', done) && !$('[data-testid="run-error"]', done),
              'The finished run shows its result with «Кадров с недостающими объектами: N»');
            const settings = $('[data-testid="run-settings"]', done);
            settings.open = true;
            assert(settings.textContent.includes('Seed: ' + seed + ' · количество: 8 · версия пресета: ' + revision) && settings.textContent.includes('Группа результата: Улица UI · '),
              '«Настройки этого запуска» show the seed, count and recipe revision');
          } finally {
            observer.disconnect();
          }
          const runPage = await request('generation.listRuns', {projectId});
          const groupId = runPage.data.items[0].outputGroupId;
          $('[data-testid="run-open-images"]').click();
          const groupButton = await wait(() => $('[data-image-group-id="' + groupId + '"]')?.getAttribute('aria-pressed') === 'true' && $('[data-image-group-id="' + groupId + '"]'), 'Images with the group');
          const generated = await wait(() => {
            const items = $$('[data-testid="image-tile"]');
            return items.length === 8 && items.every(item => item.querySelector('[data-status="loaded"] img')?.naturalWidth > 0) && items;
          }, 'Generated thumbnails', () => $$('[data-testid="image-tile"]').length + ' tiles');
          await wait(() => document.activeElement === groupButton, 'Group focused');
          assert($('[data-section="images"]').getAttribute('aria-current') === 'page' && text('[data-testid="image-group-title"]').startsWith('Улица UI · ')
            && generated.every(item => $('[data-testid="image-meta"]', item).textContent.includes('рамок: 1 · проверено')),
            '«Открыть изображения» opens the output group with its thumbnails');

          $('[data-section="classes"]').click();
          const classRow = () => $$('[data-class-id]').find(item => item.querySelector('button span')?.textContent.endsWith('Знак UI'));
          await wait(() => classRow(), 'Classes list');
          labelled('Удалить класс: Знак UI').click();
          const blocked = await wait(() => $('[data-testid="deletion-dialog"] [role="alert"]'), 'Class deletion review');
          assert(blocked.textContent.includes('Пресет «Улица UI»') && $('[data-testid="deletion-confirm"]').disabled, 'Deleting the class is blocked by the recipe with its name');
          [...$('[data-testid="deletion-dialog"]').querySelectorAll('button')].find(item => item.textContent === 'Отмена').click();
          await wait(() => !$('[data-testid="deletion-dialog"]'), 'Deletion dialog closed');
          return {checks};
        """;

    private const string GenerationUiRepeatScript = """
          $('[data-section="generation"]').click();
          await wait(() => form() && runCard()?.dataset.status === 'succeeded', 'Generation again');
          $('[data-testid="tab-runs"]').click();
          labelled('Настроить повтор: Улица UI').click();
          await wait(() => form().dataset.mode === 'repeat' && document.activeElement === $('[data-testid="recipe-name"]'), 'Repeat form');
          const runPage = await request('generation.listRuns', {projectId});
          const run = runPage.data.items[0];
          assert($('[data-testid="recipe-name"]').value === 'Улица UI · повтор' && text('[data-testid="repeat-notice"]').startsWith('Настройки сохранённого запуска')
            && $('[data-testid="launch-count"]').value === '8' && $('[data-testid="launch-seed"]').value === String(run.seed) && $('[data-testid="recipe-scale"]').value === '1'
            && $('button[role="checkbox"]', setRow('Фоны UI')).getAttribute('aria-checked') === 'true' && $('[data-testid="tab-recipes"]').getAttribute('aria-pressed') === 'true',
            '«Настроить повтор» fills the form, the seed and the count from the run');

          $('[data-testid="tab-runs"]').click();
          labelled('Удалить запуск: Улица UI').click();
          const dialog = await wait(() => $('[data-testid="generation-deletion-dialog"]'), 'Run deletion dialog');
          assert(dialog.textContent.includes('Удалить запуск из истории?') && dialog.textContent.includes('Созданная группа, изображения и разметка сохранятся.'),
            'The run deletion dialog has the old texts');
          $('[data-testid="generation-deletion-confirm"]').click();
          await wait(() => !$('[data-testid="generation-deletion-dialog"]') && !runCard() && $('[data-testid="runs-empty"]') && text('[data-testid="tab-runs"]') === 'Запуски (0)', 'Run deleted');
          const kept = await request('images.getGroup', {projectId, groupId: run.outputGroupId});
          assert(kept.ok && kept.data.imageCount === 8, 'Deleting the run keeps its images');

          $('[data-testid="tab-recipes"]').click();
          await wait(() => form().dataset.mode === 'repeat' && $('[data-testid="tab-recipes"]').getAttribute('aria-pressed') === 'true', 'Recipes tab');
          (await wait(() => enabled($('[data-testid="recipe-samples"]')), 'Samples enabled')).click();
          const shown = await wait(() => {
            const items = samplesLoaded();
            return items && $('[data-testid="generation-samples"] h3').textContent.includes('seed ' + run.seed) && items;
          }, 'Repeat samples');
          assert(shown.every(item => item.dataset.objects === '1' && item.textContent.includes('не поместилось: 1')), 'Samples of the repeat show the objects that did not fit');
          $('[data-testid="generation-samples"]').scrollIntoView({block: 'end'});
          await sleep(300);
          return {checks};
        """;

    /// <summary>
    /// The datasets section in the real page: tabs (role=tab, arrows, Home/End), the build form with group counts, origin,
    /// назначение and the reviewed share, a plan with counts, itemised skip reasons and the mixed-source warning, a stale plan,
    /// the build with live progress from job.changed and «Открыть датасет», the passport, class order, the sample grid with
    /// boxes and the shared viewer with navigation, an integrity check, an export through the «Сохранить как» seam with the file
    /// name, history deletion, «Настроить повтор» and a second build, editing, search, archive blocking verify and export,
    /// deletion through the dialog. Groups (assigned, unassigned, a duplicate), reviewed annotations and a class are seeded
    /// through Core. The page ends on the «Датасеты» tab with the second version and its samples for the capture.
    /// </summary>
    private static async Task CheckDatasetsUiAsync(CoreWebView2 web, string root, string projectId, ICore core, JsonNode report, CancellationToken token)
    {
        var project = await core.Projects.GetAsync(projectId, token);
        var size = project.InputSize;
        var directory = Path.Combine(root, "smoke-datasets-ui");
        Directory.CreateDirectory(directory);
        var variant = await project.Variants.CreateAsync("Дневной UI", "", token);
        var klass = await project.Classes.CreateAsync("Знак датасета UI", "", token);
        var binding = await project.Classes.AddVariantAsync(klass.Id, variant.Id, token);
        var unassigned = await project.Images.CreateGroupAsync("Улица UI без назначения", "", ImageSplitHint.Unassigned, token);
        var validation = await project.Images.CreateGroupAsync("Улица UI проверка", "", ImageSplitHint.Validation, token);
        var duplicates = await project.Images.CreateGroupAsync("Улица UI дубли", "", ImageSplitHint.Unassigned, token);
        string Write(int index)
        {
            var path = Path.Combine(directory, $"улица-{index:00}.jpg");
            if (!File.Exists(path)) File.WriteAllBytes(path, Photo(size.Width, size.Height, 20 + index));
            return path;
        }
        // 9 unassigned frames (one reviewed empty), 4 validation frames (one unreviewed) and a copy of a validation frame.
        var imports = new[]
        {
            (unassigned.Id, Enumerable.Range(0, 9).Select(Write).ToArray()),
            (validation.Id, Enumerable.Range(9, 4).Select(Write).ToArray()),
            (duplicates.Id, new[] { Write(9) }),
        };
        foreach (var (groupId, paths) in imports)
        {
            var job = await core.Jobs.EnqueueAsync<IImportImagesJob, ImportImagesInput>(new ImportImagesInput(groupId,
                paths.Select(path => new ImageImportSource(ImageImportSourceKind.File, path)).ToArray()), projectId, "datasets-ui-import-" + groupId, cancellationToken: token);
            for (var attempt = 0; ; attempt++)
            {
                var current = await core.Jobs.GetAsync(projectId, job.Id, token);
                if (current.Status == JobStatus.Succeeded) break;
                if (current.Status is not (JobStatus.Queued or JobStatus.Running) || attempt > 600)
                    throw new InvalidOperationException($"Datasets UI fixture import is {current.Status}.");
                await Task.Delay(50, token);
            }
        }
        var box = new AnnotationObjectInput(null, klass.Id, binding.Id, new ImageBox(0.3, 0.25, 0.35, 0.4));
        foreach (var (groupId, paths) in imports)
        {
            var images = (await project.Images.ListImagesAsync(groupId, new ImageFilter(), 100, cancellationToken: token)).Items;
            for (var index = 0; index < images.Count; index++)
            {
                var image = images[index];
                if (groupId == validation.Id && index == images.Count - 1) continue;
                var annotation = groupId == unassigned.Id && index == 0
                    ? new AnnotationInput([], null, ImageReviewState.Reviewed)
                    : new AnnotationInput([box], groupId == unassigned.Id && index == 1 ? new ImageBox(0.05, 0.05, 0.9, 0.9) : null, ImageReviewState.Reviewed);
                await project.Images.SaveAnnotationAsync(image.Id, annotation, image.AnnotationRevision, token);
            }
        }
        var destination = Path.Combine(directory, "Улица UI.zip");
        MainWindow.SmokeSaveAnswer = destination;
        var checks = report["checks"]!.AsArray();
        var arguments = JsonSerializer.Serialize(new
        {
            projectId, unassignedId = unassigned.Id, validationId = validation.Id, duplicateId = duplicates.Id, className = klass.Name, variantName = variant.Name,
            width = size.Width, height = size.Height,
        });
        foreach (var body in new[] { DatasetsUiPlanScript, DatasetsUiBuildScript, DatasetsUiOperationsScript, DatasetsUiManageScript })
        {
            var script = "(async ({projectId, unassignedId, validationId, duplicateId, className, variantName, width, height}) => {" + BackgroundsUiHelpers
                + DatasetsUiHelpers + body + "})(" + arguments + ")";
            await web.ExecuteScriptAsync("window.__smokeScript = undefined;");
            var result = await RunScriptAsync(web, script, token, attempts: 900);
            foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        }
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0) throw new InvalidOperationException("The UI export did not write the chosen ZIP.");
        checks.Add("«Экспорт ZIP» from the UI writes the file chosen in «Сохранить как»");
    }

    private const string DatasetsUiHelpers = """
          const tab = id => $('[data-testid="datasets-tab-' + id + '"]');
          const selectedTab = () => $('[role="tab"][aria-selected="true"]')?.dataset.testid;
          const groupRow = id => $('[data-dataset-group-id="' + id + '"]');
          const groupBox = id => $('button[role="checkbox"]', groupRow(id));
          const planCard = () => $('[data-testid="dataset-plan-card"]');
          const operations = kind => $$('[data-dataset-operation-id][data-kind="' + kind + '"]');
          const operation = (kind, id) => $('[data-dataset-operation-id="' + id + '"][data-kind="' + kind + '"]');
          const versionButton = id => $('[data-dataset-version-id="' + id + '"]');
          const sampleTiles = () => $$('[data-testid="dataset-sample-tile"]');
          const tilesLoaded = count => sampleTiles().length === count && sampleTiles().every(tile => tile.querySelector('[data-status="loaded"] img')?.naturalWidth > 0) && sampleTiles();
          const ended = element => element && ['succeeded', 'failed', 'cancelled', 'interrupted'].includes(element.dataset.status) ? element : null;
          const progressOf = element => $('[data-testid="operation-progress-text"]', element)?.textContent ?? '';

        """;

    private const string DatasetsUiPlanScript = """
          (await wait(() => enabled($('[data-section="datasets"]')), 'Datasets section enabled')).click();
          await wait(() => $('[data-testid="datasets-section"]') && $('[data-testid="datasets-empty"]'), 'Datasets opened');
          assert(document.body.textContent.includes('Неизменяемые версии готовых кадров и разметки.') && selectedTab() === 'datasets-tab-datasets'
            && text('[data-testid="datasets-empty"]') === 'Соберите датасет во вкладке «Сборка». Исходные изображения сохранятся.'
            && $('[data-testid="dataset-operations-verify-empty"]').textContent.startsWith('Откройте датасет во вкладке «Датасеты»'),
            'Datasets opens lazily on the «Датасеты» tab with the old empty texts');

          tab('datasets').focus();
          key(tab('datasets'), 'ArrowRight');
          await wait(() => selectedTab() === 'datasets-tab-build' && document.activeElement === tab('build'), 'ArrowRight');
          key(tab('build'), 'End');
          await wait(() => selectedTab() === 'datasets-tab-export' && document.activeElement === tab('export'), 'End');
          key(tab('export'), 'ArrowRight');
          await wait(() => selectedTab() === 'datasets-tab-datasets' && document.activeElement === tab('datasets'), 'ArrowRight wraps');
          key(tab('datasets'), 'ArrowLeft');
          await wait(() => selectedTab() === 'datasets-tab-export', 'ArrowLeft wraps');
          key(tab('export'), 'Home');
          await wait(() => selectedTab() === 'datasets-tab-datasets' && document.activeElement === tab('datasets'), 'Home');
          assert($$('[role="tablist"] [role="tab"]').map(item => item.textContent).join(',') === 'Датасеты,Сборка,Проверки,Экспорты'
            && tab('datasets').tabIndex === 0 && tab('build').tabIndex === -1 && $('#dataset-panel-build').hidden
            && tab('datasets').getAttribute('aria-controls') === 'dataset-panel-datasets', 'Tabs are role=tab with arrows, Home and End and one tab stop');

          key(tab('datasets'), 'ArrowRight');
          await wait(() => selectedTab() === 'datasets-tab-build' && $('[data-testid="dataset-build-form"]') && groupRow(unassignedId), 'Build tab');
          await wait(() => [unassignedId, validationId, duplicateId].every(id => groupRow(id)?.querySelector('[data-reviewed]')), 'Reviewed counts',
            () => [unassignedId, validationId].map(id => groupRow(id)?.textContent).join(' | '));
          const meta = id => $('[data-testid="dataset-group-meta"]', groupRow(id)).textContent;
          assert(meta(unassignedId) === '9 кадров · Исходные изображения · Без назначения · проверено: 100% (9)'
            && meta(validationId) === '4 кадров · Исходные изображения · Проверка · проверено: 75% (3)',
            'Groups show the image count, origin, назначение and the share of reviewed images: ' + meta(validationId));
          assert($('[data-testid="dataset-plan"]').disabled && text('[data-testid="dataset-form-hint"]') === 'Укажите название датасета до 120 символов.'
            && text('[data-testid="dataset-validation-hint"]') === 'Для проверки (Validation): 20% — остаток после Train и Test.'
            && $('#dataset-train').value === '80' && $('#dataset-test').value === '0', 'A new form starts at Train 80 / Test 0 and cannot be planned without a name');

          setValue($('[data-testid="dataset-name"]'), 'Улица UI');
          for (const id of [unassignedId, validationId, duplicateId]) groupBox(id).click();
          labelled('Класс: ' + className).click();
          await wait(() => enabled($('[data-testid="dataset-plan"]')), 'Form valid', () => text('[data-testid="dataset-form-hint"]'));
          setValue($('#dataset-train'), '70');
          await wait(() => text('[data-testid="dataset-validation-hint"]') === 'Для проверки (Validation): 30% — остаток после Train и Test.', 'Train 70');
          setValue($('#dataset-test'), '45');
          await wait(() => $('#dataset-test').value === '29' && text('[data-testid="dataset-validation-hint"]').startsWith('Для проверки (Validation): 1%'), 'Test clamped');
          assert(true, 'Train and Test are clamped so that Validation keeps the remainder');
          setValue($('#dataset-test'), '0');
          $('[data-testid="dataset-advanced"]').open = true;
          setValue($('[data-testid="dataset-seed"]'), 'abc');
          await wait(() => text('[data-testid="dataset-seed-hint"]') === 'Seed — целое число от 0 до 4 294 967 295.' && $('[data-testid="dataset-plan"]').disabled, 'Seed invalid');
          assert($('[data-testid="dataset-include-empty"]').getAttribute('aria-checked') === 'true' && $('[data-testid="dataset-allow-partial"]').getAttribute('aria-checked') === 'false',
            'A wrong seed blocks the plan; empty frames are on and unfinished generations off by default');
          setValue($('[data-testid="dataset-seed"]'), '7');
          (await wait(() => enabled($('[data-testid="dataset-plan"]')), 'Seed valid')).click();
          const firstPlanId = (await wait(() => planCard(), 'Plan')).dataset.planId;
          const planned = (await request('datasets.getPlan', {projectId, planId: firstPlanId})).data;
          const mixed = planned.warnings.find(warning => warning.code === 'datasets.mixed_source_splits');
          assert(planned.counts.total === 13 && planned.skipped.total === 1 && text('[data-testid="dataset-plan-counts"]')
            === 'Train ' + planned.counts.train + ' · Validation ' + planned.counts.val + ' · Test 0 · пропущено 1', 'The plan card shows the split counts: ' + text('[data-testid="dataset-plan-counts"]'));
          assert(text('[data-reason="unreviewed"]') === 'не проверены: 1' && $$('[data-testid="dataset-plan-skips"] li').length === 1, 'Skip reasons are itemised');
          assert(mixed && text('[data-warning="datasets.mixed_source_splits"]') === mautLocale.planWarningText(mixed), 'The plan shows the mixed-source warning: ' + (mixed && mautLocale.planWarningText(mixed)));
          assert($('[data-testid="dataset-build"]').disabled === !(planned.buildable && planned.sufficientSamples)
            && (planned.buildable || !!$('[data-warning="datasets.split_collision"]')), '«Собрать датасет» follows the plan (split collision disables it)');

          groupBox(duplicateId).click();
          await wait(() => $('[data-testid="dataset-plan-stale"]') && $('[data-testid="dataset-build"]').disabled, 'Stale plan');
          assert(true, 'A form changed after the plan marks it stale and disables «Собрать датасет»');
          $('[data-testid="dataset-plan"]').click();
          const second = await wait(() => planCard()?.dataset.planId !== firstPlanId && !$('[data-testid="dataset-plan-stale"]') && planCard(), 'Second plan',
            () => [planCard()?.dataset.planId, firstPlanId, text('[data-testid="datasets-notice"]'), !!$('[data-testid="dataset-plan-stale"]'), $('[data-testid="dataset-plan"]').disabled].join(' | '));
          assert(!$('[data-warning="datasets.mixed_source_splits"]', second) && enabled($('[data-testid="dataset-build"]')) && second.dataset.buildable === 'true',
            'Without the duplicate group the plan has no mixed-source warning and can be built');
          return {checks};
        """;

    private const string DatasetsUiBuildScript = """
          const seen = new Set();
          const watch = () => { for (const item of operations('build')) seen.add(item.dataset.status + ':' + $('[data-testid="operation-progress"]', item)?.getAttribute('aria-valuenow')); };
          const observer = new MutationObserver(watch);
          observer.observe(document.body, {subtree: true, childList: true, attributes: true, attributeFilter: ['data-status', 'aria-valuenow']});
          let card;
          try {
            $('[data-testid="dataset-build"]').click();
            await wait(() => text('[data-testid="datasets-notice"]').startsWith('Сборка «Улица UI» поставлена в очередь.'), 'Build queued');
            card = await wait(() => ended(operations('build')[0]), 'Build finished', () => [...seen].join(', '));
            watch();
          } finally {
            observer.disconnect();
          }
          assert(card.dataset.status === 'succeeded' && [...seen].some(value => value.startsWith('queued:') || value.startsWith('running:')) && seen.has('succeeded:100'),
            'The build card follows the build live from job.changed: ' + [...seen].join(', '));
          assert(progressOf(card) === 'Собрано изображений: 12 из 12' && !planCard() && $('[data-testid="operation-title"]', card).textContent === 'Улица UI · Готово',
            'The finished build shows «Собрано изображений: 12 из 12»');
          const versionId = card.dataset.datasetOperationId;
          (await wait(() => $('[data-testid="operation-open-dataset"]', card), 'Open dataset button')).click();
          const button = await wait(() => selectedTab() === 'datasets-tab-datasets' && versionButton(versionId)?.getAttribute('aria-pressed') === 'true'
            && document.activeElement === versionButton(versionId) && versionButton(versionId), 'Dataset opened');
          const version = (await request('datasets.get', {projectId, versionId})).data;
          assert(button.textContent.startsWith('Улица UI12 изображений · ') && !$('[data-testid="dataset-size-mismatch"]', button),
            '«Открыть датасет» switches to «Датасеты» with the version selected and focused');
          assert(text('[data-testid="dataset-counts"]') === 'Готовые кадры · Train ' + version.counts.train + ' · Validation ' + version.counts.val + ' · Test 0'
            && text('[data-testid="dataset-integrity"]').startsWith('Целостность: проверена') && text('[data-testid="dataset-integrity"]').endsWith('· Активен')
            && text('[data-testid="dataset-built"]').endsWith('· Групп-источников: 2 · Seed 7')
            && text('[data-testid="dataset-class-order"]') === 'Сохранённый порядок классов: 0: ' + className, 'The passport shows parts, integrity, the build and the class order');

          const split = version.counts.train > 0 ? 'train' : 'val';
          const shown = await wait(() => tilesLoaded(version.counts[split]), 'Sample thumbnails', () => sampleTiles().length + ' tiles');
          assert(text('[data-testid="dataset-sample-count"]') === version.counts[split] + ' из ' + version.counts[split] + ' · Снимки при сборке'
            && shown.every(tile => $$('rect[data-kind="object"]', tile).length === Number(tile.dataset.objects))
            && shown.some(tile => tile.dataset.objects === '1' && $('[data-testid="dataset-sample-caption"]', tile).textContent === width + ' × ' + height + ' · 1 объектов · 0 негативов'),
            'The sample grid shows thumbnails with their boxes and «W × H · n объектов · m негативов»');

          const index = shown.findIndex((tile, position) => tile.dataset.objects === '1' && position < shown.length - 1);
          const open = $('[data-testid="dataset-sample-open"]', shown[index]);
          open.focus();
          open.click();
          const viewer = await wait(() => $('[data-image-viewer]') && $('[data-viewer-image]')?.naturalWidth === width && $('[data-image-viewer]'), 'Viewer');
          assert(text('[data-testid="viewer-position"]') === (index + 1) + ' / ' + version.counts[split] && $$('[data-viewer-overlay^="object"]', viewer).length === 1
            && $('[data-testid="dataset-sample-details"]', viewer).textContent.includes('Объект 1: 0. ' + className)
            && $('[data-testid="dataset-sample-details"]', viewer).textContent.includes('Вариант: ' + variantName), 'The viewer shows the sample with its box and the variant name');
          labelled('Рамки объектов датасета', viewer).click();
          await wait(() => !$$('[data-viewer-overlay^="object"]', viewer).length, 'Objects hidden');
          assert(true, '«Объекты» hides the object boxes');
          key(document.activeElement ?? viewer, 'ArrowRight');
          await wait(() => text('[data-testid="viewer-position"]') === (index + 2) + ' / ' + version.counts[split], 'Next sample');
          assert(true, 'The viewer moves to the next sample');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-image-viewer]') && document.activeElement === open, 'Viewer closed');
          assert(true, 'Closing the viewer returns focus to the sample');
          return {checks, versionId};
        """;

    private const string DatasetsUiOperationsScript = """
          const versionId = $('[data-testid="dataset-details"]').dataset.datasetDetailsId;
          $('[data-testid="dataset-verify"]').click();
          const verify = await wait(() => selectedTab() === 'datasets-tab-verify' && ended(operations('verify')[0]), 'Verify finished');
          const files = /^Проверено файлов: (\d+) из (\d+)$/.exec(progressOf(verify));
          assert(verify.dataset.status === 'succeeded' && files && files[1] === files[2] && Number(files[1]) === 26
            && text('[data-testid="operation-integrity"]') === 'Целостность: проверена' && !$('[data-testid="operation-issues"]', verify),
            'The integrity check completes verified with «Проверено файлов: 26 из 26»');
          (await wait(() => $('[data-testid="operation-open-dataset"]', verify), 'Open dataset from the check')).click();
          await wait(() => selectedTab() === 'datasets-tab-datasets' && versionButton(versionId)?.getAttribute('aria-pressed') === 'true', 'Back to the dataset');

          $('[data-testid="dataset-export"]').click();
          const exported = await wait(() => selectedTab() === 'datasets-tab-export' && ended(operations('export')[0]), 'Export finished');
          assert(exported.dataset.status === 'succeeded' && progressOf(exported) === 'Экспортировано изображений: 12 из 12'
            && $('[data-testid="operation-file"]', exported).textContent.startsWith('Файл: Улица UI.zip · ')
            && $('[data-testid="operation-repeat"]', exported).textContent === 'Сохранить ещё раз', 'The export shows the file name, its progress and «Сохранить ещё раз»');

          tab('verify').click();
          $('[data-testid="operation-delete"]', verify).click();
          const dialog = await wait(() => $('[data-testid="dataset-operation-deletion-dialog"]'), 'Operation deletion dialog');
          assert(dialog.textContent.includes('Удалить запись из истории?') && dialog.textContent.includes('Готовые датасеты, исходные изображения и сохранённые вами ZIP-файлы останутся.'),
            'The history deletion dialog has the old texts');
          $('[data-testid="dataset-operation-deletion-confirm"]').click();
          await wait(() => !$('[data-testid="dataset-operation-deletion-dialog"]') && !operations('verify').length && $('[data-testid="dataset-operations-verify-empty"]'), 'Check deleted');
          assert(true, '«Удалить из истории» removes the finished check');

          tab('build').click();
          $('[data-testid="operation-repeat"]', operation('build', versionId)).click();
          await wait(() => selectedTab() === 'datasets-tab-build' && document.activeElement === $('[data-testid="dataset-name"]') && $('[data-testid="dataset-repeat-notice"]'), 'Repeat form');
          assert($('[data-testid="dataset-name"]').value === 'Улица UI' && $('[data-testid="dataset-seed"]').value === '7' && $('#dataset-train').value === '70'
            && groupBox(unassignedId).getAttribute('aria-checked') === 'true' && groupBox(validationId).getAttribute('aria-checked') === 'true'
            && groupBox(duplicateId).getAttribute('aria-checked') === 'false' && labelled('Класс: ' + className).getAttribute('aria-checked') === 'true',
            '«Настроить повтор» copies the build specification into the form');
          return {checks};
        """;

    private const string DatasetsUiManageScript = """
          const firstId = operations('build')[0].dataset.datasetOperationId;
          setValue($('[data-testid="dataset-name"]'), 'Улица UI повтор');
          (await wait(() => enabled($('[data-testid="dataset-plan"]')), 'Plan enabled')).click();
          (await wait(() => planCard() && enabled($('[data-testid="dataset-build"]')), 'Repeat plan')).click();
          const repeated = await wait(() => operations('build').length === 2 && ended(operations('build').find(item => item.dataset.datasetOperationId !== firstId)), 'Second build');
          const secondId = repeated.dataset.datasetOperationId;
          assert(repeated.dataset.status === 'succeeded', 'The repeated specification builds a second version');

          $('[data-testid="operation-open-dataset"]', operation('build', firstId)).click();
          await wait(() => versionButton(firstId)?.getAttribute('aria-pressed') === 'true' && versionButton(secondId), 'First version');
          $('[data-testid="dataset-edit"]').click();
          const editor = await wait(() => $('[data-testid="dataset-edit-dialog"]'), 'Edit dialog');
          setValue($('[data-testid="dataset-edit-name"]'), 'Улица UI первая');
          $('[data-testid="dataset-edit-save"]').click();
          await wait(() => !$('[data-testid="dataset-edit-dialog"]') && text('[data-testid="dataset-title"]') === 'Улица UI первая' && versionButton(firstId).textContent.startsWith('Улица UI первая'), 'Renamed');
          assert(true, '«Изменить» renames the version');

          setValue($('[data-testid="dataset-search"]'), 'первая');
          await wait(() => versionButton(firstId) && !versionButton(secondId), 'Search');
          setValue($('[data-testid="dataset-search"]'), 'нет такого');
          await wait(() => !versionButton(firstId) && text('[data-testid="datasets-nothing-found"]') === 'Ничего не найдено. Измените запрос.', 'Nothing found');
          setValue($('[data-testid="dataset-search"]'), '');
          await wait(() => versionButton(firstId) && versionButton(secondId), 'Search cleared');
          assert(true, 'Search filters the list by name');

          versionButton(firstId).click();
          await wait(() => versionButton(firstId).getAttribute('aria-pressed') === 'true', 'First selected');
          $('[data-testid="dataset-archive"]').click();
          await wait(() => !versionButton(firstId) && text('[data-testid="datasets-notice"]').startsWith('Датасет «Улица UI первая» перенесён в архив.'), 'Archived',
            () => text('[data-testid="datasets-notice"]'));
          $('[data-testid="dataset-show-archived"]').click();
          (await wait(() => versionButton(firstId)?.dataset.archived && versionButton(firstId), 'Archive shown')).click();
          await wait(() => versionButton(firstId).getAttribute('aria-pressed') === 'true' && $('[data-testid="dataset-archived-note"]'), 'Archived selected');
          assert(versionButton(firstId).textContent.startsWith('Улица UI первая · В архиве') && $('[data-testid="dataset-verify"]').disabled && $('[data-testid="dataset-export"]').disabled
            && $('[data-testid="dataset-edit"]').disabled && text('[data-testid="dataset-archive"]') === 'Восстановить', 'An archived dataset cannot be checked, exported or edited');
          const exportCard = operations('export')[0];
          assert($('[data-testid="operation-repeat"]', exportCard).disabled, 'The export of an archived dataset cannot be saved again');
          const blocked = await request('datasets.startVerify', {projectId, versionId: firstId, operationId: crypto.randomUUID()});
          assert(!blocked.ok && blocked.error.code === 'datasets.archived', 'The host also refuses to check an archived dataset');

          const review = (await request('datasets.reviewDeletion', {projectId, versionId: firstId})).data;
          $('[data-testid="dataset-delete"]').click();
          const dialog = await wait(() => $('[data-testid="dataset-deletion-counts"]') && $('[data-testid="dataset-deletion-dialog"]'), 'Deletion review');
          assert(dialog.textContent.includes('Удалить датасет навсегда?') && text('[data-testid="dataset-deletion-counts"]') === 'Изображений: ' + review.samples + ' · операций: ' + review.operations
            && review.samples === 12, 'The deletion dialog shows the samples and operations of the version');
          $('[data-testid="dataset-deletion-confirm"]').click();
          await wait(() => !$('[data-testid="dataset-deletion-dialog"]') && !versionButton(firstId) && text('[data-testid="datasets-notice"]').startsWith('Удалено.')
            && !operation('build', firstId) && !operations('export').length, 'Dataset deleted');
          assert(versionButton(secondId), 'Deleting a dataset removes it and its history; the other version stays');

          $('[data-testid="dataset-show-archived"]').click();
          versionButton(secondId).click();
          const version = (await request('datasets.get', {projectId, versionId: secondId})).data;
          const split = version.counts.train > 0 ? 'train' : 'val';
          await wait(() => versionButton(secondId).getAttribute('aria-pressed') === 'true' && tilesLoaded(version.counts[split]), 'Final grid');
          $('[data-testid="datasets-notice"] button')?.click();
          $('[data-testid="dataset-class-order"]').scrollIntoView({block: 'start'});
          await sleep(300);
          return {checks};
        """;

    /// <summary>Runs an async expression in the page and waits for its JSON result; ExecuteScriptAsync does not await promises.</summary>
    private static async Task<JsonNode> RunScriptAsync(CoreWebView2 web, string expression, CancellationToken token, int attempts = 150)
    {
        // Reset apart from the script: a script that does not parse must not leave the result of the previous one in place.
        await web.ExecuteScriptAsync("window.__smokeScript = undefined;");
        await web.ExecuteScriptAsync("Promise.resolve(" + expression + ").then(value => window.__smokeScript = {ok: true, value}, error => window.__smokeScript = {ok: false, error: String(error?.stack ?? error)});");
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var state = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("JSON.stringify(window.__smokeScript ?? null)"));
            if (state is not null && state != "null")
            {
                var node = JsonNode.Parse(state)!;
                if (!node["ok"]!.GetValue<bool>()) throw new InvalidOperationException("Page script failed: " + node["error"]);
                return node["value"]!;
            }
            await Task.Delay(100, token);
        }
        throw new TimeoutException("Page script did not finish.");
    }

    private const string ResourceScript = """
        async ({original, thumbnail, guarded, unknown, expected, length}) => {
          const checks = [];
          const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
          const load = src => new Promise((resolve, reject) => {
            const image = document.createElement('img');
            image.decoding = 'async';
            image.onload = () => resolve(image);
            image.onerror = () => reject(new Error('Image did not load: ' + src));
            image.src = src;
          });
          assert(/^https:\/\/maut-resource\.invalid\/r\/[A-Za-z0-9_-]{22}$/.test(original), 'Issued URL has the resource host and an opaque token');
          const image = await load(original);
          assert(image.naturalWidth === 640 && image.naturalHeight === 480, 'Original loads in <img> with its natural size');
          const small = await load(thumbnail);
          assert(small.naturalWidth === 256 && small.naturalHeight === 192, 'Thumbnail loads in <img> and fits 256 px');
          const anonymous = document.createElement('img');
          anonymous.referrerPolicy = 'no-referrer';
          const refused = await new Promise(resolve => { anonymous.onload = () => resolve(false); anonymous.onerror = () => resolve(true); anonymous.src = guarded; });
          assert(refused && (await load(guarded)).naturalWidth === 640, 'Request without the UI document as referrer is refused');
          const missing = await fetch(unknown);
          assert(missing.status === 404, 'Unknown handle returns 404');
          const whole = await fetch(original, {cache: 'no-store'});
          assert(whole.status === 200 && whole.headers.get('content-type') === 'image/jpeg' && /^private, max-age=\d+$/.test(whole.headers.get('cache-control') ?? ''),
            'Original has its type and private max-age');
          const ranged = await fetch(original, {cache: 'no-store', headers: {Range: 'bytes=10-109'}});
          const bytes = new Uint8Array(await ranged.arrayBuffer());
          const base64 = btoa(String.fromCharCode(...bytes));
          assert(ranged.status === 206 && ranged.headers.get('content-range') === 'bytes 10-109/' + length && base64 === expected,
            'Range request returns 206 with the requested bytes');
          const beyond = await fetch(original, {cache: 'no-store', headers: {Range: 'bytes=' + length + '-'}});
          assert(beyond.status === 416, 'Range beyond the end returns 416');
          const timings = [];
          for (let index = 0; index < 50; index++) {
            const start = performance.now();
            const reply = await fetch(thumbnail, {cache: 'no-store'});
            await reply.arrayBuffer();
            timings.push(performance.now() - start);
          }
          timings.sort((left, right) => left - right);
          return {checks, latency: {medianMs: timings[25], p95Ms: timings[47]}};
        }
        """;

    /// <summary>The log is written in the background, so its records are awaited for a bounded time.</summary>
    private static async Task RequireLogAsync(string root, CancellationToken token)
    {
        var expected = new[] { "Maut Vision", "started. Work directory:", "MautVision.UI", "Native smoke uncaught UI error", "Native smoke UI warning", "UI log limit" };
        var text = "";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var files = Directory.Exists(Path.Combine(root, "logs")) ? Directory.GetFiles(Path.Combine(root, "logs"), Logging.FileLoggerProvider.FilePrefix + "*.log") : Array.Empty<string>();
            if (files.Length == 1)
            {
                await using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = await reader.ReadToEndAsync(token);
                if (expected.All(text.Contains)) return;
            }
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("Application log lacks expected records: " + string.Join(", ", expected.Where(value => !text.Contains(value))));
    }

    public static void WriteFailure(string report, Exception error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { done = true, success = false, error = error.ToString() }));
    }

    private const string Script = """
        (() => {
          window.__nativeSmoke = {done:false};
          const checks = [];
          const events = [];
          const wait = async (query, label) => {
            for (let i=0; i<150; i++) {
              const value = query();
              if (value) return value;
              await new Promise(resolve=>setTimeout(resolve,100));
            }
            throw new Error('Timed out: '+label);
          };
          const assert = (value, label) => { if(!value) throw new Error(label); checks.push(label); };
          const query = id => document.querySelector('[data-testid="'+id+'"]');
          let sequence = 0;
          const request = (method,payload) => new Promise((resolve,reject)=>{
            const requestId='native-smoke-'+(++sequence);
            const timeout=setTimeout(()=>{window.chrome.webview.removeEventListener('message',listen);reject(new Error('No reply: '+method));},5000);
            const listen=event=>{
              const data=event.data;
              if(data?.kind!=='response'||data.requestId!==requestId)return;
              clearTimeout(timeout);
              window.chrome.webview.removeEventListener('message',listen);
              resolve(data);
            };
            window.chrome.webview.addEventListener('message',listen);
            window.chrome.webview.postMessage({requestId,method,payload});
          });
          window.chrome.webview.addEventListener('message',event=>{
            if(event.data?.kind==='event')events.push(event.data);
          });
          (async()=>{
            const setValue=(input,value)=>{
              Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,value);
              input.dispatchEvent(new Event('input',{bubbles:true}));
            };
            const button=text=>[...document.querySelectorAll('button')].find(item=>item.textContent.includes(text)&&!item.disabled);
            const create=await wait(()=>query('create-project')&&!query('create-project').disabled&&document.body.textContent.includes('Создайте первый проект')&&query('create-project'),'React library ready');
            assert(Boolean(window.chrome?.webview),'Native WebView2 messaging exists');
            assert(Boolean(document.querySelector('img[src$="brand/logo-mark.png"]')?.complete),'Brand mark is shown in the header');
            const initial=await request('projects.list',{});
            assert(initial.ok&&Array.isArray(initial.data.items)&&initial.data.items.length===0&&initial.data.lastProjectId===null,'Fresh root has no invented projects');
            const catalog=await request('baseModels.list',{});
            assert(catalog.ok&&catalog.data.length===59&&catalog.data.every(model=>model.status===(model.id==='yolo26n'?'installed':'not_installed')&&model.progress===0&&model.error===null),'Base model catalogue crosses bridge');
            create.click();
            const name=await wait(()=>query('project-name'),'Project editor opened');
            await wait(()=>query('project-model')?.textContent.includes('yolo26n'),'Installed model preselected');
            setValue(name,'Проверка C# и React');
            const save=await wait(()=>query('project-save')&&!query('project-save').disabled&&query('project-save'),'Create button enabled');
            save.click();
            await wait(()=>query('active-project-name')?.textContent.includes('Проверка C# и React'),'Created project opened');
            assert(document.body.textContent.includes('640 × 640 px')&&document.body.textContent.includes('yolo26n'),'Project overview shows saved settings');
            const list=await request('projects.list',{});
            assert(list.ok&&list.data.items.length===1&&list.data.items[0].baseModelId==='yolo26n'&&list.data.items[0].inputSize.width===640&&list.data.items[0].revision===1,'React form created real C# Project');
            const projectId=list.data.items[0].id;
            await wait(()=>events.some(event=>event.event==='project.opened'&&event.data.id===projectId),'C# open event delivered');
            assert(events.some(event=>event.event==='project.changed'&&event.data.id===projectId),'C# change event delivered');
            const settings={projectId,name:'Переименован',description:'Описание',baseModelId:'yolo26n',inputSize:{width:320,height:256}};
            const stale=await request('projects.update',{...settings,revision:5});
            assert(!stale.ok&&stale.error.code==='projects.revision_conflict'&&mautLocale.errorText(stale.error.code, stale.error.details).length>0,'Revision conflict crosses bridge');
            const updated=await request('projects.update',{...settings,revision:1});
            assert(updated.ok&&updated.data.revision===2&&updated.data.inputSize.height===256,'Project update crosses bridge');
            await wait(()=>query('active-project-name')?.textContent.includes('Переименован'),'UI applies project.changed');
            const notInstalled=await request('projects.create',{name:'Без весов',description:'',baseModelId:'yolo26s',inputSize:{width:640,height:640}});
            assert(!notInstalled.ok&&notInstalled.error.code==='projects.base_model_not_installed','Uninstalled model is rejected');
            const opened=await request('projects.open',{projectId});
            assert(opened.ok&&opened.data.id===projectId,'Typed open response has correct Project');
            assert(!Object.hasOwn(opened,'protocolVersion'),'Response envelope has no protocol version');
            const jobs=await request('jobs.list',{projectId});
            assert(jobs.ok&&Array.isArray(jobs.data)&&jobs.data.length===0,'Opened project lists no invented jobs');
            const missingJob=await request('jobs.cancel',{projectId,jobId:crypto.randomUUID()});
            assert(!missingJob.ok&&missingJob.error.code==='jobs.not_found'&&mautLocale.errorText(missingJob.error.code, missingJob.error.details).length>0,'Cancel of an unknown job returns jobs.not_found');
            const message='C# ↔ WebView2';
            const results=await Promise.all([request('system.ping',{message}),request('projects.list',{})]);
            assert(results[0].ok&&results[0].data.message===message&&results[0].data.host.startsWith('.NET'),'Unicode ping crossed native bridge');
            assert(results[1].ok&&results[1].data.items[0].id===projectId,'Concurrent replies correlate correctly');
            const invalid=await request('projects.create',{name:'  ',description:'',baseModelId:'yolo26n',inputSize:{width:640,height:640}});
            assert(!invalid.ok&&invalid.error.code==='projects.name_invalid','Validation error crosses bridge');
            const extra=await request('projects.list',{filePath:'C:/private'});
            assert(!extra.ok&&extra.error.code==='bridge.invalid_request','Unknown payload fields rejected');
            const unknown=await request('arbitrary.method',{});
            assert(!unknown.ok&&unknown.error.code==='bridge.method_unavailable','Unknown method rejected');
            const missingModel=await request('baseModels.install',{modelId:'unknown'});
            assert(!missingModel.ok&&missingModel.error.code==='base_models.not_found'&&mautLocale.errorText(missingModel.error.code, missingModel.error.details).length>0,'Core error code crosses bridge');
            const traversal=await request('projects.open',{projectId:'../elsewhere'});
            assert(!traversal.ok&&traversal.error.code==='bridge.validation','Traversal ID rejected');
            setTimeout(()=>{throw new Error('Native smoke uncaught UI error');},0);
            await new Promise(resolve=>setTimeout(resolve,300));
            const logged=await request('system.log',{level:'warning',message:'Native smoke UI warning'});
            assert(logged.ok&&logged.data.accepted===true,'UI log record accepted by host');
            const badLevel=await request('system.log',{level:'info',message:'Native smoke'});
            assert(!badLevel.ok&&badLevel.error.code==='bridge.validation','Unknown UI log level rejected');
            const burst=await Promise.all(Array.from({length:20},(_,index)=>request('system.log',{level:'warning',message:'Native smoke burst '+index,stack:'at smoke'})));
            assert(burst.every(reply=>reply.ok)&&burst.some(reply=>!reply.data.accepted),'UI log records over the rate limit are dropped');
            button('Базовые модели').click();
            const row=await wait(()=>query('base-model-list')?.querySelector('[data-model-id="yolo26n"]'),'Catalogue page opened');
            assert(row.dataset.status==='installed'&&query('base-model-list').querySelectorAll('[data-model-id]').length===59,'Catalogue page lists models with status');
            window.__nativeSmoke={done:true,success:true,checks,projectId,title:document.title,viewport:{width:innerWidth,height:innerHeight}};
          })().catch(error=>{window.__nativeSmoke={done:true,success:false,checks,error:String(error.stack??error)};});
        })();
        """;

    /// <summary>
    /// The training section in the real page with the fake worker (after the training API check): the empty state of a project
    /// without datasets and «Открыть датасеты»; the form with the dataset composition and warnings, the device list from the GPU
    /// inventory (or «Процессор» and manual entry without a driver), the actual «Авто» values, lr0 marked «Подберёт оптимизатор»
    /// with optimizer auto, the «Повороты» toggle, a preset saved, applied and deleted; a slow fake run followed live (epochs, charts,
    /// log) to «Готово» with the short version passport and its plot in the viewer, the history card, a hanging run cancelled. The page
    /// ends on «Текущий запуск» of the finished run with its charts for the capture.
    /// </summary>
    private static async Task CheckTrainingUiAsync(CoreWebView2 web, string projectId, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var arguments = JsonSerializer.Serialize(new { projectId });
        foreach (var body in new[] { TrainingUiEmptyScript, TrainingUiFormScript, TrainingUiRunScript, TrainingUiCancelScript })
        {
            var script = "(async ({projectId}) => {" + BackgroundsUiHelpers + TrainingUiHelpers + body + "})(" + arguments + ")";
            await web.ExecuteScriptAsync("window.__smokeScript = undefined;");
            JsonNode result;
            try
            {
                result = await RunScriptAsync(web, script, token, attempts: 900);
            }
            catch (TimeoutException error)
            {
                // A hung step is named by the last passed check of the script.
                var passed = await web.ExecuteScriptAsync("JSON.stringify(window.__trainingUiChecks ?? [])");
                throw new TimeoutException(error.Message + " Passed: " + JsonSerializer.Deserialize<string>(passed), error);
            }
            foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
        }
    }

    private const string TrainingUiHelpers = """
          window.__trainingUiChecks = checks;
          const tab = id => $('[data-testid="training-tab-' + id + '"]');
          const selectedTab = () => $('[role="tab"][aria-selected="true"]')?.dataset.testid;
          const pick = async (trigger, label) => {
            trigger.click();
            (await wait(() => $$('[role="option"]').find(item => item.textContent.includes(label)), 'Option ' + label)).click();
            await wait(() => !$('[role="listbox"]'), 'Select closed');
          };
          const field = key => $('[data-testid="training-field-' + key + '"]');
          const passport = () => $('[data-testid="training-run-passport"]');
          const headerButton = label => $$('header button').find(item => item.textContent.includes(label));
          const openProject = async id => {
            headerButton('Все проекты').click();
            (await wait(() => $('[data-project-id="' + id + '"] [data-testid="open-project"]'), 'Project row ' + id)).click();
            await wait(() => enabled($('[data-section="training"]')) && $('[data-section="overview"]')?.getAttribute('aria-current') === 'page', 'Project opened');
          };
          const openTraining = async () => {
            $('[data-section="training"]').click();
            return wait(() => $('[data-testid="training-section"]'), 'Training section');
          };
          const trainingDataset = async () => {
            const list = await request('datasets.list', {projectId});
            return list.data.find(item => ['fake:hang', 'fake:slow'].includes(item.name));
          };
          const rename = async (versionId, name) => {
            const current = await request('datasets.get', {projectId, versionId});
            const renamed = await request('datasets.update', {projectId, versionId, name, description: '', revision: current.data.revision});
            if (!renamed.ok) throw new Error('Rename failed: ' + JSON.stringify(renamed.error));
          };

        """;

    private const string TrainingUiEmptyScript = """
          const created = await request('projects.create', {name: 'Обучение без датасетов', description: '', baseModelId: 'yolo26n', inputSize: {width: 320, height: 256}});
          assert(created.ok, 'A project without datasets is created for the empty state');
          await openProject(created.data.id);
          await openTraining();
          const empty = await wait(() => $('[data-testid="training-empty"]'), 'Empty training');
          assert(document.body.textContent.includes('Обучение модели на собранном датасете и её версии с паспортом.')
            && empty.textContent.includes('Сначала соберите датасет') && empty.textContent.includes('Готовых датасетов пока нет: разметьте изображения и соберите датасет.')
            && selectedTab() === 'training-tab-settings' && $$('[role="tablist"] [role="tab"]').map(item => item.textContent).join(',') === 'Настройки,Текущий запуск,История обучения',
            'Training opens lazily on «Настройки»; without datasets it says «Сначала соберите датасет»');
          await wait(() => $('[data-testid="training-history-empty"]')?.textContent.startsWith('Запусков пока нет.'), 'History empty');
          assert(text('[data-testid="training-current-empty"]').startsWith('Запустите обучение во вкладке «Настройки».'), 'The other tabs explain that there are no runs yet');
          key(tab('settings'), 'ArrowRight');
          await wait(() => selectedTab() === 'training-tab-current' && document.activeElement === tab('current'), 'ArrowRight');
          key(tab('current'), 'End');
          await wait(() => selectedTab() === 'training-tab-history', 'End');
          key(tab('history'), 'Home');
          await wait(() => selectedTab() === 'training-tab-settings', 'Home');
          assert(tab('settings').tabIndex === 0 && tab('history').tabIndex === -1 && $('#training-panel-history').hidden, 'Training tabs follow the tablist pattern');
          $('[data-testid="training-open-datasets"]').click();
          await wait(() => $('[data-testid="datasets-section"]') && $('[data-section="datasets"]').getAttribute('aria-current') === 'page', 'Datasets opened');
          assert(true, '«Открыть датасеты» opens the datasets section');
          await openProject(projectId);
          return {checks};
        """;

    private const string TrainingUiFormScript = """
          const dataset = await trainingDataset();
          assert(dataset, 'The training API check left its dataset');
          await rename(dataset.id, 'fake:slow');
          await openTraining();
          await wait(() => $('[data-testid="training-form"]'), 'Training form');
          const project = (await request('projects.list', {})).data.items.find(item => item.id === projectId);
          assert(!$('[data-testid="training-runtime-problem"]')
            && text('[data-testid="training-architecture-note"]').startsWith('Архитектура ' + project.baseModelId + ', вход ' + project.inputSize.width + ' × ' + project.inputSize.height + '.'),
            'With the worker configured there is no runtime notice; the note names the architecture and input');
          await pick(await wait(() => enabled($('[data-testid="training-dataset"]')), 'Dataset select'), 'fake:slow');
          const summary = await wait(() => $('[data-testid="training-dataset-summary"]')?.dataset.datasetId === dataset.id && $('[data-testid="training-dataset-summary"]'), 'Dataset summary');
          assert(text('[data-testid="training-dataset-counts"]') === 'Train ' + dataset.counts.train + ' · Validation ' + dataset.counts.val + ' · Test ' + dataset.counts.test + ' · ' + dataset.width + ' × ' + dataset.height
            && text('[data-testid="training-dataset-classes"]') === 'Классы (' + dataset.classMap.length + '): ' + dataset.classMap.map(entry => entry.name).join(', '),
            'The chosen dataset shows its parts and classes');
          assert(!!$('[data-warning="no_validation"]', summary) === (dataset.counts.val === 0), 'A dataset without Validation is warned about');

          const gpus = (await request('training.listGpus', {})).data;
          const device = field('device');
          if (gpus.driverAvailable && gpus.gpus.length) {
            await wait(() => device.textContent.startsWith('Видеокарта ' + gpus.gpus[0].index + ' — ' + gpus.gpus[0].name), 'GPU preselected',
              () => device.textContent + ' | ' + JSON.stringify(gpus) + ' | ' + !!$('[data-testid="training-device-manual"]'));
            device.click();
            const options = await wait(() => $$('[role="option"]').length && $$('[role="option"]').map(item => item.textContent), 'Device options');
            key($('[role="listbox"]'), 'Escape');
            await wait(() => !$('[role="listbox"]'), 'Device list closed');
            assert(options[0] === 'Процессор' && options.at(-1) === 'Автовыбор свободной видеокарты' && options.length === gpus.gpus.length + 2
              && !$('[data-testid="training-device-manual"]'), '«Устройство» lists «Процессор», the video cards with VRAM and the automatic choice: ' + options.join(' | '));
          } else {
            await wait(() => device.textContent.includes('Процессор') && $('[data-testid="training-device-manual"]'), 'CPU and manual entry');
            assert(true, 'Without a driver «Устройство» offers «Процессор» and manual entry');
          }
          await pick(device, 'Процессор');

          setValue(field('batch'), '999');
          await wait(() => $('[data-warning="batch_exceeds_frames"]'), 'Batch warning');
          assert(text('[data-warning="batch_exceeds_frames"]') === 'Размер пакета (999) больше числа обучающих кадров (' + dataset.counts.train + '): уменьшите batch.'
            && enabled($('[data-testid="training-start"]')), 'A batch larger than the training frames is warned about without blocking');
          setValue(field('batch'), '4');
          await wait(() => !$('[data-warning="batch_exceeds_frames"]') && field('lr0').placeholder === 'Авто: 0,000125', 'lr0 auto value', () => field('lr0').placeholder);
          assert(field('warmupEpochs').placeholder.startsWith('Авто: ') && field('lr0').value === '', 'Empty «Авто» fields show the value the worker will use (lr0 = 0,001 · batch / 32)');
          setValue(field('epochs'), '0');
          await wait(() => text('[data-testid="training-form-hint"]') === 'Проверьте параметры: Эпохи.' && $('[data-testid="training-start"]').disabled, 'Epochs invalid');
          assert($('[data-training-parameter="epochs"]').dataset.invalid === 'true', 'Zero epochs blocks the start and marks the field');
          setValue(field('epochs'), '8');

          await pick(field('optimizer'), 'auto');
          await wait(() => field('lr0').disabled && text('[data-testid="training-note-lr0"]') === 'Подберёт оптимизатор' && field('lr0').placeholder === 'Подберёт оптимизатор', 'lr0 by optimizer');
          assert(true, 'With optimizer auto lr0 is marked «Подберёт оптимизатор»');
          await pick(field('optimizer'), 'AdamW');
          await wait(() => !field('lr0').disabled && !$('[data-testid="training-note-lr0"]'), 'lr0 back');

          const rotation = $('[data-testid="training-toggle-degrees"]');
          assert(rotation.getAttribute('aria-checked') === 'false' && field('degrees').disabled && field('degrees').value === '0', '«Повороты» are off by default with the degrees field disabled');
          rotation.click();
          await wait(() => rotation.getAttribute('aria-checked') === 'true' && !field('degrees').disabled && field('degrees').value === '10', 'Rotation on');
          assert(true, '«Повороты» enables the degrees field with 10°');
          rotation.click();
          await wait(() => rotation.getAttribute('aria-checked') === 'false' && field('degrees').disabled && field('degrees').value === '0', 'Rotation off');

          const before = (await request('training.listPresets', {})).data.length;
          $('[data-testid="training-preset-save-open"]').click();
          setValue(await wait(() => $('[data-testid="training-preset-name"]'), 'Preset name'), 'Smoke UI');
          await wait(() => text('[data-testid="training-preset-hint"]').startsWith('Сохраняются основные и дополнительные настройки'), 'Preset hint');
          $('[data-testid="training-preset-save"]').click();
          await wait(() => text('[data-testid="training-notice"]').startsWith('Пресет «Smoke UI» сохранён.') && $('[data-testid="training-preset"]').textContent === 'Smoke UI', 'Preset saved');
          const saved = (await request('training.listPresets', {})).data.find(item => item.name === 'Smoke UI');
          assert(saved && saved.parameters.epochs === 8 && saved.parameters.batch === 4 && saved.parameters.device === 'cpu', '«Сохранить как пресет…» saves the form parameters');
          setValue(field('epochs'), '30');
          await wait(() => text('[data-testid="training-preset-hint"]').startsWith('Параметры изменены после выбора пресета «Smoke UI»')
            && $('[data-testid="training-preset"]').textContent === 'Выберите пресет', 'Preset changed');
          assert(true, 'Editing the form after a preset clears the choice so the preset can be applied again');
          await pick($('[data-testid="training-preset"]'), 'Smoke UI');
          await wait(() => field('epochs').value === '8' && $('[data-testid="training-preset"]').textContent === 'Smoke UI', 'Preset applied');
          assert(true, 'Choosing a preset fills the form');
          $('[data-testid="training-preset-delete"]').click();
          (await wait(() => $('[data-testid="training-preset-delete-confirm"]'), 'Delete confirmation')).click();
          await wait(() => text('[data-testid="training-notice"]').startsWith('Пресет «Smoke UI» удалён.') && !$('[data-testid="training-preset-delete"]'), 'Preset deleted');
          assert((await request('training.listPresets', {})).data.length === before, '«Удалить пресет» deletes it after confirmation');
          assert(field('epochs').value === '8' && field('batch').value === '4' && $('[data-testid="training-start"]').textContent === 'Начать обучение', 'The form keeps its values for the run');
          return {checks};
        """;

    private const string TrainingUiRunScript = """
          const seen = new Set();
          const watch = () => {
            const current = passport();
            if (!current) return;
            const epochs = $$('[data-testid="training-epoch-chart"]', current).map(chart => chart.dataset.epochs).join('/');
            seen.add(current.dataset.state + '|' + text('[data-testid="training-progress-epoch"]') + '|' + epochs + '|' + $$('[data-testid="training-log-lines"] p').length);
          };
          const observer = new MutationObserver(watch);
          observer.observe(document.body, {subtree: true, childList: true, characterData: true, attributes: true});
          try {
            $('[data-testid="training-start"]').click();
            await wait(() => selectedTab() === 'training-tab-current' && passport(), 'Run passport');
            await wait(() => passport()?.dataset.state === 'succeeded', 'Run ended', () => [...seen].slice(-3).join(', '));
            watch();
          } finally {
            observer.disconnect();
          }
          const live = [...seen].filter(item => item.startsWith('training|Эпоха '));
          assert(live.some(item => /^training\|Эпоха [1-7] из 8\|/.test(item)) && live.some(item => item.split('|')[2] !== '' && Number(item.split('|')[3]) > 0),
            'Текущий запуск follows the run live: epochs, charts and log during training: ' + live.join(', '));
          const ended = passport();
          assert(text('[data-testid="training-run-state"]') === 'Готово' && text('[data-testid="training-progress-epoch"]') === 'Эпоха 8 из 8'
            && $('[role="progressbar"]', ended).getAttribute('aria-valuenow') === '100' && text('[data-testid="training-environment"]').includes('torch 2.14.0+fake'),
            'The ended run shows «Готово», «Эпоха 8 из 8» and the worker environment');
          const quality = await wait(() => $('[data-chart="quality"]', ended)?.dataset.epochs === '8' && $('[data-chart="quality"]', ended), 'Quality chart');
          const losses = $('[data-chart="losses"]', ended);
          assert($('figcaption', quality).textContent === 'Качество по эпохам' && $('figcaption', losses).textContent === 'Потери по эпохам' && losses.dataset.epochs === '8'
            && $$('[data-series]', quality).map(item => item.textContent).join(',') === 'Precision,Recall,mAP50,mAP50-95'
            && $('[data-series-line="valBoxLoss"]', losses)?.getAttribute('stroke-dasharray') === '6 4', 'Both epoch charts cover all epochs with the old series and dashed val losses');
          const map = $('[data-series="mAP50"]', quality);
          map.click();
          await wait(() => map.getAttribute('aria-pressed') === 'false' && !$('[data-series-line="mAP50"]', quality), 'Series hidden');
          map.click();
          await wait(() => $('[data-series-line="mAP50"]', quality), 'Series shown');
          assert(true, 'The legend hides and shows a series');
          const svg = $('svg[role="img"]', quality);
          const box = svg.getBoundingClientRect();
          svg.dispatchEvent(new PointerEvent('pointermove', {bubbles: true, clientX: box.left + box.width * (44 + 584 * 3 / 7) / 640, clientY: box.top + box.height / 2}));
          const tooltip = await wait(() => $('[data-testid="training-chart-tooltip"]', quality), 'Tooltip');
          assert(/^Эпоха [1-8]/.test(tooltip.textContent) && $('[data-testid="training-chart-crosshair"]', quality) && tooltip.textContent.includes('mAP50-95'),
            'The crosshair tooltip shows «Эпоха N» with the series values: ' + tooltip.textContent);
          svg.dispatchEvent(new PointerEvent('pointermove', {bubbles: true, clientX: box.left + 2, clientY: box.top + 2}));
          await wait(() => !$('[data-testid="training-chart-tooltip"]', quality), 'Tooltip hidden');

          const lines = $$('[data-testid="training-log-lines"] p').map(item => item.textContent);
          assert(lines.some(line => line.includes('8/8  box_loss')) && lines.some(line => /^Создана версия модели \d+\.$/.test(line))
            && text('[data-testid="training-log-caption"]').startsWith('Окно последних 200 сообщений'),
            '«Журнал обучения» shows the latest messages including the last epoch and the Russian system line');
          $('[data-testid="training-log-history"]').click();
          const total = (await request('training.listLog', {projectId, runId: ended.dataset.runId, limit: 1})).data.total;
          await wait(() => text('[data-testid="training-log-caption"]') === 'Всего сохранено сообщений: ' + total + '. Страница до 200 сообщений.'
            && $$('[data-testid="training-log-lines"] p').length === Math.min(200, total), '«Вся история»', () => text('[data-testid="training-log-caption"]'));
          assert(true, '«Вся история» pages the whole saved log');
          $('[data-testid="training-log-live"]').click();
          await wait(() => text('[data-testid="training-log-caption"]').startsWith('Окно последних'), 'Back to live log');

          $('[data-testid="training-run-parameters"]').open = true;
          const requested = $('[data-testid="training-parameter-list"][data-title="Запрошенные"]', ended);
          const actual = $('[data-testid="training-parameter-list"][data-title="Фактические"]', ended);
          assert($('[data-parameter="epochs"] dt', requested).textContent === 'Эпохи' && $('[data-parameter="epochs"] dd', requested).textContent === '8'
            && $('[data-parameter="optimizer"] dd', actual).textContent === 'AdamW' && $$('dd', actual).every(item => item.textContent.length < 220),
            '«Параметры запуска» lists the requested and the actual parameters, long values shortened');

          const version = await wait(() => $('[data-testid="model-version-passport"]', ended), 'Version passport');
          const stored = (await request('training.getModelVersion', {projectId, versionId: version.dataset.versionId})).data;
          assert(text('[data-testid="model-version-metric-mAP50"]').startsWith('mAP500.300') && text('[data-testid="model-version-best-epoch"]').startsWith('лучшая эпоха' + stored.bestEpoch)
            && text('[data-testid="model-version-best-epoch"]').includes('пройдено 8 из 8') && text('[data-testid="model-version-notes"]').includes('fake_warning: Fake warning.')
            && $('[data-file-kind="weights_pt"]', version) && $('[data-file-kind="onnx"]', version),
            'The short version passport shows metrics, the best epoch, notes and files: ' + text('[data-testid="model-version-best-epoch"]'));
          // Thumbnails load lazily: the plots are scrolled into view first.
          $('[data-testid="model-version-plots"]', version).scrollIntoView({block: 'center'});
          const plot = await wait(() => $('[data-testid="model-version-plot"]', version)?.querySelector('[data-status="loaded"] img')?.naturalWidth === 1 && $('[data-testid="model-version-plot"]', version), 'Plot thumbnail');
          plot.focus();
          plot.click();
          await wait(() => $('[data-image-viewer]') && $('[data-viewer-image]')?.naturalWidth === 1 && text('[data-testid="viewer-position"]') === '1 / 1', 'Plot viewer');
          assert($('[data-image-viewer] h2').textContent.startsWith('сводные графики ultralytics · '), 'A training plot opens in the shared viewer');
          key(document.activeElement ?? document.body, 'Escape');
          await wait(() => !$('[data-image-viewer]') && document.activeElement === plot, 'Viewer closed');
          assert(true, 'Closing the viewer returns focus to the plot');

          tab('history').click();
          const card = await wait(() => $('[data-testid="training-run-card"][data-run-id="' + ended.dataset.runId + '"]'), 'History card');
          assert($('[data-testid="training-run-card-state"]', card).textContent === 'Готово' && $('[data-testid="training-run-card-parameters"]', card).textContent.includes('8 эпох · batch 4 · процессор')
            && $('[data-testid="training-run-card-version"]', card).textContent === 'Паспорт версии ' + stored.number, '«История обучения» shows the run card with its version');
          $('[data-testid="training-run-card-version"]', card).click();
          await wait(() => selectedTab() === 'training-tab-current' && passport()?.dataset.runId === ended.dataset.runId && $('[data-testid="model-version-passport"]'), 'Version from history');
          assert(true, '«Паспорт версии» from the history opens the run with its version passport');
          return {checks};
        """;

    private const string TrainingUiCancelScript = """
          const dataset = await trainingDataset();
          const succeededId = passport().dataset.runId;
          await rename(dataset.id, 'fake:hang');
          tab('settings').click();
          await wait(() => $('[data-testid="training-dataset"]').textContent.startsWith('fake:hang'), 'Renamed dataset');
          $('[data-testid="training-start"]').click();
          const hanging = await wait(() => selectedTab() === 'training-tab-current' && passport()?.dataset.runId !== succeededId && passport(), 'Second run');
          await wait(() => passport().dataset.state === 'training' && text('[data-testid="training-run-state"]') === 'Обучение', 'Second run training');
          assert(text('[data-testid="training-start"]') === 'Поставить в очередь', 'While a run is active the start button reads «Поставить в очередь»');
          $('[data-testid="training-cancel"]').click();
          await wait(() => passport().dataset.state === 'cancelled' && text('[data-testid="training-run-state"]') === 'Отменено' && !$('[data-testid="training-cancel"]'), 'Cancelled',
            () => passport().dataset.state);
          assert(!$('[data-testid="training-run-error"]'), '«Отменить обучение» stops the run: «Отменено» without an error');
          tab('history').click();
          const cards = await wait(() => $$('[data-testid="training-run-card"]').length >= 2 && $$('[data-testid="training-run-card"]'), 'History cards');
          assert(cards[0].dataset.runId === hanging.dataset.runId && cards[0].dataset.state === 'cancelled'
            && $('[data-testid="training-run-card-state"]', cards[0]).textContent === 'Отменено', 'The cancelled run heads the history');
          $('[data-testid="training-run-card-open"]', $('[data-testid="training-run-card"][data-run-id="' + succeededId + '"]')).click();
          await wait(() => selectedTab() === 'training-tab-current' && passport()?.dataset.runId === succeededId && $('[data-chart="losses"]'), 'Finished run for the capture');
          await rename(dataset.id, 'Обучение UI');
          $('[data-testid="training-notice"] button')?.click();
          passport().scrollIntoView({block: 'start'});
          await sleep(300);
          return {checks};
        """;
}
