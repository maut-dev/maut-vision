using System.Globalization;

namespace MautVision.Perf;

internal sealed class PerfOptions
{
    public required string Root { get; init; }
    public required string Catalog { get; init; }
    public int Projects { get; init; } = 40;
    public int Files { get; init; } = 250_000;
    public int Largest { get; init; } = 120_000;
    public int Archived { get; init; } = 5;
    public int Jobs { get; init; } = 10_000;
    public int Runs { get; init; } = 5;
    public int Run { get; init; }
    public string? Out { get; init; }

    public static PerfOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected --name value, got '{args[index]}'.");
            }
            values[args[index][2..]] = args[index + 1];
        }
        int Number(string name, int fallback) => values.TryGetValue(name, out var text)
            ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;
        var root = values.GetValueOrDefault("root") ?? throw new ArgumentException("--root is required.");
        var catalog = values.GetValueOrDefault("catalog")
            ?? Path.Combine(AppContext.BaseDirectory, "../../../../../catalog/yolo-catalog.json");
        var options = new PerfOptions
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Catalog = Path.GetFullPath(catalog),
            Projects = Number("projects", 40),
            Files = Number("files", 250_000),
            Largest = Number("largest", 120_000),
            Archived = Number("archived", 5),
            Jobs = Number("jobs", 10_000),
            Runs = Number("runs", 5),
            Run = Number("run", 0),
            Out = values.GetValueOrDefault("out") is { } output ? Path.GetFullPath(output) : null,
        };
        if (options.Projects < 1 || options.Largest < 0 || options.Largest > options.Files || options.Archived >= options.Projects
            || options.Jobs < 0 || options.Runs < 1)
        {
            throw new ArgumentException("Invalid generator or benchmark parameters.");
        }
        return options;
    }
}
