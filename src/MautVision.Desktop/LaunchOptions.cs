using System.IO;
using MautVision.Core.Interfaces.Python;
using MautVision.Desktop.Host;

namespace MautVision.Desktop;

/// <param name="ExitWhenReady">Closes the application once the UI reports the library is shown; used by perf.ps1.</param>
/// <param name="Python">Interpreter of the pinned development environment (--python), together with <paramref name="PythonSource"/>.</param>
/// <param name="PythonSource">Folder of the worker sources (repository python/) for the interpreter (--python-source).</param>
/// <param name="WorkerExecutable">A ready worker executable (--worker-executable): a separate build or the fake worker of the smoke.</param>
/// <param name="BaseModelCatalog">Smoke only (--base-model-catalog with --smoke-report): a catalogue that matches its seeded weights.</param>
/// <param name="Language">The interface language of this run (--language system|ru|en) instead of the stored one, not saved; a smoke run
/// without the option is Russian, so its text checks do not depend on the machine.</param>
/// <param name="Tray">Starts hidden in the notification area (--tray): the stdio MCP proxy starts the application this way for an AI client.</param>
public sealed record LaunchOptions(string WorkDirectory, string? SmokeReport, bool ExitWhenReady = false, string? Python = null, string? PythonSource = null,
    string? WorkerExecutable = null, string? BaseModelCatalog = null, string? Language = null, bool Tray = false)
{
    public static LaunchOptions Parse(string[] args)
    {
        var workDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MautVision");
        string? report = null, python = null, source = null, worker = null, catalog = null, language = null;
        var exitWhenReady = false;
        var tray = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--exit-when-ready")
            {
                exitWhenReady = true;
                continue;
            }
            if (args[index] == "--tray")
            {
                tray = true;
                continue;
            }
            if (index + 1 >= args.Length)
                throw new ArgumentException(Strings.LaunchValueExpected);
            switch (args[index])
            {
                case "--work-directory": workDirectory = args[++index]; break;
                case "--smoke-report": report = args[++index]; break;
                case "--python": python = args[++index]; break;
                case "--python-source": source = args[++index]; break;
                case "--worker-executable": worker = args[++index]; break;
                case "--base-model-catalog": catalog = args[++index]; break;
                case "--language": language = args[++index]; break;
                default: throw new ArgumentException(Strings.LaunchUnknownParameter);
            }
        }
        if ((python is null) != (source is null)) throw new ArgumentException(Strings.LaunchPythonPair);
        if (catalog is not null && report is null) throw new ArgumentException(Strings.LaunchCatalogSmokeOnly);
        if (language is not null && !HostLanguage.IsSetting(language)) throw new ArgumentException(Strings.LaunchLanguageInvalid);
        return new LaunchOptions(Path.GetFullPath(workDirectory), report is null ? null : Path.GetFullPath(report), exitWhenReady,
            python is null ? null : Path.GetFullPath(python), source is null ? null : Path.GetFullPath(source), worker is null ? null : Path.GetFullPath(worker),
            catalog is null ? null : Path.GetFullPath(catalog), language ?? (report is null ? null : HostLanguage.Russian), tray && report is null);
    }

    /// <summary>
    /// The launch description of the Python worker: an explicit executable, then the interpreter with the sources (development), then
    /// the build shipped in the application directory (python/maut-training-worker/). Null — training reports runtime_not_configured.
    /// </summary>
    public PythonWorkerLaunch? PythonWorker(string applicationDirectory)
    {
        if (WorkerExecutable is not null) return PythonWorkerLaunch.Packaged(WorkerExecutable);
        if (Python is not null && PythonSource is not null) return PythonWorkerLaunch.Interpreter(Python, PythonSource);
        var packaged = Path.Combine(applicationDirectory, "python", "maut-training-worker", "maut-training-worker.exe");
        return File.Exists(packaged) ? PythonWorkerLaunch.Packaged(packaged) : null;
    }
}
