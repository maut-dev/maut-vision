using System.Diagnostics;
using System.Globalization;
using MautVision.Core.Interfaces.Hardware;

namespace MautVision.Core.Hardware;

internal enum NvidiaSmiOutcome
{
    Answered,
    Missing,
    Failed,
    TimedOut,
}

/// <summary>
/// nvidia-smi from the driver's own folders (System32, NVSMI), never through PATH, with a time limit; the process tree is killed when
/// it does not answer in time.
/// </summary>
internal static class NvidiaSmi
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static IReadOnlyList<string> DefaultCandidates { get; } =
    [
        Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
    ];

    internal const string InventoryQuery = "--query-gpu=index,name,memory.total,driver_version";
    internal const string TelemetryQuery = "--query-gpu=index,name,driver_version,memory.total,memory.used,utilization.gpu,temperature.gpu";

    /// <summary>The CSV output of a query, or why there is none; Detail is the exit code or the exception for the log.</summary>
    internal static async Task<(NvidiaSmiOutcome Outcome, string Text, string? Detail)> QueryAsync(IReadOnlyList<string> candidates, string query,
        CancellationToken cancellationToken)
    {
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is null) return (NvidiaSmiOutcome.Missing, "", null);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(query);
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("nvidia-smi did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return process.ExitCode == 0
                    ? (NvidiaSmiOutcome.Answered, text, null)
                    : (NvidiaSmiOutcome.Failed, "", $"exit code {process.ExitCode}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return (NvidiaSmiOutcome.TimedOut, "", null);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (NvidiaSmiOutcome.Failed, "", error.Message);
        }
    }

    /// <summary>Lines «index, name, memory MiB, driver»; a malformed line is skipped.</summary>
    internal static IReadOnlyList<GpuInfo> ParseInventory(string text)
    {
        var result = new List<GpuInfo>();
        foreach (var line in Lines(text))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)) continue;
            var memory = long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : 0;
            // A name may itself contain commas: everything between the index and the last two fields.
            var name = string.Join(", ", parts[1..^2]);
            result.Add(new GpuInfo(index, name, memory, parts[^1]));
        }
        return result;
    }

    /// <summary>
    /// Lines «index, name, driver, memory total MiB, used MiB, load %, temperature °C»; «[N/A]» and «[Not Supported]» are null. A line
    /// that is not of this form is a <see cref="FormatException"/>: damaged output must not turn into zeros.
    /// </summary>
    internal static IReadOnlyList<GpuSnapshot> ParseTelemetry(string text)
    {
        var result = new List<GpuSnapshot>();
        foreach (var line in Lines(text))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 7 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                throw new FormatException("An nvidia-smi line has an unexpected form.");
            var name = string.Join(", ", parts[1..^5]);
            var total = Number(parts[^4]);
            var used = Number(parts[^3]);
            result.Add(new GpuSnapshot(index, name, parts[^5], total is { } t ? (long)(t * 1024 * 1024) : null, used is { } u ? (long)(u * 1024 * 1024) : null,
                Number(parts[^2]), Number(parts[^1])));
        }
        return result;
    }

    private static IEnumerable<string> Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double? Number(string value)
    {
        var bare = value.Trim('[', ']');
        if (bare.Equals("N/A", StringComparison.OrdinalIgnoreCase) || bare.Equals("Not Supported", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0
            ? number
            : throw new FormatException($"An nvidia-smi value '{value}' is not a number.");
    }
}
