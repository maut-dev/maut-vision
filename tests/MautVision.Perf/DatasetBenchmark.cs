using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Projects;
using Microsoft.Data.Sqlite;

namespace MautVision.Perf;

/// <summary>
/// Dataset pipeline on the largest project (120 000 reviewed JPEG frames): plan, build (hard links), a sample page, the integrity
/// check and the ZIP export, then deletion. One pass in one process: a build of this size takes tens of seconds, so it is not
/// repeated per run like the start metrics. Times in ms, throughput in MB/s.
/// </summary>
internal static class DatasetBenchmark
{
    private static readonly Dictionary<string, double> Targets = new(StringComparer.Ordinal)
    {
        ["datasets.plan"] = 5000,
        ["datasets.build"] = 60000,
        ["datasets.page.cold"] = 50,
        ["datasets.page.warm"] = 50,
    };

    internal static async Task<int> RunAsync(PerfOptions options)
    {
        var manifest = PerfManifest.Load(options.Root) ?? throw new InvalidOperationException($"No generated library in {options.Root}; run generate first.");
        var core = CoreFactory.GetInstance(new CoreOptions(options.Root, options.Catalog) { LoggerFactory = new DatasetLog() });
        await core.InitializeAsync();
        var project = (Project)await core.Projects.OpenAsync(manifest.LargestProject.Id!);
        var datasets = project.Datasets;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        // Leftovers of an interrupted benchmark go first, so every pass starts without versions.
        foreach (var leftover in await datasets.ListAsync())
        {
            await datasets.DeleteAsync(leftover.Id, (await datasets.ReviewDeletionAsync(leftover.Id)).Revision);
        }
        await project.FileCleanup.CleanupTask;

        var group = (await project.Images.ListGroupsAsync()).Single(item => item.Name == LibraryGenerator.GroupName);
        var workspace = await project.Classes.GetWorkspaceAsync();
        var classId = workspace.Classes.Single(item => item.Name == "Perf object").Id;
        var specification = new DatasetSpecification("Perf dataset", "", [group.Id], [classId], [], [], DatasetUnassignedVariantRole.Target,
            DatasetUnselectedClassRole.Negative, IncludeReviewedEmpty: true, AllowPartialGeneration: false, 42, 0.2, 0.1);
        var stamp = Guid.NewGuid().ToString("N");
        var plans = new List<double>();
        DatasetPlan plan = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var watch = Stopwatch.StartNew();
            plan = await datasets.PlanAsync(specification, $"perf-plan-{stamp}-{attempt}");
            plans.Add(watch.Elapsed.TotalMilliseconds);
        }
        result["datasets.plan"] = plans.Order().ElementAt(1);
        result["datasets.plan.first"] = plans[0];
        result["datasets.frames"] = plan.Counts.Total;

        var build = Stopwatch.StartNew();
        var input = await datasets.PrepareBuildAsync(plan.Id, plan.Hash, "perf-build-" + stamp);
        result["datasets.build.start"] = build.Elapsed.TotalMilliseconds;
        var job = await core.Jobs.EnqueueAsync<IDatasetBuildJob, DatasetBuildInput>(input, project.Id, "perf-build-" + stamp);
        var built = await WaitAsync(core, project.Id, job.Id);
        result["datasets.build"] = build.Elapsed.TotalMilliseconds;
        var buildResult = built.Result!.Value.Deserialize<DatasetBuildResult>(MautVision.Core.Jobs.JobsManager.Json)!;
        result["datasets.build.linked"] = buildResult.Linked;
        result["datasets.build.samplesPerSecond"] = Math.Round(buildResult.Produced / build.Elapsed.TotalSeconds);

        SqliteConnection.ClearAllPools();
        var page = Stopwatch.StartNew();
        var first = await datasets.ListSamplesAsync(job.Id, DatasetSplit.Train, 100);
        result["datasets.page.cold"] = page.Elapsed.TotalMilliseconds;
        page.Restart();
        var deep = await datasets.ListSamplesAsync(job.Id, DatasetSplit.Train, 100, first.NextCursor);
        result["datasets.page.warm"] = page.Elapsed.TotalMilliseconds;
        if (first.Items.Count != 100 || deep.Items.Count != 100) throw new InvalidOperationException("The sample pages are not full.");

