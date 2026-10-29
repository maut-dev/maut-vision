using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Core.Jobs;
using MautVision.Core.Projects;

namespace MautVision.Perf;

/// <summary>
/// Export and import of the largest project (120 000 JPEG files): the archive goes next to the library, the imported copy is deleted
/// afterwards (and its folder removed), so the library stays as generated. Throughput in MB/s and files/s; CPU is the share of all
/// cores used during the operation, which shows whether it is bound by the disk or by the processor.
/// </summary>
internal static class TransferBenchmark
{
    // The files are small (about 8 KB): files/s is the measure. Import creates 120 000 files and is bound by file creation of the volume
    // (about 4 000 files/s from one thread on the reference machine with real-time protection), not by the processor.
    private static readonly Dictionary<string, double> MinimumTargets = new(StringComparer.Ordinal)
    {
        ["projects.export.filesPerSecond"] = 10000,
        ["projects.import.filesPerSecond"] = 2500,
    };

    internal static async Task<int> RunAsync(PerfOptions options)
    {
        var manifest = PerfManifest.Load(options.Root) ?? throw new InvalidOperationException($"No generated library in {options.Root}; run generate first.");
        var core = CoreFactory.GetInstance(new CoreOptions(options.Root, options.Catalog));
        await core.InitializeAsync();
        var projects = (ProjectsManager)core.Projects;
        var source = await projects.GetAsync(manifest.LargestProject.Id!);
        // Copies left by an interrupted pass go first.
        foreach (var leftover in (await projects.ListAsync()).Where(item => item.Id != source.Id && item.Name.StartsWith(source.Name + " (импорт", StringComparison.Ordinal)))
        {
            await projects.DeleteAsync(leftover.Id, leftover.Revision, cancelJobs: true);
        }
        await projects.RemovalsForTestingAsync();
        await projects.CleanUpLeftoversAsync();

        var archive = Path.Combine(Path.GetDirectoryName(options.Root)!, "perf-project-export.zip");
        File.Delete(archive);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var stamp = Guid.NewGuid().ToString("N");

        var cpu = Cpu.Start();
        var watch = Stopwatch.StartNew();
        var input = await projects.PrepareExportAsync(source.Id, archive, "perf-export-" + stamp);
        var job = await core.Jobs.EnqueueAsync<IProjectExportJob, ProjectExportInput>(input, source.Id, "perf-export-" + stamp, CodedText.Plain(input.ProjectName));
        var exported = (await WaitAsync(core, source.Id, job.Id)).Result!.Value.Deserialize<ProjectExportResult>(JobsManager.Json)!;
        var exportSeconds = watch.Elapsed.TotalSeconds;
        result["projects.export"] = Math.Round(watch.Elapsed.TotalMilliseconds);
        result["projects.export.files"] = exported.Files;
        result["projects.export.links"] = exported.Links;
        result["projects.export.mb"] = Math.Round(exported.Bytes / 1048576.0, 1);
        result["projects.export.archiveMb"] = Math.Round(exported.ArchiveBytes / 1048576.0, 1);
        result["projects.export.mbPerSecond"] = Math.Round(exported.Bytes / 1048576.0 / exportSeconds, 1);
        result["projects.export.filesPerSecond"] = Math.Round(exported.Files / exportSeconds);
        result["projects.export.cpuPercent"] = cpu.Percent();

        cpu = Cpu.Start();
        watch.Restart();
        var info = new FileInfo(archive);
        var import = await projects.StartImportAsync(new ProjectArchiveSource(archive, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)),
            "perf-import-" + stamp);
        await projects.Imports.CompletionForTesting(import.Id);
        var imported = projects.GetImport(import.Id);
        if (imported.Status != ProjectImportStatus.Succeeded) throw new InvalidOperationException($"Import is {imported.Status}: {imported.ErrorCode} {imported.ErrorMessage}");
        var importSeconds = watch.Elapsed.TotalSeconds;
        result["projects.import"] = Math.Round(watch.Elapsed.TotalMilliseconds);
        result["projects.import.files"] = imported.ProcessedFiles;
        result["projects.import.mb"] = Math.Round(imported.ProcessedBytes / 1048576.0, 1);
        result["projects.import.mbPerSecond"] = Math.Round(imported.ProcessedBytes / 1048576.0 / importSeconds, 1);
        result["projects.import.filesPerSecond"] = Math.Round(imported.ProcessedFiles / importSeconds);
        result["projects.import.cpuPercent"] = cpu.Percent();

        watch.Restart();
        var copy = await projects.GetAsync(imported.ProjectId!);
        var deletion = await projects.DeleteAsync(copy.Id, copy.Revision, cancelJobs: true);
        result["projects.delete"] = Math.Round(watch.Elapsed.TotalMilliseconds);
        await projects.RemovalsForTestingAsync();
        result["projects.delete.files"] = Math.Round(watch.Elapsed.TotalMilliseconds);
        if (deletion.CleanupPending) Console.WriteLine("  The folder of the imported copy is partly busy; it is removed after the next start.");
        File.Delete(archive);

        Console.WriteLine();
        Console.WriteLine($"Project transfer of the largest project ({exported.Files} files, {exported.Links} links):");
        Console.WriteLine($"{"Metric",-34}{"Value",12}{"Target",10}  Result");
        foreach (var (key, value) in result)
        {
            var target = MinimumTargets.TryGetValue(key, out var minimum) ? "≥ " + minimum.ToString("F0", CultureInfo.InvariantCulture) : "";
            var verdict = target.Length == 0 ? "" : value >= minimum ? "ok" : "MISSED";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{key,-34}{value,12:F1}{target,10}  {verdict}"));
        }
        var path = options.Out ?? Path.Combine(Path.GetDirectoryName(options.Root)!, "perf-transfer.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { date = DateTimeOffset.UtcNow, minimumTargets = MinimumTargets, result },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine($"Results: {path}");
        return result.Where(pair => MinimumTargets.ContainsKey(pair.Key)).All(pair => pair.Value >= MinimumTargets[pair.Key]) ? 0 : 3;
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

    /// <summary>Processor time of this process against the wall time of all cores.</summary>
    private sealed class Cpu
    {
        private readonly TimeSpan start = Process.GetCurrentProcess().TotalProcessorTime;
        private readonly Stopwatch watch = Stopwatch.StartNew();

        internal static Cpu Start() => new();

        internal double Percent()
            => Math.Round(100 * (Process.GetCurrentProcess().TotalProcessorTime - start).TotalSeconds / (watch.Elapsed.TotalSeconds * Environment.ProcessorCount), 1);
    }
}
