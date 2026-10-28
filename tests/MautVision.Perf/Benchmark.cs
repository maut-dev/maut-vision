using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MautVision.Core;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Images;
using Microsoft.Data.Sqlite;

namespace MautVision.Perf;

/// <summary>
/// Each run is a fresh process: the first pass measures a cold start of the process (JIT, SQLite native library,
/// empty pools and caches of Core), the second pass a new Core instance in the warm process with cleared SQLite pools.
/// The OS file cache cannot be dropped without administrator rights, so the disk is warm in both passes.
/// </summary>
internal static class Benchmark
{
    private const int PageSize = 100;

    /// <summary>Target, ms; metrics without a target are reported for comparison.</summary>
    private static readonly Dictionary<string, double> Targets = new(StringComparer.Ordinal)
    {
        ["library.cold"] = 2000,
        ["library.warm"] = 2000,
        ["project.open.cold"] = 1000,
        ["project.open.warm"] = 1000,
        ["jobs.page.cold"] = 300,
        ["jobs.page.warm"] = 300,
        ["images.page.cold"] = 300,
        ["images.page.warm"] = 300,
        ["images.annotations.cold"] = 50,
        ["images.annotations.warm"] = 50,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(PerfOptions options)
    {
        var manifest = PerfManifest.Load(options.Root)
            ?? throw new InvalidOperationException($"No generated library in {options.Root}; run generate first.");
        var runs = new List<Dictionary<string, double>>();
        for (var run = 0; run < options.Runs; run++)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "bench-run", "--root", options.Root, "--catalog", options.Catalog, "--run", run.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine(error);
                throw new InvalidOperationException($"Benchmark run {run + 1} failed with exit code {process.ExitCode}.");
            }
            runs.Add(JsonSerializer.Deserialize<Dictionary<string, double>>(output.Trim().Split('\n')[^1])!);
            Console.WriteLine($"Run {run + 1}/{options.Runs} done.");
        }

