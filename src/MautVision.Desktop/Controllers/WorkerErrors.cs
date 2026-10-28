namespace MautVision.Desktop.Controllers;

/// <summary>
/// The message of a failed worker run that the page may show: the worker's own one for <c>&lt;area&gt;.worker_&lt;code&gt;</c> it reported
/// itself. Codes the host derives from the exit code keep the raw stderr line in the stored message; it stays in the job log, the page
/// shows the text of the code.
/// </summary>
internal static class WorkerErrors
{
    private static readonly HashSet<string> HostCodes = new(StringComparer.Ordinal)
    {
        "worker_spawn_failed", "worker_io", "worker_usage", "worker_result_not_written", "worker_already_ran", "worker_interrupted", "worker_crashed",
    };

    /// <param name="area">training or checks.</param>
    internal static string? OwnMessage(string area, string code, string stored)
    {
        var prefix = area + ".";
        if (!code.StartsWith(prefix + "worker_", StringComparison.Ordinal) || stored.Length == 0) return null;
        return HostCodes.Contains(code[prefix.Length..]) ? null : stored;
    }
}