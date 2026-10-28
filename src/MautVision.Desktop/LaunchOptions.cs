using System.IO;
using MautVision.Core.Interfaces.Python;

namespace MautVision.Desktop;

/// <param name="ExitWhenReady">Closes the application once the UI reports the library is shown; used by perf.ps1.</param>
/// <param name="Python">Interpreter of the pinned development environment (--python), together with <paramref name="PythonSource"/>.</param>
/// <param name="PythonSource">Folder of the worker sources (repository python/) for the interpreter (--python-source).</param>
/// <param name="WorkerExecutable">A ready worker executable (--worker-executable): a separate build or the fake worker of the smoke.</param>
/// <param name="BaseModelCatalog">Smoke only (--base-model-catalog with --smoke-report): a catalogue that matches its seeded weights.</param>
public sealed record LaunchOptions(string WorkDirectory, string? SmokeReport, bool ExitWhenReady = false, string? Python = null, string? PythonSource = null,
    string? WorkerExecutable = null, string? BaseModelCatalog = null)
{
    public static LaunchOptions Parse(string[] args)
    {
        var workDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MautVision");
        string? report = null, python = null, source = null, worker = null, catalog = null;
        var exitWhenReady = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--exit-when-ready")
            {
                exitWhenReady = true;
                continue;
            }
            if (index + 1 >= args.Length)
                throw new ArgumentException("Ожидалось значение параметра запуска.");
            switch (args[index])
            {
                case "--work-directory": workDirectory = args[++index]; break;
                case "--smoke-report": report = args[++index]; break;
                case "--python": python = args[++index]; break;
                case "--python-source": source = args[++index]; break;
                case "--worker-executable": worker = args[++index]; break;
                case "--base-model-catalog": catalog = args[++index]; break;
                default: throw new ArgumentException("Неизвестный параметр запуска.");
            }
        }
        if ((python is null) != (source is null)) throw new ArgumentException("--python и --python-source задаются вместе.");
        if (catalog is not null && report is null) throw new ArgumentException("--base-model-catalog допускается только в smoke-проверке.");
        return new LaunchOptions(Path.GetFullPath(workDirectory), report is null ? null : Path.GetFullPath(report), exitWhenReady,
            python is null ? null : Path.GetFullPath(python), source is null ? null : Path.GetFullPath(source), worker is null ? null : Path.GetFullPath(worker),
            catalog is null ? null : Path.GetFullPath(catalog));
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
