using MautVision.Core.Interfaces;

namespace MautVision.Desktop.Controllers;

/// <summary>
/// What the page may show for a failed worker run besides the text of its code: for <c>&lt;area&gt;.worker_&lt;code&gt;</c> the worker reported
/// itself, a code of the UI catalog worker with values and the worker's English message as the fallback. A run stored by version 1.0.0 has
/// only the worker's own message, shown as is. Codes the host derives from the exit code keep the raw stderr line in the stored message; it
/// stays in the job log, the page shows the text of the code.
/// </summary>
internal static class WorkerErrors
{
    private static readonly HashSet<string> HostCodes = new(StringComparer.Ordinal)
    {
        "worker_spawn_failed", "worker_io", "worker_usage", "worker_result_not_written", "worker_already_ran", "worker_interrupted", "worker_crashed",
    };

    /// <param name="area">training or checks.</param>
    internal static CodedText? OwnMessage(string area, string code, string stored, CodedText? worker)
    {
        var prefix = area + ".";
        if (!code.StartsWith(prefix + "worker_", StringComparison.Ordinal) || HostCodes.Contains(code[prefix.Length..])) return null;
        if (worker is not null) return worker;
        return stored.Length == 0 ? null : CodedText.Plain(stored);
    }
}
