using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>The job kinds a feature starts through the bridge; each gives its job a label.</summary>
    private static readonly string[] LabelledKinds =
    [
        "images.import", "backgrounds.import", "classes.import_assets", "generation.run", "datasets.build", "datasets.verify", "datasets.export",
        "training.run", "training.export", "checks.evaluate",
    ];

    /// <summary>
    /// Overview and analytics API from the real page after the other checks filled the project: hardware.sample (CPU from collecting to available, the
    /// memory of the application with the WebView2 processes), overview.get against datasets.list, training.listModelVersions and
    /// jobs.listActive, the run of the latest version fetched by its runId, the labels of every job kind started through the bridge,
    /// and document.visibilityState while the window is minimized and restored.
    /// </summary>
    /// <param name="seen">Every job snapshot of the smoke run (some jobs are deleted by later checks).</param>
    private static async Task CheckOverviewAsync(CoreWebView2 web, string projectId, ICore core, IReadOnlyList<JobInfo> seen, Action<bool> setMinimized,
        JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var arguments = JsonSerializer.Serialize(new { projectId });
        foreach (var check in (await RunScriptAsync(web, "(async ({projectId}) => {" + OverviewScript + "})(" + arguments + ")", token, attempts: 300))
            ["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());

        var jobs = new List<JobInfo>(seen.Where(job => job.ProjectId == projectId));
        for (Guid? before = null; ;)
        {
            var page = await core.Jobs.ListAsync(projectId, 200, before, token);
            jobs.AddRange(page);
            if (page.Count < 200) break;
            before = page[^1].Id;
        }
        var unlabelled = LabelledKinds.Where(kind => !jobs.Any(job => job.Kind == kind && !string.IsNullOrWhiteSpace(job.Label))).ToArray();
        if (unlabelled.Length > 0) throw new InvalidOperationException("Jobs started through the bridge have no label: " + string.Join(", ", unlabelled));
        if (!jobs.Any(job => job is { Kind: "datasets.build", Label: "Датасет smoke" })
            || !jobs.Any(job => job.Kind == "images.import" && job.OperationId == "images-smoke-files" && !string.IsNullOrEmpty(job.Label))
            || !jobs.Where(job => job.Kind == "training.export" && job.Label is not null).All(job => job.Label!.StartsWith("Версия ", StringComparison.Ordinal))
            || !jobs.Where(job => job.Kind == "checks.evaluate" && job.Label is not null).All(job => job.Label!.StartsWith("Версия ", StringComparison.Ordinal)
                && job.Label.Contains(" · ", StringComparison.Ordinal)))
            throw new InvalidOperationException("Job labels do not name what the jobs work on.");
        checks.Add("Every job kind started through the bridge has a label: group, set, recipe, dataset or «Версия N»");

        if (await VisibilityAsync(web) != "visible") throw new InvalidOperationException("The page of the smoke window is not visible before minimizing.");
        setMinimized(true);
        await WaitVisibilityAsync(web, "hidden", token);
        setMinimized(false);
        await WaitVisibilityAsync(web, "visible", token);
        checks.Add("A minimized window hides the page (visibilityState hidden) and restoring shows it again");
    }

    private static async Task<string?> VisibilityAsync(CoreWebView2 web) => JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.visibilityState"));

    private static async Task WaitVisibilityAsync(CoreWebView2 web, string expected, CancellationToken token)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await VisibilityAsync(web) == expected) return;
            await Task.Delay(100, token);
        }
        throw new TimeoutException($"document.visibilityState did not become {expected}: {await VisibilityAsync(web)}.");
    }

    private const string OverviewScript = """
        const checks = [];
        const assert = (value, label) => { if (!value) throw new Error(label); checks.push(label); };
        let sequence = 0;
        const request = (method, payload) => new Promise((resolve, reject) => {
          const requestId = 'overview-smoke-' + Date.now() + '-' + (++sequence);
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
        const sample = await request('hardware.sample', {});
        assert(sample.ok && typeof sample.data.timestamp === 'string' && Array.isArray(sample.data.gpus)
          && ['available', 'unavailable'].includes(sample.data.gpuStatus) && (sample.data.gpuStatus === 'available') === (sample.data.gpus.length > 0)
          && (sample.data.gpuStatus === 'available' || typeof sample.data.gpuReason === 'string'), 'hardware.sample answers with the GPUs or the reason in Russian');
        assert(sample.data.ram && sample.data.ram.totalBytes > 0 && sample.data.ram.usedBytes <= sample.data.ram.totalBytes, 'hardware.sample reports the RAM of the system');
        const memory = sample.data.application;
        assert(memory.hostBytes > 0 && memory.webViewProcesses > 0 && memory.webViewBytes > 0
          && memory.totalBytes === memory.hostBytes + memory.webViewBytes + memory.workerBytes, 'The memory of the application sums the host, WebView2 and workers');
        await new Promise(resolve => setTimeout(resolve, 300));
        const next = await request('hardware.sample', {});
        assert(next.ok && next.data.cpu.status === 'available' && next.data.cpu.utilizationPercent >= 0 && next.data.cpu.utilizationPercent <= 100
          && next.data.cpu.logicalThreads > 0 && next.data.cpu.reason === null, 'The next hardware.sample has the CPU load');
        assert(Array.isArray(sample.data.gpus) && sample.data.gpus.every(gpu => gpu.temperatureC === null || gpu.temperatureC > 0), 'GPUs report the temperature');

        const overview = await request('overview.get', {projectId});
        assert(overview.ok && overview.data.projectId === projectId, 'overview.get answers for the opened project');
        const versions = (await request('datasets.list', {projectId, includeArchived: true})).data;
        const ready = versions.filter(item => item.archivedAt === null && item.integrityState !== 'corrupt').length;
        const archived = versions.filter(item => item.archivedAt !== null).length;
        const corrupt = versions.filter(item => item.archivedAt === null && item.integrityState === 'corrupt').length;
        const counts = overview.data.datasets;
        assert(counts.ready === ready && counts.archived === archived && counts.corrupt === corrupt,
          `Overview counts datasets as datasets.list: ${counts.ready}/${counts.archived}/${counts.corrupt}`);
        const models = (await request('training.listModelVersions', {projectId})).data;
        const latest = overview.data.latestVersions;
        assert(overview.data.modelVersionCount === models.length && latest.length === Math.min(5, models.length)
          && latest.every((item, index) => index === 0 || latest[index - 1].number > item.number)
          && latest.every(item => models.some(model => model.id === item.id && model.isWorking === item.isWorking)),
          'Overview has the count of model versions and the 5 latest, newest first');
        const active = await request('jobs.listActive', {projectId, limit: 1});
        assert(active.ok && active.data.total === overview.data.activeJobs.total && Array.isArray(active.data.items)
          && Object.values(active.data.countsByKind).reduce((sum, value) => sum + value, 0) === active.data.total
          && (active.data.total > 1) === (active.data.nextCursor !== null), 'jobs.listActive pages active jobs with their exact total');
        if (latest.length > 0) {
          const run = await request('training.getRun', {projectId, runId: latest[0].runId});
          assert(run.ok && run.data.id === latest[0].runId, 'The run of the latest version is fetched by its runId');
        }
        const labelled = await request('jobs.list', {projectId, limit: 200});
        assert(labelled.ok && labelled.data.every(job => job.label === null || typeof job.label === 'string')
          && labelled.data.some(job => typeof job.label === 'string'), 'Job snapshots carry their label');
        return {checks};
        """;
}