        var medians = runs[0].Keys.ToDictionary(key => key, key => Median(runs.Select(values => values[key])), StringComparer.Ordinal);
        PrintTable(manifest, runs, medians);
        var path = options.Out ?? Path.Combine(Path.GetDirectoryName(options.Root)!, "perf-bench.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            date = DateTimeOffset.UtcNow,
            machine = new
            {
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount,
            },
            library = new
            {
                root = options.Root,
                projects = manifest.Projects.Count,
                archived = manifest.Projects.Count(project => project.Archived),
                files = manifest.Files,
                largest = manifest.LargestProject.Files,
                jobs = manifest.Jobs,
            },
            targets = Targets,
            medians,
            runs,
        }, Json));
        Console.WriteLine($"Results: {path}");
        return medians.Where(pair => Targets.ContainsKey(pair.Key)).All(pair => pair.Value <= Targets[pair.Key]) ? 0 : 3;
    }

    /// <summary>One measurement in this process; prints a JSON object of metric to milliseconds (or megabytes).</summary>
    internal static async Task RunOnceAsync(PerfOptions options)
    {
        var manifest = PerfManifest.Load(options.Root)!;
        var largest = manifest.LargestProject.Id!;
        var coreOptions = new CoreOptions(options.Root, options.Catalog);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        // Cold: the first Core of the process, as at application start.
        await MeasureCoreAsync(CoreFactory.GetInstance(coreOptions), largest, "cold", result);
        // Warm: a new composition in the same process; JIT and the OS cache are warm, SQLite pools are empty.
        SqliteConnection.ClearAllPools();
        await MeasureCoreAsync(new MautVision.Core.Core(coreOptions), largest, "warm", result);

        var project = await CoreFactory.GetInstance(coreOptions).Projects.GetAsync(largest);
        var flat = Path.Combine(options.Root, "projects", project.Id, "files", "images");
        var buckets = PerfManifest.BucketsDirectory(options.Root);
        var ids = Directory.EnumerateFiles(flat, "*.jpg").Take(5000).Select(Path.GetFileName).ToArray();
        var probe = Enumerable.Range(0, 100).Select(index => ids[index * 37 % ids.Length]!).ToArray();
        // Alternate the order so neither layout always benefits from the other one's warm-up.
        var layouts = options.Run % 2 == 0 ? new[] { "flat", "buckets" } : ["buckets", "flat"];
        foreach (var layout in layouts)
        {
            var root = layout == "flat" ? flat : buckets;
            result[$"folder.{layout}.first100"] = Time(() => FirstEntries(root, layout == "buckets").Count());
            result[$"folder.{layout}.all"] = Time(() => AllEntries(root, layout == "buckets"));
            result[$"folder.{layout}.lookup100"] = Time(() => probe.Count(name => File.Exists(layout == "flat"
                ? Path.Combine(root, name)
                : Path.Combine(root, name[..2], name))));
        }
        result["files.read100"] = Time(() => FirstEntries(flat, buckets: false).Sum(file => File.ReadAllBytes(file).LongLength));

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        result["memory.peakWorkingSetMb"] = Math.Round(self.PeakWorkingSet64 / 1048576.0, 1);
        result["memory.managedMb"] = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, 1);
        SqliteConnection.ClearAllPools();
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    private static async Task MeasureCoreAsync(ICore core, string largest, string pass, Dictionary<string, double> result)
    {
        var watch = Stopwatch.StartNew();
        await core.InitializeAsync();
        result[$"core.init.{pass}"] = watch.Elapsed.TotalMilliseconds;
        var projects = await core.Projects.ListAsync();
        result[$"library.{pass}"] = watch.Elapsed.TotalMilliseconds;
        if (projects.Count == 0) throw new InvalidOperationException("The library is empty.");

        result[$"project.open.{pass}"] = await TimeAsync(() => core.Projects.OpenAsync(largest));
        var page = 0;
        result[$"jobs.page.{pass}"] = await TimeAsync(async () => page = (await core.Jobs.ListAsync(largest, 50)).Count);
        if (page != 50) throw new InvalidOperationException("The jobs page is not full.");

        // The gallery of the largest group: the group list, the first page of 100 active images and the annotations of that page.
        var project = await core.Projects.GetAsync(largest);
        IReadOnlyList<ImageGroup> groups = [];
        result[$"images.groups.{pass}"] = await TimeAsync(async () => groups = await project.Images.ListGroupsAsync());
        var group = groups.MaxBy(item => item.ImageCount) ?? throw new InvalidOperationException("The largest project has no image group.");
        ImagePage? images = null;
        result[$"images.page.{pass}"] = await TimeAsync(async () => images = await project.Images.ListImagesAsync(group.Id, new ImageFilter(), PageSize));
        if (images!.Items.Count != PageSize) throw new InvalidOperationException("The gallery page is not full.");
        var ids = images.Items.Select(item => item.Id).ToArray();
        IReadOnlyList<ImageAnnotation> annotations = [];
        result[$"images.annotations.{pass}"] = await TimeAsync(async () => annotations = await project.Images.ListAnnotationsAsync(ids));
        if (annotations.Count != PageSize || annotations.All(item => item.Objects.Count == 0))
            throw new InvalidOperationException("The annotation page is not full or has no objects.");
        result[$"images.page.deep.{pass}"] = await TimeAsync(async () => (await project.Images.ListImagesAsync(group.Id,
            new ImageFilter(ReviewState: ImageReviewState.Unreviewed), PageSize)).Items.Count);
    }

    private static IEnumerable<string> FirstEntries(string root, bool buckets) => (buckets
        ? Directory.EnumerateDirectories(root).SelectMany(bucket => Directory.EnumerateFiles(bucket))
        : Directory.EnumerateFiles(root)).Take(PageSize);

    private static int AllEntries(string root, bool buckets) => buckets
        ? Directory.EnumerateDirectories(root).Sum(bucket => Directory.EnumerateFiles(bucket).Count())
        : Directory.EnumerateFiles(root).Count();

    private static double Time<T>(Func<T> action)
    {
        var watch = Stopwatch.StartNew();
        GC.KeepAlive(action());
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<double> TimeAsync<T>(Func<Task<T>> action)
    {
        var watch = Stopwatch.StartNew();
        GC.KeepAlive(await action());
        return watch.Elapsed.TotalMilliseconds;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static void PrintTable(PerfManifest manifest, List<Dictionary<string, double>> runs, Dictionary<string, double> medians)
    {
        Console.WriteLine();
        Console.WriteLine($"Library: {manifest.Projects.Count} projects ({manifest.Projects.Count(project => project.Archived)} archived), "
            + $"{manifest.Files} files, largest {manifest.LargestProject.Files}, {manifest.Jobs} jobs. Runs: {runs.Count}.");
        Console.WriteLine($"{"Metric",-28}{"Median",10}{"Min",10}{"Max",10}{"Target",10}  Result");
        foreach (var (key, median) in medians)
        {
            var values = runs.Select(run => run[key]).ToArray();
            var target = Targets.TryGetValue(key, out var limit) ? limit.ToString("F0", CultureInfo.InvariantCulture) : "";
            var verdict = target.Length == 0 ? "" : median <= limit ? "ok" : "MISSED";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{key,-28}{median,10:F1}{values.Min(),10:F1}{values.Max(),10:F1}{target,10}  {verdict}"));
        }
    }
}
