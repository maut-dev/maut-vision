using System.Text.Json;

namespace MautVision.Perf;

/// <summary>
/// Plan and progress of a generated library, stored next to the projects. A rerun reads it and only adds what is missing.
/// </summary>
internal sealed class PerfManifest
{
    internal const string FileName = "perf-library.json";
    internal const string CompareDirectory = "perf-compare";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Files { get; set; }
    public int Largest { get; set; }
    public int Jobs { get; set; }
    public List<PlannedProject> Projects { get; set; } = [];

    public PlannedProject LargestProject => Projects.MaxBy(project => project.Files)!;

    internal static string PathFor(string root) => Path.Combine(root, FileName);

    /// <summary>Hard links to the largest project's images, bucketed by the first two hex characters of the ID.</summary>
    internal static string BucketsDirectory(string root) => Path.Combine(root, CompareDirectory, "buckets");

    internal static PerfManifest? Load(string root)
    {
        var path = PathFor(root);
        return File.Exists(path) ? JsonSerializer.Deserialize<PerfManifest>(File.ReadAllText(path), Json) : null;
    }

    internal void Save(string root)
    {
        var path = PathFor(root);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// Uneven distribution: the largest project, then a Zipf-like tail; about a tenth of the projects stay empty,
    /// as new projects do in a real library.
    /// </summary>
    internal static PerfManifest Plan(PerfOptions options)
    {
        var manifest = new PerfManifest { Files = options.Files, Largest = options.Largest, Jobs = options.Jobs };
        var others = options.Projects - 1;
        var empty = others >= 10 ? others / 10 : 0;
        var filled = others - empty;
        var counts = new int[options.Projects];
        counts[0] = options.Largest;
        var rest = options.Files - options.Largest;
        if (filled > 0)
        {
            var weights = Enumerable.Range(1, filled).Select(rank => 1.0 / rank).ToArray();
            var total = weights.Sum();
            var assigned = 0;
            for (var index = 0; index < filled; index++)
            {
                counts[index + 1] = (int)(rest * weights[index] / total);
                assigned += counts[index + 1];
            }
            counts[1] += rest - assigned;
        }
        else
        {
            counts[0] += rest;
        }
        // Archived projects are spread evenly over the list: from large ones to the empty tail.
        var archived = Enumerable.Range(0, options.Archived).Select(k => 1 + (int)((2L * k + 1) * others / (2L * options.Archived))).ToHashSet();
        for (var index = 0; index < options.Projects; index++)
        {
            manifest.Projects.Add(new PlannedProject
            {
                Name = index == 0 ? "Perf largest" : $"Perf {index:00}",
                Files = counts[index],
                Archived = archived.Contains(index),
            });
        }
        return manifest;
    }
}

internal sealed class PlannedProject
{
    public string? Id { get; set; }
    public required string Name { get; set; }
    public int Files { get; set; }
    public bool Archived { get; set; }
}