        var verify = await core.Jobs.EnqueueAsync<IDatasetVerifyJob, DatasetVerifyInput>(await datasets.PrepareVerifyAsync(job.Id, "perf-verify-" + stamp),
            project.Id, "perf-verify-" + stamp);
        var verified = (await WaitAsync(core, project.Id, verify.Id)).Result!.Value.Deserialize<DatasetVerifyResult>(MautVision.Core.Jobs.JobsManager.Json)!;
        if (verified.State != DatasetIntegrityState.Verified) throw new InvalidOperationException($"The perf dataset is {verified.State}.");
        result["datasets.verify"] = verified.ElapsedMs;
        result["datasets.verify.mbPerSecond"] = Math.Round(verified.Bytes / 1048576.0 / Math.Max(0.001, verified.ElapsedMs / 1000.0), 1);
        result["datasets.verify.files"] = verified.TotalFiles;

        var zip = Path.Combine(Path.GetDirectoryName(options.Root)!, "perf-dataset-export.zip");
        var export = await core.Jobs.EnqueueAsync<IDatasetExportJob, DatasetExportInput>(await datasets.PrepareExportAsync(job.Id, zip, "perf-export-" + stamp),
            project.Id, "perf-export-" + stamp);
        var exported = (await WaitAsync(core, project.Id, export.Id)).Result!.Value.Deserialize<DatasetExportResult>(MautVision.Core.Jobs.JobsManager.Json)!;
        result["datasets.export"] = exported.ElapsedMs;
        result["datasets.export.mb"] = Math.Round(exported.Bytes / 1048576.0, 1);
        result["datasets.export.mbPerSecond"] = Math.Round(exported.Bytes / 1048576.0 / Math.Max(0.001, exported.ElapsedMs / 1000.0), 1);
        File.Delete(zip);

        var deletion = Stopwatch.StartNew();
        await datasets.DeleteAsync(job.Id, (await datasets.ReviewDeletionAsync(job.Id)).Revision);
        result["datasets.delete"] = deletion.Elapsed.TotalMilliseconds;
        await project.FileCleanup.CleanupTask;
        result["datasets.delete.files"] = deletion.Elapsed.TotalMilliseconds;

        Console.WriteLine();
        Console.WriteLine($"Datasets on the largest project ({plan.Counts.Total} frames):");
        Console.WriteLine($"{"Metric",-34}{"Value",12}{"Target",10}  Result");
        foreach (var (key, value) in result)
        {
            var target = Targets.TryGetValue(key, out var limit) ? limit.ToString("F0", CultureInfo.InvariantCulture) : "";
            var verdict = target.Length == 0 ? "" : value <= limit ? "ok" : "MISSED";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key,-34}{value,12:F1}{target,10}  {verdict}"));
        }
        var path = options.Out ?? Path.Combine(Path.GetDirectoryName(options.Root)!, "perf-datasets.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { date = DateTimeOffset.UtcNow, targets = Targets, result },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"Results: {path}");
        return result.Where(pair => Targets.ContainsKey(pair.Key)).All(pair => pair.Value <= Targets[pair.Key]) ? 0 : 3;
    }

    private static async Task<JobInfo> WaitAsync(ICore core, string projectId, Guid jobId)
    {
        while (true)
        {
            var job = await core.Jobs.GetAsync(projectId, jobId);
            if (job.Status == JobStatus.Succeeded) return job;
            if (job.Status is not (JobStatus.Queued or JobStatus.Running))
                throw new InvalidOperationException($"Job {job.Kind} is {job.Status}: {job.Error?.Code} {job.Error?.Message}");
            await Task.Delay(50);
        }
    }

    /// <summary>Prints the phase records of dataset jobs (preparation, files, publication) to the console.</summary>
    private sealed class DatasetLog : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger();

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => throw new NotSupportedException();

        public void Dispose()
        {
        }

        private sealed class Logger : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                if (message.Contains("ataset", StringComparison.Ordinal)) Console.WriteLine("  log: " + message);
            }
        }
    }
}