using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Logging;

/// <summary>
/// Startup stages with the time since the process started. perf.ps1 reads these records, so the message format is stable:
/// "Startup stage {Stage} reached in {ElapsedMilliseconds} ms since process start."
/// </summary>
internal static class StartupTimings
{
    public const string LoadingWindowShown = "loading_window_shown";
    public const string CoreInitialized = "core_initialized";
    public const string MainWindowShown = "main_window_shown";
    public const string WebViewReady = "webview_ready";
    public const string NavigationCompleted = "ui_navigation_completed";
    public const string LibraryReady = "library_ready";

    private static readonly DateTime ProcessStart = ReadProcessStart();

    public static long ElapsedMilliseconds => (long)(DateTime.UtcNow - ProcessStart).TotalMilliseconds;

    public static void Log(ILogger logger, string stage, long? elapsedMilliseconds = null)
        => logger.LogInformation("Startup stage {Stage} reached in {ElapsedMilliseconds} ms since process start.",
            stage, elapsedMilliseconds ?? ElapsedMilliseconds);

    private static DateTime ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
