using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MautVision.Core.Interfaces;

/// <summary>
/// LoggerFactory belongs to the caller and is not part of the CoreFactory configuration comparison:
/// the log sink does not change the data Core works with.
/// </summary>
/// <param name="WorkDirectory">User data: projects, installed weights and other mutable files.</param>
/// <param name="BaseModelCatalogPath">Catalogue shipped with the application; read in place, never copied to the work directory.</param>
public sealed record CoreOptions(string WorkDirectory, string BaseModelCatalogPath)
{
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

    /// <summary>
    /// Shipped folder with ffmpeg.exe and ffprobe.exe (LGPL build next to the application). null or a folder without them — video
    /// is unavailable: <c>video.ffmpeg_missing</c> on use, the rest of Core works.
    /// </summary>
    public string? FfmpegDirectory { get; init; }

    /// <summary>
    /// How to launch the Python worker (training, model checks). null — the runtime is not configured: training answers
    /// <c>training.runtime_not_configured</c> when a run starts, the rest of Core works.
    /// </summary>
    public Python.PythonWorkerLaunch? PythonWorker { get; init; }

    /// <summary>
    /// Only for Desktop smoke checks (<c>--smoke-report</c>): registers the <c>smoke-fixture</c> material kind,
    /// whose files lie in files/smoke-fixture/&lt;id&gt;.jpg|.png without rows in the database.
    /// </summary>
    public bool SmokeFixtures { get; init; }
}
